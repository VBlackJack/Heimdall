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
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Services.PostConnect;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.App.ViewModels.Settings;
using Heimdall.Core.Configuration;
using Heimdall.Core.Import;
using Heimdall.Core.Localization;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;
using Heimdall.Core.Updates;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class SettingsViewModelTests
{
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
        SettingsViewModel viewModel = new(
            new FakeConfigManager(),
            localizer,
            dialog,
            trustedHostKeys,
            new PinManager(),
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

        return new SettingsViewModel(
            config,
            localizer,
            dialog,
            trustedHostKeys,
            new PinManager(),
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

        public string DownloadResultPath { get; set; } = @"C:\Temp\HeimdallSetup.exe";

        public Exception? DownloadException { get; set; }

        public int DownloadCallCount { get; private set; }

        public Task<UpdateCheckResult> CheckForUpdatesAsync(HeimdallVersion current, string owner, string repo, CancellationToken cancellationToken)
            => Task.FromResult(Result);

        public Task<string> DownloadVerifiedAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            DownloadCallCount++;
            if (DownloadException is not null)
            {
                throw DownloadException;
            }

            return Task.FromResult(DownloadResultPath);
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

        public List<ServerProfileDto> Servers { get; set; } = [];

        public List<ServerProfileDto>? SavedServers { get; private set; }

        public string ConfigPath => "config";

        public string SettingsPath => "settings.json";

        public string ServersPath => "servers.json";

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(CloneSettings(Settings));

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

        public Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            MergeSettingCallCount++;
            AppSettings currentSettings = CloneSettings(Settings);
            mutate(currentSettings);
            Settings = currentSettings;
            SavedSettings = currentSettings;
            SettingsChanged?.Invoke(currentSettings);
            return Task.CompletedTask;
        }

        public Task<List<ServerProfileDto>> LoadServersAsync()
            => Task.FromResult(Servers);

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

        public List<(string Title, string Message, string Severity)> ConfirmCalls { get; } = [];

        public List<(string Title, string Message)> ErrorCalls { get; } = [];

        public List<(string Title, string Message)> WarningCalls { get; } = [];

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
            => Task.FromResult<bool?>(null);

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
