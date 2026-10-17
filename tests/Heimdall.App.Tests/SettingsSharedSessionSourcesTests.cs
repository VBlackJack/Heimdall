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
using System.Text.Json.Nodes;
using Heimdall.App.Services.SharedSessions;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

/// <summary>
/// The settings card of the shared session sources: validation, persistence, reload, and the
/// post-connect approvals that never leave this machine.
/// </summary>
public sealed partial class SettingsViewModelTests
{
    private const string SavedSourceId = "savedsource1";

    [Fact]
    public async Task SharedSources_LoadFromSettings_ShowEachSourceWithItsStatus()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        var provider = new StubSharedSessionProvider(
            [],
            [],
            [StubSharedSessionProvider.Loaded(SavedSourceId, "Team", 12)]);
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer, sharedSessions: provider);

        viewModel.LoadFromSettings(config.Settings);

        SharedSessionSourceItemViewModel row = Assert.Single(viewModel.SharedSessionSources);
        Assert.Equal(SavedSourceId, row.Id);
        Assert.Equal("Team", row.Name);
        Assert.Equal(@"%OneDriveCommercial%\Team\sessions.mxtsessions", row.Path);
        Assert.True(row.Enabled);
        Assert.True(row.IsValid);
        Assert.Equal(localizer.FormatCount(12, "SettingsSharedSourceStatusLoadedOne", "SettingsSharedSourceStatusLoaded", 12), row.StatusDescription);
        Assert.False(row.HasStatusError);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public async Task SharedSources_StatusOfASourceInError_IsItsReason()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        var provider = new StubSharedSessionProvider(
            [],
            [],
            [StubSharedSessionProvider.Failed(SavedSourceId, "Team", "The file was not found.")]);
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer, sharedSessions: provider);

        viewModel.LoadFromSettings(config.Settings);

        SharedSessionSourceItemViewModel row = Assert.Single(viewModel.SharedSessionSources);
        Assert.Equal("The file was not found.", row.StatusDescription);
        Assert.True(row.HasStatusError);
    }

    [Fact]
    public async Task SharedSources_NameIsRequiredAndUniqueInAnyCase_PathIsRequired()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);

        viewModel.AddSharedSessionSourceCommand.Execute(null);
        SharedSessionSourceItemViewModel added = viewModel.SharedSessionSources[^1];

        Assert.Equal(localizer["SettingsSharedSourceNameRequired"], added.NameError);
        Assert.Equal(localizer["SettingsSharedSourcePathRequired"], added.PathError);
        Assert.True(SharedSessionIds.IsValidSourceId(added.Id));
        Assert.NotEqual(SavedSourceId, added.Id);

        added.Name = "  tEAM ";
        added.Path = @"D:\other.mxtsessions";

        Assert.All(
            viewModel.SharedSessionSources,
            row => Assert.Equal(localizer["SettingsSharedSourceNameDuplicate"], row.NameError));
        Assert.Null(added.PathError);

        added.Name = "Other team";

        Assert.All(viewModel.SharedSessionSources, row => Assert.True(row.IsValid));
        Assert.False(viewModel.HasInvalidSharedSessionSources);
    }

    [Fact]
    public async Task SharedSources_InvalidRow_RefusesTheSave()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer);
        viewModel.LoadFromSettings(config.Settings);
        viewModel.AddSharedSessionSourceCommand.Execute(null);
        int mergesBefore = config.MergeSettingCallCount;

        bool saved = await viewModel.TrySaveAsync();

        Assert.False(saved);
        Assert.Equal(localizer["ValidationSharedSessionSourcesInvalid"], viewModel.ValidationSummary);
        Assert.Equal(mergesBefore, config.MergeSettingCallCount);
        Assert.Single(config.Settings.SharedSessionSources);

        // Fixing the row withdraws the banner and lets the save through.
        SharedSessionSourceItemViewModel added = viewModel.SharedSessionSources[^1];
        added.Name = "Lab";
        Assert.Null(viewModel.ValidationSummary);
        added.Path = @"\\files.example.test\lab\sessions.mxtsessions";

        Assert.True(await viewModel.TrySaveAsync());
    }

    [Fact]
    public async Task SharedSources_Save_WritesTheRowsTrimmed_KeepingEachIdentifier()
    {
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.LoadFromSettings(config.Settings);
        SharedSessionSourceItemViewModel saved = Assert.Single(viewModel.SharedSessionSources);
        saved.Enabled = false;
        viewModel.AddSharedSessionSourceCommand.Execute(null);
        SharedSessionSourceItemViewModel added = viewModel.SharedSessionSources[^1];
        added.Name = "  Lab  ";
        added.Path = @"  %USERPROFILE%\lab.mxtsessions  ";
        Assert.True(viewModel.IsDirty);

        Assert.True(await viewModel.TrySaveAsync());

        List<SharedSessionSourceDto> written = config.Settings.SharedSessionSources;
        Assert.Equal(2, written.Count);
        Assert.Equal(SavedSourceId, written[0].Id);
        Assert.False(written[0].Enabled);
        Assert.Equal(added.Id, written[1].Id);
        Assert.Equal("Lab", written[1].Name);
        Assert.Equal(@"%USERPROFILE%\lab.mxtsessions", written[1].Path);
        Assert.True(written[1].Enabled);
    }

    [Fact]
    public async Task SharedSources_Remove_DropsTheRowAtTheNextSave()
    {
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.LoadFromSettings(config.Settings);

        viewModel.RemoveSharedSessionSourceCommand.Execute(Assert.Single(viewModel.SharedSessionSources));

        Assert.Empty(viewModel.SharedSessionSources);
        Assert.True(viewModel.IsDirty);
        Assert.True(await viewModel.TrySaveAsync());
        Assert.Empty(config.Settings.SharedSessionSources);
    }

    [Fact]
    public async Task SharedSources_Reload_ReadsTheSavedSourcesAndRefreshesTheStatus()
    {
        LocalizationManager localizer = await CreateLocalizerAsync();
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        var provider = new StubSharedSessionProvider([], [], []);
        SettingsViewModel viewModel = CreateViewModel(config, localizer: localizer, sharedSessions: provider);
        viewModel.LoadFromSettings(config.Settings);
        SharedSessionSourceItemViewModel row = Assert.Single(viewModel.SharedSessionSources);
        Assert.Equal(localizer["SettingsSharedSourceStatusPending"], row.StatusDescription);

        provider.SetContents([], [], [StubSharedSessionProvider.Loaded(SavedSourceId, "Team", 1)]);
        Assert.True(viewModel.ReloadSharedSessionSourcesCommand.CanExecute(null));
        await viewModel.ReloadSharedSessionSourcesCommand.ExecuteAsync(null);

        Assert.Equal(1, provider.LoadCount);
        Assert.Equal(SavedSourceId, Assert.Single(provider.LastLoadedSettings!.SharedSessionSources).Id);
        Assert.Equal(localizer.FormatCount(1, "SettingsSharedSourceStatusLoadedOne", "SettingsSharedSourceStatusLoaded", 1), row.StatusDescription);
        Assert.False(viewModel.IsDirty);
    }

    [Fact]
    public void SharedSources_WithoutAProvider_OfferNoReload()
    {
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        SettingsViewModel viewModel = CreateViewModel(config);

        Assert.False(viewModel.CanReloadSharedSessions);
        Assert.False(viewModel.ReloadSharedSessionSourcesCommand.CanExecute(null));
    }

    [Fact]
    public async Task SharedSources_ResetDefaults_KeepsTheSources_AndClearsTheApprovalsAtTheSave()
    {
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        config.Settings.SharedPostConnectApprovals["shared-" + SavedSourceId + "-abc"] = "fingerprint";
        var dialog = new FakeDialogService { ConfirmResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);

        await viewModel.ResetToDefaultsCommand.ExecuteAsync(null);

        Assert.Equal(SavedSourceId, Assert.Single(viewModel.SharedSessionSources).Id);
        Assert.True(viewModel.ClearsSharedApprovalsOnSave);
        Assert.NotEmpty(config.Settings.SharedPostConnectApprovals);

        Assert.True(await viewModel.TrySaveAsync());

        Assert.Empty(config.Settings.SharedPostConnectApprovals);
        Assert.Equal(SavedSourceId, Assert.Single(config.Settings.SharedSessionSources).Id);
        Assert.False(viewModel.ClearsSharedApprovalsOnSave);
    }

    [Fact]
    public async Task SharedSources_OrdinarySave_LeavesTheApprovalsAlone()
    {
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        config.Settings.SharedPostConnectApprovals["shared-" + SavedSourceId + "-abc"] = "fingerprint";
        SettingsViewModel viewModel = CreateViewModel(config);
        viewModel.LoadFromSettings(config.Settings);
        Assert.Single(viewModel.SharedSessionSources).Name = "Renamed";

        Assert.True(await viewModel.TrySaveAsync());

        Assert.Equal("fingerprint", Assert.Single(config.Settings.SharedPostConnectApprovals).Value);
    }

    [Fact]
    public async Task SharedSources_DiscardAfterAReset_DoesNotClearTheApprovals()
    {
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        config.Settings.SharedPostConnectApprovals["shared-" + SavedSourceId + "-abc"] = "fingerprint";
        var dialog = new FakeDialogService { ConfirmResult = true };
        SettingsViewModel viewModel = CreateViewModel(config, dialog);
        viewModel.LoadFromSettings(config.Settings);
        await viewModel.ResetToDefaultsCommand.ExecuteAsync(null);

        viewModel.LoadFromSettings(config.Settings);

        Assert.False(viewModel.ClearsSharedApprovalsOnSave);
    }

    [Fact]
    public void SettingsTransfer_CarriesTheSources_AndNeverTheApprovals()
    {
        AppSettings settings = SettingsWithOneSource();
        settings.SharedPostConnectApprovals["shared-" + SavedSourceId + "-abc"] = "fingerprint-secret-to-this-user";

        JsonObject exported = SettingsTransfer.Export(settings, includeUserPaths: false, @"C:\Users\someone", out _);
        JsonObject carried = exported[SettingsTransfer.SettingsProperty]!.AsObject();

        Assert.Contains(nameof(AppSettings.SharedSessionSources), SettingsTransfer.TransferableKeys);
        Assert.DoesNotContain(nameof(AppSettings.SharedPostConnectApprovals), SettingsTransfer.TransferableKeys);
        Assert.True(carried.ContainsKey(nameof(AppSettings.SharedSessionSources)));
        Assert.False(carried.ContainsKey(nameof(AppSettings.SharedPostConnectApprovals)));
        Assert.DoesNotContain("fingerprint-secret-to-this-user", exported.ToJsonString(), StringComparison.Ordinal);

        // A hand-edited file that carries approvals cannot plant them either.
        carried[nameof(AppSettings.SharedPostConnectApprovals)] = new JsonObject { ["shared-x-y"] = "planted" };
        (AppSettings merged, _) = SettingsTransfer.Import(new AppSettings(), exported);

        Assert.Empty(merged.SharedPostConnectApprovals);
        Assert.Equal(SavedSourceId, Assert.Single(merged.SharedSessionSources).Id);
    }

    [Fact]
    public void SharedSources_ARowWithFindings_OffersTheReport_AndOpensIt()
    {
        FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
        config.Settings.SharedSessionSources.Add(new SharedSessionSourceDto { Id = "cleansrc1", Name = "Clean", Path = @"C:\shares\clean.mxtsessions" });
        var report = new SharedSessionImportReport(
            SavedSourceId,
            "Team",
            [new SharedSessionReportEntry(SharedSessionReportCheck.DuplicateHost, "PROD", "db01", "db01.example.test", "db01-bis")]);
        var provider = new StubSharedSessionProvider(
            [],
            [],
            [
                StubSharedSessionProvider.Loaded(SavedSourceId, "Team", 3) with { Report = report },
                StubSharedSessionProvider.Loaded("cleansrc1", "Clean", 1) with { Report = SharedSessionImportReport.Empty("cleansrc1", "Clean") },
            ]);
        var dialog = new FakeDialogService();
        SettingsViewModel viewModel = CreateViewModel(config, dialog, sharedSessions: provider);
        viewModel.LoadFromSettings(config.Settings);
        SharedSessionSourceItemViewModel reported = viewModel.SharedSessionSources[0];
        SharedSessionSourceItemViewModel clean = viewModel.SharedSessionSources[1];

        Assert.True(reported.HasIssues);
        Assert.Equal(1, reported.IssueCount);
        Assert.False(clean.HasIssues);

        viewModel.ShowSharedSourceReportCommand.Execute(reported);

        Assert.Same(report, Assert.Single(dialog.SharedReports).Report);
    }

    [Fact]
    public async Task SharedSources_SavedPasswords_AreCountedPerRow_AndForgottenForThatSourceOnly()
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimdall-settings-shared-secrets", Guid.NewGuid().ToString("N"));
        try
        {
            var secrets = new SharedSessionSecretStore(Path.Combine(directory, SharedSessionSecretStore.FileName));
            await secrets.RememberAsync(SharedSecretKind.Gateway, SavedSourceId, "g", "sealed-1");
            await secrets.RememberAsync(SharedSecretKind.Session, SavedSourceId, "s", "sealed-2");
            await secrets.RememberAsync(SharedSecretKind.Session, "othersource1", "s", "sealed-3");
            FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
            SettingsViewModel viewModel = CreateViewModel(config, sharedSecrets: secrets);
            viewModel.LoadFromSettings(config.Settings);
            SharedSessionSourceItemViewModel row = Assert.Single(viewModel.SharedSessionSources);

            Assert.Equal(2, row.SavedPasswordCount);
            Assert.True(row.HasSavedPasswords);

            await viewModel.ForgetSharedSourcePasswordsCommand.ExecuteAsync(row);

            Assert.Equal(0, row.SavedPasswordCount);
            Assert.False(row.HasSavedPasswords);
            Assert.Equal(0, new SharedSessionSecretStore(secrets.FilePath).CountFor(SavedSourceId));
            Assert.Equal(1, secrets.CountFor("othersource1"));

            // Not a panel value: nothing to save, and Cancel cannot bring them back.
            Assert.False(viewModel.IsDirty);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SharedSources_ResetDefaults_KeepsTheSavedPasswords()
    {
        string directory = Path.Combine(Path.GetTempPath(), "heimdall-settings-shared-secrets", Guid.NewGuid().ToString("N"));
        try
        {
            var secrets = new SharedSessionSecretStore(Path.Combine(directory, SharedSessionSecretStore.FileName));
            await secrets.RememberAsync(SharedSecretKind.Session, SavedSourceId, "s", "sealed-1");
            FakeConfigManager config = new() { Settings = SettingsWithOneSource() };
            var dialog = new FakeDialogService { ConfirmResult = true };
            SettingsViewModel viewModel = CreateViewModel(config, dialog, sharedSecrets: secrets);
            viewModel.LoadFromSettings(config.Settings);

            await viewModel.ResetToDefaultsCommand.ExecuteAsync(null);
            Assert.True(await viewModel.TrySaveAsync());

            Assert.Equal(1, new SharedSessionSecretStore(secrets.FilePath).CountFor(SavedSourceId));
            Assert.Equal(1, Assert.Single(viewModel.SharedSessionSources).SavedPasswordCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AppSettings SettingsWithOneSource()
    {
        var settings = new AppSettings();
        settings.SharedSessionSources.Add(new SharedSessionSourceDto
        {
            Id = SavedSourceId,
            Name = "Team",
            Path = @"%OneDriveCommercial%\Team\sessions.mxtsessions",
            Enabled = true,
        });
        return settings;
    }
}
