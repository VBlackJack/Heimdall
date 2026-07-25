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
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Certificates;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class CertificateGeneratorViewModelTests
{
    [Fact]
    public async Task Generate_WithEmptyCn_RaisesCnRequiredValidation()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService());
        vm.Initialize(localizer);
        vm.ValidityDaysText = "365";

        string? focusField = null;
        vm.ValidationFocusRequested += (_, field) => focusField = field;

        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(localizer["ToolCertGenErrorCnRequired"], vm.ValidationMessage);
        Assert.Equal("Cn", focusField);
    }

    [Fact]
    public async Task Generate_WithInvalidValidity_RaisesInvalidValidityValidation()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService());
        vm.Initialize(localizer);
        vm.Cn = "server.local";
        vm.ValidityDaysText = "0";

        string? focusField = null;
        vm.ValidationFocusRequested += (_, field) => focusField = field;

        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(localizer["ToolCertGenErrorInvalidValidity"], vm.ValidationMessage);
        Assert.Equal("ValidityDays", focusField);
    }

    [Fact]
    public async Task Generate_WithValidOptions_InSelfSignedMode_StoresSelfSignedResult()
    {
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService());
        vm.Cn = "server.local";

        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.Equal("SELF-CERT", vm.CurrentCertPem);
        Assert.Equal("SELF-KEY", vm.CurrentKeyPem);
        Assert.False(vm.IsLeafPanelVisible);
    }

    [Fact]
    public async Task Generate_WithValidOptions_InCaLeafMode_StoresCaLeafResult()
    {
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService());
        vm.Mode = CertificateMode.CaLeaf;
        vm.Cn = "server.local";

        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.True(vm.HasResult);
        Assert.Equal("CA-CERT", vm.CurrentCertPem);
        Assert.Equal("CA-KEY", vm.CurrentKeyPem);
        Assert.True(vm.IsLeafPanelVisible);
        Assert.Equal("LEAF-CERT", vm.LeafCertPem);
    }

    [Fact]
    public async Task Generate_IsDisabledWhileRunning()
    {
        var service = new BlockingCertificateGeneratorService();
        var vm = new CertificateGeneratorViewModel(service) { Cn = "server.local" };

        var task = vm.GenerateCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsGenerating);

        Assert.False(vm.GenerateCommand.CanExecute(null));

        service.ReleaseSelfSigned(CreateSelfSignedResult());
        await task;
    }

    [Fact]
    public void Mode_SelfSigned_SetsComputedFlag()
    {
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService());

        vm.Mode = CertificateMode.SelfSigned;

        Assert.True(vm.IsSelfSigned);
        Assert.False(vm.IsCaLeafMode);
    }

    [Fact]
    public void Mode_CaLeaf_SetsComputedFlag()
    {
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService());

        vm.Mode = CertificateMode.CaLeaf;

        Assert.True(vm.IsCaLeafMode);
        Assert.False(vm.IsSelfSigned);
    }

    [Fact]
    public void IsSelfSigned_SetToTrue_UpdatesMode()
    {
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService())
        {
            Mode = CertificateMode.CaLeaf,
        };

        vm.IsSelfSigned = true;

        Assert.Equal(CertificateMode.SelfSigned, vm.Mode);
    }

    [Fact]
    public async Task ToggleKey_FlipsDisplayedKeyTextToRealKey()
    {
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService()) { Cn = "server.local" };
        await vm.GenerateCommand.ExecuteAsync(null);

        Assert.Equal(CertificateGeneratorViewModel.MaskedPlaceholder, vm.DisplayedKeyText);

        vm.ToggleKeyCommand.Execute(null);

        Assert.Equal("SELF-KEY", vm.DisplayedKeyText);
    }

    [Fact]
    public async Task CopyFingerprint_RaisesCopyTextRequested_WithFingerprint()
    {
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService()) { Cn = "server.local" };
        await vm.GenerateCommand.ExecuteAsync(null);

        string? payload = null;
        vm.CopyTextRequested += (_, text) => payload = text;

        vm.CopyFingerprintCommand.Execute(null);

        Assert.Equal("SHA256:AA:BB", payload);
    }

    [Fact]
    public async Task CopyKey_InCaLeafMode_CopiesCaKeyNotLeaf()
    {
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService())
        {
            Mode = CertificateMode.CaLeaf,
            Cn = "server.local",
        };
        await vm.GenerateCommand.ExecuteAsync(null);

        string? payload = null;
        vm.CopyTextRequested += (_, text) => payload = text;

        vm.CopyKeyCommand.Execute(null);

        Assert.Equal("CA-KEY", payload);
    }

    [Fact]
    public async Task SavePem_InSelfMode_SendsSelfCert()
    {
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService()) { Cn = "server.local" };
        await vm.GenerateCommand.ExecuteAsync(null);

        SaveFileRequest? request = null;
        vm.SaveFileRequested += (_, payload) => request = payload;

        vm.SavePemCommand.Execute(null);

        Assert.NotNull(request);
        Assert.Equal("SELF-CERT", Assert.IsType<string>(request!.Content));
        Assert.False(request.IsBinary);
    }

    [Fact]
    public async Task SavePem_InCaLeafMode_SendsLeafCert()
    {
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService())
        {
            Mode = CertificateMode.CaLeaf,
            Cn = "server.local",
        };
        await vm.GenerateCommand.ExecuteAsync(null);

        SaveFileRequest? request = null;
        vm.SaveFileRequested += (_, payload) => request = payload;

        vm.SavePemCommand.Execute(null);

        Assert.NotNull(request);
        Assert.Equal("LEAF-CERT", Assert.IsType<string>(request!.Content));
    }

    [Fact]
    public async Task SavePfx_WithCancelledPassword_DoesNotRaiseSaveFile()
    {
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService()) { Cn = "server.local" };
        await vm.GenerateCommand.ExecuteAsync(null);

        var passwordRequests = 0;
        var saveRequests = 0;
        vm.PfxPasswordRequested += (_, request) =>
        {
            passwordRequests++;
            request.ResultCallback(null);
        };
        vm.SaveFileRequested += (_, _) => saveRequests++;

        vm.SavePfxCommand.Execute(null);

        Assert.Equal(1, passwordRequests);
        Assert.Equal(0, saveRequests);
    }

    [Fact]
    public async Task SavePfx_WithEnteredPassword_RaisesSaveFileWithPfxBytes()
    {
        var service = new FakeCertificateGeneratorService();
        var vm = new CertificateGeneratorViewModel(service) { Cn = "server.local" };
        await vm.GenerateCommand.ExecuteAsync(null);

        SaveFileRequest? request = null;
        vm.PfxPasswordRequested += (_, passwordRequest) => passwordRequest.ResultCallback("secret");
        vm.SaveFileRequested += (_, payload) => request = payload;

        vm.SavePfxCommand.Execute(null);

        Assert.NotNull(request);
        Assert.True(request!.IsBinary);
        Assert.Equal(service.BuiltSelfSignedPfxBytes, Assert.IsType<byte[]>(request.Content));
    }

    [Fact]
    public async Task LocaleChange_RefreshesCertLabelTextAndShowButton()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService())
        {
            Mode = CertificateMode.CaLeaf,
            Cn = "server.local",
        };
        vm.Initialize(localizer);
        await vm.GenerateCommand.ExecuteAsync(null);
        var englishLabel = vm.CertLabelText;
        var englishButton = vm.BtnShowKeyText;

        await localizer.SwitchLocaleAsync("fr");

        Assert.NotEqual(englishLabel, vm.CertLabelText);
        Assert.NotEqual(englishButton, vm.BtnShowKeyText);
    }

    [Fact]
    public async Task Dispose_ClearsSensitiveState()
    {
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService()) { Cn = "server.local" };
        await vm.GenerateCommand.ExecuteAsync(null);

        vm.Dispose();

        Assert.False(vm.HasResult);
        Assert.Equal(string.Empty, vm.CurrentCertPem);
        Assert.Equal(string.Empty, vm.LeafKeyPem);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromLocaleChanged()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CertificateGeneratorViewModel(new FakeCertificateGeneratorService());
        vm.Initialize(localizer);

        vm.Dispose();
        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal("ToolHelpCERTGEN", vm.HelpContentText);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    /// <summary>
    /// Failure bound for a polled condition. The poll returns as soon as the
    /// predicate holds, so this is not a synchronisation point: it only has to
    /// outlast a saturated thread pool scheduling the work being observed.
    /// </summary>
    private const int PollBackstopMs = 30000;

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = PollBackstopMs)
    {
        var timeoutAt = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 > timeoutAt)
            {
                throw new TimeoutException("Condition was not met before timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static SelfSignedCertificateResult CreateSelfSignedResult() =>
        new("SELF-CERT", "SELF-KEY", "SHA256:AA:BB", [0x01], [0x02]);

    private static CaLeafCertificateResult CreateCaLeafResult() =>
        new("CA-CERT", "CA-KEY", "LEAF-CERT", "LEAF-KEY", "SHA256:AA:BB", [0x03]);

    private sealed class FakeCertificateGeneratorService : ICertificateGeneratorService
    {
        public byte[] BuiltSelfSignedPfxBytes { get; } = [0x10, 0x20];

        public Task<SelfSignedCertificateResult> GenerateSelfSignedAsync(CertificateOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateSelfSignedResult());

        public Task<CaLeafCertificateResult> GenerateCaLeafPairAsync(CertificateOptions options, int caValidityDays, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateCaLeafResult());

        public byte[] BuildPfx(SelfSignedCertificateResult result, string password) => BuiltSelfSignedPfxBytes;

        public byte[] BuildPfx(CaLeafCertificateResult result, string password) => [0x30];
    }

    private sealed class BlockingCertificateGeneratorService : ICertificateGeneratorService
    {
        private readonly TaskCompletionSource<SelfSignedCertificateResult> _selfSignedGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SelfSignedCertificateResult> GenerateSelfSignedAsync(CertificateOptions options, CancellationToken cancellationToken = default) =>
            _selfSignedGate.Task;

        public Task<CaLeafCertificateResult> GenerateCaLeafPairAsync(CertificateOptions options, int caValidityDays, CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateCaLeafResult());

        public byte[] BuildPfx(SelfSignedCertificateResult result, string password) => [0x01];

        public byte[] BuildPfx(CaLeafCertificateResult result, string password) => [0x02];

        public void ReleaseSelfSigned(SelfSignedCertificateResult result) => _selfSignedGate.TrySetResult(result);
    }
}
