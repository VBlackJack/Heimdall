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
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Rdp.ActiveX;
using Microsoft.Extensions.Time.Testing;

namespace Heimdall.App.Tests;

public sealed class C2DeadSettingsRegressionTests : IDisposable
{
    private readonly string _temporaryRoot =
        Path.Combine(Path.GetTempPath(), $"heimdall-c2-settings-{Guid.NewGuid():N}");

    // Producer: ConnectionViewModel.AddSession, the common tab registration path.
    [Fact]
    public void AddSession_WithRemoteLimit_RejectsBeforeRegistrationAndShowsLocalizedWarning()
    {
        var dialog = DispatchProxy.Create<IDialogService, TrackingDialogProxy>();
        var split = DispatchProxy.Create<ISplitService, TrackingSplitProxy>();
        var viewModel = new ConnectionViewModel(new LocalizationManager(), dialog, split,
            new PaneCloseArbiter(), NoDetachedWindows());
        var existing = viewModel.AddSession("existing", "Existing", "RDP");
        existing.HostControl = new object();

        MethodInfo? guardedAdd = typeof(ConnectionViewModel).GetMethod(
            nameof(ConnectionViewModel.AddSession),
            [typeof(string), typeof(string), typeof(string), typeof(int)]);

        Assert.NotNull(guardedAdd);
        object? rejected = guardedAdd!.Invoke(
            viewModel,
            ["rejected", "Rejected", "SSH", 1]);

        Assert.Null(rejected);
        Assert.Single(viewModel.ActiveSessions);
        Assert.Equal(1, ((TrackingSplitProxy)(object)split).RegisterCallCount);
        Assert.Equal(1, ((TrackingDialogProxy)(object)dialog).WarningCallCount);
    }

    // Producer: SessionWindowService reintroduces an already-open pane through the
    // unguarded three-argument overload. Reintroduction must remain possible at the limit.
    [Fact]
    public void AddSession_WithoutRemoteLimit_ReintroducesExistingSessionAtLimit()
    {
        var dialog = DispatchProxy.Create<IDialogService, TrackingDialogProxy>();
        var split = DispatchProxy.Create<ISplitService, TrackingSplitProxy>();
        var viewModel = new ConnectionViewModel(new LocalizationManager(), dialog, split,
            new PaneCloseArbiter(), NoDetachedWindows());
        var existing = viewModel.AddSession("existing", "Existing", "RDP");
        existing.HostControl = new object();

        SessionTabViewModel restored = viewModel.AddSession("restored", "Restored", "SSH");

        Assert.Equal(2, viewModel.ActiveSessions.Count);
        Assert.Same(restored, viewModel.ActiveSession);
        Assert.Equal(2, ((TrackingSplitProxy)(object)split).RegisterCallCount);
        Assert.Equal(0, ((TrackingDialogProxy)(object)dialog).WarningCallCount);
    }

    // Producer: MainViewModel.OpenToolTabAsync uses the unguarded overload because a
    // local tool is not a remote embedded session.
    [Fact]
    public void AddSession_WithoutRemoteLimit_DoesNotCountLocalToolTabs()
    {
        var dialog = DispatchProxy.Create<IDialogService, TrackingDialogProxy>();
        var split = DispatchProxy.Create<ISplitService, TrackingSplitProxy>();
        var viewModel = new ConnectionViewModel(new LocalizationManager(), dialog, split,
            new PaneCloseArbiter(), NoDetachedWindows());
        var existing = viewModel.AddSession("existing", "Existing", "RDP");
        existing.HostControl = new object();

        SessionTabViewModel tool = viewModel.AddSession("tool-hash", "Hash", "TOOL:HASH");
        tool.HostControl = new object();

        Assert.Equal(2, viewModel.ActiveSessions.Count);
        Assert.Equal(0, ((TrackingDialogProxy)(object)dialog).WarningCallCount);
    }

    // Producer: TunnelService waits after a fresh SSH.NET or Plink tunnel succeeds.
    [Fact]
    public async Task TunnelEstablishmentDelay_UsesConfiguredDelayAndTimeProvider()
    {
        MethodInfo? waitMethod = typeof(TunnelService).GetMethod(
            "WaitForTunnelEstablishmentAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(waitMethod);

        var timeProvider = new FakeTimeProvider();
        var task = Assert.IsAssignableFrom<Task>(
            waitMethod!.Invoke(null, [2500, timeProvider, CancellationToken.None]));

        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(2499));
        Assert.False(task.IsCompleted);
        timeProvider.Advance(TimeSpan.FromMilliseconds(1));
        await task;
    }

    // Producer: MainWindow RDP preset editor binds to SettingsViewModel's existing
    // string[] model through its multi-line text projection.
    [Fact]
    public void RdpResolutionPresetEditor_BindsToExistingTextProjection()
    {
        string xaml = ReadRepoFile("src", "Heimdall.App", "MainWindow.xaml");

        Assert.Contains(
            "Text=\"{Binding Settings.RdpResolutionPresetsText, UpdateSourceTrigger=PropertyChanged}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("RdpResolutionPresetItems", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AddRdpResolutionPresetCommand", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveRdpResolutionPresetCommand", xaml, StringComparison.Ordinal);
    }

    // Producer: the Settings value must reach AppSettings validation and the
    // EmbeddedRdpView SetResilienceOptions call.
    [Fact]
    public void RdpAutoReconnectMaximum_HasCompleteSettingsToRuntimeChain()
    {
        PropertyInfo? appProperty = typeof(AppSettings).GetProperty("RdpAutoReconnectMaxAttempts");
        PropertyInfo? viewModelProperty = typeof(SettingsViewModel).GetProperty("RdpAutoReconnectMaxAttempts");
        Assert.NotNull(appProperty);
        Assert.NotNull(viewModelProperty);

        var defaults = new AppSettings();
        Assert.Equal(
            RdpActiveXHost.MaxAutoReconnectAttempts,
            appProperty!.GetValue(defaults));

        appProperty.SetValue(defaults, 0);
        ValidationResult validation = SchemaValidator.ValidateSettings(defaults);
        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Errors,
            error => error.Contains("RdpAutoReconnectMaxAttempts", StringComparison.Ordinal));

        string embeddedView = ReadRepoFile(
            "src", "Heimdall.App", "Views", "EmbeddedRdpView.xaml.cs");
        Assert.Contains(
            "settings.RdpAutoReconnectMaxAttempts",
            embeddedView,
            StringComparison.Ordinal);
    }

    // Producer: MigrationService maps the legacy EmbeddedRdpTimeoutMs key into the
    // surviving RdpConnectWatchdogTimeoutMs property.
    [Fact]
    public async Task LegacyEmbeddedRdpTimeout_MigratesIntoConnectWatchdogTimeout()
    {
        string legacyPath = Path.Combine(_temporaryRoot, "legacy");
        string targetPath = Path.Combine(_temporaryRoot, "target");
        Directory.CreateDirectory(Path.Combine(legacyPath, "config"));
        Directory.CreateDirectory(Path.Combine(targetPath, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(legacyPath, "config", "settings.json"),
            """{"EmbeddedRdpTimeoutMs":12345}""");
        await File.WriteAllTextAsync(
            Path.Combine(legacyPath, "config", "servers.json"),
            "[]");

        var configManager = new ConfigManager(targetPath);
        var service = new MigrationService(configManager, new LocalizationManager());

        MigrationResult result = await service.ImportFromLegacyAsync(legacyPath);
        AppSettings migrated = await configManager.LoadSettingsAsync();

        Assert.True(result.Success);
        Assert.Equal(12345, migrated.RdpConnectWatchdogTimeoutMs);
    }

    // Producer: SettingsViewModel.ApplyRdpDefaults implements the reset confirmed by
    // SettingsResetRdpDefaultsConfirmBody.
    [Fact]
    public void ApplyRdpDefaults_CoversEveryRdpSettingExposedBySettings()
    {
        string source = ReadRepoFile(
            "src", "Heimdall.App", "ViewModels", "SettingsViewModel.cs");
        string body = SliceMethodBody(source, "private void ApplyRdpDefaults");
        string[] expectedProperties =
        [
            "DefaultResolutionWidth",
            "DefaultResolutionHeight",
            "RdpDefaultMode",
            "RdpDefaultNla",
            "RdpDefaultStrictServerAuthentication",
            "RdpDefaultColorDepth",
            "RdpDefaultDynamicResolution",
            "RdpDefaultMultiMonitor",
            "RdpDefaultRedirectClipboard",
            "RdpDefaultRedirectDrives",
            "RdpDefaultRedirectPrinters",
            "RdpDefaultRedirectComPorts",
            "RdpDefaultRedirectSmartCards",
            "RdpDefaultRedirectWebcam",
            "RdpDefaultRedirectUsb",
            "RdpDefaultAudioCapture",
            "RdpDefaultAutoReconnect",
            "RdpDefaultBitmapCaching",
            "RdpDefaultCompression",
            "RdpDefaultAudioMode",
            "RdpResizeEnableDelayMs",
            "RdpArtifactCleanupDelayMs",
            "RdpCredentialAutofillTimeoutMs",
            "RdpAutoReconnectMaxAttempts",
            "RdpKeepAliveIntervalMs",
            "RdpDialogAdvancedDefault",
            "RdpResolutionPresets",
            "RdpConnectWatchdogTimeoutMs",
        ];

        foreach (string property in expectedProperties)
        {
            Assert.Contains(
                $"{property} = defaults.{property};",
                body,
                StringComparison.Ordinal);
        }
    }

    // Producer: the Advanced expander in ServerDialog.xaml, the single control
    // RdpDialogAdvancedDefault exists to open. A OneTime binding cannot carry a preference that
    // is read after the dialog is built, which is how this setting spent its life inert.
    [Fact]
    public void RdpDialogAdvancedDefault_ReachesTheServerDialogAdvancedExpander()
    {
        string xaml = ReadRepoFile(
            "src", "Heimdall.App", "Views", "Dialogs", "ServerDialog.xaml");
        string startTag = SliceElementStartTag(xaml, "DlgSrv_AdvancedResolutionExpander");

        Assert.Contains(
            "IsExpanded=\"{Binding IsAdvancedMode, Mode=OneWay}\"",
            startTag,
            StringComparison.Ordinal);
        Assert.DoesNotContain("OneTime", startTag, StringComparison.Ordinal);
    }

    // Producer: RdpDisableUdp and RdpDisableUdpHint describe the exact behavior wired
    // by the RDP ActiveX host without promising a transport policy.
    [Fact]
    public void RdpUdpProbeLabels_DoNotPromiseTcpOnlyTransport()
    {
        using JsonDocument english = ReadLocale("en.json");
        using JsonDocument french = ReadLocale("fr.json");

        string englishLabel = english.RootElement.GetProperty("RdpDisableUdp").GetString()!;
        string englishHint = english.RootElement.GetProperty("RdpDisableUdpHint").GetString()!;
        string frenchLabel = french.RootElement.GetProperty("RdpDisableUdp").GetString()!;
        string frenchHint = french.RootElement.GetProperty("RdpDisableUdpHint").GetString()!;

        Assert.DoesNotContain("TCP-only", englishLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("use TCP only", englishHint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TCP uniquement", frenchLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("uniquement TCP", frenchHint, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UDP", englishLabel, StringComparison.Ordinal);
        Assert.Contains("UDP", englishHint, StringComparison.Ordinal);
        Assert.Contains("UDP", frenchLabel, StringComparison.Ordinal);
        Assert.Contains("UDP", frenchHint, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, recursive: true);
        }
    }

    private static string ReadRepoFile(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine([FindRepoRoot(), .. relativeParts]));

    private static JsonDocument ReadLocale(string fileName) =>
        JsonDocument.Parse(ReadRepoFile("locales", fileName));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Heimdall repository root.");
    }

    /// <summary>
    /// Returns the opening tag of the element carrying <paramref name="elementName"/>, so a
    /// binding assertion cannot be satisfied by an unrelated element elsewhere in the view.
    /// </summary>
    private static string SliceElementStartTag(string source, string elementName)
    {
        int named = source.IndexOf($"x:Name=\"{elementName}\"", StringComparison.Ordinal);
        Assert.True(named >= 0, $"Missing element: {elementName}");

        int start = source.LastIndexOf('<', named);
        int end = source.IndexOf('>', named);
        Assert.True(start >= 0 && end > start, $"Unterminated start tag: {elementName}");

        return source[start..(end + 1)];
    }

    private static string SliceMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method signature: {signature}");
        int nextMethod = source.IndexOf("\n    [", start, StringComparison.Ordinal);
        Assert.True(nextMethod > start, $"Could not find end of method: {signature}");
        return source[start..nextMethod];
    }

    private class TrackingDialogProxy : DispatchProxy
    {
        public int WarningCallCount { get; private set; }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowWarning))
            {
                WarningCallCount++;
                return null;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private class TrackingSplitProxy : DispatchProxy
    {
        public int RegisterCallCount { get; private set; }

        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISplitService.RegisterSession))
            {
                RegisterCallCount++;
                return null;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    /// <summary>
    /// A window service that reports nothing detached, so these tests keep asserting exactly what
    /// they were written for: the limit as seen from one window.
    /// </summary>
    private static SessionWindowService NoDetachedWindows() => new(static (_, _) => { });

}
