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
using System.Runtime.InteropServices;
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
using Heimdall.Core.Redaction;

namespace Heimdall.App.UiTests.E2E;

/// <summary>
/// "Copy output, secrets masked" through the real product: an embedded local shell, a secret
/// typed into its terminal, the keyboard shortcut, the review dialog and the clipboard.
/// </summary>
/// <remarks>
/// Keyboard rather than mouse: on this desktop the app may not get the foreground, so the tests
/// select with UI Automation, take the foreground, and then type. The terminal is a WebView2
/// page, so its text is not in the UI Automation tree; the test learns that the shell has
/// processed the command by asking for the masked copy until the text shows it.
/// <para>
/// Global keyboard input goes to whichever window is in front, so every input is preceded by a
/// check that the Heimdall main window is the foreground window; the test fails rather than
/// type into another window. The clipboard text is saved before the run and put back after it.
/// </para>
/// </remarks>
[Collection(DesktopUiCollection.Name)]
public sealed class MaskedCopyTests
{
    private const string DataDirOption = "--data-dir";
    private const string SettingsFileName = "settings.json";
    private const string ServersFileName = "servers.json";
    private const string TempFolderPrefix = "heimdall-masked-copy-";
    private const string EnglishLocale = "en";
    private const string LocaleFileName = "en.json";
    private const string ProfileId = "masked-copy-local";
    private const string ProfileName = "Masked copy shell";
    private const string ProfileConnectionType = "LOCAL";
    private const string ShellExecutable = "cmd.exe";
    private const string NameSeparator = ", ";
    private const int DiagnosticNameLimit = 80;
    private const string Secret = "hunter2";
    private const string SecretCommand = "echo password=" + Secret;

    private const string TextAutomationId = "MaskedCopyText";
    private const string CopyButtonAutomationId = "MaskedCopyCopyButton";
    private const string CancelButtonAutomationId = "MaskedCopyCancelButton";
    private const string PlaceholderZero = "{0}";
    private const string PlaceholderOne = "{1}";

    private const string MainWindowDescription = "Heimdall main window";
    private const string ProfileRowDescription = "local shell row in the session tree";
    private const string DialogDescription = "masked copy dialog showing the masked command";
    private const string DialogClosedDescription = "masked copy dialog closed";
    private const string CopyButtonDescription = "Copy button of the masked copy dialog";
    private const string CountLineDescription = "count line of the key-value rule";

    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DialogProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(15);


    [ProductLaunchFact]
    [Trait("Category", "RequiresDesktop")]
    public void ShortcutOnALocalShell_CopiesTheOutputWithTheSecretMasked()
    {
        var exe = LocateHeimdallExe();
        Assert.True(File.Exists(exe), $"Heimdall executable not found at '{exe}'.");

        var dataDir = Path.Combine(Path.GetTempPath(), TempFolderPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        Application? app = null;
        var savedClipboardText = SaveClipboardText();
        try
        {
            File.WriteAllText(Path.Combine(dataDir, SettingsFileName), BuildSettingsJson(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dataDir, ServersFileName), BuildServersJson(), new UTF8Encoding(false));

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
                () => app.GetMainWindow(automation),
                MainWindowDescription,
                LaunchTimeout);

            OpenLocalShell(window);

            // The shell has to be up before text typed into it counts: the first answer of the
            // shortcut proves the page is ready, and the typed command follows it.
            var dialogTitle = LocaleText(locale, "MaskedCopyDialogTitle");
            Window firstDialog;
            try
            {
                firstDialog = WaitHelpers.WaitFor(
                    () => TryOpenDialog(automation, app, window, dialogTitle),
                    DialogDescription,
                    ShellTimeout);
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                var seen = window.FindAllDescendants().Select(NameOf).Where(name => name.Length > 0).Take(DiagnosticNameLimit);
                throw new Xunit.Sdk.XunitException(ex.Message + " Named elements seen: " + string.Join(" | ", seen));
            }
            CancelDialog(firstDialog);

            ForegroundGuard.Ensure(window);
            Keyboard.Type(SecretCommand);
            ForegroundGuard.Ensure(window);
            Keyboard.Type(VirtualKeyShort.ENTER);

            var dialog = WaitHelpers.WaitFor(
                () => OpenDialogShowingMask(automation, app, window, dialogTitle),
                DialogDescription,
                ShellTimeout);

            var maskedText = ReadDialogText(dialog);
            Assert.DoesNotContain(Secret, maskedText, StringComparison.Ordinal);
            Assert.Contains(MaskPrefix(), maskedText, StringComparison.Ordinal);

            var occurrences = CountOccurrences(maskedText, MaskToken(RedactionRuleIds.KeyValueSecret));
            Assert.True(occurrences >= 1, "The key-value rule masked nothing.");
            var expectedLine = ExpectedCountLine(locale, occurrences);
            WaitHelpers.WaitFor(
                () => dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                    .FirstOrDefault(element => string.Equals(NameOf(element), expectedLine, StringComparison.Ordinal)),
                CountLineDescription + $" ('{expectedLine}')",
                UiTimeout);

            var copyButton = WaitHelpers.WaitFor(
                () => dialog.FindFirstDescendant(cf => cf.ByAutomationId(CopyButtonAutomationId)),
                CopyButtonDescription,
                UiTimeout);
            copyButton.Patterns.Invoke.Pattern.Invoke();

            WaitHelpers.WaitUntil(
                () => FindWindowByTitle(automation, app, window, dialogTitle) is null,
                DialogClosedDescription,
                CloseTimeout);

            var clipboard = System.Windows.Clipboard.GetText();
            Assert.Equal(maskedText, clipboard);
            Assert.DoesNotContain(Secret, clipboard, StringComparison.Ordinal);
        }
        finally
        {
            StopApplication(app);
            TryDeleteDirectory(dataDir);
            RestoreClipboardText(savedClipboardText);
        }
    }

    // The clipboard is user data the test overwrites: its text is kept and put back. Only the
    // text format is saved; a clipboard that is busy or holds no text is left as it is found.
    private static string? SaveClipboardText()
    {
        try
        {
            return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    private static void RestoreClipboardText(string? saved)
    {
        try
        {
            if (saved is null)
            {
                System.Windows.Clipboard.Clear();
            }
            else
            {
                System.Windows.Clipboard.SetText(saved);
            }
        }
        catch (ExternalException)
        {
            // Best effort: the clipboard is busy.
        }
    }

    // Selects the profile with UI Automation and connects it with Enter, the way the tree's own
    // help says: a mouse click would go to whatever window Windows keeps in front of the app.
    private static void OpenLocalShell(Window window)
    {
        var rowPrefix = ProfileName + NameSeparator;
        AutomationElement row;
        try
        {
            row = WaitHelpers.WaitFor(
                () =>
                {
                    var items = window.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem));

                    // A profile without a folder sits under the "No folder" node, folded at start.
                    foreach (var folded in items.Where(IsCollapsed))
                    {
                        folded.Patterns.ExpandCollapse.Pattern.Expand();
                    }

                    return items.FirstOrDefault(item => NameOf(item).StartsWith(rowPrefix, StringComparison.Ordinal));
                },
                ProfileRowDescription,
                UiTimeout);
        }
        catch (Xunit.Sdk.XunitException ex)
        {
            var seen = window.FindAllDescendants().Select(NameOf).Where(name => name.Length > 0).Take(DiagnosticNameLimit);
            throw new Xunit.Sdk.XunitException(ex.Message + " Named elements seen: " + string.Join(" | ", seen));
        }

        row.Patterns.SelectionItem.Pattern.Select();
        ForegroundGuard.Ensure(window);
        row.Focus();
        ForegroundGuard.Ensure(window);
        Keyboard.Type(VirtualKeyShort.ENTER);
    }

    // Asks for the masked copy until the text shows the typed command, masked. Each probe that
    // misses closes its dialog again, so the caller gets the one that matched, still open.
    private static Window? OpenDialogShowingMask(UIA3Automation automation, Application app, Window window, string title)
    {
        var dialog = TryOpenDialog(automation, app, window, title);
        if (dialog is null)
        {
            return null;
        }

        if (ReadDialogText(dialog).Contains(MaskToken(RedactionRuleIds.KeyValueSecret), StringComparison.Ordinal))
        {
            return dialog;
        }

        CancelDialog(dialog);
        return null;
    }

    private static Window? TryOpenDialog(UIA3Automation automation, Application app, Window window, string title)
    {
        ForegroundGuard.Ensure(window);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_M);

        var deadline = DateTime.UtcNow + DialogProbeTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = FindWindowByTitle(automation, app, window, title);
            if (found is not null)
            {
                return found;
            }

            Thread.Sleep(ProbeInterval);
        }

        return null;
    }

    private static void CancelDialog(Window dialog)
    {
        var cancel = WaitHelpers.WaitFor(
            () => dialog.FindFirstDescendant(cf => cf.ByAutomationId(CancelButtonAutomationId)),
            "Cancel button of the masked copy dialog",
            UiTimeout);
        cancel.Patterns.Invoke.Pattern.Invoke();
        Thread.Sleep(ProbeInterval);
    }

    private static string ReadDialogText(Window dialog)
    {
        var box = WaitHelpers.WaitFor(
            () => dialog.FindFirstDescendant(cf => cf.ByAutomationId(TextAutomationId)),
            "masked text box",
            UiTimeout);
        return box.Patterns.Value.Pattern.Value.Value ?? string.Empty;
    }

    private static string ExpectedCountLine(JsonElement locale, int occurrences)
    {
        var ruleName = LocaleText(locale, "MaskedCopyRuleKeyValueSecret");
        var counted = LocaleText(locale, occurrences == 1 ? "MaskedCopyOccurrencesOne" : "MaskedCopyOccurrences")
            .Replace(PlaceholderZero, occurrences.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return LocaleText(locale, "MaskedCopyCountLine")
            .Replace(PlaceholderZero, ruleName, StringComparison.Ordinal)
            .Replace(PlaceholderOne, counted, StringComparison.Ordinal);
    }

    private static string MaskPrefix() => RedactionMask.Prefix;

    private static string MaskToken(string ruleId) => string.Format(System.Globalization.CultureInfo.InvariantCulture, RedactionMask.Format, ruleId);

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static Window? FindWindowByTitle(UIA3Automation automation, Application app, Window main, string title)
    {
        var top = app.GetAllTopLevelWindows(automation).FirstOrDefault(w => string.Equals(w.Title, title, StringComparison.Ordinal));
        if (top is not null)
        {
            return top;
        }

        return main.FindFirstDescendant(cf => cf.ByControlType(ControlType.Window).And(cf.ByName(title)))?.AsWindow();
    }

    private static bool IsCollapsed(AutomationElement element) =>
        element.Patterns.ExpandCollapse.PatternOrDefault?.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Collapsed;

    private static string NameOf(AutomationElement element) =>
        element.Properties.Name.ValueOrDefault ?? string.Empty;

    // The same naming policy ConfigManager writes with; every other setting keeps its default.
    private static string BuildSettingsJson()
    {
        var settings = new Dictionary<string, object>
        {
            ["schemaVersion"] = AppSettings.CurrentSchemaVersion,
            ["defaultLocale"] = EnglishLocale,

            // The first-run tour covers the window and swallows the keys the test sends.
            ["onboardingCompleted"] = true,
        };
        return JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string BuildServersJson()
    {
        var document = new Dictionary<string, object>
        {
            ["schemaVersion"] = ServerInventoryDocument.CurrentSchemaVersion,
            ["servers"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["id"] = ProfileId,
                    ["displayName"] = ProfileName,
                    ["connectionType"] = ProfileConnectionType,
                    ["localShellExecutable"] = ShellExecutable,

                    // A custom shell is a local-execution payload: unconfirmed, it asks first.
                    ["executionConfirmed"] = true,
                },
            },
        };
        return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
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
