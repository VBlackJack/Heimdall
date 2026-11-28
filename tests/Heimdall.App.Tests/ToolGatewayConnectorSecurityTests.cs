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

using System.IO;
using System.Text.Json;
using Heimdall.App.Localization;
using Heimdall.App.Services;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Renci.SshNet;

namespace Heimdall.App.Tests;

public sealed class ToolGatewayConnectorSecurityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _localesDir;

    public ToolGatewayConnectorSecurityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"heimdall-tgw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _localesDir = Path.Combine(_tempDir, "locales");
        Directory.CreateDirectory(_localesDir);
        File.WriteAllText(
            Path.Combine(_localesDir, "en.json"),
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [SshLocalizationKeys.ErrorSshHostKeyRevoked] = "Host key revoked.",
                ["ErrorGatewayHostKeyNotTrusted"] = "Not trusted: {0}:{1}",
                ["ErrorGatewayRouteEmpty"] = "Empty route."
            }));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ConnectCoreAsync_RejectsRevokedGatewayHostKeyBeforeDial()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(_localesDir, "en");

        HostKeyStore store = new();
        HostKeyTrustService trust = new(store);
        const string fp = "SHA256:revokedgatewayfingerprint0123456789abcdef";
        trust.Trust("bastion.example.test", 22, fp, "ssh-ed25519", HostKeySource.UserConfirmed);
        store.AddRevokedKey("bastion.example.test", fp);

        bool dialed = false;
        SshConnectionParams[] chain =
        [
            new SshConnectionParams
            {
                Host = "bastion.example.test",
                Port = 22,
                Username = "ops",
                Password = "secret"
            }
        ];

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ToolGatewayConnector.ConnectCoreAsync(
                chain,
                trust,
                localizer,
                keepAliveSeconds: 30,
                CancellationToken.None,
                createClient: (_, _) =>
                {
                    dialed = true;
                    throw new InvalidOperationException("Should not dial revoked gateway.");
                }));

        Assert.False(dialed);
        Assert.Equal("Host key revoked.", ex.Message);
    }

    [Fact]
    public async Task ConnectCoreAsync_MultiHop_PreservesCryptoPolicyCertAndAgentSignCallbacks()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(_localesDir, "en");

        HostKeyStore store = new();
        HostKeyTrustService trust = new(store);
        trust.Trust("hop1.example.test", 22, "SHA256:hop1fp", "ssh-ed25519", HostKeySource.UserConfirmed);
        trust.Trust("hop2.example.test", 22, "SHA256:hop2fp", "ssh-ed25519", HostKeySource.UserConfirmed);

        Func<ISshAgentKey, bool> confirmCallback = _ => true;
        Action<ISshAgentKey> beforeSignCallback = _ => { };
        SshConnectionParams? capturedDial = null;

        SshConnectionParams[] chain =
        [
            new SshConnectionParams
            {
                Host = "hop1.example.test",
                Port = 22,
                Username = "ops",
                Password = "secret",
                CryptoPolicy = SshCryptoPolicy.StrictModern
            },
            new SshConnectionParams
            {
                Host = "hop2.example.test",
                Port = 22,
                Username = "ops",
                Password = "secret",
                CertificatePath = @"C:\keys\id_ed25519-cert.pub",
                AgentKeyFingerprint = "SHA256:agentfp",
                CryptoPolicy = SshCryptoPolicy.StrictModern,
                OnBeforeAgentSign = beforeSignCallback,
                ConfirmAgentSign = confirmCallback
            }
        ];

        var sentinel = new InvalidOperationException("stop-after-capture");
        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ToolGatewayConnector.ConnectCoreAsync(
                chain,
                trust,
                localizer,
                keepAliveSeconds: 30,
                CancellationToken.None,
                openParents: (_, _, _, _, _) => Task.FromResult(
                    new ToolGatewayConnector.ParentRoute("127.0.0.1", 54321, new NoOpDisposable())),
                createClient: (dialParams, _) =>
                {
                    capturedDial = dialParams;
                    throw sentinel;
                }));

        Assert.Same(sentinel, ex);
        Assert.NotNull(capturedDial);
        Assert.Equal("127.0.0.1", capturedDial.Host);
        Assert.Equal(54321, capturedDial.Port);
        Assert.Equal("hop2.example.test", capturedDial.LogicalHost);
        Assert.Equal(22, capturedDial.LogicalPort);
        Assert.Equal(SshCryptoPolicy.StrictModern, capturedDial.CryptoPolicy);
        Assert.Equal(@"C:\keys\id_ed25519-cert.pub", capturedDial.CertificatePath);
        Assert.Equal("SHA256:agentfp", capturedDial.AgentKeyFingerprint);
        Assert.Same(beforeSignCallback, capturedDial.OnBeforeAgentSign);
        Assert.Same(confirmCallback, capturedDial.ConfirmAgentSign);
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
