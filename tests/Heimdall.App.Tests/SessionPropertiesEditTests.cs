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
using System.Xml.Linq;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The edit state of the properties panel: what it reads from a profile, when it is dirty, valid
/// and savable, which errors it shows under which field, and how the changed fields are written.
/// </summary>
public sealed class SessionPropertiesEditTests : IAsyncLifetime
{
    private LocalizationManager _localizer = new();

    public async Task InitializeAsync()
    {
        _localizer = new LocalizationManager();
        await _localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("RDP", true, true, true)]
    [InlineData("SSH", true, true, true)]
    [InlineData("SFTP", true, true, true)]
    [InlineData("FTP", true, true, true)]
    [InlineData("WINRM", true, true, true)]
    [InlineData("WEB", true, true, true)]
    [InlineData("VNC", true, true, false)]
    [InlineData("Telnet", true, true, false)]
    [InlineData("Local", false, false, false)]
    [InlineData("Citrix", false, false, false)]
    public void TheFieldsOfAProtocol_AreThoseTheDialogOffersForIt(
        string connectionType,
        bool host,
        bool port,
        bool username)
    {
        SessionBasicFieldCapabilities capabilities = SessionBasicFieldCapabilities.For(connectionType);

        Assert.Equal(host, capabilities.HasHost);
        Assert.Equal(port, capabilities.HasPort);
        Assert.Equal(username, capabilities.HasUsername);
    }

    [Fact]
    public void AnEditJustOpened_IsClean_AndCannotBeSaved()
    {
        SessionPropertiesEditViewModel edit = CreateEdit(SshProfile());

        Assert.False(edit.IsDirty);
        Assert.True(edit.IsValid);
        Assert.False(edit.CanSave);
        Assert.False(edit.SaveCommand.CanExecute(null));
        Assert.Equal("Alpha", edit.DisplayName);
        Assert.Equal("alpha.example.com", edit.Host);
        Assert.Equal("2222", edit.PortText);
        Assert.Equal("root", edit.Username);
        Assert.Equal("ops/web", edit.Folder);
    }

    [Fact]
    public void ChangingAField_MakesTheEditDirtyAndSavable_AndRestoringItMakesItCleanAgain()
    {
        SessionPropertiesEditViewModel edit = CreateEdit(SshProfile());

        edit.DisplayName = "Alpha 2";
        Assert.True(edit.IsDirty);
        Assert.True(edit.CanSave);
        Assert.True(edit.SaveCommand.CanExecute(null));

        edit.DisplayName = "Alpha";
        Assert.False(edit.IsDirty);
        Assert.False(edit.CanSave);
    }

    [Fact]
    public void AnInvalidField_BlocksSave_EvenWhenAnotherFieldChanged()
    {
        SessionPropertiesEditViewModel edit = CreateEdit(SshProfile());

        edit.Host = "";
        edit.Folder = "ops/db";

        Assert.True(edit.IsDirty);
        Assert.False(edit.IsValid);
        Assert.False(edit.CanSave);
        Assert.False(edit.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void EachError_ShowsUnderItsOwnField_WithTheMessageOfTheDialog()
    {
        SessionPropertiesEditViewModel edit = CreateEdit(SshProfile());

        edit.DisplayName = "";
        Assert.Equal(_localizer["ValidationDisplayNameRequired"], edit.DisplayNameError);
        Assert.Null(edit.HostError);
        Assert.Null(edit.PortError);
        Assert.Null(edit.UsernameError);

        edit.DisplayName = "Alpha";
        edit.Host = "";
        Assert.Equal(_localizer["ValidationServerAddressRequired"], edit.HostError);
        Assert.Null(edit.DisplayNameError);

        edit.Host = "alpha.example.com";
        edit.PortText = "70000";
        Assert.Equal(_localizer["ValidationSshPortRange"], edit.PortError);
        Assert.Null(edit.HostError);

        edit.PortText = "not a number";
        Assert.Equal(_localizer["ValidationSshPortRange"], edit.PortError);

        edit.PortText = "22";
        edit.Username = "";
        Assert.Equal(_localizer["ValidationInlineSshUserRequired"], edit.UsernameError);
        Assert.Null(edit.PortError);

        edit.Username = "root";
        Assert.True(edit.IsValid);
        Assert.Null(edit.UsernameError);
    }

    [Fact]
    public void ThePortMessage_IsTheOneOfTheProtocol()
    {
        ServerProfileDto rdp = new()
        {
            Id = "rdp",
            DisplayName = "Rdp",
            RemoteServer = "rdp.example.com",
            ConnectionType = "RDP",
            RemotePort = 3389,
        };
        SessionPropertiesEditViewModel edit = CreateEdit(rdp);

        edit.PortText = "0";

        Assert.Equal(_localizer["ValidationPortRange"], edit.PortError);
    }

    /// <summary>
    /// The panel does not copy the dialog's rules, it asks the dialog: the same profile and the
    /// same typed values give the same sentences.
    /// </summary>
    [Fact]
    public void TheErrors_AreExactlyThoseOfTheServerDialog()
    {
        ServerProfileDto profile = SshProfile();
        ServerDialogViewModel dialog = ServerDialogViewModel.FromDto(profile.CloneFaithfully());
        dialog.Localizer = _localizer;
        dialog.DisplayName = "";
        dialog.RemoteServer = "";
        dialog.SshPort = 0;
        dialog.SshUsername = "";
        dialog.ValidateCommand.Execute(null);

        SessionPropertiesEditViewModel edit = CreateEdit(profile);
        edit.DisplayName = "";
        edit.Host = "";
        edit.PortText = "0";
        edit.Username = "";

        Assert.Equal(dialog.DisplayNameError, edit.DisplayNameError);
        Assert.Equal(dialog.RemoteServerError, edit.HostError);
        Assert.Equal(dialog.EndpointPortError, edit.PortError);
        Assert.Equal(dialog.SshUsernameError, edit.UsernameError);
        Assert.All(
            new[] { edit.DisplayNameError, edit.HostError, edit.PortError, edit.UsernameError },
            Assert.NotNull);
    }

    [Fact]
    public void AnErrorOnAFieldThePanelDoesNotEdit_DoesNotBlockSave()
    {
        ServerProfileDto profile = new()
        {
            Id = "rdp",
            DisplayName = "Rdp",
            RemoteServer = "rdp.example.com",
            ConnectionType = "RDP",
            RdpGateway = "not a valid gateway !!",
        };
        ServerDialogViewModel dialog = ServerDialogViewModel.FromDto(profile.CloneFaithfully());
        dialog.Localizer = _localizer;
        dialog.ValidateCommand.Execute(null);
        Assert.NotNull(dialog.RdpGatewayError);
        SessionPropertiesEditViewModel edit = CreateEdit(profile);

        edit.DisplayName = "Rdp 2";

        Assert.True(edit.IsValid);
        Assert.True(edit.CanSave);
    }

    [Fact]
    public void ALocalShell_HasNeitherAddressNorPortNorUsername_ButStillAFolder()
    {
        ServerProfileDto local = new()
        {
            Id = "local",
            DisplayName = "Shell",
            ConnectionType = "Local",
            Group = "tools",
        };
        SessionPropertiesEditViewModel edit = CreateEdit(local);

        Assert.False(edit.HasHost);
        Assert.False(edit.HasPort);
        Assert.False(edit.HasUsername);

        edit.Folder = "tools/shells";
        Assert.True(edit.CanSave);
    }

    [Theory]
    [InlineData("host", true, true)]
    [InlineData("port", true, true)]
    [InlineData("username", true, true)]
    [InlineData("name", true, false)]
    [InlineData("folder", true, false)]
    [InlineData("host", false, false)]
    [InlineData("name", false, false)]
    public void TheConnectedNote_AppearsOnlyWhenAnOpenSessionHasItsHostPortOrUsernameChanged(
        string field,
        bool sessionOpen,
        bool noteExpected)
    {
        bool open = sessionOpen;
        SessionPropertiesEditViewModel edit = CreateEdit(SshProfile(), isSessionOpen: () => open);

        switch (field)
        {
            case "host":
                edit.Host = "other.example.com";
                break;
            case "port":
                edit.PortText = "23";
                break;
            case "username":
                edit.Username = "admin";
                break;
            case "name":
                edit.DisplayName = "Alpha 2";
                break;
            default:
                edit.Folder = "ops/db";
                break;
        }

        Assert.Equal(noteExpected, edit.HasConnectionNote);
        Assert.Equal(noteExpected ? _localizer["PropertiesEditConnectedNote"] : "", edit.ConnectionNote);
    }

    [Fact]
    public void TheConnectedNote_FollowsTheSessionConnectingAndClosing()
    {
        bool open = false;
        SessionPropertiesEditViewModel edit = CreateEdit(SshProfile(), isSessionOpen: () => open);
        edit.Host = "other.example.com";
        Assert.False(edit.HasConnectionNote);

        open = true;
        edit.RefreshOpenState();
        Assert.True(edit.HasConnectionNote);

        open = false;
        edit.RefreshOpenState();
        Assert.False(edit.HasConnectionNote);
    }

    [Fact]
    public void Save_RunsOnlyWhenAllowed_AndCancelAlwaysRuns()
    {
        int saves = 0;
        int cancels = 0;
        SessionPropertiesEditViewModel edit = CreateEdit(
            SshProfile(),
            save: _ =>
            {
                saves++;
                return Task.CompletedTask;
            },
            cancel: _ => cancels++);

        edit.SaveCommand.Execute(null);
        Assert.Equal(0, saves);

        edit.DisplayName = "Alpha 2";
        edit.SaveCommand.Execute(null);
        Assert.Equal(1, saves);

        edit.CancelCommand.Execute(null);
        Assert.Equal(1, cancels);
    }

    [Fact]
    public void Apply_WritesOnlyTheChangedFields()
    {
        ServerProfileDto stored = SshProfile();
        stored.Tags = "keep";
        SessionBasicFieldValues original = new("Alpha", "alpha.example.com", 2222, "root", "ops/web");

        // The user changed the name and the folder; the host in the store moved on meanwhile.
        stored.RemoteServer = "moved.example.com";
        stored.SortOrder = 99;
        SessionBasicFields.Apply(stored, original, original with { DisplayName = "Alpha 2", Folder = "ops/db" });

        Assert.Equal("Alpha 2", stored.DisplayName);
        Assert.Equal("ops/db", stored.Group);
        Assert.Equal("moved.example.com", stored.RemoteServer);
        Assert.Equal(2222, stored.SshPort);
        Assert.Equal("root", stored.SshUsername);
        Assert.Equal("keep", stored.Tags);
        Assert.Equal(99, stored.SortOrder);
    }

    [Fact]
    public void Apply_StoresABlankUsernameAndAnEmptyFolderAsNull_AsTheDialogDoes()
    {
        ServerProfileDto stored = SshProfile();
        SessionBasicFieldValues original = new("Alpha", "alpha.example.com", 2222, "root", "ops/web");

        SessionBasicFields.Apply(stored, original, original with { Username = "", Folder = "" });

        Assert.Null(stored.SshUsername);
        Assert.Null(stored.Group);
    }

    [Fact]
    public void Apply_LeavesThePresenceOfAPortAlone_WhenThePortIsNotChanged()
    {
        ServerProfileDto stored = new()
        {
            Id = "w",
            DisplayName = "Win",
            RemoteServer = "win.example.com",
            ConnectionType = "WINRM",
        };
        Assert.False(stored.HasWinRmPortField);
        SessionPropertiesEditViewModel edit = CreateEdit(stored);

        SessionBasicFields.Apply(stored, edit.Original, edit.Values with { DisplayName = "Win 2" });

        Assert.False(stored.HasWinRmPortField);
    }

    /// <summary>
    /// What the panel writes is what the dialog reads back, and what the read-only list shows: one
    /// mapping from the typed value to the protocol's own field, for every protocol.
    /// </summary>
    [Theory]
    [InlineData("RDP")]
    [InlineData("SSH")]
    [InlineData("SFTP")]
    [InlineData("FTP")]
    [InlineData("WINRM")]
    [InlineData("WEB")]
    [InlineData("VNC")]
    [InlineData("Telnet")]
    public void TheValuesWritten_AreTheValuesTheDialogAndTheRowReadBack(string connectionType)
    {
        ServerProfileDto stored = new()
        {
            Id = "p",
            DisplayName = "P",
            RemoteServer = "p.example.com",
            ConnectionType = connectionType,
        };
        SessionBasicFieldCapabilities capabilities = SessionBasicFieldCapabilities.For(connectionType);
        SessionBasicFieldsValidator before = new(stored, _localizer, settings: null);
        SessionBasicFieldValues edited = before.Current with
        {
            DisplayName = "Renamed",
            Host = "other.example.com",
            Port = 4242,
            Username = capabilities.HasUsername ? "operator" : "",
            Folder = "a/b",
        };

        SessionBasicFields.Apply(stored, before.Current, edited);

        SessionBasicFieldsValidator after = new(stored, _localizer, settings: null);
        Assert.Equal(edited, after.Current);
        Assert.Equal(capabilities.HasUsername ? "operator" : "", ServerItemViewModel.GetUsername(stored));
    }

    [Theory]
    [InlineData("SSH")]
    [InlineData("SFTP")]
    [InlineData("FTP")]
    [InlineData("VNC")]
    [InlineData("Telnet")]
    [InlineData("WEB")]
    [InlineData("RDP")]
    public void TheGenericRemotePort_IsKeptInStepWithTheProtocolPort_AsTheDialogAndTheBulkEditDo(string connectionType)
    {
        ServerProfileDto stored = new()
        {
            Id = "p",
            DisplayName = "P",
            RemoteServer = "p.example.com",
            ConnectionType = connectionType,
        };
        SessionBasicFieldValues original = new SessionBasicFieldsValidator(stored, _localizer, settings: null).Current;

        SessionBasicFields.Apply(stored, original, original with { Port = 4242 });

        Assert.Equal(4242, stored.RemotePort);
    }

    [Fact]
    public void Merge_TakesTheStoredValueOfEveryFieldTheUserLeftAlone()
    {
        SessionBasicFieldValues original = new("A", "old.example.com", 22, "root", "ops");
        SessionBasicFieldValues current = new("A", "moved.example.com", 2222, "admin", "prod");
        SessionBasicFieldValues edited = original with { DisplayName = "B" };

        SessionBasicFieldValues merged = SessionBasicFields.Merge(current, original, edited);

        Assert.Equal(new SessionBasicFieldValues("B", "moved.example.com", 2222, "admin", "prod"), merged);
    }

    [Fact]
    public async Task TheForm_FollowsALanguageChangeWhileItIsOpen()
    {
        SessionPropertiesEditViewModel edit = CreateEdit(SshProfile());
        edit.DisplayName = "";
        string english = _localizer["ValidationDisplayNameRequired"];
        Assert.Equal(english, edit.DisplayNameError);
        string portLabel = edit.PortLabel;
        List<string?> raised = [];
        edit.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await _localizer.SwitchLocaleAsync("fr");

        Assert.NotEqual(english, _localizer["ValidationDisplayNameRequired"]);
        Assert.Equal(_localizer["ValidationDisplayNameRequired"], edit.DisplayNameError);
        Assert.Contains(nameof(edit.PortLabel), raised);
        Assert.NotEqual(portLabel, edit.PortLabel);
    }

    [Fact]
    public async Task ASettledEdit_StopsFollowingTheLanguage_AndSettlesOnce()
    {
        SessionPropertiesEditViewModel edit = CreateEdit(SshProfile());

        Assert.True(edit.TrySettle());
        Assert.False(edit.TrySettle());
        edit.DisplayName = "";
        string before = edit.DisplayNameError!;
        await _localizer.SwitchLocaleAsync("fr");

        Assert.Equal(before, edit.DisplayNameError);
    }

    [Fact]
    public void TheFocusGate_GivesTheFocusOncePerEdit_NeverWhenTheFormMerelyBecomesVisibleAgain()
    {
        Heimdall.App.Controls.EditFocusGate gate = new();

        Assert.False(gate.TryConsume(isVisible: true));

        gate.Arm();
        Assert.False(gate.TryConsume(isVisible: false));
        Assert.True(gate.TryConsume(isVisible: true));

        // The row was hidden by a filter and came back: the form is visible again, the focus is not owed.
        Assert.False(gate.TryConsume(isVisible: true));

        gate.Arm();
        gate.Disarm();
        Assert.False(gate.TryConsume(isVisible: true));
    }

    private SessionPropertiesEditViewModel CreateEdit(
        ServerProfileDto profile,
        Func<bool>? isSessionOpen = null,
        Func<SessionPropertiesEditViewModel, Task>? save = null,
        Action<SessionPropertiesEditViewModel>? cancel = null) =>
        new(
            profile.Id,
            profile.DisplayName,
            new SessionBasicFieldsValidator(profile, _localizer, settings: null),
            _localizer,
            ["ops", "ops/web"],
            isSessionOpen ?? (() => false),
            save ?? (_ => Task.CompletedTask),
            cancel ?? (_ => { }));

    private static ServerProfileDto SshProfile() =>
        new()
        {
            Id = "alpha",
            DisplayName = "Alpha",
            RemoteServer = "alpha.example.com",
            ConnectionType = "SSH",
            SshPort = 2222,
            SshUsername = "root",
            Group = "ops/web",
        };

    /// <summary>The detail pane keeps its read-only list: the form lives in the panel only.</summary>
    [Fact]
    public void TheEditForm_IsInThePanelOnly_AndTheReadOnlyListHasNoInput()
    {
        XDocument window = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "Heimdall.App", "MainWindow.xaml"));
        XDocument list = XDocument.Load(
            Path.Combine(FindRepositoryRoot(), "src", "Heimdall.App", "Controls", "SessionPropertiesView.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement panel = Assert.Single(
            window.Descendants(),
            element => (string?)element.Attribute(x + "Name") == "Mw_PropertiesPanelSession");
        Assert.Single(
            panel.Descendants(),
            element => element.Name.LocalName == "SessionPropertiesEditView");
        Assert.Single(
            window.Descendants(),
            element => element.Name.LocalName == "SessionPropertiesEditView");
        Assert.DoesNotContain(
            list.Descendants(),
            element => element.Name.LocalName is "TextBox" or "ComboBox" or "SessionPropertiesEditView");
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Heimdall.slnx")))
            {
                return directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from {AppContext.BaseDirectory}.");
    }
}
