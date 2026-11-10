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
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Editing the five basic fields of the selected session in the properties panel, seen through
/// the list view model and a real inventory file.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task EditHere_IsOfferedOnlyForOneLocalSession()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        ServerProfileDto tool = new()
        {
            Id = "tool-1",
            DisplayName = "Ping",
            ConnectionType = "TOOL:ping",
            Group = "ops",
        };
        ServerProfileDto shared = CreateSshServer("shared-src-1", "Shared", "ops");
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"),
            tool,
            shared);

        Assert.False(fixture.ViewModel.PropertiesPanelCanEditHere);
        Assert.False(fixture.ViewModel.BeginPropertiesEditCommand.CanExecute(null));

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        Assert.True(fixture.ViewModel.PropertiesPanelCanEditHere);
        Assert.True(fixture.ViewModel.BeginPropertiesEditCommand.CanExecute(null));

        fixture.ViewModel.SelectSingle(fixture.ServerById("tool-1"));
        Assert.False(fixture.ViewModel.PropertiesPanelCanEditHere);

        fixture.ViewModel.SelectSingle(fixture.ServerById("shared-src-1"));
        Assert.False(fixture.ViewModel.PropertiesPanelCanEditHere);

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.ToggleSelection(fixture.ServerById("beta"));
        Assert.False(fixture.ViewModel.PropertiesPanelCanEditHere);

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        fixture.ViewModel.PropertiesFolder = fixture.FolderByPath("ops");
        Assert.False(fixture.ViewModel.PropertiesPanelCanEditHere);
    }

    [Fact]
    public async Task EditHere_SwitchesThePanelToTheForm_AndCancelBringsTheReadOnlyViewBack()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops/web"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));
        Assert.True(fixture.ViewModel.PropertiesPanelShowsActions);

        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");

        Assert.True(fixture.ViewModel.PropertiesPanelShowsEditor);
        Assert.False(fixture.ViewModel.PropertiesPanelShowsActions);
        Assert.False(fixture.ViewModel.PropertiesPanelCanEditHere);
        Assert.Equal(["ops", "ops/web"], edit.FolderChoices);

        edit.DisplayName = "Typed and abandoned";
        edit.CancelCommand.Execute(null);

        Assert.Null(fixture.ViewModel.PropertiesEdit);
        Assert.False(fixture.ViewModel.PropertiesPanelShowsEditor);
        Assert.True(fixture.ViewModel.PropertiesPanelShowsActions);
        Assert.Equal("Alpha", fixture.ServerById("alpha").DisplayName);
        Assert.Equal("Alpha", StoredProfile(await fixture.ConfigManager.LoadServersAsync(), "alpha").DisplayName);
        Assert.Empty(fixture.Dialogs.NamedConfirmations);
    }

    /// <summary>
    /// The panel can stay open while a drag or a nudge rewrites the same profile: a save keeps
    /// what changed in the store meanwhile, and writes only the fields the user changed.
    /// </summary>
    [Fact]
    public async Task Save_WritesTheFiveFields_AndKeepsWhatChangedInTheStoreMeanwhile()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        ServerProfileDto alpha = CreateSshServer("alpha", "Alpha", "ops", sortOrder: 1);
        alpha.Tags = "original-tags";
        alpha.SshKeyPath = "C:/keys/alpha";
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "prod", "prod/web"),
            alpha,
            CreateSshServer("beta", "Beta", "ops", sortOrder: 2));
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");

        // Edit mode has begun; now the store moves under the panel.
        await fixture.ConfigManager.MutateServersAsync(inventory =>
        {
            ServerProfileDto stored = StoredProfile(inventory, "alpha");
            stored.SortOrder = 42;
            stored.Tags = "changed-meanwhile";
            stored.IsFavorite = true;
            return true;
        });

        edit.DisplayName = "Alpha renamed";
        edit.Host = "new.example.com";
        edit.PortText = "2200";
        edit.Username = "admin";
        edit.Folder = "prod/web";
        await edit.SaveCommand.ExecuteAsync(null);

        ServerProfileDto saved = StoredProfile(await fixture.ConfigManager.LoadServersAsync(), "alpha");
        Assert.Equal("Alpha renamed", saved.DisplayName);
        Assert.Equal("new.example.com", saved.RemoteServer);
        Assert.Equal(2200, saved.SshPort);
        Assert.Equal("admin", saved.SshUsername);
        Assert.Equal("prod/web", saved.Group);
        Assert.Equal(42, saved.SortOrder);
        Assert.Equal("changed-meanwhile", saved.Tags);
        Assert.True(saved.IsFavorite);
        Assert.Equal("C:/keys/alpha", saved.SshKeyPath);
        ServerProfileDto untouched = StoredProfile(await fixture.ConfigManager.LoadServersAsync(), "beta");
        Assert.Equal("Beta", untouched.DisplayName);
        Assert.Equal("ops", untouched.Group);
    }

    [Fact]
    public async Task Save_MovesTheSessionToItsNewFolder_KeepsItSelected_AndReturnsToTheReadOnlyView()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "prod"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"));
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");

        edit.Folder = "prod";
        await edit.SaveCommand.ExecuteAsync(null);

        Assert.Null(fixture.ViewModel.PropertiesEdit);
        Assert.Equal("alpha", fixture.ViewModel.SelectedServer?.Id);
        Assert.Equal("prod", fixture.ServerById("alpha").Group);
        Assert.Contains(fixture.FolderByPath("prod").Servers, server => server.Id == "alpha");
        Assert.DoesNotContain(fixture.FolderByPath("ops").Servers, server => server.Id == "alpha");
        Assert.True(fixture.ViewModel.PropertiesPanelShowsActions);
        Assert.Empty(fixture.Dialogs.NamedConfirmations);
    }

    [Fact]
    public async Task Save_OfAProfileDeletedMeanwhile_IsRefused_AndLeavesEditMode()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        LocalizationManager localizer = await LoadLocalizerAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"));
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        await fixture.ConfigManager.MutateServersAsync(inventory => inventory.RemoveAll(server => server.Id == "alpha"));

        edit.DisplayName = "Alpha renamed";
        await edit.SaveCommand.ExecuteAsync(null);

        Assert.Null(fixture.ViewModel.PropertiesEdit);
        Assert.Equal(localizer["ErrorServerNotFound"], Assert.Single(statuses));
        List<ServerProfileDto> inventory = await fixture.ConfigManager.LoadServersAsync();
        Assert.Equal("beta", Assert.Single(inventory).Id);
    }

    [Fact]
    public async Task Save_OfASharedSession_IsRefusedWithTheReadOnlyLine_AndWritesNothing()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        LocalizationManager localizer = await LoadLocalizerAsync();
        ServerProfileDto shared = CreateSshServer("shared-src-1", "Shared", "ops");
        fixture.LoadServers(fixture.ExpandGroups("ops"), shared);
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;
        SessionPropertiesEditViewModel edit = new(
            shared.Id,
            shared.DisplayName,
            new SessionBasicFieldsValidator(shared, localizer, settings: null),
            localizer,
            [],
            () => false,
            fixture.ViewModel.SavePropertiesEditAsync,
            _ => { });
        edit.DisplayName = "Renamed";

        await fixture.ViewModel.SavePropertiesEditAsync(edit);

        Assert.Equal(localizer["StatusSharedSessionReadOnly"], Assert.Single(statuses));
        Assert.Empty(await fixture.ConfigManager.LoadServersAsync());
    }

    /// <summary>
    /// A field the user left alone is judged by what is stored when Save runs: the login cleared
    /// meanwhile makes a rename of an SSH session invalid, and the panel stays open with the error
    /// under the field.
    /// </summary>
    [Fact]
    public async Task Save_WhoseValuesTheStoredProfileNowRefuses_StaysInEditModeWithTheError()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        LocalizationManager localizer = await LoadLocalizerAsync();
        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), CreateSshServer("alpha", "Alpha", "ops"));
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        edit.DisplayName = "Alpha renamed";
        Assert.True(edit.CanSave);
        await fixture.ConfigManager.MutateServersAsync(inventory =>
        {
            StoredProfile(inventory, "alpha").SshUsername = null;
            return true;
        });

        await edit.SaveCommand.ExecuteAsync(null);

        Assert.Same(edit, fixture.ViewModel.PropertiesEdit);
        Assert.Equal(localizer["ValidationInlineSshUserRequired"], edit.UsernameError);
        Assert.False(edit.CanSave);
        Assert.Equal("Alpha", StoredProfile(await fixture.ConfigManager.LoadServersAsync(), "alpha").DisplayName);
    }

    [Fact]
    public async Task ChoosingAnotherSession_WithValidUnsavedEdits_AsksAndSavingAppliesToTheOriginalSession()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"));
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        edit.DisplayName = "Alpha renamed";
        fixture.Dialogs.NamedConfirmAnswer = false;

        fixture.ViewModel.SelectSingle(fixture.ServerById("beta"));
        await fixture.ViewModel.PropertiesEditResolution;

        (string message, string confirmLabel, string declineLabel) = Assert.Single(fixture.Dialogs.NamedConfirmations);
        Assert.Equal("Save changes to Alpha?", message);

        // The answer that loses nothing takes Enter, which this overload gives to the decline label.
        Assert.Equal("Discard", confirmLabel);
        Assert.Equal("Save", declineLabel);
        List<ServerProfileDto> inventory = await fixture.ConfigManager.LoadServersAsync();
        Assert.Equal("Alpha renamed", StoredProfile(inventory, "alpha").DisplayName);
        Assert.Equal("Beta", StoredProfile(inventory, "beta").DisplayName);
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        Assert.Null(fixture.ViewModel.PropertiesEdit);
        Assert.Equal("Alpha renamed", fixture.ServerById("alpha").DisplayName);
    }

    [Fact]
    public async Task ChoosingAnotherSession_WithValidUnsavedEdits_AndDiscarding_WritesNothing()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"));
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        edit.DisplayName = "Alpha renamed";
        fixture.Dialogs.NamedConfirmAnswer = true;

        fixture.ViewModel.SelectSingle(fixture.ServerById("beta"));
        await fixture.ViewModel.PropertiesEditResolution;

        Assert.Single(fixture.Dialogs.NamedConfirmations);
        List<ServerProfileDto> inventory = await fixture.ConfigManager.LoadServersAsync();
        Assert.Equal("Alpha", StoredProfile(inventory, "alpha").DisplayName);
        Assert.Equal("Beta", StoredProfile(inventory, "beta").DisplayName);
        Assert.Equal("beta", fixture.ViewModel.SelectedServer?.Id);
        Assert.Null(fixture.ViewModel.PropertiesEdit);
    }

    [Fact]
    public async Task ChoosingAnotherSession_WithInvalidUnsavedEdits_DiscardsThemAndSaysSo()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        LocalizationManager localizer = await LoadLocalizerAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"));
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        edit.DisplayName = "Alpha renamed";
        edit.Host = "";

        fixture.ViewModel.SelectSingle(fixture.ServerById("beta"));
        await fixture.ViewModel.PropertiesEditResolution;

        Assert.Empty(fixture.Dialogs.NamedConfirmations);
        Assert.Equal(localizer.Format("PropertiesEditDiscardedInvalid", "Alpha"), Assert.Single(statuses));
        Assert.Equal(
            "Alpha",
            StoredProfile(await fixture.ConfigManager.LoadServersAsync(), "alpha").DisplayName);
        Assert.Null(fixture.ViewModel.PropertiesEdit);
    }

    [Fact]
    public async Task ChoosingAnotherSession_WithNothingChanged_AsksNothing()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"));
        await OpenEditAsync(fixture, "alpha");

        fixture.ViewModel.SelectSingle(fixture.ServerById("beta"));
        await fixture.ViewModel.PropertiesEditResolution;

        Assert.Empty(fixture.Dialogs.NamedConfirmations);
        Assert.Null(fixture.ViewModel.PropertiesEdit);
    }

    [Fact]
    public async Task SelectingTheSameSessionAgain_KeepsTheEditOpen()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"));
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        edit.DisplayName = "Alpha renamed";

        fixture.ViewModel.SelectSingle(fixture.ServerById("alpha"));

        Assert.Same(edit, fixture.ViewModel.PropertiesEdit);
        Assert.True(fixture.ViewModel.PropertiesPanelShowsEditor);
        Assert.Empty(fixture.Dialogs.NamedConfirmations);
    }

    [Fact]
    public async Task HidingThePanel_WithValidUnsavedEdits_AsksAndSavesForTheSessionEdited()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"));
        fixture.ViewModel.ShowPropertiesPanel = true;
        await fixture.ViewModel.ViewPreferencePersistence;
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        edit.DisplayName = "Alpha renamed";
        fixture.Dialogs.NamedConfirmAnswer = false;

        fixture.ViewModel.ShowPropertiesPanel = false;
        await fixture.ViewModel.PropertiesEditResolution;
        await fixture.ViewModel.ViewPreferencePersistence;

        Assert.Single(fixture.Dialogs.NamedConfirmations);
        Assert.Equal(
            "Alpha renamed",
            StoredProfile(await fixture.ConfigManager.LoadServersAsync(), "alpha").DisplayName);
        Assert.Null(fixture.ViewModel.PropertiesEdit);
        Assert.Equal("alpha", fixture.ViewModel.SelectedServer?.Id);
    }

    [Fact]
    public async Task ClosingTheApplication_DropsUnsavedEditsWithoutAQuestion()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), CreateSshServer("alpha", "Alpha", "ops"));
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        edit.DisplayName = "Alpha renamed";

        fixture.ViewModel.Dispose();

        Assert.Null(fixture.ViewModel.PropertiesEdit);
        Assert.Empty(fixture.Dialogs.NamedConfirmations);
        Assert.Equal(
            "Alpha",
            StoredProfile(await fixture.ConfigManager.LoadServersAsync(), "alpha").DisplayName);
    }

    [Fact]
    public async Task Save_WhenOnlyTheNameChanged_KeepsTheFolderADragMovedItTo()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops", "prod"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"));
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        await fixture.ConfigManager.MutateServersAsync(inventory =>
        {
            StoredProfile(inventory, "alpha").Group = "prod";
            return true;
        });

        edit.DisplayName = "Alpha renamed";
        await edit.SaveCommand.ExecuteAsync(null);

        ServerProfileDto saved = StoredProfile(await fixture.ConfigManager.LoadServersAsync(), "alpha");
        Assert.Equal("Alpha renamed", saved.DisplayName);
        Assert.Equal("prod", saved.Group);
        Assert.Equal("prod", fixture.ServerById("alpha").Group);
    }

    [Fact]
    public async Task Save_OfAProfileWhoseProtocolChangedMeanwhile_IsRefused_AndWritesNothing()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        LocalizationManager localizer = await LoadLocalizerAsync();
        await fixture.LoadServersAsync(fixture.ExpandGroups("ops"), CreateSshServer("alpha", "Alpha", "ops"));
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        await fixture.ConfigManager.MutateServersAsync(inventory =>
        {
            StoredProfile(inventory, "alpha").ConnectionType = "RDP";
            return true;
        });

        edit.PortText = "2200";
        edit.Username = "admin";
        await edit.SaveCommand.ExecuteAsync(null);

        Assert.Null(fixture.ViewModel.PropertiesEdit);
        Assert.Equal(localizer.Format("PropertiesEditProtocolChanged", "Alpha"), Assert.Single(statuses));
        ServerProfileDto stored = StoredProfile(await fixture.ConfigManager.LoadServersAsync(), "alpha");
        Assert.Equal("RDP", stored.ConnectionType);
        Assert.Equal(DefaultPorts.Rdp, stored.RemotePort);
        Assert.Null(stored.RdpUsername);
        Assert.Equal("root", stored.SshUsername);
    }

    [Fact]
    public async Task ChoosingAnotherSession_WhileTheApplicationCloses_DropsTheEditsWithoutAskingOrSaving()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"));
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        edit.DisplayName = "Alpha renamed";
        fixture.ViewModel.ApplicationIsShuttingDown = () => true;
        fixture.Dialogs.NamedConfirmAnswer = false;

        fixture.ViewModel.SelectSingle(fixture.ServerById("beta"));
        await fixture.ViewModel.PropertiesEditResolution;

        Assert.Empty(fixture.Dialogs.NamedConfirmations);
        Assert.Equal("Alpha", StoredProfile(await fixture.ConfigManager.LoadServersAsync(), "alpha").DisplayName);
        Assert.True(edit.IsSettled);
    }

    [Fact]
    public async Task ChoosingAnotherSession_WhileASaveOfTheEditIsInFlight_DoesNotAskAgain()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        await fixture.LoadServersAsync(
            fixture.ExpandGroups("ops"),
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"));
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        edit.DisplayName = "Alpha renamed";
        edit.IsSaving = true;

        fixture.ViewModel.SelectSingle(fixture.ServerById("beta"));
        await fixture.ViewModel.PropertiesEditResolution;

        Assert.Empty(fixture.Dialogs.NamedConfirmations);
    }

    [Fact]
    public async Task TheQuestion_IsDroppedWithTheNotFoundLine_WhenTheSessionIsGoneByTheTimeItWouldBeAsked()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        LocalizationManager localizer = await LoadLocalizerAsync();
        AppSettings settings = fixture.ExpandGroups("ops");
        await fixture.LoadServersAsync(
            settings,
            CreateSshServer("alpha", "Alpha", "ops"),
            CreateSshServer("beta", "Beta", "ops"));
        List<string> statuses = [];
        fixture.ViewModel.StatusMessageRequested += statuses.Add;
        SessionPropertiesEditViewModel edit = await OpenEditAsync(fixture, "alpha");
        edit.DisplayName = "Alpha renamed";

        fixture.LoadServers(settings, CreateSshServer("beta", "Beta", "ops"));
        fixture.ViewModel.SelectSingle(fixture.ServerById("beta"));
        await fixture.ViewModel.PropertiesEditResolution;

        Assert.Empty(fixture.Dialogs.NamedConfirmations);
        Assert.Contains(localizer["ErrorServerNotFound"], statuses);
    }

    /// <summary>A session with a login: SSH needs one, and an edit of a profile without it starts invalid.</summary>
    private static ServerProfileDto CreateSshServer(string id, string displayName, string group, int sortOrder = 0)
    {
        ServerProfileDto server = CreateServer(id, displayName, group, sortOrder);
        server.SshUsername = "root";
        return server;
    }

    private static async Task<SessionPropertiesEditViewModel> OpenEditAsync(
        ServerListSelectionFixture fixture,
        string serverId)
    {
        fixture.ViewModel.SelectSingle(fixture.ServerById(serverId));
        await fixture.ViewModel.BeginPropertiesEditCommand.ExecuteAsync(null);
        return Assert.IsType<SessionPropertiesEditViewModel>(fixture.ViewModel.PropertiesEdit);
    }

    private static ServerProfileDto StoredProfile(IEnumerable<ServerProfileDto> inventory, string id) =>
        Assert.Single(inventory, server => string.Equals(server.Id, id, StringComparison.Ordinal));

    private static async Task<LocalizationManager> LoadLocalizerAsync()
    {
        LocalizationManager localizer = new();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
        return localizer;
    }
}
