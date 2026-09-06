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

using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Settings;
using Heimdall.App.Views.EmbeddedRdp;
using Heimdall.Core.Certificates;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Heimdall.Core.Security.Vault;
using Heimdall.Core.Ssh;
using Heimdall.Core.Updates;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class SettingsViewModelTests : IDisposable
{
    // A few tests below enable the vault inside a try and restore it in the finally; the scope
    // pins the same baseline around every test of this class, whatever the previous member left.
    private readonly CredentialProtectorStateScope _scope = new();

    public void Dispose()
    {
        _scope.Dispose();
    }

    // There has to be a way back into the welcome tour. One reflex Escape used to end it
    // permanently: SkipAsync and EscapeAsync are both bare calls to CompleteAsync, which persists
    // OnboardingCompleted, and that flag had exactly one reader in the product - the first-launch
    // check. No menu item, no setting and no help entry offered to show it again, so the only
    // orientation Heimdall provides could be destroyed by the keystroke every user reaches for on
    // an overlay they did not ask for.
    //
    // The panel raises rather than shows, because the overlay belongs to the shell. That seam is
    // what these pin: a command that fires nothing leaves a dead button, which is the shape this
    // repo has already been caught by twice - a close guard attached to no host, and an
    // empty-state button that changed nothing where the user was looking.
    [Fact]
    public void ReplayOnboarding_RaisesTheRequest()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);

        int raised = 0;
        viewModel.ReplayOnboardingRequested += () => raised++;

        viewModel.ReplayOnboardingCommand.Execute(null);

        Assert.Equal(1, raised);
    }

    // A user who leaves the tour twice must be able to come back twice. Nothing about the request
    // is one-shot, and a stale guard would be indistinguishable from a dead button.
    [Fact]
    public void ReplayOnboarding_CanBeRaisedRepeatedly()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);

        int raised = 0;
        viewModel.ReplayOnboardingRequested += () => raised++;

        viewModel.ReplayOnboardingCommand.Execute(null);
        viewModel.ReplayOnboardingCommand.Execute(null);
        viewModel.ReplayOnboardingCommand.Execute(null);

        Assert.Equal(3, raised);
    }

    // The panel can exist before the shell has wired itself up.
    [Fact]
    public void ReplayOnboarding_WithNoSubscriber_DoesNotThrow()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);

        viewModel.ReplayOnboardingCommand.Execute(null);
    }

    private static SshGatewayDto Gateway(string id, string name) => new()
    {
        Id = id,
        Name = name,
        Host = name,
        Port = 22,
        User = "user"
    };

    // The settings panel seeds its gateway buffer at LoadFromSettings and nothing reseeds it on
    // the way in, so a gateway created from a session dialog sat on disk and in the session tree
    // while this panel still read "no gateway configured". Reported from a live session on
    // 2026-08-25, immediately after - and distinct from - the missing-badge defect it resembles.
    [Fact]
    public void AbsorbExternallyCreatedGateways_ShowsAGatewayCreatedOutsideThePanel()
    {
        FakeConfigManager config = new();
        config.Settings.SshGateways.Add(Gateway("gw-known", "test01"));
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.LoadFromSettings(config.Settings);
        Assert.Equal("test01", Assert.Single(viewModel.Gateways).Name);

        AppSettings external = new();
        external.SshGateways.Add(Gateway("gw-known", "test01"));
        external.SshGateways.Add(Gateway("gw-new", "test02"));

        viewModel.AbsorbExternallyCreatedGateways(external);

        Assert.Contains(viewModel.Gateways, gateway => gateway.Name == "test02");
        Assert.Equal(2, viewModel.Gateways.Count);

        // Absorbing someone else's write is not a user edit. Arming Save here would invite the
        // user to write this buffer back over configuration they never touched.
        Assert.False(viewModel.IsDirty);
    }

    // Reseeding wholesale would be the obvious fix and it would be worse than the defect: this
    // panel buffers on purpose so that Cancel can discard, and an unrelated external write must
    // not destroy an edit in progress.
    [Fact]
    public void AbsorbExternallyCreatedGateways_LeavesAnUnsavedEditAlone()
    {
        FakeConfigManager config = new();
        config.Settings.SshGateways.Add(Gateway("gw-known", "test01"));
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.LoadFromSettings(config.Settings);

        GatewayItemViewModel editing = Assert.Single(viewModel.Gateways);
        editing.Name = "renamed but not saved";

        AppSettings external = new();
        external.SshGateways.Add(Gateway("gw-known", "test01"));
        external.SshGateways.Add(Gateway("gw-new", "test02"));

        viewModel.AbsorbExternallyCreatedGateways(external);

        // Read the edit back OUT OF THE COLLECTION, not off the reference captured above. A
        // wholesale reseed replaces the collection with fresh items and leaves the old object
        // untouched, so asserting on `editing` alone passes while the panel shows "test01" - the
        // exact defect this test exists to catch, surviving vacuously. Caught by a mutant that
        // reseeded here and killed a different test than this one.
        GatewayItemViewModel shown = Assert.Single(
            viewModel.Gateways, gateway => gateway.Id == "gw-known");
        Assert.Same(editing, shown);
        Assert.Equal("renamed but not saved", shown.Name);
        Assert.Contains(viewModel.Gateways, gateway => gateway.Name == "test02");
    }

    // A gateway staged for deletion is absent from the buffer but still present on disk until
    // Save runs, which is exactly the shape "not in the buffer" matches. Absorbing on that test
    // alone would resurrect it under the user's cursor.
    [Fact]
    public async Task AbsorbExternallyCreatedGateways_DoesNotResurrectOneStagedForDeletion()
    {
        FakeConfigManager config = new();
        config.Settings.SshGateways.Add(Gateway("gw-doomed", "test01"));
        FakeDialogService dialog = new() { ConfirmResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);

        viewModel.SelectedGateway = Assert.Single(viewModel.Gateways);
        await viewModel.DeleteGatewayCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.Gateways);

        // Disk still holds it - Save has not run.
        viewModel.AbsorbExternallyCreatedGateways(config.Settings);

        Assert.Empty(viewModel.Gateways);
    }

    [Fact]
    public void ExportJsonOptions_StripsCredentialFieldsFromServerProfiles()
    {
        ServerProfileDto server = new()
        {
            Id = "credential-export-test",
            DisplayName = "Credential Export Test",
            RemoteServer = "10.0.0.1",
            RdpPasswordEncrypted = "rdp-secret",
            SshPasswordEncrypted = "ssh-secret",
            WinRmPasswordEncrypted = "winrm-secret",
            FtpPasswordEncrypted = "ftp-secret",
            TelnetPasswordEncrypted = "telnet-secret",
            SshKeyPassphraseEncrypted = "key-secret",
            VncPassword = "vnc-secret"
        };
        JsonSerializerOptions options = GetExportJsonOptions();

        string json = JsonSerializer.Serialize(new[] { server }, options);
        JsonArray? servers = JsonNode.Parse(json)?.AsArray();

        Assert.NotNull(servers);
        JsonObject exported = Assert.IsType<JsonObject>(servers![0]);
        Assert.Equal("credential-export-test", exported["id"]?.GetValue<string>());
        Assert.False(exported.ContainsKey("rdpPasswordEncrypted"));
        Assert.False(exported.ContainsKey("sshPasswordEncrypted"));
        Assert.False(exported.ContainsKey("winRmPasswordEncrypted"));
        Assert.False(exported.ContainsKey("ftpPasswordEncrypted"));
        Assert.False(exported.ContainsKey("telnetPasswordEncrypted"));
        Assert.False(exported.ContainsKey("sshKeyPassphraseEncrypted"));
        Assert.False(exported.ContainsKey("vncPassword"));

        foreach (string propertyName in exported.Select(property => property.Key))
        {
            Assert.DoesNotContain("password", propertyName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("encrypted", propertyName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("passphrase", propertyName, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ExportJsonOptions_StripsCredentialFieldsFromSshGateways()
    {
        SshGatewayDto gateway = new()
        {
            Id = "gateway-export-test",
            Name = "Gateway Export Test",
            Host = "bastion.example.test",
            Port = 22,
            User = "ops",
            SshPasswordEncrypted = "gateway-password",
            SshKeyPassphraseEncrypted = "gateway-key-secret"
        };
        JsonSerializerOptions options = GetExportJsonOptions();

        string json = JsonSerializer.Serialize(gateway, options);
        JsonObject? exported = JsonNode.Parse(json)?.AsObject();

        Assert.NotNull(exported);
        Assert.Equal("gateway-export-test", exported!["id"]?.GetValue<string>());
        Assert.Equal("bastion.example.test", exported["host"]?.GetValue<string>());
        Assert.False(exported.ContainsKey("sshPasswordEncrypted"));
        Assert.False(exported.ContainsKey("sshKeyPassphraseEncrypted"));
    }

    [Fact]
    public void ExportJsonOptions_StripsCitrixLaunchCommandLine()
    {
        ServerProfileDto server = new()
        {
            Id = "citrix-export-test",
            DisplayName = "Citrix Export Test",
            RemoteServer = "citrix.example.test",
            CitrixLaunchCommandLine = "-launch local cache blob"
        };
        JsonSerializerOptions options = GetExportJsonOptions();

        string json = JsonSerializer.Serialize(new[] { server }, options);
        JsonArray? servers = JsonNode.Parse(json)?.AsArray();

        Assert.NotNull(servers);
        JsonObject exported = Assert.IsType<JsonObject>(servers![0]);
        Assert.Equal("citrix-export-test", exported["id"]?.GetValue<string>());
        Assert.Equal("Citrix Export Test", exported["displayName"]?.GetValue<string>());
        Assert.Equal("citrix.example.test", exported["remoteServer"]?.GetValue<string>());
        Assert.False(exported.ContainsKey("citrixLaunchCommandLine"));
    }

    [Fact]
    public void BuildExportConfigDocument_UsesVersionedEnvelopeWithServersAndGateways()
    {
        ServerProfileDto server = new()
        {
            Id = "server-1",
            DisplayName = "Server One",
            RemoteServer = "server.example.test",
            SshGatewayId = "gateway-1"
        };
        AppSettings settings = new()
        {
            SshGateways =
            [
                new SshGatewayDto
                {
                    Id = "gateway-1",
                    Name = "Bastion",
                    Host = "bastion.example.test",
                    Port = 22,
                    User = "ops"
                }
            ]
        };
        JsonSerializerOptions options = GetExportJsonOptions();

        ProfileConfigDocument document = SettingsViewModel.BuildExportConfigDocument([server], settings);
        string json = JsonSerializer.Serialize(document, options);
        JsonObject? exported = JsonNode.Parse(json)?.AsObject();

        Assert.NotNull(exported);
        Assert.Equal(ProfileConfigDocument.CurrentSchemaVersion, exported!["schemaVersion"]?.GetValue<int>());
        JsonArray servers = Assert.IsType<JsonArray>(exported["servers"]);
        JsonArray gateways = Assert.IsType<JsonArray>(exported["gateways"]);
        Assert.Equal("server-1", servers[0]?["id"]?.GetValue<string>());
        Assert.Equal("gateway-1", gateways[0]?["id"]?.GetValue<string>());
    }

    [Fact]
    public void HaveSameGatewayIds_IgnoresOrderAndCase()
    {
        Assert.True(SettingsViewModel.HaveSameGatewayIds(
            [CreateGateway("GW-A", "Gateway A"), CreateGateway("gw-b", "Gateway B")],
            [CreateGateway("gw-B", "Gateway B"), CreateGateway("gw-a", "Gateway A")]));
    }

    [Fact]
    public void HaveSameGatewayIds_DetectsAddedOrRemovedIds()
    {
        Assert.False(SettingsViewModel.HaveSameGatewayIds(
            [CreateGateway("gw-a", "Gateway A")],
            [CreateGateway("gw-a", "Gateway A"), CreateGateway("gw-b", "Gateway B")]));
    }

    [Fact]
    public async Task ShowGatewayOverviewCommand_UsesPersistedGatewaysAndWarnsWhenPendingGatewaysDiffer()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new()
        {
            Settings = new AppSettings
            {
                SshGateways = [CreateGateway("gw-persisted", "Persisted")]
            },
            Servers =
            [
                CreateServer("alpha", "Alpha", "gw-draft"),
                CreateServer("beta", "Beta", "gw-persisted")
            ]
        };
        FakeDialogService dialog = new()
        {
            GatewayDialogResultToReturn = new GatewayDialogResult(CreateGateway("gw-draft", "Draft"), true)
        };
        SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);

        await viewModel.AddGatewayCommand.ExecuteAsync(null);
        await viewModel.ShowGatewayOverviewCommand.ExecuteAsync(null);

        GatewayOverviewDialogViewModel overview = Assert.IsType<GatewayOverviewDialogViewModel>(
            dialog.LastGatewayOverviewViewModel);
        Assert.True(overview.HasWarningMessage);
        Assert.Contains("Unsaved gateway changes", overview.WarningMessage, StringComparison.Ordinal);
        GatewayOverviewMissingReferenceItemViewModel missing = Assert.Single(overview.MissingReferences);
        Assert.Equal("gw-draft", missing.GatewayId);
        Assert.Equal(["alpha"], missing.SessionIds);
        GatewayOverviewGatewayItemViewModel gateway = Assert.Single(overview.Gateways);
        Assert.Equal("Persisted", gateway.GatewayName);
        GatewayOption option = Assert.Single(missing.AvailableGateways);
        Assert.Equal("gw-persisted", option.Id);
    }

    [Fact]
    public async Task GatewayOverviewReload_UsesPersistedGatewaysAfterAction()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new()
        {
            Settings = new AppSettings
            {
                SshGateways = [CreateGateway("gw-persisted", "Persisted")]
            },
            Servers = [CreateServer("alpha", "Alpha", "gw-missing")]
        };
        FakeDialogService dialog = new()
        {
            GatewayDialogResultToReturn = new GatewayDialogResult(CreateGateway("gw-draft", "Draft"), true)
        };
        SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);
        viewModel.GatewayReferenceMutationHandler = (_, _) =>
        {
            config.Servers = [CreateServer("alpha", "Alpha", "gw-draft")];
            return Task.FromResult(1);
        };

        await viewModel.AddGatewayCommand.ExecuteAsync(null);
        await viewModel.ShowGatewayOverviewCommand.ExecuteAsync(null);

        GatewayOverviewDialogViewModel overview = Assert.IsType<GatewayOverviewDialogViewModel>(
            dialog.LastGatewayOverviewViewModel);
        GatewayOverviewMissingReferenceItemViewModel missing = Assert.Single(overview.MissingReferences);
        Assert.Equal("gw-missing", missing.GatewayId);

        await missing.ClearCommand.ExecuteAsync(null);

        GatewayOverviewMissingReferenceItemViewModel refreshedMissing = Assert.Single(overview.MissingReferences);
        Assert.Equal("gw-draft", refreshedMissing.GatewayId);
    }

    // A2 of BL-0094. The panel commits gateways by wholesale replacement from the
    // snapshot taken at LoadFromSettings, and SettingsChanged never reseeds that
    // snapshot. A gateway persisted by any other surface while the panel is open is
    // therefore erased by the next Save, even when the panel never touched gateways.
    [Fact]
    public async Task Save_KeepsGatewayPersistedByAnotherSurfaceWhilePanelWasOpen()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new()
        {
            Settings = new AppSettings
            {
                SshGateways = [CreateGateway("gw-panel", "Known to the panel")]
            }
        };
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);

        // Another surface - the Add menu, the tree context menu, a profile import -
        // persists a gateway through the config manager while the panel is open.
        await config.MergeSettingAsync(settings =>
            settings.SshGateways.Add(CreateGateway("gw-outside", "Added outside the panel")));

        // The panel saves an unrelated preference. It never touched the gateway list.
        viewModel.PreventSleepDuringSession = !viewModel.PreventSleepDuringSession;
        bool saved = await viewModel.TrySaveAsync();

        Assert.True(saved);
        Assert.Contains(
            config.Settings.SshGateways,
            gateway => string.Equals(gateway.Id, "gw-outside", StringComparison.Ordinal));
        Assert.Contains(
            config.Settings.SshGateways,
            gateway => string.Equals(gateway.Id, "gw-panel", StringComparison.Ordinal));
    }

    [Fact]
    public void ReconcileGateways_KeepsGatewayAddedElsewhereAndLetsThePanelEditWin()
    {
        SshGatewayDto storedShared = CreateGateway("gw-shared", "Stored name");
        SshGatewayDto storedElsewhere = CreateGateway("gw-outside", "Added outside the panel");
        SshGatewayDto editedShared = CreateGateway("GW-SHARED", "Renamed in the panel");
        SshGatewayDto createdInPanel = CreateGateway("gw-new", "Created in the panel");

        List<SshGatewayDto> reconciled = SettingsViewModel.ReconcileGateways(
            [storedShared, storedElsewhere],
            [editedShared, createdInPanel],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(
            ["GW-SHARED", "gw-outside", "gw-new"],
            reconciled.Select(gateway => gateway.Id));
        Assert.Equal("Renamed in the panel", reconciled[0].Name);
    }

    // The parent fixup runs on the reconciled list, not on the panel buffer: before A2 of
    // BL-0094 a gateway persisted elsewhere never reached that pass, so deleting its parent
    // in the panel left it pointing at an id that no longer existed.
    [Fact]
    public void ReconcileGateways_DropsDeletedGatewayAndClearsParentOfGatewayAddedElsewhere()
    {
        SshGatewayDto storedParent = CreateGateway("gw-parent", "Parent");
        SshGatewayDto addedElsewhere = CreateGateway("gw-outside", "Added outside the panel");
        addedElsewhere.ParentGatewayId = "GW-PARENT";

        List<SshGatewayDto> reconciled = SettingsViewModel.ReconcileGateways(
            [storedParent, addedElsewhere],
            [],
            new HashSet<string>(["gw-parent"], StringComparer.OrdinalIgnoreCase));

        SshGatewayDto survivor = Assert.Single(reconciled);
        Assert.Equal("gw-outside", survivor.Id);
        Assert.Null(survivor.ParentGatewayId);
    }

    // A1 of BL-0094. The doors outside the panel own no Save button, so they write through
    // MergeSettingAsync. LoadSettingsAsync is exactly what AddServerAsync calls when the
    // server dialog opens, so what this reads is what that dialog is handed.
    [Fact]
    public async Task AddGatewayOutsidePanel_PersistsImmediatelyAndLeavesThePanelClean()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new();
        FakeDialogService dialog = new()
        {
            GatewayDialogResultToReturn = new GatewayDialogResult(CreateGateway("unused", "Bastion"), true)
        };
        SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);

        await viewModel.AddGatewayOutsidePanelCommand.ExecuteAsync(null);

        SshGatewayDto persisted = Assert.Single((await config.LoadSettingsAsync()).SshGateways);
        Assert.Equal("Bastion", persisted.Name);
        Assert.NotEqual("unused", persisted.Id);
        Assert.Equal(1, config.MergeSettingCallCount);
        Assert.False(viewModel.IsDirty);
        Assert.Single(viewModel.Gateways);
    }

    // The asymmetry between the two doors is deliberate: inside the panel the Save button
    // is the contract and Cancel must still discard. Frozen here so nobody unifies them by
    // accident while tidying.
    [Fact]
    public async Task AddGateway_FromInsideThePanel_StillBuffersUntilSave()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new();
        FakeDialogService dialog = new()
        {
            GatewayDialogResultToReturn = new GatewayDialogResult(CreateGateway("gw-draft", "Draft"), true)
        };
        SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);

        await viewModel.AddGatewayCommand.ExecuteAsync(null);

        Assert.Empty((await config.LoadSettingsAsync()).SshGateways);
        Assert.Equal(0, config.MergeSettingCallCount);
        Assert.True(viewModel.IsDirty);
    }

    [Fact]
    public async Task DeleteGateway_ReferencedByGroupDefault_IncludedInImpact()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new()
        {
            Settings = new AppSettings
            {
                SshGateways = [CreateGateway("Gateway-Root", "Root")],
                GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Production"] = new GroupDefaultsDto { SshGatewayId = "gateway-root" }
                }
            }
        };
        var dialog = new FakeDialogService { ConfirmResult = false };
        SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);
        viewModel.SelectedGateway = Assert.Single(viewModel.Gateways);

        await viewModel.DeleteGatewayCommand.ExecuteAsync(null);

        string message = Assert.Single(dialog.ConfirmCalls).Message;
        Assert.Contains("Servers: 0", message, StringComparison.Ordinal);
        Assert.Contains("Folder defaults: 1", message, StringComparison.Ordinal);
        Assert.Contains("Child gateways: 0", message, StringComparison.Ordinal);
        Assert.Single(viewModel.Gateways);
    }

    [Fact]
    public async Task DeleteGateway_ReferencedByChildGatewayParent_IncludedInImpact()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        SshGatewayDto root = CreateGateway("Gateway-Root", "Root");
        SshGatewayDto child = CreateGateway("gateway-child", "Child");
        child.ParentGatewayId = "gateway-root";
        FakeConfigManager config = new()
        {
            Settings = new AppSettings { SshGateways = [root, child] }
        };
        var dialog = new FakeDialogService { ConfirmResult = false };
        SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);
        viewModel.SelectedGateway = viewModel.Gateways.Single(gateway => gateway.Id == root.Id);

        await viewModel.DeleteGatewayCommand.ExecuteAsync(null);

        string message = Assert.Single(dialog.ConfirmCalls).Message;
        Assert.Contains("Servers: 0", message, StringComparison.Ordinal);
        Assert.Contains("Folder defaults: 0", message, StringComparison.Ordinal);
        Assert.Contains("Child gateways: 1", message, StringComparison.Ordinal);
        Assert.Equal(2, viewModel.Gateways.Count);
    }

    [Fact]
    public async Task DeleteGateway_OnConfirm_ClearsServersFolderDefaultsAndChildParents()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        SshGatewayDto root = CreateGateway("Gateway-Root", "Root");
        SshGatewayDto child = CreateGateway("gateway-child", "Child");
        child.ParentGatewayId = "gateway-root";
        FakeConfigManager config = new()
        {
            Settings = new AppSettings
            {
                SshGateways = [root, child],
                GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Production"] = new GroupDefaultsDto { SshGatewayId = "GATEWAY-ROOT" }
                }
            },
            Servers = [CreateServer("alpha", "Alpha", "gateway-ROOT")]
        };
        var dialog = new FakeDialogService { ConfirmResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);
        viewModel.SelectedGateway = viewModel.Gateways.Single(gateway => gateway.Id == root.Id);

        await viewModel.DeleteGatewayCommand.ExecuteAsync(null);
        bool saved = await viewModel.TrySaveAsync();

        Assert.True(saved);
        string message = Assert.Single(dialog.ConfirmCalls).Message;
        Assert.Contains("Servers: 1", message, StringComparison.Ordinal);
        Assert.Contains("Folder defaults: 1", message, StringComparison.Ordinal);
        Assert.Contains("Child gateways: 1", message, StringComparison.Ordinal);
        Assert.Equal(["servers", "settings"], config.PersistenceCalls);
        Assert.Null(Assert.Single(config.Servers).SshGatewayId);
        Assert.Null(config.Settings.GroupDefaults["Production"].SshGatewayId);
        SshGatewayDto savedChild = Assert.Single(config.Settings.SshGateways);
        Assert.Equal(child.Id, savedChild.Id);
        Assert.Null(savedChild.ParentGatewayId);
    }

    [Fact]
    public async Task DeleteGateway_InterruptedBeforeSettingsWrite_LeavesRecoverableState()
    {
        SshGatewayDto root = CreateGateway("Gateway-Root", "Root");
        SshGatewayDto child = CreateGateway("gateway-child", "Child");
        child.ParentGatewayId = "gateway-root";
        FakeConfigManager config = new()
        {
            Settings = new AppSettings
            {
                SshGateways = [root, child],
                GroupDefaults = new Dictionary<string, GroupDefaultsDto>
                {
                    ["Production"] = new GroupDefaultsDto { SshGatewayId = "GATEWAY-ROOT" }
                }
            },
            Servers = [CreateServer("alpha", "Alpha", "gateway-ROOT")]
        };
        var dialog = new FakeDialogService { ConfirmResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);
        viewModel.SelectedGateway = viewModel.Gateways.Single(gateway => gateway.Id == root.Id);
        await viewModel.DeleteGatewayCommand.ExecuteAsync(null);
        config.FailOnMergeSetting = true;

        bool saved = await viewModel.TrySaveAsync();

        Assert.False(saved);
        Assert.Equal(["servers", "settings"], config.PersistenceCalls);
        Assert.Null(Assert.Single(config.Servers).SshGatewayId);
        Assert.Equal("GATEWAY-ROOT", config.Settings.GroupDefaults["Production"].SshGatewayId);
        Assert.Contains(config.Settings.SshGateways, gateway => gateway.Id == root.Id);
        Assert.Equal(
            "gateway-root",
            config.Settings.SshGateways.Single(gateway => gateway.Id == child.Id).ParentGatewayId);
    }

    [Fact]
    public async Task DeleteGateway_Unreferenced_StillDeletesCleanly()
    {
        SshGatewayDto gateway = CreateGateway("gateway-unused", "Unused");
        FakeConfigManager config = new()
        {
            Settings = new AppSettings { SshGateways = [gateway] }
        };
        var dialog = new FakeDialogService { ConfirmResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);
        viewModel.SelectedGateway = Assert.Single(viewModel.Gateways);

        await viewModel.DeleteGatewayCommand.ExecuteAsync(null);
        bool saved = await viewModel.TrySaveAsync();

        Assert.True(saved);
        Assert.Empty(config.Settings.SshGateways);
        Assert.Empty(viewModel.Gateways);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task SaveThenLoad_PreservesNewRdpRedirectionDefaults()
    {
        var config = new FakeConfigManager();
        var viewModel = CreateViewModel(config);
        viewModel.RdpDefaultRedirectComPorts = true;
        viewModel.RdpDefaultRedirectSmartCards = true;
        viewModel.RdpDefaultRedirectWebcam = true;
        viewModel.RdpDefaultRedirectUsb = true;
        viewModel.RdpDefaultAudioCapture = true;
        viewModel.RdpDefaultStrictServerAuthentication = true;

        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.True(saved.RdpDefaultRedirectComPorts);
        Assert.True(saved.RdpDefaultRedirectSmartCards);
        Assert.True(saved.RdpDefaultRedirectWebcam);
        Assert.True(saved.RdpDefaultRedirectUsb);
        Assert.True(saved.RdpDefaultAudioCapture);
        Assert.True(saved.RdpDefaultStrictServerAuthentication);

        var reloaded = CreateViewModel(new FakeConfigManager());
        reloaded.LoadFromSettings(saved);

        Assert.True(reloaded.RdpDefaultRedirectComPorts);
        Assert.True(reloaded.RdpDefaultRedirectSmartCards);
        Assert.True(reloaded.RdpDefaultRedirectWebcam);
        Assert.True(reloaded.RdpDefaultRedirectUsb);
        Assert.True(reloaded.RdpDefaultAudioCapture);
        Assert.True(reloaded.RdpDefaultStrictServerAuthentication);
    }

    [Fact]
    public void NewRdpRedirectionDefaults_AreFalseByDefault()
    {
        var settings = new AppSettings();

        Assert.False(settings.RdpDefaultRedirectComPorts);
        Assert.False(settings.RdpDefaultRedirectSmartCards);
        Assert.False(settings.RdpDefaultRedirectWebcam);
        Assert.False(settings.RdpDefaultRedirectUsb);
        Assert.False(settings.RdpDefaultAudioCapture);
    }

    [Fact]
    public void RdpResolutionPresets_DefaultMatchesBuiltInSet()
    {
        var settings = new AppSettings();

        Assert.Equal(10, settings.RdpResolutionPresets.Length);
        Assert.Equal("1920x1080", settings.RdpResolutionPresets[0]);
        Assert.Contains("3840x2160", settings.RdpResolutionPresets);
    }

    [Fact]
    public async Task SaveThenLoad_PreservesResolutionPresetsAndAdvancedTimeouts()
    {
        var config = new FakeConfigManager();
        var viewModel = CreateViewModel(config);
        viewModel.RdpResolutionPresets = ["2560x1080", "3440x1440"];
        viewModel.RdpResizeEnableDelayMs = 5000;
        viewModel.RdpArtifactCleanupDelayMs = 7000;
        viewModel.RdpCredentialAutofillTimeoutMs = 60000;
        viewModel.RdpKeepAliveIntervalMs = 300000;

        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.Equal(new[] { "2560x1080", "3440x1440" }, saved.RdpResolutionPresets);
        Assert.Equal(5000, saved.RdpResizeEnableDelayMs);
        Assert.Equal(7000, saved.RdpArtifactCleanupDelayMs);
        Assert.Equal(60000, saved.RdpCredentialAutofillTimeoutMs);
        Assert.Equal(300000, saved.RdpKeepAliveIntervalMs);

        var reloaded = CreateViewModel(new FakeConfigManager());
        reloaded.LoadFromSettings(saved);

        Assert.Equal(new[] { "2560x1080", "3440x1440" }, reloaded.RdpResolutionPresets);
        Assert.Equal(5000, reloaded.RdpResizeEnableDelayMs);
        Assert.Equal(7000, reloaded.RdpArtifactCleanupDelayMs);
        Assert.Equal(60000, reloaded.RdpCredentialAutofillTimeoutMs);
        Assert.Equal(300000, reloaded.RdpKeepAliveIntervalMs);
    }

    [Fact]
    public async Task SaveThenLoad_PreservesCredentialProviderKeyFile()
    {
        var config = new FakeConfigManager();
        var viewModel = CreateViewModel(config);
        viewModel.CredentialProviderKeyFile = @"C:\vault\company.keyx";

        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.Equal(@"C:\vault\company.keyx", saved.CredentialProviderKeyFile);

        var reloaded = CreateViewModel(new FakeConfigManager());
        reloaded.LoadFromSettings(saved);

        Assert.Equal(@"C:\vault\company.keyx", reloaded.CredentialProviderKeyFile);
    }

    [Fact]
    public async Task SaveCredentialProviderKeyFile_BlankBecomesNull()
    {
        var config = new FakeConfigManager();
        var viewModel = CreateViewModel(config);
        viewModel.CredentialProviderKeyFile = "   ";

        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.Null(saved.CredentialProviderKeyFile);
    }

    [Fact]
    public async Task TestCredentialProvider_KeyFileTemplateButBlankKeyFile_WarnsWithoutLaunching()
    {
        // A {KeyFile} template with a blank key file must surface the warning and return
        // before constructing or launching any process.
        var localizer = await CreateLocalizerAsync();
        var viewModel = CreateViewModel(new FakeConfigManager(), localizer: localizer);
        viewModel.CredentialProviderCommand = "keepassxc-cli show -s -k \"{KeyFile}\" -a Password";
        viewModel.CredentialProviderKeyFile = "";

        await viewModel.TestCredentialProviderCommand.ExecuteAsync(null);

        Assert.Equal(localizer["CredProvTestNoKeyFile"], viewModel.CredentialProviderTestResult);
    }

    [Fact]
    public void CollapseTunnelsPanelByDefault_LoadFromSettings_DefaultIsTrue()
    {
        var viewModel = CreateViewModel(new FakeConfigManager());

        viewModel.LoadFromSettings(new AppSettings());

        Assert.True(viewModel.CollapseTunnelsPanelByDefault);
    }

    [Fact]
    public void SessionHealthMonitorSettings_LoadFromSettings_MirrorsAllFields()
    {
        var viewModel = CreateViewModel(new FakeConfigManager());

        viewModel.LoadFromSettings(new AppSettings
        {
            SessionHealthMonitorEnabled = false,
            SessionHealthCheckIntervalSeconds = 120,
            SessionHealthProbeTimeoutMs = 4000,
            SessionHealthMaxConcurrent = 20
        });

        Assert.False(viewModel.SessionHealthMonitorEnabled);
        Assert.Equal(120, viewModel.SessionHealthCheckIntervalSeconds);
        Assert.Equal(4000, viewModel.SessionHealthProbeTimeoutMs);
        Assert.Equal(20, viewModel.SessionHealthMaxConcurrent);
    }

    [Fact]
    public async Task SessionHealthMonitorSettings_SaveCommand_PersistsAllFieldsToAppSettings()
    {
        var config = new FakeConfigManager();
        var viewModel = CreateViewModel(config);
        viewModel.SessionHealthMonitorEnabled = false;
        viewModel.SessionHealthCheckIntervalSeconds = 90;
        viewModel.SessionHealthProbeTimeoutMs = 3500;
        viewModel.SessionHealthMaxConcurrent = 25;

        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.False(saved.SessionHealthMonitorEnabled);
        Assert.Equal(90, saved.SessionHealthCheckIntervalSeconds);
        Assert.Equal(3500, saved.SessionHealthProbeTimeoutMs);
        Assert.Equal(25, saved.SessionHealthMaxConcurrent);
    }

    [Fact]
    public async Task SessionHealthCheckInterval_OutOfRange_BlocksSave()
    {
        var config = new FakeConfigManager();
        var viewModel = CreateViewModel(config);
        viewModel.SessionHealthCheckIntervalSeconds = 5; // below the documented 15 s floor

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasErrors);
        Assert.Null(config.SavedSettings);
    }

    [Fact]
    public void CollapseTunnelsPanelByDefault_LoadFromSettings_PreservesFalse()
    {
        var viewModel = CreateViewModel(new FakeConfigManager());

        viewModel.LoadFromSettings(new AppSettings
        {
            CollapseTunnelsPanelByDefault = false
        });

        Assert.False(viewModel.CollapseTunnelsPanelByDefault);
    }

    [Fact]
    public void CollapseTunnelsPanelByDefault_LoadFromSettings_PreservesTrue()
    {
        var viewModel = CreateViewModel(new FakeConfigManager());

        viewModel.LoadFromSettings(new AppSettings
        {
            CollapseTunnelsPanelByDefault = true
        });

        Assert.True(viewModel.CollapseTunnelsPanelByDefault);
    }

    [Fact]
    public async Task SaveAsync_PersistsCollapseTunnelsPanelByDefault_True()
    {
        var config = new FakeConfigManager
        {
            Settings = new AppSettings
            {
                CollapseTunnelsPanelByDefault = false
            }
        };
        var viewModel = CreateViewModel(config);
        viewModel.CollapseTunnelsPanelByDefault = true;

        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.True(saved.CollapseTunnelsPanelByDefault);
    }

    [Fact]
    public async Task SaveAsync_PersistsCollapseTunnelsPanelByDefault_False()
    {
        var config = new FakeConfigManager();
        var viewModel = CreateViewModel(config);
        viewModel.CollapseTunnelsPanelByDefault = false;

        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.False(saved.CollapseTunnelsPanelByDefault);
    }

    [Fact]
    public void FileShareEnableTftp_LoadFromSettings_PreservesFalse()
    {
        var viewModel = CreateViewModel(new FakeConfigManager());

        viewModel.LoadFromSettings(new AppSettings
        {
            FileShareEnableTftp = false
        });

        Assert.False(viewModel.FileShareEnableTftp);
    }

    [Fact]
    public void FileShareEnableTftp_LoadFromSettings_PreservesTrue()
    {
        var viewModel = CreateViewModel(new FakeConfigManager());

        viewModel.LoadFromSettings(new AppSettings
        {
            FileShareEnableTftp = true
        });

        Assert.True(viewModel.FileShareEnableTftp);
    }

    [Fact]
    public async Task SaveAsync_PersistsFileShareEnableTftp()
    {
        var config = new FakeConfigManager();
        var viewModel = CreateViewModel(config);
        viewModel.FileShareEnableTftp = true;

        await viewModel.SaveCommand.ExecuteAsync(null);

        var saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.True(saved.FileShareEnableTftp);
    }

    [Fact]
    public async Task SaveAsync_UsesMergeSettingAsyncInsteadOfSaveSettingsAsync()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.DefaultTheme = "Buffy";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, config.MergeSettingCallCount);
        Assert.Equal(0, config.SaveSettingsCallCount);
    }

    [Fact]
    public async Task SaveAsync_PreservesExternallyManagedGitToken()
    {
        FakeConfigManager config = new()
        {
            Settings = new AppSettings
            {
                CmdLibGitSyncToken = "tok",
                CmdLibGitSyncUrl = "https://old.example/repo.git"
            }
        };
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.LoadFromSettings(config.Settings);
        viewModel.CmdLibGitSyncUrl = "https://new.example/repo.git";

        await viewModel.SaveCommand.ExecuteAsync(null);

        AppSettings saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.Equal("tok", saved.CmdLibGitSyncToken);
        Assert.Equal("https://new.example/repo.git", saved.CmdLibGitSyncUrl);
    }

    [Fact]
    public async Task SaveAsync_PreservesExternallyManagedPinFields()
    {
        FakeConfigManager config = new()
        {
            Settings = new AppSettings
            {
                PinHash = "hash",
                PinSalt = "salt",
                DefaultTheme = "Drakul"
            }
        };
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.LoadFromSettings(config.Settings);
        viewModel.DefaultTheme = "Buffy";

        await viewModel.SaveCommand.ExecuteAsync(null);

        AppSettings saved = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.Equal("hash", saved.PinHash);
        Assert.Equal("salt", saved.PinSalt);
        Assert.Equal("Buffy", saved.DefaultTheme);
    }

    [Fact]
    public async Task ProjectDeletion_ClearsInventoryProjectIds_RecoverableIfInterrupted()
    {
        var config = new FakeConfigManager
        {
            Settings = new AppSettings
            {
                Projects =
                [
                    new ProjectDto
                    {
                        Id = "project-a",
                        Name = "Project A"
                    }
                ]
            },
            Servers =
            [
                new ServerProfileDto
                {
                    Id = "alpha",
                    DisplayName = "Alpha",
                    RemoteServer = "alpha.example.test",
                    ProjectId = "project-a"
                }
            ]
        };
        var dialog = new FakeDialogService { ConfirmResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);
        viewModel.SelectedProject = Assert.Single(viewModel.Projects);
        await viewModel.DeleteProjectCommand.ExecuteAsync(null);
        config.FailOnMergeSetting = true;

        bool saved = await viewModel.TrySaveAsync();

        Assert.False(saved);
        Assert.Null(Assert.Single(config.Servers).ProjectId);
        Assert.Contains(config.Settings.Projects, project => project.Id == "project-a");
    }

    [Fact]
    public async Task ConfigurePin_SetResult_PersistsHashSaltAndResetsLockout()
    {
        DateTime lockoutUntilUtc = DateTime.UtcNow.AddMinutes(5);
        FakeConfigManager config = new FakeConfigManager
        {
            Settings = new AppSettings
            {
                PinFailureCount = 2,
                PinLockoutUntilUtc = lockoutUntilUtc
            }
        };
        FakeDialogService dialog = new FakeDialogService
        {
            PinSetupResultToReturn = new PinSetupResult(PinSetupOutcome.Set, "H", "S")
        };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);

        await viewModel.ConfigurePinCommand.ExecuteAsync(null);

        Assert.Equal("H", config.Settings.PinHash);
        Assert.Equal("S", config.Settings.PinSalt);
        Assert.Equal(0, config.Settings.PinFailureCount);
        Assert.Null(config.Settings.PinLockoutUntilUtc);
        Assert.True(viewModel.IsPinConfigured);
        Assert.NotNull(dialog.LastPinSetupViewModel);
        Assert.False(dialog.LastPinSetupViewModel!.IsPinSet);
    }

    [Fact]
    public async Task ConfigurePin_RemovedResult_ClearsPinAndLockout()
    {
        FakeConfigManager config = new FakeConfigManager
        {
            Settings = new AppSettings
            {
                PinHash = "H",
                PinSalt = "S",
                PinFailureCount = 2,
                PinLockoutUntilUtc = DateTime.UtcNow.AddMinutes(5)
            }
        };
        FakeDialogService dialog = new FakeDialogService
        {
            PinSetupResultToReturn = new PinSetupResult(PinSetupOutcome.Removed, null, null)
        };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);

        await viewModel.ConfigurePinCommand.ExecuteAsync(null);

        Assert.Null(config.Settings.PinHash);
        Assert.Null(config.Settings.PinSalt);
        Assert.Equal(0, config.Settings.PinFailureCount);
        Assert.Null(config.Settings.PinLockoutUntilUtc);
        Assert.False(viewModel.IsPinConfigured);
        Assert.NotNull(dialog.LastPinSetupViewModel);
        Assert.True(dialog.LastPinSetupViewModel!.IsPinSet);
    }

    [Fact]
    public async Task ConfigurePin_CancelledNullResult_DoesNotChangePinState()
    {
        DateTime lockoutUntilUtc = DateTime.UtcNow.AddMinutes(5);
        FakeConfigManager config = new FakeConfigManager
        {
            Settings = new AppSettings
            {
                PinHash = "H",
                PinSalt = "S",
                PinFailureCount = 2,
                PinLockoutUntilUtc = lockoutUntilUtc
            }
        };
        FakeDialogService dialog = new FakeDialogService
        {
            PinSetupResultToReturn = null
        };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);

        await viewModel.ConfigurePinCommand.ExecuteAsync(null);

        Assert.Equal("H", config.Settings.PinHash);
        Assert.Equal("S", config.Settings.PinSalt);
        Assert.Equal(2, config.Settings.PinFailureCount);
        Assert.Equal(lockoutUntilUtc, config.Settings.PinLockoutUntilUtc);
        Assert.True(viewModel.IsPinConfigured);
    }

    [Fact]
    public void LoadFromSettings_WithPin_SetsIsPinConfiguredTrue()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());

        viewModel.LoadFromSettings(new AppSettings
        {
            PinHash = "H",
            PinSalt = "S"
        });

        Assert.True(viewModel.IsPinConfigured);
    }

    [Fact]
    public void LoadFromSettings_WithoutPin_SetsIsPinConfiguredFalse()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());

        viewModel.LoadFromSettings(new AppSettings());

        Assert.False(viewModel.IsPinConfigured);
    }

    [Fact]
    public async Task SaveAsync_InvalidAnnotatedSettingShowsValidationSummaryAndDoesNotPersist()
    {
        var config = new FakeConfigManager();
        var viewModel = CreateViewModel(config);
        viewModel.SshAutoReconnectAttempts = 0;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Null(config.SavedSettings);
        Assert.True(viewModel.HasValidationErrors);
        Assert.Equal("ValidationSettingsSshAutoReconnectAttempts", viewModel.ValidationSummary);
        Assert.Equal(0, viewModel.GeneralTabErrorCount);
        Assert.False(viewModel.HasGeneralTabErrors);
        Assert.Equal(0, viewModel.TerminalTabErrorCount);
        Assert.False(viewModel.HasTerminalTabErrors);
        Assert.Equal(1, viewModel.SshTabErrorCount);
        Assert.True(viewModel.HasSshTabErrors);
        Assert.Equal(0, viewModel.AdvancedTabErrorCount);
        Assert.False(viewModel.HasAdvancedTabErrors);
    }

    // Through the real view model with a loaded localizer: the message the user reads carries the
    // declared bounds, in order, and no placeholder. The two numbers come from the declaration on
    // AppSettings, not from the translation, which is a template.
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public async Task ScreenMessage_ThroughTheViewModel_CarriesTheDeclaredBoundsInOrder(string locale)
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        SettingRange range = SettingRanges.Of(nameof(AppSettings.ExternalToolTimeoutMs));
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager(), localizer: localizer);

        viewModel.ExternalToolTimeoutMs = range.Max + 1;
        bool saved = await viewModel.TrySaveAsync();

        Assert.False(saved);
        string message = Assert.IsType<string>(viewModel.ValidationSummary);
        string min = range.Min.ToString(CultureInfo.InvariantCulture);
        string max = range.Max.ToString(CultureInfo.InvariantCulture);
        Assert.DoesNotContain("{0}", message, StringComparison.Ordinal);
        Assert.Contains(min, message, StringComparison.Ordinal);
        Assert.Contains(max, message, StringComparison.Ordinal);
        Assert.True(
            message.IndexOf(min, StringComparison.Ordinal) < message.IndexOf(max, StringComparison.Ordinal),
            $"the bounds are formatted in the wrong order: {message}");
    }

    [Fact]
    public async Task SaveAsync_InvalidAdvancedSettingUpdatesOnlyAdvancedTabErrorBadge()
    {
        FakeConfigManager config = new FakeConfigManager();
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.ExternalToolTimeoutMs = 1000;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Null(config.SavedSettings);
        Assert.True(viewModel.HasValidationErrors);
        Assert.Equal("ValidationSettingsExtToolTimeout", viewModel.ValidationSummary);
        Assert.Equal(0, viewModel.GeneralTabErrorCount);
        Assert.False(viewModel.HasGeneralTabErrors);
        Assert.Equal(0, viewModel.TerminalTabErrorCount);
        Assert.False(viewModel.HasTerminalTabErrors);
        Assert.Equal(0, viewModel.SshTabErrorCount);
        Assert.False(viewModel.HasSshTabErrors);
        Assert.Equal(1, viewModel.AdvancedTabErrorCount);
        Assert.True(viewModel.HasAdvancedTabErrors);
    }

    [Theory]
    // Each row names the badge that must light. Those rows used to say Advanced for every one of
    // them, which is how four settings whose fields are on the RDP tab came to be counted on the
    // Advanced badge: the assertion agreed with the code and both were wrong about the screen.
    [InlineData(nameof(SettingsViewModel.RdpResizeEnableDelayMs), -1, "ValidationSettingsRdpResizeDelay", "Rdp")]
    [InlineData(nameof(SettingsViewModel.RdpResizeEnableDelayMs), 1, "ValidationSettingsRdpResizeDelay", "Rdp")]
    [InlineData(nameof(SettingsViewModel.RdpResizeEnableDelayMs), 999, "ValidationSettingsRdpResizeDelay", "Rdp")]
    [InlineData(nameof(SettingsViewModel.RdpResizeEnableDelayMs), 60001, "ValidationSettingsRdpResizeDelay", "Rdp")]
    [InlineData(nameof(SettingsViewModel.RdpArtifactCleanupDelayMs), 999, "ValidationSettingsRdpArtifactCleanupDelay", "Rdp")]
    [InlineData(nameof(SettingsViewModel.RdpArtifactCleanupDelayMs), 60001, "ValidationSettingsRdpArtifactCleanupDelay", "Rdp")]
    [InlineData(nameof(SettingsViewModel.RdpCredentialAutofillTimeoutMs), 4999, "ValidationSettingsRdpCredentialAutofillTimeout", "Rdp")]
    [InlineData(nameof(SettingsViewModel.RdpCredentialAutofillTimeoutMs), 300001, "ValidationSettingsRdpCredentialAutofillTimeout", "Rdp")]
    [InlineData(nameof(SettingsViewModel.RdpConnectWatchdogTimeoutMs), -1, "ValidationSettingsRdpTimeout", "Advanced")]
    [InlineData(nameof(SettingsViewModel.RdpConnectWatchdogTimeoutMs), 1, "ValidationSettingsRdpTimeout", "Advanced")]
    [InlineData(nameof(SettingsViewModel.RdpConnectWatchdogTimeoutMs), 4999, "ValidationSettingsRdpTimeout", "Advanced")]
    [InlineData(nameof(SettingsViewModel.RdpConnectWatchdogTimeoutMs), 600001, "ValidationSettingsRdpTimeout", "Advanced")]
    // The two settings that were in no array at all: the save was refused with nothing shown.
    [InlineData(nameof(SettingsViewModel.RdpKeepAliveIntervalMs), 4999, "ValidationSettingsRdpKeepAlive", "Rdp")]
    [InlineData(nameof(SettingsViewModel.RdpKeepAliveIntervalMs), 300001, "ValidationSettingsRdpKeepAlive", "Rdp")]
    [InlineData(nameof(SettingsViewModel.UpdateCheckIntervalHours), 0, "ValidationSettingsUpdateCheckInterval", "General")]
    [InlineData(nameof(SettingsViewModel.UpdateCheckIntervalHours), 8761, "ValidationSettingsUpdateCheckInterval", "General")]
    public async Task SaveAsync_InvalidAdvancedRdpTimeoutRejectsPersistenceAndUpdatesAdvancedBadge(
        string propertyName,
        int value,
        string expectedValidationKey,
        string expectedBadge)
    {
        FakeConfigManager config = new FakeConfigManager();
        SettingsViewModel viewModel = CreateViewModel(config);
        SetAdvancedRdpTimeout(viewModel, propertyName, value);

        bool saved = await viewModel.TrySaveAsync();

        Assert.False(saved);
        Assert.Null(config.SavedSettings);
        Assert.True(viewModel.HasValidationErrors);
        Assert.Equal(expectedValidationKey, viewModel.ValidationSummary);
        Assert.NotEmpty(viewModel.GetErrors(propertyName));
        AssertOnlyBadgeLit(viewModel, expectedBadge);
    }

    /// <summary>
    /// Asserts the named tab badge shows exactly one error and no other badge shows any.
    /// </summary>
    /// <remarks>
    /// Written as "one badge, and only that one" rather than as an assertion about the expected
    /// badge alone, because the defect this replaces was a badge lighting on the wrong tab: an
    /// assertion that only looked at the tab it expected would have been satisfied by the very
    /// arrangement that sent the user to the wrong place.
    /// </remarks>
    private static void AssertOnlyBadgeLit(SettingsViewModel viewModel, string expectedBadge)
    {
        (string Name, int Count, bool Has)[] badges =
        [
            ("General", viewModel.GeneralTabErrorCount, viewModel.HasGeneralTabErrors),
            ("Terminal", viewModel.TerminalTabErrorCount, viewModel.HasTerminalTabErrors),
            ("Ssh", viewModel.SshTabErrorCount, viewModel.HasSshTabErrors),
            ("Rdp", viewModel.RdpTabErrorCount, viewModel.HasRdpTabErrors),
            ("Security", viewModel.SecurityTabErrorCount, viewModel.HasSecurityTabErrors),
            ("Advanced", viewModel.AdvancedTabErrorCount, viewModel.HasAdvancedTabErrors),
        ];

        Assert.Contains(badges, badge => badge.Name == expectedBadge);

        foreach ((string name, int count, bool has) in badges)
        {
            if (name == expectedBadge)
            {
                Assert.Equal(1, count);
                Assert.True(has, $"the {name} badge should be showing");
            }
            else
            {
                Assert.Equal(0, count);
                Assert.False(has, $"the {name} badge should not be showing");
            }
        }
    }

    /// <summary>Types into a settings field the way the binding delivers what the user typed.</summary>
    /// <remarks>
    /// UpdateSourceTrigger=PropertyChanged hands the view model the whole content of the box on
    /// every keystroke, so "24h" arrives as "2", then "24", then "24h". Assigned in one shot the
    /// number never sees the prefix that a real user cannot avoid typing, and an oracle written
    /// that way certifies a state no user can reach: that shortcut is how this file came to
    /// document as a guarantee an invariant the product did not hold.
    /// </remarks>
    private static void TypeInto(Action<string> field, string text)
    {
        for (int length = 1; length <= text.Length; length++)
        {
            field(text[..length]);
        }
    }

    // A number field used to bind straight to its int. A text that did not convert was dropped by
    // the binding before the setter ran, so no error was recorded, nothing was marked dirty, the
    // banner and the badge stayed empty and TrySaveAsync had nothing to refuse: the old number was
    // written and the box went on showing what the user had typed.
    [Fact]
    public async Task TrySaveAsync_NonNumericTextInANumberField_IsRefusedWithABannerAndABadge()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);

        TypeInto(text => viewModel.MaxEmbeddedSessionsText = text, "24h");
        bool saved = await viewModel.TrySaveAsync();

        Assert.False(saved);
        Assert.Null(config.SavedSettings);
        Assert.True(viewModel.HasValidationErrors);
        Assert.True(viewModel.IsDirty, "typing in a settings field must raise the unsaved marker");

        // One field, one error, naming what the box shows. "24" was a whole number on the way to
        // "24h", so it committed and stayed - above the bound, and reported by nothing, because the
        // user is not looking at 24 and cannot act on being told about it. Counting it as well made
        // the badge read 2 for one field and put the range message in the banner.
        Assert.Equal("ValidationSettingsWholeNumber", viewModel.ValidationSummary);
        AssertOnlyBadgeLit(viewModel, "General");
        Assert.Equal(24, viewModel.MaxEmbeddedSessions);
    }

    // Emptying the box needs its own oracle rather than a row on the one above: the obvious "do not
    // nag about an empty field" validator would pass that one and leave this one silent, and an
    // emptied field is exactly what the user who selected the contents and pressed Delete has.
    [Fact]
    public async Task TrySaveAsync_EmptiedNumberField_IsRefusedTheSameWay()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);

        // Two backspaces on a field that reads "24", one keystroke at a time.
        viewModel.UpdateCheckIntervalHoursText = "2";
        viewModel.UpdateCheckIntervalHoursText = string.Empty;
        bool saved = await viewModel.TrySaveAsync();

        Assert.False(saved);
        Assert.Null(config.SavedSettings);
        Assert.Equal("ValidationSettingsWholeNumber", viewModel.ValidationSummary);
        AssertOnlyBadgeLit(viewModel, "General");
        Assert.Equal(2, viewModel.UpdateCheckIntervalHours);
    }

    // Reporting the error is only half of it. A text property that validated but never assigned
    // would pass the two oracles above while freezing every number field at the value it loaded
    // with, which is worse than the defect being fixed.
    [Fact]
    public async Task NumberFieldText_ValidValue_ReachesThePersistedSetting()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);

        TypeInto(text => viewModel.MaxEmbeddedSessionsText = text, "12");

        Assert.Equal(12, viewModel.MaxEmbeddedSessions);

        bool saved = await viewModel.TrySaveAsync();

        Assert.True(saved);
        AppSettings persisted = Assert.IsType<AppSettings>(config.SavedSettings);
        Assert.Equal(12, persisted.MaxEmbeddedSessions);
    }

    // The whole-number check must not swallow the range check: a value that is a number but out of
    // bounds still names the bound it missed, in the message that is already translated for it.
    // The single lit badge is the other half of the claim - a text failure that also poisoned the
    // number, or a text check that duplicated the range, would count the same field twice.
    [Fact]
    public async Task NumberFieldText_OutOfRange_StillReportsTheFieldsOwnRangeMessage()
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);

        TypeInto(text => viewModel.MaxEmbeddedSessionsText = text, "99");
        bool saved = await viewModel.TrySaveAsync();

        Assert.False(saved);
        Assert.Null(config.SavedSettings);
        Assert.Equal("ValidationSettingsMaxSessions", viewModel.ValidationSummary);
        AssertOnlyBadgeLit(viewModel, "General");
    }

    // The counterweight to holding the number's error back: it is held back only while the text
    // does not parse. A text that parses to an out-of-range number is the user's actual mistake and
    // must still be named, or the fix above would trade one wrong message for a missing one.
    [Theory]
    // The four fields the conversion missed, each driven through its own tab's badge. Two of them
    // validated nothing at all before, so a mistyped value reached no surface anywhere - and on an
    // idle auto-lock threshold that is a security timeout the user believes is set and is not.
    [InlineData(nameof(SettingsViewModel.AutoLockIdleMinutesText), "15m", "Security")]
    [InlineData(nameof(SettingsViewModel.WindowsHelloGraceMinutesText), "5m", "Security")]
    [InlineData(nameof(SettingsViewModel.DefaultResolutionWidthText), "1920px", "Rdp")]
    [InlineData(nameof(SettingsViewModel.DefaultResolutionHeightText), "1080px", "Rdp")]
    public async Task TrySaveAsync_NonNumericTextInALateConvertedField_IsRefusedTheSameWay(
        string textProperty,
        string typed,
        string expectedBadge)
    {
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config);
        PropertyInfo property = typeof(SettingsViewModel).GetProperty(textProperty)!;

        TypeInto(text => property.SetValue(viewModel, text), typed);
        bool saved = await viewModel.TrySaveAsync();

        Assert.False(saved);
        Assert.Null(config.SavedSettings);
        Assert.True(viewModel.HasValidationErrors);
        Assert.Equal("ValidationSettingsWholeNumber", viewModel.ValidationSummary);
        AssertOnlyBadgeLit(viewModel, expectedBadge);
    }

    // The bounds on this screen and the bounds the schema enforces used to be one decision written
    // in two places, and they drifted. They are now one declaration on the setting, read by both;
    // this measures that the screen's attribute names that declaration and that the loader agrees
    // at the ends, so a field re-annotated with its own numbers is caught here.
    [Theory]
    [InlineData(nameof(SettingsViewModel.DefaultResolutionWidth))]
    [InlineData(nameof(SettingsViewModel.DefaultResolutionHeight))]
    [InlineData(nameof(SettingsViewModel.WindowsHelloGraceMinutes))]
    [InlineData(nameof(SettingsViewModel.AutoLockIdleMinutes))]
    public void LateConvertedFieldBounds_AreTheBoundsTheSchemaEnforces(string propertyName)
    {
        FieldInfo? backing = typeof(SettingsViewModel).GetField(
            "_" + char.ToLowerInvariant(propertyName[0]) + propertyName[1..],
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(backing);

        SettingRangeOfAttribute? bound = backing!.GetCustomAttribute<SettingRangeOfAttribute>();
        Assert.NotNull(bound);
        Assert.Equal(propertyName, bound!.SettingsPropertyName);

        int minimum = bound.Range.Min;
        int maximum = bound.Range.Max;

        Assert.True(SchemaAccepts(propertyName, minimum), $"the schema refuses {propertyName} = {minimum}");
        Assert.True(SchemaAccepts(propertyName, maximum), $"the schema refuses {propertyName} = {maximum}");
        Assert.False(
            SchemaAccepts(propertyName, minimum - 1),
            $"the schema accepts {propertyName} = {minimum - 1}, which this screen refuses");
        Assert.False(
            SchemaAccepts(propertyName, maximum + 1),
            $"the schema accepts {propertyName} = {maximum + 1}, which this screen refuses");
    }

    private static bool SchemaAccepts(string propertyName, int value)
    {
        AppSettings settings = new();
        typeof(AppSettings).GetProperty(propertyName)!.SetValue(settings, value);

        return !SchemaValidator.ValidateSettings(settings).Errors
            .Any(error => error.StartsWith(propertyName + ":", StringComparison.Ordinal));
    }

    // The badge is bound to Has<Tab>TabErrors, which is computed and so changes nothing on screen
    // unless the count that feeds it announces itself. The RDP badge had the count and no such
    // line, so it stayed hidden however many errors its tab held - and every assertion in this file
    // reads the property directly, which is why they all passed over it.
    [Fact]
    public void EveryTabErrorCountAnnouncesItsBadge()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());
        const string suffix = "TabErrorCount";

        PropertyInfo[] counts =
        [
            .. typeof(SettingsViewModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType == typeof(int)
                    && property.Name.EndsWith(suffix, StringComparison.Ordinal))
                .OrderBy(property => property.Name, StringComparer.Ordinal)
        ];

        Assert.True(
            counts.Length >= 6,
            $"only {counts.Length} tab error counts were found, so the scan is no longer reading "
                + "what it thinks it is");

        List<string> silent = [];
        foreach (PropertyInfo count in counts)
        {
            string badge = "Has" + count.Name[..^suffix.Length] + "TabErrors";
            List<string> raised = [];
            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
                => raised.Add(e.PropertyName ?? string.Empty);

            viewModel.PropertyChanged += OnChanged;
            count.SetValue(viewModel, (int)count.GetValue(viewModel)! + 1);
            viewModel.PropertyChanged -= OnChanged;

            if (!raised.Contains(badge))
            {
                silent.Add(
                    $"{count.Name} changed and {badge} was not raised, so that tab's badge never "
                        + "appears");
            }
        }

        Assert.True(silent.Count == 0, string.Join("\n", silent));
    }

    // The text follows the number only where the number is assigned from outside the fields. Miss
    // one field there and its box keeps showing a value the product is not using, which is the same
    // lie as the defect, told the other way round.
    [Fact]
    public void LoadFromSettings_ReseedsEveryNumberFieldText()
    {
        IReadOnlyList<(PropertyInfo Number, PropertyInfo Text)> pairs = SettingsNumericFields.Pairs();

        Assert.True(
            pairs.Count >= 21,
            $"only {pairs.Count} number fields were found on the view model, so nothing was checked");

        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());
        foreach ((_, PropertyInfo text) in pairs)
        {
            text.SetValue(viewModel, "1");
        }

        viewModel.LoadFromSettings(new AppSettings());

        List<string> stale = [];
        foreach ((PropertyInfo number, PropertyInfo text) in pairs)
        {
            string expected = ((int)number.GetValue(viewModel)!).ToString(CultureInfo.InvariantCulture);
            string actual = (string)text.GetValue(viewModel)!;
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                stale.Add($"{text.Name} shows \"{actual}\" while {number.Name} is {expected}");
            }
        }

        Assert.True(stale.Count == 0, string.Join("\n", stale));
        Assert.False(viewModel.IsDirty, "loading settings must not leave the view model dirty");
    }

    [Theory]
    [InlineData(nameof(SettingsViewModel.RdpResizeEnableDelayMs), 0)]
    [InlineData(nameof(SettingsViewModel.RdpResizeEnableDelayMs), 1000)]
    [InlineData(nameof(SettingsViewModel.RdpResizeEnableDelayMs), 60000)]
    [InlineData(nameof(SettingsViewModel.RdpArtifactCleanupDelayMs), 1000)]
    [InlineData(nameof(SettingsViewModel.RdpArtifactCleanupDelayMs), 60000)]
    [InlineData(nameof(SettingsViewModel.RdpCredentialAutofillTimeoutMs), 5000)]
    [InlineData(nameof(SettingsViewModel.RdpCredentialAutofillTimeoutMs), 300000)]
    // Zero disables the connect watchdog. The schema and the watchdog itself both accept it; the
    // settings screen refused it, so a configuration with the watchdog turned off opened with an
    // error on a field the user had set correctly, and could not be saved until it was changed.
    [InlineData(nameof(SettingsViewModel.RdpConnectWatchdogTimeoutMs), 0)]
    [InlineData(nameof(SettingsViewModel.RdpConnectWatchdogTimeoutMs), 5000)]
    [InlineData(nameof(SettingsViewModel.RdpConnectWatchdogTimeoutMs), 45000)]
    [InlineData(nameof(SettingsViewModel.RdpConnectWatchdogTimeoutMs), 600000)]
    public async Task SaveAsync_AdvancedRdpTimeoutBoundaryPersists(
        string propertyName,
        int value)
    {
        FakeConfigManager config = new FakeConfigManager();
        SettingsViewModel viewModel = CreateViewModel(config);
        SetAdvancedRdpTimeout(viewModel, propertyName, value);

        bool saved = await viewModel.TrySaveAsync();

        Assert.True(saved);
        Assert.NotNull(config.SavedSettings);
        Assert.False(viewModel.HasValidationErrors);
        Assert.Equal(0, viewModel.AdvancedTabErrorCount);
    }

    [Fact]
    public async Task Closing_WithInvalidSettings_StaysOpenAndWarns_DoesNotDiscard()
    {
        var config = new FakeConfigManager();
        var dialog = new FakeDialogService { SaveDiscardResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.SshAutoReconnectAttempts = 0;
        var windowStatePersistCount = 0;

        bool canClose = await WindowClosingFlow.TryPrepareCloseAsync(
            viewModel.IsDirty,
            () => dialog.ShowSaveDiscardCancelAsync("Unsaved", "Save changes?"),
            () => viewModel.TrySaveAsync(),
            () =>
            {
                windowStatePersistCount++;
                return Task.CompletedTask;
            },
            () => dialog.ShowWarning("Not saved", "Fix validation errors."));

        Assert.False(canClose);
        Assert.True(viewModel.IsDirty);
        Assert.True(viewModel.HasValidationErrors);
        Assert.Equal(0, config.MergeSettingCallCount);
        Assert.Equal(0, windowStatePersistCount);
        Assert.Single(dialog.WarningCalls);
    }

    [Fact]
    public async Task Closing_ValidSettings_AwaitsPersistBeforeClose()
    {
        var config = new FakeConfigManager();
        var dialog = new FakeDialogService { SaveDiscardResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(new AppSettings());
        viewModel.DefaultTheme = "Buffy";
        var mergeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMerge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var windowStateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWindowState = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        config.MergeSettingStarted = mergeStarted;
        config.MergeSettingRelease = releaseMerge.Task;

        Task<bool> closing = WindowClosingFlow.TryPrepareCloseAsync(
            viewModel.IsDirty,
            () => dialog.ShowSaveDiscardCancelAsync("Unsaved", "Save changes?"),
            () => viewModel.TrySaveAsync(),
            async () =>
            {
                windowStateStarted.SetResult();
                await releaseWindowState.Task;
            },
            () => dialog.ShowWarning("Not saved", "Retry."));

        await mergeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(closing.IsCompleted);
        Assert.False(windowStateStarted.Task.IsCompleted);

        releaseMerge.SetResult();
        await windowStateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(closing.IsCompleted);
        Assert.False(viewModel.IsDirty);

        releaseWindowState.SetResult();
        Assert.True(await closing);
        Assert.Empty(dialog.WarningCalls);
    }

    [Fact]
    public async Task Closing_SaveFailure_DoesNotClose()
    {
        var config = new FakeConfigManager { FailOnMergeSetting = true };
        var dialog = new FakeDialogService { SaveDiscardResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(new AppSettings());
        viewModel.DefaultTheme = "Buffy";
        var windowStatePersistCount = 0;

        bool canClose = await WindowClosingFlow.TryPrepareCloseAsync(
            viewModel.IsDirty,
            () => dialog.ShowSaveDiscardCancelAsync("Unsaved", "Save changes?"),
            () => viewModel.TrySaveAsync(),
            () =>
            {
                windowStatePersistCount++;
                return Task.CompletedTask;
            },
            () => dialog.ShowWarning("Not saved", "Retry."));

        Assert.False(canClose);
        Assert.True(viewModel.IsDirty);
        Assert.Equal(1, config.MergeSettingCallCount);
        Assert.Equal(0, windowStatePersistCount);
        Assert.Single(dialog.WarningCalls);
    }

    [Fact]
    public void CollapseTunnelsPanelByDefault_RaisesPropertyChanged()
    {
        var viewModel = CreateViewModel(new FakeConfigManager());
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        viewModel.CollapseTunnelsPanelByDefault = false;

        Assert.Contains(nameof(SettingsViewModel.CollapseTunnelsPanelByDefault), changes);
    }

    [Fact]
    public async Task SaveCommand_WhenPersistenceFails_TellsTheUser()
    {
        var config = new FakeConfigManager { FailOnMergeSetting = true };
        var dialog = new FakeDialogService();
        var viewModel = CreateViewModel(config, dialog);
        viewModel.DefaultTheme = "Buffy";

        await viewModel.SaveCommand.ExecuteAsync(null);

        // A persistence failure raises no badge and no validation summary: before this
        // it went to the log and nowhere the user could see.
        var warning = Assert.Single(dialog.WarningCalls);
        Assert.Equal("SettingsCloseSaveFailedTitle", warning.Title);
        Assert.Equal("SettingsCloseSaveFailedMessage", warning.Message);
    }

    [Fact]
    public async Task SaveCommand_WhenPersistenceSucceeds_SaysNothing()
    {
        var config = new FakeConfigManager();
        var dialog = new FakeDialogService();
        var viewModel = CreateViewModel(config, dialog);
        viewModel.DefaultTheme = "Buffy";

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Empty(dialog.WarningCalls);
    }

    [Fact]
    public async Task ResetToDefaultsCommand_RestoresFactoryDefaultsAfterConfirmation()
    {
        var dialog = new FakeDialogService { ConfirmResult = true };
        var viewModel = CreateViewModel(new FakeConfigManager(), dialog);
        viewModel.DefaultTheme = "Buffy";
        viewModel.MaxEmbeddedSessions = 7;
        viewModel.TerminalFontSize = 22;

        await viewModel.ResetToDefaultsCommand.ExecuteAsync(null);

        var expected = await LoadExpectedFactoryDefaultsAsync();
        Assert.Equal(expected.DefaultTheme, viewModel.DefaultTheme);
        Assert.Equal(expected.MaxEmbeddedSessions, viewModel.MaxEmbeddedSessions);
        Assert.Equal(expected.TerminalFontSize, viewModel.TerminalFontSize);
        Assert.True(viewModel.IsDirty);
        var confirm = Assert.Single(dialog.ConfirmCalls);
        Assert.Equal("SettingsResetDefaultsConfirmTitle", confirm.Title);
        Assert.Equal("SettingsResetDefaultsConfirmBody", confirm.Message);
        Assert.Equal("warning", confirm.Severity);
    }

    [Fact]
    public async Task ResetToDefaultsCommand_RestoresPreferencesButKeepsGatewaysAndProjects()
    {
        var dialog = new FakeDialogService { ConfirmResult = true };
        var viewModel = CreateViewModel(new FakeConfigManager(), dialog);

        var seeded = new AppSettings();
        seeded.SshGateways.Add(new SshGatewayDto
        {
            Id = "gw-1",
            Name = "Bastion",
            Host = "10.0.0.1",
            Port = 2222,
            User = "ops",
            SshPasswordEncrypted = "encrypted-secret"
        });
        seeded.Projects.Add(new ProjectDto { Id = "prj-1", Name = "Production" });
        viewModel.LoadFromSettings(seeded);
        viewModel.DefaultTheme = "Buffy";

        await viewModel.ResetToDefaultsCommand.ExecuteAsync(null);

        // Preferences go back to factory values, which is what the button promises.
        var expected = await LoadExpectedFactoryDefaultsAsync();
        Assert.Equal(expected.DefaultTheme, viewModel.DefaultTheme);

        // The inventory survives. A gateway's stored password is only ever reported
        // back as a boolean, so dropping it here would destroy a secret the user
        // cannot read out of the product and cannot retype from memory.
        var gateway = Assert.Single(viewModel.Gateways);
        Assert.Equal("Bastion", gateway.Name);
        Assert.Equal("10.0.0.1", gateway.Host);
        Assert.Equal(2222, gateway.Port);
        Assert.True(gateway.HasPassword);

        var project = Assert.Single(viewModel.Projects);
        Assert.Equal("Production", project.Name);
    }

    [Fact]
    public async Task ResetToDefaultsCommand_CancelledConfirmationDoesNotModifyState()
    {
        var dialog = new FakeDialogService { ConfirmResult = false };
        var viewModel = CreateViewModel(new FakeConfigManager(), dialog);
        viewModel.DefaultTheme = "Buffy";
        viewModel.MaxEmbeddedSessions = 7;
        viewModel.TerminalFontSize = 22;

        await viewModel.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.Equal("Buffy", viewModel.DefaultTheme);
        Assert.Equal(7, viewModel.MaxEmbeddedSessions);
        Assert.Equal(22, viewModel.TerminalFontSize);
        var confirm = Assert.Single(dialog.ConfirmCalls);
        Assert.Equal("SettingsResetDefaultsConfirmTitle", confirm.Title);
        Assert.Equal("SettingsResetDefaultsConfirmBody", confirm.Message);
        Assert.Equal("warning", confirm.Severity);
    }

    [Fact]
    public async Task ReofferLegacyMigrationNextStartupCommand_ClearsBothMarkersWithoutPromptingNow()
    {
        FakeConfigManager config = new()
        {
            Settings = new AppSettings
            {
                LegacyMigrationDeclinedOfferVersion = 2,
                LegacyMigrationDeclinedSourceFingerprint = "ABC123",
            },
        };
        FakeDialogService dialog = new();
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);

        Assert.True(viewModel.ReofferLegacyMigrationNextStartupCommand.CanExecute(null));

        await viewModel.ReofferLegacyMigrationNextStartupCommand.ExecuteAsync(null);

        Assert.Equal(0, config.Settings.LegacyMigrationDeclinedOfferVersion);
        Assert.Null(config.Settings.LegacyMigrationDeclinedSourceFingerprint);
        Assert.False(viewModel.ReofferLegacyMigrationNextStartupCommand.CanExecute(null));
        Assert.Equal(["settings"], config.PersistenceCalls);
        Assert.Empty(dialog.ConfirmCalls);
        (string title, string message) = Assert.Single(dialog.InfoCalls);
        Assert.Equal("SettingsSectionLegacyMigration", title);
        Assert.Equal("SettingsLegacyMigrationReofferScheduled", message);
    }

    [Fact]
    public void ReofferLegacyMigrationNextStartupCommand_NoMarker_IsDisabled()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());

        viewModel.LoadFromSettings(new AppSettings());

        Assert.False(viewModel.ReofferLegacyMigrationNextStartupCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReofferLegacyMigrationNextStartupCommand_PersistenceFailureKeepsCommandAvailable()
    {
        FakeConfigManager config = new()
        {
            FailOnMergeSetting = true,
            Settings = new AppSettings
            {
                LegacyMigrationDeclinedOfferVersion = 1,
                LegacyMigrationDeclinedSourceFingerprint = "ABC123",
            },
        };
        FakeDialogService dialog = new();
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);

        await viewModel.ReofferLegacyMigrationNextStartupCommand.ExecuteAsync(null);

        Assert.True(viewModel.ReofferLegacyMigrationNextStartupCommand.CanExecute(null));
        Assert.Empty(dialog.InfoCalls);
        (string title, string message) = Assert.Single(dialog.ErrorCalls);
        Assert.Equal("SettingsSectionLegacyMigration", title);
        Assert.Equal("SettingsLegacyMigrationReofferFailed", message);
    }

    // The scheduling is written to disk by the command itself, through the config manager and
    // outside the pending-edit buffer. Marking the panel dirty for it raised the amber dot and,
    // on leaving Settings, a Save/Discard/Cancel prompt about a change neither answer can reach -
    // Discard reads as undoing the scheduling and does not, because nothing was ever pending.
    [Fact]
    public async Task ReofferLegacyMigrationNextStartupCommand_LeavesThePanelClean()
    {
        FakeConfigManager config = new()
        {
            Settings = new AppSettings
            {
                LegacyMigrationDeclinedOfferVersion = 2,
                LegacyMigrationDeclinedSourceFingerprint = "ABC123",
            },
        };
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.LoadFromSettings(config.Settings);

        Assert.False(viewModel.IsDirty);

        await viewModel.ReofferLegacyMigrationNextStartupCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsDirty);
    }

    // The banner and the tab badge were computed inside Save and nowhere else, so correcting the
    // very field the banner named left both on screen asserting an error that no longer existed,
    // with no way to clear them but pressing Save again.
    [Fact]
    public async Task ValidationBanner_ClearsWhenTheFieldItNamesIsCorrected()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());
        viewModel.MaxEmbeddedSessionsText = "50";

        Assert.False(await viewModel.TrySaveAsync());
        Assert.True(viewModel.HasValidationErrors);
        Assert.True(viewModel.HasGeneralTabErrors);
        Assert.Equal(1, viewModel.GeneralTabErrorCount);

        viewModel.MaxEmbeddedSessionsText = "15";

        Assert.False(viewModel.HasValidationErrors);
        Assert.Null(viewModel.ValidationSummary);
        Assert.False(viewModel.HasGeneralTabErrors);
        Assert.Equal(0, viewModel.GeneralTabErrorCount);
    }

    // A live refresh rebuilds the summary from the annotation error set, which the external-tool
    // verdict is not part of: without carrying it, the first keystroke in an unrelated field would
    // wipe a message about a tool that is still incomplete.
    [Fact]
    public async Task ValidationBanner_TracksTheLiveErrorSetWithoutLosingTheExternalToolVerdict()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());
        viewModel.ExternalTools.Add(new ExternalToolItemViewModel());

        Assert.False(await viewModel.TrySaveAsync());
        Assert.Equal("ValidationExtToolIncomplete", viewModel.ValidationSummary);

        // A field error takes the banner over while it stands: a save never reaches the tools.
        viewModel.MaxEmbeddedSessionsText = "50";

        Assert.Equal("ValidationSettingsMaxSessions", viewModel.ValidationSummary);

        // and hands it back rather than clearing it, because the tool is still incomplete.
        viewModel.MaxEmbeddedSessionsText = "15";

        Assert.Equal("ValidationExtToolIncomplete", viewModel.ValidationSummary);
        Assert.True(viewModel.HasValidationErrors);
    }

    // Abandoning an unwanted edit had no control of its own: the only visible way back was Reset
    // Defaults, which loads the factory values over all six tabs. Reverting must reload what is on
    // disk, not what the factory ships.
    [Fact]
    public async Task RevertChangesCommand_ReloadsThePersistedValuesAndLeavesThePanelClean()
    {
        FakeConfigManager config = new()
        {
            Settings = new AppSettings { MaxEmbeddedSessions = 7, DefaultTheme = "Carmilla" },
        };
        FakeDialogService dialog = new() { ConfirmResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);

        viewModel.MaxEmbeddedSessions = 19;
        viewModel.DefaultTheme = "Wormwood";
        Assert.True(viewModel.IsDirty);

        await viewModel.RevertChangesCommand.ExecuteAsync(null);

        Assert.Equal(7, viewModel.MaxEmbeddedSessions);
        Assert.Equal("Carmilla", viewModel.DefaultTheme);
        Assert.False(viewModel.IsDirty);
        Assert.Equal(0, config.SaveSettingsCallCount);
    }

    // Revert sits one place away from Reset Defaults and is the only one of the two whose effect
    // cannot be taken back by declining to save, so a declined confirmation has to change nothing.
    [Fact]
    public async Task RevertChangesCommand_CancelledConfirmationKeepsTheEdits()
    {
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = false };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);

        viewModel.MaxEmbeddedSessions = 19;

        await viewModel.RevertChangesCommand.ExecuteAsync(null);

        Assert.Equal(19, viewModel.MaxEmbeddedSessions);
        Assert.True(viewModel.IsDirty);
        (string title, string message, string severity) = Assert.Single(dialog.ConfirmCalls);
        Assert.Equal("SettingsRevertChangesConfirmTitle", title);
        Assert.Equal("SettingsRevertChangesConfirmBody", message);
        Assert.Equal("warning", severity);
    }

    // A command whose availability nothing announces is a button that stays greyed out while it
    // could be pressed, which is the shape this repository has already shipped twice.
    [Fact]
    public void RevertChangesCommand_AnnouncesItselfAsSoonAsThereIsSomethingToRevert()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());
        viewModel.LoadFromSettings(new AppSettings());

        Assert.False(viewModel.RevertChangesCommand.CanExecute(null));

        int announced = 0;
        viewModel.RevertChangesCommand.CanExecuteChanged += (_, _) => announced++;

        viewModel.MaxEmbeddedSessions = 19;

        Assert.True(viewModel.RevertChangesCommand.CanExecute(null));
        Assert.True(
            announced > 0,
            "the command never told the button it had become available, so the button stays "
                + "greyed out until something unrelated pokes the command manager");
    }

    // The language box answered a pick with nothing at all until Save, while the theme box
    // beside it repainted the window on the spot. A newcomer picks a language, watches the panel
    // stay in English, and concludes the setting is broken.
    [Fact]
    public async Task PickingALanguage_AppliesItWithoutWaitingForSave()
    {
        LocalizationManager localizer = await CreateProductLocalizerAsync();
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);

        viewModel.DefaultLocale = "fr";
        await viewModel.WhenLocaleAppliedAsync();

        Assert.Equal("fr", localizer.CurrentLocale);

        // A preview, not a commitment: the pick is on screen and still nowhere on disk.
        Assert.Equal(0, config.MergeSettingCallCount);
        Assert.True(viewModel.IsDirty);
    }

    // The reason applying live was worth being careful about. The language is the only setting
    // that can be seen before it is kept, so a discard has to be able to take it back - and this
    // is the path where the reseed cannot do it: the reload throws, so the panel is never
    // reseeded, and only an explicit restore stands between the user and a settings screen in a
    // language they did not choose and may not be able to navigate out of.
    [Fact]
    public async Task DiscardChanges_PutsTheLanguageBackEvenWhenTheReloadFails()
    {
        LocalizationManager localizer = await CreateProductLocalizerAsync();
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);

        viewModel.DefaultLocale = "fr";
        await viewModel.WhenLocaleAppliedAsync();
        Assert.Equal("fr", localizer.CurrentLocale);

        config.FailOnLoadSettings = true;

        await Assert.ThrowsAsync<IOException>(viewModel.DiscardChangesAsync);

        Assert.Equal("en", localizer.CurrentLocale);
        Assert.Equal("en", viewModel.DefaultLocale);
    }

    // The one path that reloads the panel without leaving it. Reset Defaults calls
    // LoadFromSettings, which reseeds the restore point from what is on screen - and by then that
    // may be a language the user has only previewed. Without holding the original across the
    // reload, Reset makes the abandoned language the one Discard returns to, which is the parked
    // risk of applying the locale live arriving through a side door.
    [Fact]
    public async Task ResetDefaultsBetweenAPreviewAndADiscard_StillReturnsToTheOriginalLanguage()
    {
        LocalizationManager localizer = await CreateProductLocalizerAsync();
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);

        viewModel.DefaultLocale = "fr";
        await viewModel.WhenLocaleAppliedAsync();
        Assert.Equal("fr", localizer.CurrentLocale);

        await viewModel.ResetToDefaultsCommand.ExecuteAsync(null);

        // The reload is broken on purpose: it is what stops the reseed from covering for a lost
        // restore point, and it is the state DiscardChanges_PutsTheLanguageBackEvenWhenTheReloadFails
        // already established a discard has to survive.
        config.FailOnLoadSettings = true;
        await Assert.ThrowsAsync<IOException>(viewModel.DiscardChangesAsync);

        Assert.Equal("en", localizer.CurrentLocale);
    }

    // The revert a user actually presses. The language is read with no drain in between: a
    // discard that returns while the switch is still in flight reports the edit abandoned before
    // it is, and every caller downstream - the tab guard included - reads a stale language.
    [Fact]
    public async Task RevertChangesCommand_PutsTheLanguageBack()
    {
        LocalizationManager localizer = await CreateProductLocalizerAsync();
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);

        viewModel.DefaultLocale = "fr";
        await viewModel.WhenLocaleAppliedAsync();
        Assert.Equal("fr", localizer.CurrentLocale);

        await viewModel.RevertChangesCommand.ExecuteAsync(null);

        Assert.Equal("en", localizer.CurrentLocale);
        Assert.Equal("en", viewModel.DefaultLocale);
        Assert.False(viewModel.IsDirty);
    }

    // Saving moves the point a later discard comes back to. Without that, abandoning a second
    // edit would undo the first one as well - and here the reload cannot paper over it, so the
    // panel has to be holding the right answer rather than able to look it up.
    [Fact]
    public async Task Saving_MakesTheSavedLanguageTheOneADiscardComesBackTo()
    {
        LocalizationManager localizer = await CreateProductLocalizerAsync();
        FakeConfigManager config = new();
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);

        viewModel.DefaultLocale = "fr";
        Assert.True(await viewModel.TrySaveAsync());
        Assert.Equal("fr", localizer.CurrentLocale);

        viewModel.DefaultLocale = "en";
        await viewModel.WhenLocaleAppliedAsync();
        Assert.Equal("en", localizer.CurrentLocale);

        config.FailOnLoadSettings = true;

        await Assert.ThrowsAsync<IOException>(viewModel.DiscardChangesAsync);

        Assert.Equal("fr", localizer.CurrentLocale);
        Assert.Equal("fr", viewModel.DefaultLocale);
    }

    // An installation missing a locale file must not leave the box naming the language it could
    // not load. The box is what Save writes, and the next launch reads that value before there is
    // any panel to correct it from.
    [Fact]
    public async Task PickingALanguageWhoseFileIsMissing_PutsTheBoxBackAndSaysSo()
    {
        string localesPath = CreateEnglishOnlyLocalesDirectory();
        try
        {
            LocalizationManager localizer = new();
            await localizer.LoadAsync(localesPath, "en");
            FakeConfigManager config = new();
            FakeDialogService dialog = new();
            SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
            viewModel.LoadFromSettings(config.Settings);

            viewModel.DefaultLocale = "fr";
            await viewModel.WhenLocaleAppliedAsync();

            Assert.Equal("en", localizer.CurrentLocale);
            Assert.Equal("en", viewModel.DefaultLocale);

            // Resolved through the localizer rather than written out, so the assertion keeps
            // measuring the key once these two keys have translations.
            (string title, string message) = Assert.Single(dialog.WarningCalls);
            Assert.Equal(localizer["SettingsLanguageApplyFailedTitle"], title);
            Assert.Equal(localizer.Format("SettingsLanguageApplyFailedMessage", "fr"), message);
        }
        finally
        {
            Directory.Delete(localesPath, recursive: true);
        }
    }

    // The switch used to happen inside the save, after the write, and its exception was caught by
    // the same handler that reports a persistence failure - so a missing locale file made Save
    // announce that it had failed to write settings it had in fact just written.
    [Fact]
    public async Task Saving_DoesNotReportFailureBecauseALanguageCouldNotBeLoaded()
    {
        string localesPath = CreateEnglishOnlyLocalesDirectory();
        try
        {
            LocalizationManager localizer = new();
            await localizer.LoadAsync(localesPath, "en");
            FakeConfigManager config = new();
            FakeDialogService dialog = new();
            SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
            viewModel.LoadFromSettings(config.Settings);

            viewModel.DefaultLocale = "fr";
            await viewModel.WhenLocaleAppliedAsync();

            Assert.True(await viewModel.TrySaveAsync());
            Assert.False(viewModel.IsDirty);
            Assert.Equal("en", config.Settings.DefaultLocale);
        }
        finally
        {
            Directory.Delete(localesPath, recursive: true);
        }
    }

    private static async Task<LocalizationManager> CreateProductLocalizerAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    /// <summary>
    /// A locales directory holding English alone, standing in for an installation whose other
    /// locale file did not survive the copy.
    /// </summary>
    private static string CreateEnglishOnlyLocalesDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            nameof(SettingsViewModelTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "locales", "en.json"),
            Path.Combine(path, "en.json"));
        return path;
    }

    [Fact]
    public async Task ResetRdpDefaultsCommand_RestoresRdpDefaults()
    {
        var dialog = new FakeDialogService { ConfirmResult = true };
        var viewModel = CreateViewModel(new FakeConfigManager(), dialog);
        SetNonDefaultRdpValues(viewModel);

        await viewModel.ResetRdpDefaultsCommand.ExecuteAsync(null);

        var expected = await LoadExpectedFactoryDefaultsAsync();
        AssertRdpDefaultsMatch(viewModel, expected);
        Assert.True(viewModel.IsDirty);
    }

    // The confirmed RDP reset is the second place a number is assigned from outside the fields,
    // and the only one that does not route through the load. Without a reseed the boxes went on
    // showing the values being reset away from: Save wrote numbers the screen never displayed, and
    // one keystroke in any of those boxes committed the stale text back over the default.
    [Fact]
    public async Task ResetRdpDefaultsCommand_ReseedsTheTextOfEveryFieldItResets()
    {
        IReadOnlyList<(PropertyInfo Number, PropertyInfo Text)> pairs = SettingsNumericFields.Pairs();
        Assert.True(
            pairs.Count >= 21,
            $"only {pairs.Count} number fields were found on the view model, so nothing was checked");

        var dialog = new FakeDialogService { ConfirmResult = true };
        var viewModel = CreateViewModel(new FakeConfigManager(), dialog);

        // Move every box off its current value through the text, the way a user does, so that any
        // field the reset touches has something visibly stale to leave behind.
        foreach ((PropertyInfo number, PropertyInfo text) in pairs)
        {
            int typed = (int)number.GetValue(viewModel)! + 1;
            text.SetValue(viewModel, typed.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(typed, (int)number.GetValue(viewModel)!);
        }

        await viewModel.ResetRdpDefaultsCommand.ExecuteAsync(null);

        // The premise: the reset really did move a field back. Without this the scan below would
        // pass on a reset that did nothing at all.
        AppSettings expected = await LoadExpectedFactoryDefaultsAsync();
        Assert.Equal(expected.RdpKeepAliveIntervalMs, viewModel.RdpKeepAliveIntervalMs);

        List<string> stale = [];
        foreach ((PropertyInfo number, PropertyInfo text) in pairs)
        {
            string shown = (string)text.GetValue(viewModel)!;
            string held = ((int)number.GetValue(viewModel)!).ToString(CultureInfo.InvariantCulture);
            if (!string.Equals(shown, held, StringComparison.Ordinal))
            {
                stale.Add($"{text.Name} shows \"{shown}\" while {number.Name} is {held}");
            }
        }

        Assert.True(stale.Count == 0, string.Join("\n", stale));
    }

    [Fact]
    public async Task ResetRdpDefaultsCommand_DoesNotTouchUnrelatedProperties()
    {
        var dialog = new FakeDialogService { ConfirmResult = true };
        var viewModel = CreateViewModel(new FakeConfigManager(), dialog);
        viewModel.DefaultTheme = "Buffy";
        viewModel.PlinkPath = @"C:\Tools\plink.exe";
        viewModel.TerminalFontSize = 22;
        SetNonDefaultRdpValues(viewModel);

        await viewModel.ResetRdpDefaultsCommand.ExecuteAsync(null);

        Assert.Equal("Buffy", viewModel.DefaultTheme);
        Assert.Equal(@"C:\Tools\plink.exe", viewModel.PlinkPath);
        Assert.Equal(22, viewModel.TerminalFontSize);
    }

    [Fact]
    public async Task ResetRdpDefaultsCommand_CancelledConfirmationDoesNotModifyState()
    {
        var dialog = new FakeDialogService { ConfirmResult = false };
        var viewModel = CreateViewModel(new FakeConfigManager(), dialog);
        SetNonDefaultRdpValues(viewModel);

        await viewModel.ResetRdpDefaultsCommand.ExecuteAsync(null);

        Assert.Equal(1280, viewModel.DefaultResolutionWidth);
        Assert.Equal(720, viewModel.DefaultResolutionHeight);
        Assert.Equal("External", viewModel.RdpDefaultMode);
        Assert.False(viewModel.RdpDefaultNla);
        Assert.True(viewModel.RdpDefaultStrictServerAuthentication);
        Assert.False(viewModel.RdpDefaultRedirectClipboard);
        Assert.False(viewModel.RdpDefaultAutoReconnect);
    }

    [Fact]
    public async Task ApplyRdpModeToAllCommand_OnlyUpdatesRdpProfiles()
    {
        var config = new FakeConfigManager
        {
            Servers =
            [
                new ServerProfileDto { ConnectionType = "RDP", RdpMode = "Embedded" },
                new ServerProfileDto { ConnectionType = "SSH", RdpMode = "Embedded" },
                new ServerProfileDto { ConnectionType = "RDP", RdpMode = "External" }
            ]
        };
        var dialog = new FakeDialogService { ConfirmResult = true };
        var viewModel = CreateViewModel(config, dialog);
        viewModel.RdpDefaultMode = "External";

        await viewModel.ApplyRdpModeToAllCommand.ExecuteAsync(null);

        Assert.NotNull(config.SavedServers);
        Assert.Equal("External", config.Servers[0].RdpMode);
        Assert.Equal("Embedded", config.Servers[1].RdpMode);
        Assert.Equal("External", config.Servers[2].RdpMode);
        var confirm = Assert.Single(dialog.ConfirmCalls);
        Assert.Equal("danger", confirm.Severity);
    }

    [Fact]
    public async Task ImportConfigCommand_RdpDelegatesToProfileImportService()
    {
        var config = new FakeConfigManager();
        var profileImport = new FakeProfileImportService
        {
            Result = new ProfileImportResult { HasChanges = true }
        };
        var viewModel = CreateViewModel(config, profileImportService: profileImport);
        var importPath = Path.Combine(Path.GetTempPath(), "profile.rdp");
        viewModel.ImportFilePathProvider = () => importPath;
        var configurationChanged = false;
        viewModel.ConfigurationChanged += () => configurationChanged = true;

        await viewModel.ImportConfigCommand.ExecuteAsync(null);

        Assert.Equal(importPath, Assert.Single(profileImport.ImportedPaths));
        Assert.True(configurationChanged);
    }

    [Fact]
    public async Task ImportConfigCommand_NoSelectedPath_DoesNotCallProfileImportService()
    {
        var profileImport = new FakeProfileImportService();
        var viewModel = CreateViewModel(new FakeConfigManager(), profileImportService: profileImport);
        viewModel.ImportFilePathProvider = () => null;

        await viewModel.ImportConfigCommand.ExecuteAsync(null);

        Assert.Empty(profileImport.ImportedPaths);
    }

    [Fact]
    public async Task ImportConfigCommand_ProfileImportFailure_ShowsError()
    {
        var dialog = new FakeDialogService();
        var profileImport = new FakeProfileImportService
        {
            Result = ProfileImportResult.Failure("Unsupported import file type: .txt.")
        };
        var viewModel = CreateViewModel(new FakeConfigManager(), dialog, profileImport);
        viewModel.ImportFilePathProvider = () => Path.Combine(Path.GetTempPath(), "profile.txt");

        await viewModel.ImportConfigCommand.ExecuteAsync(null);

        Assert.Single(dialog.ErrorCalls);
        Assert.Contains(".txt", dialog.ErrorCalls[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportConfigCommand_LegacyInvalidPort_FiltersBeforePersistence()
    {
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        string importRoot = Path.Combine(
            Path.GetTempPath(),
            "heimdall-settings-import-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(importRoot);
        string importPath = Path.Combine(importRoot, "servers.rdg");
        const string content = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.7" schemaVersion="3">
              <file>
                <name>Root</name>
                <server>
                  <name>bad.example.com</name>
                  <displayName>Bad Port</displayName>
                  <connectionSettings>
                    <port>70000</port>
                  </connectionSettings>
                </server>
              </file>
            </RDCMan>
            """;

        try
        {
            await File.WriteAllTextAsync(importPath, content);
            viewModel.ImportFilePathProvider = () => importPath;

            await viewModel.ImportConfigCommand.ExecuteAsync(null);

            Assert.Null(config.SavedServers);
            Assert.Empty(config.Servers);
            Assert.Empty(dialog.ConfirmCalls);
            (string Title, string Message) warning = Assert.Single(dialog.WarningCalls);
            Assert.Contains("Bad Port", warning.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(ServerProfileDto.RemotePort), warning.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(importRoot))
            {
                Directory.Delete(importRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImportConfigCommand_MobaXtermStoredCredentials_ShowsDetectedPasswordNotice()
    {
        FakeConfigManager config = new();
        FakeDialogService dialog = new() { ConfirmResult = true };
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
        string importRoot = Path.Combine(
            Path.GetTempPath(),
            "heimdall-settings-import-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(importRoot);
        string importPath = Path.Combine(importRoot, "servers.ini");
        const string content = """
            [Bookmarks]
            SubRep=Production
            ImgNum=42
            WebServer= #109#0%web01.example.com%22%admin%
            [Passwords]
            web01.example.com=encrypted-password-1
            db01.example.com=encrypted-password-2
            """;

        try
        {
            await File.WriteAllTextAsync(importPath, content);
            viewModel.ImportFilePathProvider = () => importPath;

            await viewModel.ImportConfigCommand.ExecuteAsync(null);

            Assert.NotNull(config.SavedServers);
            ServerProfileDto savedServer = Assert.Single(config.SavedServers);
            Assert.Equal("WebServer", savedServer.DisplayName);
            (string Title, string Message) warning = Assert.Single(dialog.WarningCalls);
            Assert.Contains("Detected 2 stored password(s)", warning.Message, StringComparison.Ordinal);
            Assert.Contains("MobaXterm encrypts them with a proprietary algorithm", warning.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(importRoot))
            {
                Directory.Delete(importRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ImportCitrixApps_VaultDisabled_PreservesHistoricPlaintextPath()
    {
        const string launchToken = "-qlaunch app=LegacyCalculator";
        ResetCredentialProtector();
        try
        {
            FakeConfigManager config = new();
            FakeDialogService dialog = new() { ConfirmResult = true };
            SettingsViewModel viewModel = CreateViewModel(config, dialog);
            viewModel.CitrixScanProvider = () => CreateCitrixScanResult(launchToken);

            await viewModel.ImportCitrixAppsCommand.ExecuteAsync(null);

            ServerProfileDto imported = Assert.Single(config.Servers);
            Assert.Equal(launchToken, imported.CitrixLaunchCommandLine);
            Assert.Contains("servers", config.PersistenceCalls);
            Assert.Single(dialog.InfoCalls);
        }
        finally
        {
            ResetCredentialProtector();
        }
    }

    [Fact]
    public async Task ImportCitrixApps_VaultEnabledAndUnlocked_EncryptsBeforeMutation()
    {
        const string launchToken = "-qlaunch app=ProtectedCalculator";
        ResetCredentialProtector();
        try
        {
            using VaultDekHolder dek = VaultKeyManager.GenerateDek();
            CredentialProtector.SetVaultEnabled(true);
            CredentialProtector.SetVaultKey(dek);
            FakeConfigManager config = new();
            FakeDialogService dialog = new() { ConfirmResult = true };
            SettingsViewModel viewModel = CreateViewModel(config, dialog);
            viewModel.CitrixScanProvider = () => CreateCitrixScanResult(launchToken);

            await viewModel.ImportCitrixAppsCommand.ExecuteAsync(null);

            ServerProfileDto imported = Assert.Single(config.Servers);
            Assert.True(VaultSecretBlob.IsSecretBlob(imported.CitrixLaunchCommandLine));
            Assert.NotEqual(launchToken, imported.CitrixLaunchCommandLine);
            Assert.Equal(launchToken, CredentialProtector.Unprotect(imported.CitrixLaunchCommandLine));
            Assert.Contains("servers", config.PersistenceCalls);
        }
        finally
        {
            ResetCredentialProtector();
        }
    }

    [Fact]
    public async Task ImportCitrixApps_VaultEnabledAndLocked_RefusesWithLocalizedMessageWithoutMutation()
    {
        const string launchToken = "-qlaunch app=LockedCalculator";
        ResetCredentialProtector();
        try
        {
            CredentialProtector.SetVaultEnabled(true);
            LocalizationManager localizer = await CreateLocalizerAsync();
            FakeConfigManager config = new()
            {
                Servers = [CreateServer("existing", "Existing", null)]
            };
            FakeDialogService dialog = new() { ConfirmResult = true };
            SettingsViewModel viewModel = CreateViewModel(config, dialog, localizer: localizer);
            CitrixScanResult scanResult = CreateCitrixScanResult(launchToken);
            viewModel.CitrixScanProvider = () => scanResult;
            int configurationChangedCount = 0;
            viewModel.ConfigurationChanged += () => configurationChangedCount++;

            await viewModel.ImportCitrixAppsCommand.ExecuteAsync(null);

            Assert.Single(config.Servers);
            Assert.Equal("existing", config.Servers[0].Id);
            Assert.Null(config.SavedServers);
            Assert.DoesNotContain("servers", config.PersistenceCalls);
            Assert.Equal(0, configurationChangedCount);
            (string Title, string Message) info = Assert.Single(dialog.InfoCalls);
            Assert.Equal(localizer["CitrixScanTitle"], info.Title);
            Assert.Equal(localizer["CitrixImportVaultLocked"], info.Message);
            Assert.Equal(launchToken, Assert.Single(scanResult.Resources).LaunchCommandLine);
        }
        finally
        {
            ResetCredentialProtector();
        }
    }

    [Fact]
    public void Dispose_UnsubscribesExternalToolTracking()
    {
        SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());
        viewModel.LoadFromSettings(new AppSettings
        {
            ExternalTools =
            [
                new ExternalToolDefinition
                {
                    Name = "Ping",
                    ExecutablePath = "ping.exe",
                    Arguments = "{Host}"
                }
            ]
        });
        viewModel.IsDirty = false;
        ExternalToolItemViewModel tool = Assert.Single(viewModel.ExternalTools);

        viewModel.Dispose();
        tool.Name = "Changed";

        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void Dispose_DisposesTrustedHostKeysAndIsIdempotent()
    {
        HostKeyStore store = new();
        HostKeyTrustService trust = new(store);
        LocalizationManager localizer = new();
        FakeDialogService dialog = new();
        TrustedHostKeysSettingsViewModel trustedHostKeys = new(
            trust,
            () => new KnownHostsImportReport(0, 0, []),
            () => new KnownHostsExportReport(0, 0, 0),
            localizer,
            dialog,
            new FakeClipboardService(),
            new FakeUiDispatcher());
        TrustedRdpCertificatesSettingsViewModel trustedRdpCertificates = new(
            new RdpCertificateTrustStore(),
            () => Task.FromResult<IReadOnlyList<ServerProfileDto>>([]),
            localizer,
            dialog,
            new FakeUiDispatcher());
        SettingsViewModel viewModel = new(
            new FakeConfigManager(),
            localizer,
            dialog,
            trustedHostKeys,
            trustedRdpCertificates,
            new PinManager(),
            new VaultLifecycleService(new FakeConfigManager()),
            new FakeUpdateService(),
            new AppVersionProvider("2026.061501"),
            new FakeUpdateInstallFlow(),
            new StubBrowserLauncher());

        viewModel.Dispose();
        viewModel.Dispose();
        trust.Trust(
            "server.example.com",
            22,
            "SHA256:fingerprint",
            "ssh-ed25519",
            HostKeySource.UserConfirmed);

        Assert.Empty(trustedHostKeys.Rows);
    }

    /// <summary>
    /// Opening the settings screen fills the trusted RDP certificate panel.
    /// </summary>
    /// <remarks>
    /// <para>The panel builds no rows of its own: its constructor subscribes to the store and to
    /// the localizer, and nothing else. Until something asks it to refresh, it reports the empty
    /// state - "nothing is trusted" - over a store holding every approval the user has ever given.
    /// The one statement in <c>LoadFromSettings</c> that asks is what makes the revocation screen
    /// this branch shipped visible at all, and both it and the panel were delivered with green
    /// suites either side of the junction, which is the shape this repository has already been
    /// bitten by.</para>
    /// <para>The first two assertions are the control. Without them a panel that filled itself in
    /// its constructor, or one already holding rows from an earlier test, would satisfy the
    /// assertions after the load without the reload having done anything.</para>
    /// </remarks>
    [Fact]
    public async Task LoadFromSettings_FillsTheTrustedRdpCertificatePanelFromTheStore()
    {
        var store = new RdpCertificateTrustStore();
        store.Trust(
            RdpTrustKey.ForProfile("srv-1"),
            new RdpCertificateEntry("SHA256:AA:BB:01", new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero)));

        FakeConfigManager config = new();
        LocalizationManager localizer = new();
        FakeDialogService dialog = new();
        TrustedRdpCertificatesSettingsViewModel panel = CreateTrustedRdpCertificatesPanel(
            store, localizer, dialog);
        SettingsViewModel viewModel = CreateViewModelOver(config, panel, localizer, dialog);

        Assert.Empty(panel.Rows);
        Assert.True(panel.IsEmptyStateVisible);

        viewModel.LoadFromSettings(config.Settings);
        await (panel.RefreshCommand.ExecutionTask ?? Task.CompletedTask);

        Assert.Equal(["SHA256:AA:BB:01"], panel.Rows.Select(row => row.Thumbprint));
        Assert.False(panel.IsEmptyStateVisible);
    }

    /// <summary>
    /// Closing the settings screen lets the trusted RDP certificate panel go.
    /// </summary>
    /// <remarks>
    /// <para>The settings view model is registered transient, so a new one is built every time the
    /// screen is opened, and the panel's own <c>Dispose</c> is the only place its store and
    /// localizer subscriptions are detached. Without the one statement that calls it, every
    /// settings window opened leaves a live subscriber behind, rebuilding rows on every later
    /// trust decision and locale change for the life of the process.</para>
    /// <para>The first half is the control: without it a panel that never subscribed at all would
    /// satisfy the second half, and this would describe a stub rather than measure a detach.</para>
    /// </remarks>
    [Fact]
    public void Dispose_DetachesTheTrustedRdpCertificatePanelFromTheStore()
    {
        var store = new RdpCertificateTrustStore();
        var stamp = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);

        FakeConfigManager config = new();
        LocalizationManager localizer = new();
        FakeDialogService dialog = new();
        TrustedRdpCertificatesSettingsViewModel panel = CreateTrustedRdpCertificatesPanel(
            store, localizer, dialog);
        SettingsViewModel viewModel = CreateViewModelOver(config, panel, localizer, dialog);

        store.Trust(RdpTrustKey.ForProfile("srv-1"), new RdpCertificateEntry("SHA256:AA:BB:01", stamp));
        Assert.Equal(["SHA256:AA:BB:01"], panel.Rows.Select(row => row.Thumbprint));

        viewModel.Dispose();
        store.Trust(RdpTrustKey.ForProfile("srv-2"), new RdpCertificateEntry("SHA256:AA:BB:02", stamp));

        Assert.Equal(["SHA256:AA:BB:01"], panel.Rows.Select(row => row.Thumbprint));
    }

    /// <summary>The trust panel over a caller-owned store, with an empty inventory.</summary>
    private static TrustedRdpCertificatesSettingsViewModel CreateTrustedRdpCertificatesPanel(
        RdpCertificateTrustStore store,
        LocalizationManager localizer,
        FakeDialogService dialog)
        => new(
            store,
            () => Task.FromResult<IReadOnlyList<ServerProfileDto>>([]),
            localizer,
            dialog,
            new FakeUiDispatcher());

    /// <summary>A settings view model over a caller-owned trust panel.</summary>
    /// <remarks>
    /// <see cref="CreateViewModel"/> builds its own panel over its own store, which no test can
    /// seed or watch. The two above need both, so they pass one in.
    /// </remarks>
    private static SettingsViewModel CreateViewModelOver(
        FakeConfigManager config,
        TrustedRdpCertificatesSettingsViewModel trustedRdpCertificates,
        LocalizationManager localizer,
        FakeDialogService dialog)
        => new(
            config,
            localizer,
            dialog,
            new TrustedHostKeysSettingsViewModel(
                new HostKeyTrustService(new HostKeyStore()),
                () => new KnownHostsImportReport(0, 0, []),
                () => new KnownHostsExportReport(0, 0, 0),
                localizer,
                dialog,
                new FakeClipboardService(),
                new FakeUiDispatcher()),
            trustedRdpCertificates,
            new PinManager(),
            new VaultLifecycleService(config),
            new FakeUpdateService(),
            new AppVersionProvider("2026.061501"),
            new FakeUpdateInstallFlow(),
            new StubBrowserLauncher());

    /// <summary>
    /// The settings screen and the settings schema must admit exactly the same watchdog timeouts.
    /// </summary>
    /// <remarks>
    /// <para>Three places decide this: the watchdog reads its bounds from
    /// <c>RdpConnectWatchdogPolicy</c>, the schema states them again in its own constants, and the
    /// screen stated them a third time in a validation attribute. The screen's copy left out the
    /// zero that disables the watchdog.</para>
    /// <para>The sweep compares answers rather than restating bounds, so it holds whatever the
    /// bounds become. What is frozen is that the three are one. It goes through the view model's
    /// own validation rather than calling the validator method directly, because the defect was in
    /// which attribute the property carried: a test that calls the method would pass even with the
    /// attribute removed entirely.</para>
    /// </remarks>
    [Fact]
    public void WatchdogTimeout_SettingsScreenAndSchemaAdmitTheSameValues()
    {
        int[] probes =
        [
            RdpConnectWatchdogPolicy.DisabledTimeoutMs,
            RdpConnectWatchdogPolicy.DisabledTimeoutMs + 1,
            RdpConnectWatchdogPolicy.MinTimeoutMs - 1,
            RdpConnectWatchdogPolicy.MinTimeoutMs,
            RdpConnectWatchdogPolicy.MinTimeoutMs + 1,
            RdpConnectWatchdogPolicy.DefaultTimeoutMs,
            RdpConnectWatchdogPolicy.MaxTimeoutMs - 1,
            RdpConnectWatchdogPolicy.MaxTimeoutMs,
            RdpConnectWatchdogPolicy.MaxTimeoutMs + 1,
            -1,
        ];

        List<string> disagreements = [];
        int refusals = 0;

        foreach (int value in probes)
        {
            SettingsViewModel viewModel = CreateViewModel(new FakeConfigManager());
            viewModel.RdpConnectWatchdogTimeoutMs = value;
            bool screenAccepts =
                !viewModel.GetErrors(nameof(SettingsViewModel.RdpConnectWatchdogTimeoutMs))
                    .OfType<object>()
                    .Any();

            bool schemaAccepts = SchemaAcceptsWatchdogTimeout(value);
            if (!screenAccepts)
            {
                refusals++;
            }

            if (screenAccepts != schemaAccepts)
            {
                disagreements.Add(
                    $"{value} ms: the settings screen {(screenAccepts ? "accepts" : "refuses")} it "
                        + $"and the schema {(schemaAccepts ? "accepts" : "refuses")} it");
            }
        }

        // Guarding the guard: two predicates that accepted everything would agree perfectly while
        // proving nothing, and that is exactly the shape a broken probe would take.
        Assert.True(refusals > 0, "the screen refused nothing, so the comparison is trivial");
        Assert.True(disagreements.Count == 0, string.Join("\n", disagreements));
    }

    private static bool SchemaAcceptsWatchdogTimeout(int value)
    {
        AppSettings settings = new() { RdpConnectWatchdogTimeoutMs = value };
        Heimdall.Core.Configuration.ValidationResult result = SchemaValidator.ValidateSettings(settings);

        return !result.Errors.Any(error =>
            error.Contains("RdpConnectWatchdogTimeoutMs", StringComparison.OrdinalIgnoreCase)
            || error.Contains("watchdog", StringComparison.OrdinalIgnoreCase));
    }

    private static void SetAdvancedRdpTimeout(
        SettingsViewModel viewModel,
        string propertyName,
        int value)
    {
        switch (propertyName)
        {
            case nameof(SettingsViewModel.RdpResizeEnableDelayMs):
                viewModel.RdpResizeEnableDelayMs = value;
                break;
            case nameof(SettingsViewModel.RdpArtifactCleanupDelayMs):
                viewModel.RdpArtifactCleanupDelayMs = value;
                break;
            case nameof(SettingsViewModel.RdpCredentialAutofillTimeoutMs):
                viewModel.RdpCredentialAutofillTimeoutMs = value;
                break;
            case nameof(SettingsViewModel.RdpConnectWatchdogTimeoutMs):
                viewModel.RdpConnectWatchdogTimeoutMs = value;
                break;
            case nameof(SettingsViewModel.RdpKeepAliveIntervalMs):
                viewModel.RdpKeepAliveIntervalMs = value;
                break;
            case nameof(SettingsViewModel.UpdateCheckIntervalHours):
                viewModel.UpdateCheckIntervalHours = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, null);
        }
    }

    private static SettingsViewModel CreateViewModel(
        FakeConfigManager config,
        FakeDialogService? dialog = null,
        IProfileImportService? profileImportService = null,
        LocalizationManager? localizer = null,
        IUpdateService? updateService = null,
        IAppVersionProvider? appVersionProvider = null,
        IUpdateInstallFlow? installFlow = null,
        IBrowserLauncher? browserLauncher = null)
    {
        localizer ??= new LocalizationManager();
        dialog ??= new FakeDialogService();
        updateService ??= new FakeUpdateService();
        appVersionProvider ??= new AppVersionProvider("2026.061501");
        installFlow ??= new FakeUpdateInstallFlow();
        browserLauncher ??= new StubBrowserLauncher();
        var trustedHostKeys = new TrustedHostKeysSettingsViewModel(
            new HostKeyTrustService(new HostKeyStore()),
            () => new KnownHostsImportReport(0, 0, []),
            () => new KnownHostsExportReport(0, 0, 0),
            localizer,
            dialog,
            new FakeClipboardService(),
            new FakeUiDispatcher());
        var trustedRdpCertificates = new TrustedRdpCertificatesSettingsViewModel(
            new RdpCertificateTrustStore(),
            () => Task.FromResult<IReadOnlyList<ServerProfileDto>>([]),
            localizer,
            dialog,
            new FakeUiDispatcher());

        return new SettingsViewModel(
            config,
            localizer,
            dialog,
            trustedHostKeys,
            trustedRdpCertificates,
            new PinManager(),
            new VaultLifecycleService(config),
            updateService,
            appVersionProvider,
            installFlow,
            browserLauncher,
            profileImportService);
    }

    [Theory]
    [InlineData(UpdateCheckStatus.UpToDate, "SettingsUpdateStatusUpToDate")]
    [InlineData(UpdateCheckStatus.CheckFailed, "SettingsUpdateStatusFailed")]
    public async Task CheckNowAsync_MapsStatusToExpectedKey_WithoutMarkingDirty(UpdateCheckStatus status, string expectedKey)
    {
        var localizer = await CreateLocalizerAsync();
        var updateService = new FakeUpdateService { Result = new UpdateCheckResult(status, null) };
        var viewModel = CreateViewModel(new FakeConfigManager(), localizer: localizer, updateService: updateService);

        await viewModel.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(localizer.Format(expectedKey), viewModel.UpdateStatusText);
        Assert.False(viewModel.IsCheckingUpdate);
        Assert.False(viewModel.IsDirty);
    }

    /// <remarks>
    /// The startup check catches everything; this command caught nothing, so the same
    /// fault crashed the application from one button and was logged from the other.
    /// </remarks>
    [Fact]
    public async Task CheckNowAsync_ServiceThrows_ShowsFailedStatusAndDoesNotPropagate()
    {
        var localizer = await CreateLocalizerAsync();
        var updateService = new FakeUpdateService { CheckException = new UriFormatException("bad owner") };
        var viewModel = CreateViewModel(new FakeConfigManager(), localizer: localizer, updateService: updateService);

        await viewModel.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(localizer.Format("SettingsUpdateStatusFailed"), viewModel.UpdateStatusText);
        Assert.False(viewModel.IsCheckingUpdate);
    }

    [Fact]
    public async Task CheckNowAsync_UpdateAvailable_IncludesVersionWithoutMarkingDirty()
    {
        var localizer = await CreateLocalizerAsync();
        var info = new UpdateInfo(
            HeimdallVersion.Parse("2026.061502"),
            "v2026.061502",
            "https://example.test",
            "notes",
            new UpdateAsset("Heimdall_2026.061502_Standard_Setup.exe", "https://example.test/setup.exe", 1),
            null);
        var updateService = new FakeUpdateService { Result = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, info) };
        var viewModel = CreateViewModel(new FakeConfigManager(), localizer: localizer, updateService: updateService);

        await viewModel.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(localizer.Format("SettingsUpdateStatusAvailable", "2026.061502"), viewModel.UpdateStatusText);
        Assert.True(viewModel.IsUpdateReleaseAvailable);
        Assert.True(viewModel.OpenUpdateReleaseCommand.CanExecute(null));
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task CheckNowAsync_UpdateNotInstallable_ShowsVersionWithoutInstallButton()
    {
        var localizer = await CreateLocalizerAsync();
        var release = new ReleaseRef(
            HeimdallVersion.Parse("2026.061502"),
            "v2026.061502",
            "https://github.com/VBlackJack/Heimdall/releases/tag/v2026.061502");
        var updateService = new FakeUpdateService
        {
            Result = new UpdateCheckResult(UpdateCheckStatus.UpdateNotInstallable, null, release)
        };
        var browser = new StubBrowserLauncher();
        var viewModel = CreateViewModel(
            new FakeConfigManager(), localizer: localizer,
            updateService: updateService, browserLauncher: browser);

        await viewModel.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(localizer.Format("SettingsUpdateStatusNotInstallable", "2026.061502"), viewModel.UpdateStatusText);
        Assert.False(viewModel.IsUpdateAvailable);
        Assert.False(viewModel.DownloadAndInstallCommand.CanExecute(null));
        Assert.True(viewModel.IsUpdateReleaseAvailable);
        Assert.True(viewModel.OpenUpdateReleaseCommand.CanExecute(null));

        viewModel.OpenUpdateReleaseCommand.Execute(null);

        Assert.Equal(release.HtmlUrl, browser.OpenedUrl);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void DownloadAndInstall_NoUpdateAvailable_CommandCannotExecute()
    {
        var viewModel = CreateViewModel(new FakeConfigManager());

        Assert.False(viewModel.IsUpdateAvailable);
        Assert.False(viewModel.DownloadAndInstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task DownloadAndInstall_DeclinedConfirm_DoesNotRunFlow()
    {
        var localizer = await CreateLocalizerAsync();
        var updateService = new FakeUpdateService { Result = AvailableResult() };
        var flow = new FakeUpdateInstallFlow();
        var dialog = new FakeDialogService { ConfirmResult = false };
        var viewModel = CreateViewModel(
            new FakeConfigManager(), dialog, localizer: localizer,
            updateService: updateService, installFlow: flow);

        await viewModel.CheckNowCommand.ExecuteAsync(null);
        await viewModel.DownloadAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(0, flow.RunCallCount);
        Assert.False(viewModel.IsInstallingUpdate);
    }

    [Fact]
    public async Task DownloadAndInstall_OutcomeStarted_RunsFlowAndSetsNoErrorStatus()
    {
        var localizer = await CreateLocalizerAsync();
        var updateService = new FakeUpdateService { Result = AvailableResult() };
        var flow = new FakeUpdateInstallFlow { Outcome = UpdateInstallOutcome.Started };
        var dialog = new FakeDialogService { ConfirmResult = true };
        var viewModel = CreateViewModel(
            new FakeConfigManager(), dialog, localizer: localizer,
            updateService: updateService, installFlow: flow);

        await viewModel.CheckNowCommand.ExecuteAsync(null);
        await viewModel.DownloadAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(1, flow.RunCallCount);
        // Started maps to a null status key, so the last status remains the "downloading" message.
        Assert.Equal(localizer.Format("SettingsUpdateStatusDownloading"), viewModel.UpdateStatusText);
        Assert.False(viewModel.IsInstallingUpdate);
    }

    [Fact]
    public async Task DownloadAndInstall_OutcomeInstallLaunchFailed_ShowsInstallFailedStatus()
    {
        var localizer = await CreateLocalizerAsync();
        var updateService = new FakeUpdateService { Result = AvailableResult() };
        var flow = new FakeUpdateInstallFlow { Outcome = UpdateInstallOutcome.InstallLaunchFailed };
        var dialog = new FakeDialogService { ConfirmResult = true };
        var viewModel = CreateViewModel(
            new FakeConfigManager(), dialog, localizer: localizer,
            updateService: updateService, installFlow: flow);

        await viewModel.CheckNowCommand.ExecuteAsync(null);
        await viewModel.DownloadAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(1, flow.RunCallCount);
        Assert.Equal(localizer.Format("SettingsUpdateStatusInstallFailed"), viewModel.UpdateStatusText);
        Assert.False(viewModel.IsInstallingUpdate);
    }

    [Fact]
    public async Task DownloadAndInstall_OutcomeVerificationFailed_ShowsVerificationFailedStatus()
    {
        var localizer = await CreateLocalizerAsync();
        var updateService = new FakeUpdateService { Result = AvailableResult() };
        var flow = new FakeUpdateInstallFlow { Outcome = UpdateInstallOutcome.VerificationFailed };
        var dialog = new FakeDialogService { ConfirmResult = true };
        var viewModel = CreateViewModel(
            new FakeConfigManager(), dialog, localizer: localizer,
            updateService: updateService, installFlow: flow);

        await viewModel.CheckNowCommand.ExecuteAsync(null);
        await viewModel.DownloadAndInstallCommand.ExecuteAsync(null);

        Assert.Equal(1, flow.RunCallCount);
        Assert.Equal(localizer.Format("SettingsUpdateStatusVerificationFailed"), viewModel.UpdateStatusText);
        Assert.False(viewModel.IsInstallingUpdate);
    }

    private static UpdateCheckResult AvailableResult() =>
        new(
            UpdateCheckStatus.UpdateAvailable,
            new UpdateInfo(
                HeimdallVersion.Parse("2026.061502"),
                "v2026.061502",
                "https://example.test",
                "notes",
                new UpdateAsset("Heimdall_2026.061502_Standard_Setup.exe", "https://example.test/setup.exe", 1),
                null));

    private sealed class StubBrowserLauncher : IBrowserLauncher
    {
        public string? OpenedUrl { get; private set; }

        public void Open(string url) => OpenedUrl = url;
    }

    private sealed class FakeUpdateService : IUpdateService
    {
        public UpdateCheckResult Result { get; set; } = new(UpdateCheckStatus.UpToDate, null);

        public Exception? CheckException { get; set; }

        public string DownloadResultPath { get; set; } = @"C:\Temp\HeimdallSetup.exe";

        public Exception? DownloadException { get; set; }

        public int DownloadCallCount { get; private set; }

        public Task<UpdateCheckResult> CheckForUpdatesAsync(HeimdallVersion current, string owner, string repo, CancellationToken cancellationToken)
            => CheckException is null ? Task.FromResult(Result) : throw CheckException;

        public Task<IVerifiedUpdatePackage> DownloadVerifiedAsync(
            UpdateInfo update,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            DownloadCallCount++;
            if (DownloadException is not null)
            {
                throw DownloadException;
            }

            throw new NotSupportedException();
        }
    }

    private sealed class FakeUpdateInstallFlow : IUpdateInstallFlow
    {
        public UpdateInstallOutcome Outcome { get; set; } = UpdateInstallOutcome.Started;

        public int RunCallCount { get; private set; }

        public Task<UpdateInstallOutcome> RunAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            RunCallCount++;
            return Task.FromResult(Outcome);
        }
    }

    private static JsonSerializerOptions GetExportJsonOptions()
    {
        FieldInfo? field = typeof(SettingsViewModel).GetField(
            "ExportJsonOptions",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        return Assert.IsType<JsonSerializerOptions>(field!.GetValue(null));
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }

    private static SshGatewayDto CreateGateway(string id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            Host = $"{id}.example.test",
            Port = 22,
            User = "ops"
        };

    private static ServerProfileDto CreateServer(string id, string displayName, string? gatewayId) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            RemoteServer = $"{id}.example.test",
            ConnectionType = "SSH",
            SshGatewayId = gatewayId
        };

    private static CitrixScanResult CreateCitrixScanResult(string launchToken)
    {
        var result = new CitrixScanResult();
        result.Resources.Add(new CitrixResource
        {
            FriendlyName = "Calculator",
            LaunchCommandLine = launchToken,
            StoreFrontUrl = "https://citrix.example.test"
        });
        return result;
    }

    private static void ResetCredentialProtector()
    {
        CredentialProtectorStateScope.Reset();
    }

    private static async Task<AppSettings> LoadExpectedFactoryDefaultsAsync()
    {
        var defaultsPath = Path.Combine(AppContext.BaseDirectory, "config", "settings.default.json");
        if (!File.Exists(defaultsPath))
        {
            return new AppSettings();
        }

        var json = await File.ReadAllTextAsync(defaultsPath);
        return JsonSerializer.Deserialize<AppSettings>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new AppSettings();
    }

    private static void SetNonDefaultRdpValues(SettingsViewModel viewModel)
    {
        viewModel.DefaultResolutionWidth = 1280;
        viewModel.DefaultResolutionHeight = 720;
        viewModel.RdpDefaultMode = "External";
        viewModel.RdpDefaultNla = false;
        viewModel.RdpDefaultStrictServerAuthentication = true;
        viewModel.RdpDefaultColorDepth = 16;
        viewModel.RdpDefaultDynamicResolution = false;
        viewModel.RdpDefaultMultiMonitor = true;
        viewModel.RdpDefaultRedirectClipboard = false;
        viewModel.RdpDefaultRedirectDrives = true;
        viewModel.RdpDefaultRedirectPrinters = true;
        viewModel.RdpDefaultRedirectComPorts = true;
        viewModel.RdpDefaultRedirectSmartCards = true;
        viewModel.RdpDefaultRedirectWebcam = true;
        viewModel.RdpDefaultRedirectUsb = true;
        viewModel.RdpDefaultAudioCapture = true;
        viewModel.RdpDefaultAutoReconnect = false;
        viewModel.RdpDefaultBitmapCaching = false;
        viewModel.RdpDefaultCompression = false;
        viewModel.RdpDefaultAudioMode = 2;
        viewModel.RdpKeepAliveIntervalMs = 5000;
    }

    private static void AssertRdpDefaultsMatch(SettingsViewModel viewModel, AppSettings expected)
    {
        Assert.Equal(expected.DefaultResolutionWidth, viewModel.DefaultResolutionWidth);
        Assert.Equal(expected.DefaultResolutionHeight, viewModel.DefaultResolutionHeight);
        Assert.Equal(expected.RdpDefaultMode, viewModel.RdpDefaultMode);
        Assert.Equal(expected.RdpDefaultNla, viewModel.RdpDefaultNla);
        Assert.Equal(expected.RdpDefaultStrictServerAuthentication, viewModel.RdpDefaultStrictServerAuthentication);
        Assert.Equal(expected.RdpDefaultColorDepth, viewModel.RdpDefaultColorDepth);
        Assert.Equal(expected.RdpDefaultDynamicResolution, viewModel.RdpDefaultDynamicResolution);
        Assert.Equal(expected.RdpDefaultMultiMonitor, viewModel.RdpDefaultMultiMonitor);
        Assert.Equal(expected.RdpDefaultRedirectClipboard, viewModel.RdpDefaultRedirectClipboard);
        Assert.Equal(expected.RdpDefaultRedirectDrives, viewModel.RdpDefaultRedirectDrives);
        Assert.Equal(expected.RdpDefaultRedirectPrinters, viewModel.RdpDefaultRedirectPrinters);
        Assert.Equal(expected.RdpDefaultRedirectComPorts, viewModel.RdpDefaultRedirectComPorts);
        Assert.Equal(expected.RdpDefaultRedirectSmartCards, viewModel.RdpDefaultRedirectSmartCards);
        Assert.Equal(expected.RdpDefaultRedirectWebcam, viewModel.RdpDefaultRedirectWebcam);
        Assert.Equal(expected.RdpDefaultRedirectUsb, viewModel.RdpDefaultRedirectUsb);
        Assert.Equal(expected.RdpDefaultAudioCapture, viewModel.RdpDefaultAudioCapture);
        Assert.Equal(expected.RdpDefaultAutoReconnect, viewModel.RdpDefaultAutoReconnect);
        Assert.Equal(expected.RdpDefaultBitmapCaching, viewModel.RdpDefaultBitmapCaching);
        Assert.Equal(expected.RdpDefaultCompression, viewModel.RdpDefaultCompression);
        Assert.Equal(expected.RdpDefaultAudioMode, viewModel.RdpDefaultAudioMode);
        Assert.Equal(expected.RdpKeepAliveIntervalMs, viewModel.RdpKeepAliveIntervalMs);
    }

    private sealed class FakeConfigManager : IConfigManager
    {
        public AppSettings Settings { get; set; } = new();

        public AppSettings? SavedSettings { get; private set; }

        public int SaveSettingsCallCount { get; private set; }

        public int MergeSettingCallCount { get; private set; }

        public bool FailOnMergeSetting { get; set; }

        public bool FailOnLoadSettings { get; set; }

        public TaskCompletionSource? MergeSettingStarted { get; set; }

        public Task? MergeSettingRelease { get; set; }

        public List<ServerProfileDto> Servers { get; set; } = [];

        public List<ServerProfileDto>? SavedServers { get; private set; }

        public List<string> PersistenceCalls { get; } = [];

        public string ConfigPath => "config";

        public string SettingsPath => "settings.json";

        public string ServersPath => "servers.json";

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => FailOnLoadSettings
            ? Task.FromException<AppSettings>(new IOException("Simulated LoadSettingsAsync failure"))
            : Task.FromResult(CloneSettings(Settings));

        public Task SaveSettingsAsync(AppSettings settings)
        {
            SaveSettingsCallCount++;
            AppSettings storedSettings = CloneSettings(settings);
            SavedSettings = storedSettings;
            Settings = storedSettings;
            SettingsChanged?.Invoke(storedSettings);
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint)
            => Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries)
            => Task.FromResult(0);

        public async Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            MergeSettingCallCount++;
            PersistenceCalls.Add("settings");
            if (FailOnMergeSetting)
            {
                throw new IOException("Simulated MergeSettingAsync failure");
            }

            AppSettings currentSettings = CloneSettings(Settings);
            mutate(currentSettings);
            MergeSettingStarted?.TrySetResult();
            if (MergeSettingRelease is not null)
            {
                await MergeSettingRelease;
            }

            Settings = currentSettings;
            SavedSettings = currentSettings;
            SettingsChanged?.Invoke(currentSettings);
        }

        public Task<List<ServerProfileDto>> LoadServersAsync()
            => Task.FromResult(Servers);

        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate)
        {
            PersistenceCalls.Add("servers");
            List<ServerProfileDto> servers = Servers.ToList();
            TResult result = mutate(servers);
            SavedServers = servers;
            Servers = servers;
            return Task.FromResult(result);
        }

        public Task SaveServersAsync(List<ServerProfileDto> servers)
        {
            SavedServers = servers;
            Servers = servers;
            return Task.CompletedTask;
        }

        private static AppSettings CloneSettings(AppSettings settings)
        {
            string json = JsonSerializer.Serialize(settings);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public void SetText(string text)
        {
        }
    }

    private sealed class FakeDialogService : IDialogService
    {
        public bool ConfirmResult { get; set; }

        public bool? SaveDiscardResult { get; set; }

        public List<(string Title, string Message, string Severity)> ConfirmCalls { get; } = [];

        public List<(string Title, string Message)> ErrorCalls { get; } = [];

        public List<(string Title, string Message)> WarningCalls { get; } = [];

        public List<(string Title, string Message)> InfoCalls { get; } = [];

        public PinSetupResult? PinSetupResultToReturn { get; set; }

        public PinSetupDialogViewModel? LastPinSetupViewModel { get; private set; }

        public GatewayDialogResult? GatewayDialogResultToReturn { get; set; }

        public GatewayOverviewDialogViewModel? LastGatewayOverviewViewModel { get; private set; }

        public Task<bool> ShowConfirmAsync(string title, string message, string severity = "info")
        {
            ConfirmCalls.Add((title, message, severity));
            return Task.FromResult(ConfirmResult);
        }

        public Task<bool?> ShowSaveDiscardCancelAsync(string title, string message)
            => Task.FromResult(SaveDiscardResult);

        public Task<string?> ShowInputAsync(string title, string prompt, string? defaultValue = null)
            => Task.FromResult<string?>(null);

        public Task<string?> ShowPasswordInputAsync(string title, string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task<ServerDialogResult?> ShowServerDialogAsync(ServerDialogViewModel? editVm = null)
            => Task.FromResult<ServerDialogResult?>(null);

        public Task<GatewayDialogResult?> ShowGatewayDialogAsync(GatewayDialogViewModel? editVm = null)
            => Task.FromResult(GatewayDialogResultToReturn);

        public Task ShowGatewayOverviewAsync(GatewayOverviewDialogViewModel viewModel)
        {
            LastGatewayOverviewViewModel = viewModel;
            return Task.CompletedTask;
        }

        public Task<ProjectDialogResult?> ShowProjectDialogAsync(ProjectDialogViewModel? editVm = null)
            => Task.FromResult<ProjectDialogResult?>(null);

        public Task<ScheduledTaskDialogResult?> ShowScheduledTaskDialogAsync(ScheduledTaskDialogViewModel? editVm = null)
            => Task.FromResult<ScheduledTaskDialogResult?>(null);

        public Task ShowPinDialogAsync(PinDialogViewModel viewModel)
            => Task.CompletedTask;

        public Task<PinSetupResult?> ShowPinSetupDialogAsync(PinSetupDialogViewModel viewModel)
        {
            LastPinSetupViewModel = viewModel;
            return Task.FromResult(PinSetupResultToReturn);
        }

        public Task<SnapshotRestoreDialogResult?> ShowSnapshotRestoreDialogAsync(SnapshotRestoreDialogViewModel viewModel)
            => Task.FromResult<SnapshotRestoreDialogResult?>(null);

        public Task<RdpImportSelection?> ShowRdpImportDialogAsync(RdpImportDialogViewModel viewModel)
            => Task.FromResult<RdpImportSelection?>(null);

        public Task<ImportOutcome?> ShowImportOpenSshConfigAsync(OpenSshParseResult parseResult)
            => Task.FromResult<ImportOutcome?>(null);

        public Task<ImportOutcome?> ShowImportPuttySessionsAsync(PuttySessionParseResult parseResult)
            => Task.FromResult<ImportOutcome?>(null);

        public Task<KnownHostsImportOutcome?> ShowImportKnownHostsAsync(KnownHostsImportPreview preview)
            => Task.FromResult<KnownHostsImportOutcome?>(null);

        public Task ShowTrustedHostKeyDetailsAsync(TrustedHostKeyDetailsDialogViewModel viewModel)
            => Task.CompletedTask;

        public Task<ImportKnownHostsConflictResolution?> ShowImportKnownHostsConflictAsync(
            ImportKnownHostsConflictDialogViewModel viewModel)
            => Task.FromResult<ImportKnownHostsConflictResolution?>(null);

        public Task<CommandLibraryPickerResult?> ShowCommandLibraryPickerAsync(
            CommandLibraryPickerDialogViewModel viewModel,
            AutoPrefillContext? prefillContext = null,
            string? existingActionId = null,
            IReadOnlyDictionary<string, string>? existingValues = null)
            => Task.FromResult<CommandLibraryPickerResult?>(null);

        public Task<int?> ShowBulkEditPortAsync(int count, int? initialPort, CancellationToken cancellationToken)
            => Task.FromResult<int?>(null);

        public Task<string?> ShowBulkEditUsernameAsync(int count, string? initialUsername, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public Task<string?> ShowBulkEditPasswordAsync(int count, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public void ShowError(string title, string message)
        {
            ErrorCalls.Add((title, message));
        }

        public void ShowInfo(string title, string message)
        {
            InfoCalls.Add((title, message));
        }

        public void ShowWarning(string title, string message)
        {
            WarningCalls.Add((title, message));
        }
    }

    private sealed class FakeProfileImportService : IProfileImportService
    {
        public ProfileImportResult Result { get; set; } = ProfileImportResult.NoChanges();

        public List<string> ImportedPaths { get; } = [];

        public Task<ProfileImportResult> ImportFromPathAsync(string path, CancellationToken ct = default)
        {
            ImportedPaths.Add(path);
            return Task.FromResult(Result);
        }

        public Task<ProfileImportResult> ImportFromPathsAsync(IEnumerable<string> paths, CancellationToken ct = default)
        {
            ImportedPaths.AddRange(paths);
            return Task.FromResult(Result);
        }
    }
}
