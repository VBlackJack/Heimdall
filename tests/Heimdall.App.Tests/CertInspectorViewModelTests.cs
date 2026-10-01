/*
 * Copyright 2026 Julien Bombled
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Security;

namespace Heimdall.App.Tests;

public class CertInspectorViewModelTests
{
    /// <summary>The scan footer words both counts by their number, each with its own noun.</summary>
    [Theory]
    [InlineData("en", "443", "1 certificate found on 1 port scanned")]
    [InlineData("en", "443,8443", "2 certificates found on 2 ports scanned")]
    [InlineData("fr", "443", "1 certificat trouvé sur 1 port scanné")]
    [InlineData("fr", "443,8443", "2 certificats trouvés sur 2 ports scannés")]
    [InlineData("es", "443", "1 certificado encontrado en 1 puerto escaneado")]
    [InlineData("es", "443,8443", "2 certificados encontrados en 2 puertos escaneados")]
    public async Task CheckAsync_ScanMode_WordsTheFooterByItsCounts(string locale, string ports, string expected)
    {
        using var cert = CreateTestCert();
        var localizer = new Heimdall.Core.Localization.LocalizationManager();
        await localizer.LoadAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        var vm = new CertInspectorViewModel();
        vm.Initialize(localizer);
        vm.SetProber(new FakeProber((_, _, _) => Task.FromResult<CertProbeResult?>(CreateProbeResult(cert))));
        vm.Host = "test.example.com";
        vm.Port = string.Empty;
        vm.SelectedProfile = "custom";
        vm.CustomPorts = ports;

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.Equal(expected, vm.ScanSummaryText);
    }

    [Fact]
    public async Task CheckAsync_EmptyHost_ShowsError()
    {
        var vm = new CertInspectorViewModel();
        vm.Initialize(null);
        vm.Host = "   ";
        vm.Port = "443";

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.ShowError);
        Assert.False(vm.ShowSingleResult);
    }

    [Fact]
    public async Task CheckAsync_InvalidPort_ShowsError()
    {
        var vm = new CertInspectorViewModel();
        vm.Initialize(null);
        vm.Host = "example.com";
        vm.Port = "99999";

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.ShowError);
    }

    [Fact]
    public async Task CheckAsync_SinglePort_Success()
    {
        using var cert = CreateTestCert();
        var prober = new FakeProber((_, _, _) => Task.FromResult<CertProbeResult?>(CreateProbeResult(cert)));

        var vm = new CertInspectorViewModel();
        vm.Initialize(null);
        vm.SetProber(prober);
        vm.Host = "test.example.com";
        vm.Port = "443";

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.ShowSingleResult);
        Assert.NotNull(vm.SingleResult);
        Assert.Equal("CN=test.example.com", vm.SingleResult!.Subject);
        Assert.True(vm.SingleResult.HostnameMatches);
        Assert.False(string.IsNullOrEmpty(vm.SingleDetailsText));
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task CheckAsync_SinglePort_ProbeReturnsNull_ShowsError()
    {
        var vm = new CertInspectorViewModel();
        vm.Initialize(null);
        vm.SetProber(new FakeProber());
        vm.Host = "unreachable.example.com";
        vm.Port = "443";

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.ShowError);
        Assert.False(vm.ShowSingleResult);
    }

    [Fact]
    public async Task CheckAsync_ScanMode_FindsCerts()
    {
        using var cert = CreateTestCert();
        var prober = new FakeProber((_, port, _) =>
        {
            if (port == 443)
                return Task.FromResult<CertProbeResult?>(CreateProbeResult(cert));

            return Task.FromResult<CertProbeResult?>(null);
        });

        var vm = new CertInspectorViewModel();
        vm.Initialize(null);
        vm.SetProber(prober);
        vm.Host = "test.example.com";
        vm.Port = string.Empty;
        vm.SelectedProfile = "custom";
        vm.CustomPorts = "443,8443,9443";

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.ShowScanResults);
        Assert.Single(vm.ScanResults);
        Assert.Equal(443, vm.ScanResults[0].Port);
        Assert.True(vm.ShowScanFooter);
        Assert.False(vm.IsChecking);
    }

    [Fact]
    public async Task CheckAsync_ScanMode_NoCerts_ShowsNoResults()
    {
        var vm = new CertInspectorViewModel();
        vm.Initialize(null);
        vm.SetProber(new FakeProber());
        vm.Host = "empty.example.com";
        vm.Port = string.Empty;
        vm.SelectedProfile = "custom";
        vm.CustomPorts = "8080,9090";

        await vm.CheckCommand.ExecuteAsync(null);

        Assert.True(vm.ShowScanResults);
        Assert.True(vm.ShowScanNoResults);
        Assert.Empty(vm.ScanResults);
    }

    [Fact]
    public void BuildCsvExport_ReturnsValidCsv()
    {
        var vm = new CertInspectorViewModel();
        vm.Initialize(null);
        vm.ScanResults =
        [
            new CertInspectionSummary
            {
                Subject = "CN=csv.test",
                Issuer = "CN=CA",
                NotBefore = DateTime.UtcNow,
                NotAfter = DateTime.UtcNow.AddDays(365),
                DaysRemaining = 365,
                Serial = "AA",
                Thumbprint = "BB",
                SigAlgorithm = "sha256RSA",
                KeySizeBits = 2048,
                Sans = ["csv.test"],
                TlsVersion = "TLS 1.3",
                HostnameMatches = true,
                ExpirationStatus = CertExpirationStatus.Valid,
                Chain = [],
                Port = 443
            }
        ];

        var csv = vm.BuildCsvExport();

        Assert.Contains("443", csv);
        Assert.Contains("csv.test", csv);
    }

    private sealed class FakeProber : ICertProber
    {
        private readonly Func<string, int, CancellationToken, Task<CertProbeResult?>> _probeFunc;

        public FakeProber(Func<string, int, CancellationToken, Task<CertProbeResult?>>? probeFunc = null)
        {
            _probeFunc = probeFunc ?? ((_, _, _) => Task.FromResult<CertProbeResult?>(null));
        }

        public Task<CertProbeResult?> ProbeAsync(string host, int port, CancellationToken ct)
            => _probeFunc(host, port, ct);

        public void Cleanup()
        {
        }
    }

    private static X509Certificate2 CreateTestCert(string cn = "CN=test.example.com", int days = 365)
    {
        using var rsa = RSA.Create(2048);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(cn.Replace("CN=", string.Empty, StringComparison.Ordinal));

        var request = new CertificateRequest(
            cn,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(sanBuilder.Build());

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(days));
    }

    private static CertProbeResult CreateProbeResult(X509Certificate2 cert)
    {
        var copy = new X509Certificate2(cert);
        return new CertProbeResult
        {
            Certificate = copy,
            Protocol = SslProtocols.Tls13,
            Chain = CertInspectorEngine.BuildChain(copy)
        };
    }
}
