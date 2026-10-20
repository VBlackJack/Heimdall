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
using System.Text;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Heimdall.App.UiTests.Infrastructure;
using Heimdall.Core.Configuration;

namespace Heimdall.App.UiTests.E2E;

/// <summary>
/// Day one of a shared MobaXterm file, through the real product: a fresh data directory, one
/// shared source, and what a user sees in the session tree and in the import report.
/// </summary>
[Collection(DesktopUiCollection.Name)]
public sealed class SharedSessionsDayOneTests
{
    private const string DataDirOption = "--data-dir";
    private const string SettingsFileName = "settings.json";
    private const string SessionsFileName = "day-one.mxtsessions";
    private const string TempFolderPrefix = "heimdall-day-one-";
    private const string EnglishLocale = "en";
    private const string LocaleFileName = "en.json";
    private const string SourceName = "Day one source";

    private const string MainWindowDescription = "Heimdall main window";
    private const string SharedRootDescription = "shared source root in the session tree";
    private const string ContextMenuDescription = "import report context menu item";
    private const string ReportDialogDescription = "import report dialog";
    private const string ReportClosedDescription = "import report dialog closed";
    private const string PlaceholderZero = "{0}";
    private const string PlaceholderOne = "{1}";
    private const string PlaceholderTwo = "{2}";
    private const string PlaceholderThree = "{3}";
    private const string NameSeparator = ", ";
    private const string SessionTreeAutomationId = "SessionTreeView";
    private const string SessionTreeDescription = "session tree";
    private const string TreeRowsDescription = "session tree rows";

    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(15);

    private const string SshName = "ssh-a";
    private const string RdpName = "rdp-a";
    private const string TelnetName = "telnet-a";
    private const string VncName = "vnc-a";
    private const string SftpName = "sftp-a";
    private const string FtpName = "ftp-a";
    private const string WslName = "AlmaLinux-9";
    private const string TelnetGatewayName = "telnet-gw";

    private const string JumpHost = "jumpt.example.test";
    private const string ProxyHost = "proxyt.example.test";

    private const string MobaFontTail =
        "#MobaFont%10%0%0%-1%15%236,236,236%30,30,30%180,180,192%0%-1%0%%xterm%-1%0%_Std_Colors_0_%80%24%0%1%-1%<none>%%0%0%-1#0#";

    private static readonly (string Name, string ConnectionType)[] ImportedSessions =
    [
        (SshName, "SSH"),
        (RdpName, "RDP"),
        (TelnetName, "TELNET"),
        (VncName, "VNC"),
        (SftpName, "SFTP"),
    ];

    private static readonly string[] SkippedSessions = [FtpName, WslName, TelnetGatewayName];

    [ProductLaunchFact]
    [Trait("Category", "RequiresDesktop")]
    public void SharedFile_ShowsImportedProtocols_AndReportsSkippedSessions()
    {
        var exe = LocateHeimdallExe();
        Assert.True(File.Exists(exe), $"Heimdall executable not found at '{exe}'.");

        var dataDir = Path.Combine(Path.GetTempPath(), TempFolderPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        Application? app = null;
        try
        {
            var sessionsPath = Path.Combine(dataDir, SessionsFileName);
            File.WriteAllText(sessionsPath, BuildSessionsFile(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dataDir, SettingsFileName), BuildSettingsJson(sessionsPath), new UTF8Encoding(false));

            var locale = LoadEnglishLocale();
            var startInfo = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            startInfo.ArgumentList.Add(DataDirOption);
            startInfo.ArgumentList.Add(dataDir);
            app = Application.Launch(startInfo);

            using var automation = new UIA3Automation();
            var window = WaitHelpers.WaitFor(
                () => UiaTimeout.MissOnTimeout(() => app.GetMainWindow(automation)),
                MainWindowDescription,
                LaunchTimeout);

            var tree = WaitHelpers.WaitFor(
                () => UiaTimeout.MissOnTimeout(() => FindSessionTree(window)),
                SessionTreeDescription,
                UiTimeout);

            var rootPrefix = SourceName + NameSeparator;
            var root = WaitHelpers.WaitFor(
                () => UiaTimeout.MissOnTimeout(
                    () => FindTreeItems(tree).FirstOrDefault(item => NameOf(item).StartsWith(rootPrefix, StringComparison.Ordinal))),
                SharedRootDescription,
                UiTimeout);
            root.Patterns.ExpandCollapse.PatternOrDefault?.Expand();

            AssertImportedRows(tree, locale);
            AssertSkippedRowsAbsent(tree);

            var dialog = OpenImportReport(automation, app, window, root, locale);
            try
            {
                AssertReportReasons(dialog, locale);
            }
            finally
            {
                dialog.Close();
            }

            var title = LocaleText(locale, "SharedReportDialogTitle").Replace(PlaceholderZero, SourceName, StringComparison.Ordinal);
            // A search that timed out did not see the report gone: it reads as still open.
            WaitHelpers.WaitUntil(
                () => UiaTimeout.FalseOnTimeout(() => FindWindowByTitle(automation, app, window, title) is null),
                ReportClosedDescription,
                CloseTimeout);
        }
        finally
        {
            StopApplication(app);
            TryDeleteDirectory(dataDir);
        }
    }

    private static void AssertImportedRows(AutomationElement tree, JsonElement locale)
    {
        var sharedTemplate = LocaleText(locale, "SessionTreeServerAccessibleNameShared");
        foreach (var (name, connectionType) in ImportedSessions)
        {
            var protocolKey = ConnectionTypeCatalog.GetDisplayNameKey(connectionType);
            Assert.NotNull(protocolKey);
            var protocol = LocaleText(locale, protocolKey!);

            // Everything the row says before its state: name, source, protocol.
            var expectedPrefix = sharedTemplate[..sharedTemplate.IndexOf(PlaceholderTwo, StringComparison.Ordinal)]
                .Replace(PlaceholderZero, name, StringComparison.Ordinal)
                .Replace(PlaceholderOne, protocol, StringComparison.Ordinal)
                .Replace(PlaceholderThree, SourceName, StringComparison.Ordinal);

            WaitHelpers.WaitFor(
                () => UiaTimeout.MissOnTimeout(
                    () => FindTreeItems(tree).FirstOrDefault(item => NameOf(item).StartsWith(expectedPrefix, StringComparison.Ordinal))),
                $"tree row '{expectedPrefix}'",
                UiTimeout);
        }
    }

    private static void AssertSkippedRowsAbsent(AutomationElement tree)
    {
        // An absence is asserted on a read that completed: a timed-out read is retried within
        // the wait, never taken for an empty tree.
        var names = WaitHelpers.WaitFor(
            () => UiaTimeout.MissOnTimeout(() => FindTreeItems(tree).Select(NameOf).ToList()),
            TreeRowsDescription,
            UiTimeout);
        foreach (var skipped in SkippedSessions)
        {
            Assert.DoesNotContain(names, name => name.StartsWith(skipped + NameSeparator, StringComparison.Ordinal));
        }
    }

    private static Window OpenImportReport(
        UIA3Automation automation,
        Application app,
        Window window,
        AutomationElement root,
        JsonElement locale)
    {
        var header = LocaleText(locale, "TreeCtxShowImportReport");

        // The menu is opened from the keyboard on the selected root, the way a keyboard user
        // reaches it: a mouse right-click lands on whatever window is in front of the app when
        // Windows refuses it the foreground, and an expanded root's centre is a session row.
        root.Patterns.SelectionItem.Pattern.Select();
        ForegroundGuard.Ensure(window);
        root.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.F10);

        var item = WaitHelpers.WaitFor(
            () => UiaTimeout.MissOnTimeout(() => FindMenuItem(automation, app, window, header)),
            ContextMenuDescription,
            UiTimeout);
        item.Patterns.Invoke.Pattern.Invoke();

        var title = LocaleText(locale, "SharedReportDialogTitle").Replace(PlaceholderZero, SourceName, StringComparison.Ordinal);
        return WaitHelpers.WaitFor(
            () => UiaTimeout.MissOnTimeout(() => FindWindowByTitle(automation, app, window, title)),
            ReportDialogDescription,
            UiTimeout);
    }

    private static void AssertReportReasons(Window dialog, JsonElement locale)
    {
        // The report lists folder, session, reason: the reason follows the session name.
        var texts = WaitHelpers.WaitFor(
            () => UiaTimeout.MissOnTimeout(() =>
            {
                var found = dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .Select(NameOf)
                    .ToList();
                return found.Contains(FtpName) ? found : null;
            }),
            "skipped sessions listed in the report",
            UiTimeout);

        AssertReason(texts, FtpName, [ExactText(locale, "SharedReportSkipFtpType")]);
        AssertReason(texts, WslName, [ExactText(locale, "SharedReportSkipWslType")]);
        AssertReason(
            texts,
            TelnetGatewayName,
            [TemplateWithDetail(locale, "SharedReportSkipJumpHost", JumpHost)]);
    }

    private static void AssertReason(List<string> texts, string session, IReadOnlyList<Func<string, bool>> acceptable)
    {
        var index = texts.IndexOf(session);
        Assert.True(index >= 0 && index + 1 < texts.Count, $"'{session}' is not listed in the import report.");
        var reason = texts[index + 1];
        Assert.False(int.TryParse(reason, out _), $"The reason for '{session}' is a bare number: '{reason}'.");
        Assert.True(
            acceptable.Any(matcher => matcher(reason)),
            $"The reason for '{session}' is not the expected localized text: '{reason}'.");
    }

    private static Func<string, bool> ExactText(JsonElement locale, string key)
    {
        var expected = LocaleText(locale, key);
        return actual => string.Equals(actual, expected, StringComparison.Ordinal);
    }

    // A reason with a detail: the text around {0} is fixed, the detail must be the named host.
    private static Func<string, bool> TemplateWithDetail(JsonElement locale, string key, string detail)
    {
        var template = LocaleText(locale, key);
        var split = template.IndexOf(PlaceholderZero, StringComparison.Ordinal);
        Assert.True(split >= 0, $"Locale key '{key}' has no {PlaceholderZero} placeholder.");
        var head = template[..split];
        var tail = template[(split + PlaceholderZero.Length)..];
        return actual => actual.StartsWith(head, StringComparison.Ordinal)
            && actual.EndsWith(tail, StringComparison.Ordinal)
            && actual.Contains(detail, StringComparison.Ordinal);
    }

    // A context menu opens in a popup of its own, which UI Automation shows as a child window of
    // the main window (measured: Window/Popup > Menu/ContextMenu > MenuItem), or else as another
    // top-level window of the product. Only those windows are searched, never the main window's
    // own tree or the whole desktop. A UI Automation timeout is left to the caller.
    private static AutomationElement? FindMenuItem(UIA3Automation automation, Application app, Window main, string header)
    {
        var mainHandle = main.Properties.NativeWindowHandle.ValueOrDefault;
        var otherTopLevel = automation.GetDesktop()
            .FindAllChildren(cf => cf.ByProcessId(app.ProcessId))
            .Where(element => element.Properties.NativeWindowHandle.ValueOrDefault != mainHandle);
        return main.FindAllChildren(cf => cf.ByControlType(ControlType.Window))
            .Concat(otherTopLevel)
            .Select(popup => popup.FindFirstDescendant(cf => cf.ByControlType(ControlType.MenuItem).And(cf.ByName(header))))
            .FirstOrDefault(found => found is not null);
    }

    // The dialog is a top-level window of the product, or a child of the main window when UI
    // Automation parents it to its owner: two one-level searches, never the main window's tree.
    // A UI Automation timeout is left to the caller.
    private static Window? FindWindowByTitle(UIA3Automation automation, Application app, Window main, string title)
    {
        var top = automation.GetDesktop().FindFirstChild(cf => cf.ByControlType(ControlType.Window)
            .And(cf.ByProcessId(app.ProcessId))
            .And(cf.ByName(title)));
        if (top is not null)
        {
            return top.AsWindow();
        }

        return main.FindFirstChild(cf => cf.ByControlType(ControlType.Window).And(cf.ByName(title)))?.AsWindow();
    }

    private static AutomationElement? FindSessionTree(Window window) =>
        window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree).And(cf.ByAutomationId(SessionTreeAutomationId)));

    private static AutomationElement[] FindTreeItems(AutomationElement tree) =>
        tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem));

    private static string NameOf(AutomationElement element) =>
        element.Properties.Name.ValueOrDefault ?? string.Empty;

    private static string BuildSessionsFile()
    {
        var lines = new[]
        {
            "[Bookmarks]",
            "SubRep=",
            "ImgNum=42",
            $"{SshName}= #140#0%ssh-a.example.test%22%[DAS]%%0%-1%%%%%-1%0%0%%%-1%0%0%0%%1080%%0%0%1{MobaFontTail}",
            $"{RdpName}= #91#4%rdp-a.example.test%3389%Administrator%0%-1%-1%-1%-1%0%0%-1%%%%%0%0%%-1%%-1%-1%0%-1%0%-1%0%0%0%0%{MobaFontTail}",
            $"{TelnetName}= #98#1%telnet-a.example.test%2323%telusera%%2%%%%%0%0%%1080%%%0%-1%0{MobaFontTail}",
            $"{VncName}= #128#5%vnc-a.example.test%5911%-1%0%%%%%-1%-1%0%vncusera%0%%1080%%%0{MobaFontTail}",
            $"{SftpName}= #140#7%sftp-a.example.test%2202%sftpusera%-1%0%/srv/sftpa%0%0%%0%%1080%%%%0{MobaFontTail}",
            $"{FtpName}= #130#6%ftp-a.example.test%2121%ftpusera%-1%/srv/ftpa%0%-1%0%0%%21%%%0%0%-1%0%-1%0%%0{MobaFontTail}",
            $"{WslName}= #151#14%{WslName}%{MobaFontTail}",
            $"{TelnetGatewayName}= #98#1%telnet-gw.example.test%23%telusergw%%1%{JumpHost}%2299%jumpusert%%0%1%{ProxyHost}%3128%proxyusert%%0%-1%0{MobaFontTail}",
        };
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    // Serialized with the same naming policy ConfigManager writes settings with; every other
    // setting keeps its default when the file omits it.
    private static string BuildSettingsJson(string sessionsPath)
    {
        var settings = new Dictionary<string, object>
        {
            ["schemaVersion"] = AppSettings.CurrentSchemaVersion,
            ["defaultLocale"] = EnglishLocale,

            // The first-run tour covers the window and swallows the clicks the test makes.
            ["onboardingCompleted"] = true,
            ["sharedSessionSources"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["name"] = SourceName,
                    ["path"] = sessionsPath,
                    ["enabled"] = true,
                },
            },
        };
        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonElement LoadEnglishLocale()
    {
        var path = Path.Combine(WpfTestHost.RepoRoot, "locales", LocaleFileName);
        Assert.True(File.Exists(path), $"Locale file not found at '{path}'.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    private static string LocaleText(JsonElement locale, string key)
    {
        Assert.True(locale.TryGetProperty(key, out var value), $"Locale key '{key}' is missing from {LocaleFileName}.");
        return value.GetString() ?? string.Empty;
    }

    private static void StopApplication(Application? app)
    {
        if (app is null)
        {
            return;
        }

        try
        {
            app.Close();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            if (!app.HasExited)
            {
                app.Kill();
            }

        }
        catch
        {
            // Already gone.
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(app.ProcessId);
            process.WaitForExit(ExitTimeout);
        }
        catch (ArgumentException)
        {
            // The process is already gone.
        }

        app.Dispose();
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort: the folder is under the temp directory.
        }
    }

    private static string LocateHeimdallExe()
    {
        var debugDir = WpfTestHost.ResolveAppBuildDir("Debug");
        if (debugDir is not null)
        {
            return Path.Combine(debugDir, "Heimdall.exe");
        }

        var releaseDir = WpfTestHost.ResolveAppBuildDir("Release");
        if (releaseDir is not null)
        {
            return Path.Combine(releaseDir, "Heimdall.exe");
        }

        return Path.Combine(WpfTestHost.RepoRoot, "src", "Heimdall.App", "bin", "Debug", "Heimdall.exe");
    }
}
