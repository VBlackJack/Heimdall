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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Heimdall.App.Tests;

/// <summary>
/// The unsaved-changes guard used to fire on every visit, before the user had touched anything,
/// which trains people to dismiss it unread and drains it for the edits it exists to protect.
/// </summary>
[Collection(CredentialProtectorAppCollection.Name)]
public sealed class ServerDialogDirtyStateTests
{
    [Fact]
    public void AssigningSettingsIsNotAnEdit()
    {
        ServerDialogViewModel vm = Hydrate();
        vm.IsDirty = false;

        vm.Settings = new AppSettings();

        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void AssigningTheLocalizerIsNotAnEdit()
    {
        ServerDialogViewModel vm = Hydrate();
        vm.IsDirty = false;

        vm.Localizer = new LocalizationManager();

        Assert.False(vm.IsDirty);
    }

    // The whole user-visible defect in one assertion: what the edit path does before the window is
    // shown, in its order - PopulateServerDialogOptions and the settings the caller hands over,
    // then everything the dialog service assigns on every open: the localizer, the scope factory
    // and the link resolution. Its fourth assignment, DialogService, is a plain property that
    // notifies nothing and so cannot reach the guard. The edit path is the one with no reset of
    // its own.
    //
    // The steps carry a library link on purpose. Without one InitializePostConnectLinksAsync
    // returns at its linkedIds guard, and calling it proves nothing; the two assertions above the
    // verdict are there so a fixture that stops reaching the writes fails instead of going quiet.
    [Fact]
    public async Task ADialogHydratedTheWayTheDialogServiceDoesIsNotDirty()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(SeedWithLinkedSteps());
        vm.DialogTitle = "Edit session";
        vm.AvailableGateways = [new GatewayOption("gw-1", "Bastion")];
        vm.CreateGatewayRequested = () => Task.FromResult<GatewayOption?>(null);
        vm.Settings = new AppSettings();
        vm.Localizer = new LocalizationManager();
        vm.ServiceScopeFactory = CommandLibrary().GetRequiredService<IServiceScopeFactory>();
        await vm.InitializePostConnectLinksAsync();

        Assert.Equal(LinkedActionTitle, vm.PostConnectSteps[0].LinkedActionTitle);
        Assert.True(vm.PostConnectSteps[1].IsBroken);
        Assert.False(vm.IsDirty);
    }

    // The counterweight to suspending tracking while the links resolve: the suspension has to be
    // lifted again. An outer flag left set makes every assertion above pass and loses the edit.
    [Fact]
    public async Task EditingAStepAfterTheLinksResolveStillArmsTheGuard()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(SeedWithLinkedSteps());
        vm.ServiceScopeFactory = CommandLibrary().GetRequiredService<IServiceScopeFactory>();
        await vm.InitializePostConnectLinksAsync();

        Assert.False(vm.IsDirty);

        vm.PostConnectSteps[0].Input = "uptime";

        Assert.True(vm.IsDirty);
    }

    // Drives the properties the reachability command writes rather than the command itself, to
    // keep the suite off the network. The command body writes nothing else.
    [Fact]
    public void AReachabilityProbeIsNotAnEdit()
    {
        ServerDialogViewModel vm = Hydrate();
        vm.IsDirty = false;

        vm.IsTestingReachability = true;
        vm.TestChipState = SshTestChipState.InProgress;
        vm.TestChipText = "Testing...";
        vm.IsTestingRdpConnection = true;

        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void TheAgentChipProbeIsNotAnEdit()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(Seed("SSH"));

        // Hydration already probed once, so the chip carries its verdict before this test runs.
        // Emptying it is what gives the command something to write: without that the setters see
        // an unchanged value, raise nothing, and the assertion below would hold whatever the
        // exclusion set said. The NotEqual is the proof that the probe really wrote.
        vm.AgentChipText = "";
        vm.AgentChipState = SshAgentChipState.Off;
        vm.IsDirty = false;

        vm.RefreshAgentChipCommand.Execute(null);

        Assert.NotEqual("", vm.AgentChipText);
        Assert.False(vm.IsDirty);
    }

    // The counterweight to suspending tracking while the shell replaces the gateway option list:
    // picking one out of that list is still an edit.
    [Fact]
    public void ChoosingAGatewayStillArmsTheGuard()
    {
        ServerDialogViewModel vm = ServerDialogViewModel.FromDto(Seed("SSH"));
        vm.AvailableGateways = [new GatewayOption("gw-1", "Bastion")];

        Assert.False(vm.IsDirty);

        vm.SelectedGatewayId = "gw-1";

        Assert.True(vm.IsDirty);
    }

    // The counterweight. An over-broad fix - leaving tracking suspended after the Localizer
    // setter, or excluding by name prefix - passes everything above and fails here.
    [Fact]
    public void ARealEditStillArmsTheGuard()
    {
        ServerDialogViewModel vm = Hydrate();

        Assert.False(vm.IsDirty);

        vm.DisplayName = "renamed";

        Assert.True(vm.IsDirty);
    }

    private static ServerDialogViewModel Hydrate() => ServerDialogViewModel.FromDto(Seed("RDP"));

    private const string LinkedActionId = "tail-log";
    private const string LinkedActionTitle = "Tail log";

    private static IServiceProvider CommandLibrary()
        => CommandLibraryTestHelpers.CreateResolverServiceProvider(
            CommandLibraryTestHelpers.CreateLinuxAction(LinkedActionId, LinkedActionTitle, "tail -f /var/log/app.log"));

    // One step the library resolves and one it does not, so both branches of the resolution loop
    // run: it writes LinkedActionTitle on the first and IsBroken on the second, and neither field
    // is anything ToModel saves.
    private static ServerProfileDto SeedWithLinkedSteps()
    {
        ServerProfileDto dto = Seed("SSH");
        dto.PostConnectSteps =
        [
            new PostConnectStep { Id = "1", Input = "pwd", CommandLibraryId = LinkedActionId },
            new PostConnectStep { Id = "2", Input = "hostname", CommandLibraryId = "deleted-from-the-library" }
        ];
        return dto;
    }

    private static ServerProfileDto Seed(string connectionType) => new()
    {
        DisplayName = "Session",
        RemoteServer = "host.example.com",
        ConnectionType = connectionType
    };
}
