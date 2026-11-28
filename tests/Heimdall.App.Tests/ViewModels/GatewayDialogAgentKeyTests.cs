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

using Heimdall.App.Services;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Ssh;
using Heimdall.Ssh.Agents;

namespace Heimdall.App.Tests.ViewModels;

public sealed class GatewayDialogAgentKeyTests
{
    private const string FingerprintA = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string FingerprintB = "SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void GatewayDialogViewModel_RoundTripsSshAgentKeyFingerprint_AndPreservesUnloadedKey()
    {
        var dto = new SshGatewayDto
        {
            Name = "Bastion",
            Host = "bastion.example.com",
            Port = 22,
            User = "ops",
            SshAgentKeyFingerprint = FingerprintA
        };

        var vm = GatewayDialogViewModel.FromDto(dto);
        vm.SetLoadedAgentKeys([
            new SshAgentKeyInfo("OpenSSH", "ssh-ed25519", FingerprintB, "ssh-ed25519 AAAAB", "loaded@host")
        ]);

        Assert.True(vm.HasChosenSshAgentKey);
        Assert.Equal(FingerprintA, vm.SelectedSshAgentKeyChoice?.Fingerprint);
        Assert.Equal(3, vm.SshAgentKeyChoices.Count);

        var exported = vm.ToDto();
        Assert.Equal(FingerprintA, exported.SshAgentKeyFingerprint);

        vm.SelectedSshAgentKeyChoice = vm.SshAgentKeyChoices.First(c => c.Fingerprint == FingerprintB);
        Assert.Equal(FingerprintB, vm.ToDto().SshAgentKeyFingerprint);

        vm.SelectedSshAgentKeyChoice = vm.SshAgentKeyChoices[0];
        Assert.Null(vm.ToDto().SshAgentKeyFingerprint);
    }

    [Fact]
    public void GatewayDialogViewModel_RoundTripsCertificatePathAndCryptoPolicy()
    {
        var dto = new SshGatewayDto
        {
            Name = "StrictBastion",
            Host = "bastion.example.com",
            Port = 22,
            User = "ops",
            KeyPath = @"C:\keys\id_ed25519",
            SshCertificatePath = @"C:\keys\id_ed25519-cert.pub",
            SshCryptoPolicy = SshCryptoPolicy.StrictModern
        };

        var vm = GatewayDialogViewModel.FromDto(dto);
        Assert.Equal(@"C:\keys\id_ed25519-cert.pub", vm.SshCertificatePath);
        Assert.Equal("StrictModern", vm.SshCryptoPolicySelection);

        var exported = vm.ToDto();
        Assert.Equal(@"C:\keys\id_ed25519-cert.pub", exported.SshCertificatePath);
        Assert.Equal(SshCryptoPolicy.StrictModern, exported.SshCryptoPolicy);

        var chainParams = GatewayChainResolver.ToConnectionParams(
            [exported],
            static s => s,
            SshAgentPreference.AutoOpenSshFirst,
            SshCryptoPolicy.Standard);
        Assert.Equal(@"C:\keys\id_ed25519-cert.pub", chainParams[0].CertificatePath);
        Assert.Equal(SshCryptoPolicy.StrictModern, chainParams[0].CryptoPolicy);
    }

    [Fact]
    public void HostKeyTrustService_MultiAlgorithmRemoval_PreservesRemainingAlgorithm()
    {
        var store = new HostKeyStore();
        var trust = new HostKeyTrustService(store);

        trust.Trust("dual.example.com", 22, FingerprintA, "ssh-ed25519", HostKeySource.UserConfirmed);
        store.TrustAdditionalAlgorithm("dual.example.com", 22, FingerprintB, "ssh-rsa", HostKeySource.UserConfirmed);

        Assert.Equal(2, trust.GetAllAlgorithmEntries().Count);

        Assert.True(trust.RemoveAlgorithm("dual.example.com", 22, "ssh-rsa"));
        Assert.Single(trust.GetAllAlgorithmEntries());
        Assert.Equal(FingerprintA, trust.GetEntry("dual.example.com", 22)?.Fingerprint);

        store.TrustAdditionalAlgorithm("dual.example.com", 22, FingerprintB, "ssh-rsa", HostKeySource.UserConfirmed);
        Assert.True(trust.RemoveAlgorithm("dual.example.com", 22, "ssh-ed25519"));
        Assert.Single(trust.GetAllAlgorithmEntries());
        Assert.Equal(FingerprintB, trust.GetEntry("dual.example.com", 22)?.Fingerprint);
    }

    [Fact]
    public async Task SshPublicKeyDeployUiHelper_LocalizesStructuredErrorCodes()
    {
        var localizer = new LocalizationManager();
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "Heimdall.slnx")))
        {
            dir = System.IO.Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        await localizer.LoadAsync(System.IO.Path.Combine(dir, "locales"), "en");

        var invalid = new SshPublicKeyDeployResult(false, false, "raw", SshPublicKeyDeployErrorCode.InvalidPublicKey);
        var exit = new SshPublicKeyDeployResult(false, false, "Remote command exited with status 127.", SshPublicKeyDeployErrorCode.RemoteCommandFailed, 127);
        var noConfirm = new SshPublicKeyDeployResult(false, false, "raw", SshPublicKeyDeployErrorCode.ConfirmationMissing);

        Assert.NotEqual("raw", SshPublicKeyDeployUiHelper.FormatDeployError(invalid, localizer));
        Assert.Contains("127", SshPublicKeyDeployUiHelper.FormatDeployError(exit, localizer), StringComparison.Ordinal);
        Assert.NotEqual("raw", SshPublicKeyDeployUiHelper.FormatDeployError(noConfirm, localizer));
    }
}
