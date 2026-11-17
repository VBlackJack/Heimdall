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

using System.ComponentModel;
using System.IO;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Ssh;
using Heimdall.Ssh.Agents;

namespace Heimdall.App.Tests;

/// <summary>
/// A session can name one agent key to offer. The dialog lists what the agents hold, and above
/// all must never lose the saved choice: a list rebuilt around a key that is not loaded would
/// otherwise widen the session to every key of the agent on the next save.
/// </summary>
public sealed class ServerDialogAgentKeyTests
{
    private static readonly TimeSpan FailureBound = TimeSpan.FromSeconds(10);

    private static readonly byte[] BlobA = [1, 2, 3, 4];
    private static readonly byte[] BlobB = [5, 6, 7, 8];
    private static readonly byte[] BlobC = [9, 10, 11, 12];

    private static readonly string FingerprintA = SshAgentKeyFingerprints.Compute(BlobA);
    private static readonly string FingerprintB = SshAgentKeyFingerprints.Compute(BlobB);
    private static readonly string FingerprintC = SshAgentKeyFingerprints.Compute(BlobC);

    [Fact]
    public async Task Probe_ListsKeysWithFingerprint()
    {
        ServerDialogViewModel vm = ProbedDialog(
            await CreateLocalizerAsync(),
            new FakeAgent("Pageant", new FakeKey("work laptop", "ssh-ed25519", BlobA)));

        Assert.Equal(2, vm.SshAgentKeyChoices.Count);
        Assert.Null(vm.SshAgentKeyChoices[0].Fingerprint);
        Assert.Equal(FingerprintA, vm.SshAgentKeyChoices[1].Fingerprint);
        Assert.Equal("Any key (default)", vm.SshAgentKeyChoices[0].DisplayText);
        Assert.Equal($"work laptop - ssh-ed25519 - {FingerprintA}", vm.SshAgentKeyChoices[1].DisplayText);
        Assert.Same(vm.SshAgentKeyChoices[0], vm.SelectedSshAgentKeyChoice);
    }

    [Fact]
    public void DuplicateKeyInTwoAgents_IsListedOnce()
    {
        ServerDialogViewModel vm = ProbedDialog(
            new FakeAgent("Pageant", new FakeKey("a", "ssh-ed25519", BlobA)),
            new FakeAgent("OpenSSH", new FakeKey("a", "ssh-ed25519", BlobA), new FakeKey("b", "ssh-rsa", BlobB)));

        Assert.Equal(
            new string?[] { null, FingerprintA, FingerprintB },
            vm.SshAgentKeyChoices.Select(choice => choice.Fingerprint));
    }

    [Fact]
    public async Task SavedKeyNotLoaded_StaysSelectedAndSaved()
    {
        ServerDialogViewModel vm = ProbedDialog(
            await CreateLocalizerAsync(),
            new FakeAgent("Pageant", new FakeKey("b", "ssh-rsa", BlobB)));

        vm.SshAgentKeyFingerprint = FingerprintC;

        SshAgentKeyChoice? selected = vm.SelectedSshAgentKeyChoice;
        Assert.NotNull(selected);
        Assert.Equal(FingerprintC, selected.Fingerprint);
        Assert.Equal($"Not loaded now: {FingerprintC}", selected.DisplayText);
        Assert.Contains(selected, vm.SshAgentKeyChoices);
        Assert.Equal(FingerprintC, vm.ToDto().SshAgentKeyFingerprint);
    }

    [Fact]
    public async Task AgentDown_KeepsSavedValue()
    {
        ServerDialogViewModel vm = ProbedDialog(
            new FakeAgent("Pageant", new FakeKey("a", "ssh-ed25519", BlobA)));
        vm.SshAgentKeyFingerprint = FingerprintA;

        vm.AgentRegistryFactory = _ => new SshAgentRegistry([]);
        await vm.RefreshAgentChipCommand.ExecuteAsync(null).WaitAsync(FailureBound);

        Assert.Equal(FingerprintA, vm.SshAgentKeyFingerprint);
        Assert.Equal(FingerprintA, vm.SelectedSshAgentKeyChoice?.Fingerprint);
        Assert.Equal(FingerprintA, vm.ToDto().SshAgentKeyFingerprint);
    }

    [Fact]
    public async Task Refresh_NeverClearsTheSavedFingerprint()
    {
        ServerDialogViewModel vm = ProbedDialog(
            new FakeAgent("Pageant", new FakeKey("a", "ssh-ed25519", BlobA)));
        vm.SshAgentKeyFingerprint = FingerprintC;

        // The control pushes null into the selection each time its list is replaced.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ServerDialogViewModel.SshAgentKeyChoices))
            {
                vm.SelectedSshAgentKeyChoice = null;
            }
        };

        await vm.RefreshAgentChipCommand.ExecuteAsync(null).WaitAsync(FailureBound);

        Assert.Equal(FingerprintC, vm.SshAgentKeyFingerprint);
        Assert.Equal(FingerprintC, vm.SelectedSshAgentKeyChoice?.Fingerprint);
        Assert.Equal(FingerprintC, vm.ToDto().SshAgentKeyFingerprint);
    }

    [Fact]
    public async Task Refresh_DoesNotMarkDirty()
    {
        ServerDialogViewModel vm = ProbedDialog(
            new FakeAgent("Pageant", new FakeKey("a", "ssh-ed25519", BlobA)));
        vm.SshAgentKeyFingerprint = FingerprintA;
        vm.IsDirty = false;

        vm.AgentRegistryFactory = _ => new SshAgentRegistry(
            [new FakeAgent("Pageant", new FakeKey("b", "ssh-rsa", BlobB))]);
        await vm.RefreshAgentChipCommand.ExecuteAsync(null).WaitAsync(FailureBound);

        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void UserSelection_MarksDirty_AndIsSaved()
    {
        ServerDialogViewModel vm = ProbedDialog(
            new FakeAgent("Pageant", new FakeKey("a", "ssh-ed25519", BlobA)));
        vm.IsDirty = false;

        vm.SelectedSshAgentKeyChoice = vm.SshAgentKeyChoices.Single(choice => choice.Fingerprint == FingerprintA);

        Assert.True(vm.IsDirty);
        Assert.Equal(FingerprintA, vm.SshAgentKeyFingerprint);
        Assert.Equal(FingerprintA, vm.ToDto().SshAgentKeyFingerprint);
    }

    [Fact]
    public void SelectingAnyKey_SavesNull()
    {
        ServerDialogViewModel vm = ProbedDialog(
            new FakeAgent("Pageant", new FakeKey("a", "ssh-ed25519", BlobA)));
        vm.SshAgentKeyFingerprint = FingerprintA;
        vm.IsDirty = false;

        vm.SelectedSshAgentKeyChoice = vm.SshAgentKeyChoices.Single(choice => choice.Fingerprint is null);

        Assert.True(vm.IsDirty);
        Assert.Null(vm.SshAgentKeyFingerprint);
        Assert.Null(vm.ToDto().SshAgentKeyFingerprint);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void ForwardingWarning_Visibility(bool forwarding, bool keyChosen, bool expected)
    {
        ServerDialogViewModel vm = new()
        {
            SshAgentForwarding = forwarding,
            SshAgentKeyFingerprint = keyChosen ? FingerprintA : null
        };

        Assert.Equal(expected, vm.ShowAgentKeyForwardingWarning);
    }

    [Fact]
    public void ForwardingWarning_IsRaisedWhenEitherInputChanges()
    {
        ServerDialogViewModel vm = new();
        List<string?> raised = [];
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SshAgentForwarding = true;
        vm.SshAgentKeyFingerprint = FingerprintA;

        Assert.Equal(2, raised.Count(name => name == nameof(ServerDialogViewModel.ShowAgentKeyForwardingWarning)));
    }

    [Fact]
    public async Task ForwardingWarningText_IsWrittenOnlyWhileTheWarningApplies()
    {
        ServerDialogViewModel vm = new() { Localizer = await CreateLocalizerAsync(), SshAgentForwarding = true };
        Assert.Equal("", vm.AgentKeyForwardingWarningText);

        vm.SshAgentKeyFingerprint = FingerprintA;
        Assert.StartsWith("Agent forwarding is on", vm.AgentKeyForwardingWarningText, StringComparison.Ordinal);

        vm.SshAgentKeyFingerprint = null;
        Assert.Equal("", vm.AgentKeyForwardingWarningText);
    }

    [Fact]
    public void ToDto_FromDto_RoundTripsTheFingerprint()
    {
        ServerProfileDto stored = SshProfile(FingerprintA);

        ServerProfileDto edited = ServerDialogViewModel.FromDto(stored).ToDto();

        Assert.Equal(FingerprintA, edited.SshAgentKeyFingerprint);
        Assert.Equal(FingerprintA, ServerDialogViewModel.FromDto(edited).SelectedSshAgentKeyChoice?.Fingerprint);
    }

    [Fact]
    public void NonSshProfile_KeepsTheStoredFingerprint()
    {
        ServerProfileDto stored = new()
        {
            Id = "rdp-1",
            DisplayName = "Desktop",
            ConnectionType = "RDP",
            RemoteServer = "desk.example.test",
            SshAgentKeyFingerprint = FingerprintB
        };

        ServerProfileDto edited = ServerDialogViewModel.FromDto(stored).ToDto();

        Assert.Equal(FingerprintB, edited.SshAgentKeyFingerprint);
    }

    [Fact]
    public async Task AuthSummary_ShowsChosenAgentKey()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();

        ServerItemViewModel row = ServerItemViewModel.FromDto(SshProfile(FingerprintA), localizer: localizer);

        Assert.Equal($"Username + SSH agent key {FingerprintA}", row.AuthSummary);
    }

    [Fact]
    public async Task AuthSummary_WithoutChosenKey_IsUnchanged()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();

        ServerItemViewModel row = ServerItemViewModel.FromDto(SshProfile(null), localizer: localizer);

        Assert.Equal("Username + SSH agent", row.AuthSummary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankFingerprint_BehavesAsAnyKey(string blank)
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        ServerDialogViewModel vm = ProbedDialog(
            localizer,
            new FakeAgent("Pageant", new FakeKey("a", "ssh-ed25519", BlobA)));
        vm.SshAgentForwarding = true;

        vm.SshAgentKeyFingerprint = blank;

        Assert.False(vm.HasChosenSshAgentKey);
        Assert.False(vm.ShowAgentKeyForwardingWarning);
        Assert.Equal("", vm.SshAgentKeyHelpText);
        Assert.Null(vm.SelectedSshAgentKeyChoice?.Fingerprint);
        Assert.Equal("Any key (default)", vm.SelectedSshAgentKeyChoice?.DisplayText);
        Assert.DoesNotContain(vm.SshAgentKeyChoices, choice => choice.DisplayText.StartsWith("Not loaded", StringComparison.Ordinal));
        Assert.Null(vm.ToDto().SshAgentKeyFingerprint);

        ServerItemViewModel row = ServerItemViewModel.FromDto(
            new ServerProfileDto
            {
                Id = "ssh-2",
                DisplayName = "Blank",
                ConnectionType = "SSH",
                RemoteServer = "prod.example.test",
                SshUsername = "operator",
                SshAgentKeyFingerprint = blank
            },
            localizer: localizer);
        Assert.Equal("Username + SSH agent", row.AuthSummary);
    }

    private static ServerProfileDto SshProfile(string? fingerprint) => new()
    {
        Id = "ssh-1",
        DisplayName = "Production",
        ConnectionType = "SSH",
        RemoteServer = "prod.example.test",
        SshUsername = "operator",
        SshAgentKeyFingerprint = fingerprint
    };

    private static async Task<LocalizationManager> CreateLocalizerAsync()
    {
        LocalizationManager manager = new();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return manager;
    }

    /// <summary>
    /// A dialog switched to SSH whose own probe, run against <paramref name="agents"/>, has landed.
    /// Waiting for it keeps every probe a test observes one the test started: its continuation
    /// resumes on the thread pool here, and could otherwise land in the middle of an assertion.
    /// </summary>
    private static ServerDialogViewModel ProbedDialog(params ISshAgent[] agents) =>
        ProbedDialog(null, agents);

    private static ServerDialogViewModel ProbedDialog(LocalizationManager? localizer, params ISshAgent[] agents)
    {
        ServerDialogViewModel vm = new() { DisplayName = "Production", RemoteServer = "prod.example.test" };
        vm.Localizer = localizer;
        vm.AgentRegistryFactory = _ => new SshAgentRegistry(agents);

        using ManualResetEventSlim landed = new(false);
        PropertyChangedEventHandler onChanged = (_, e) =>
        {
            if (e.PropertyName == nameof(ServerDialogViewModel.AgentChipText) && vm.AgentChipText.Length > 0)
            {
                landed.Set();
            }
        };

        vm.PropertyChanged += onChanged;
        try
        {
            vm.ConnectionType = "SSH";
            Assert.True(landed.Wait(FailureBound), "The setup probe never reached the chip.");
        }
        finally
        {
            vm.PropertyChanged -= onChanged;
        }

        return vm;
    }

    private sealed class FakeAgent(string name, params FakeKey[] keys) : ISshAgent
    {
        public string Name => name;

        public bool IsAvailable() => true;

        public IReadOnlyList<ISshAgentKey> GetIdentities() => keys;
    }

    private sealed class FakeKey(string comment, string keyType, byte[] blob) : ISshAgentKey
    {
        public string Comment => comment;

        public string KeyType => keyType;

        public byte[] PublicKeyBlob => blob;

        public byte[] Sign(byte[] data, SshAgentSignFlags flags) => throw new NotSupportedException();
    }
}
