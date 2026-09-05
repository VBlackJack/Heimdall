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
using System.Text.RegularExpressions;

namespace Heimdall.App.Tests;

/// <summary>
/// Blocks new hardcoded UI colors in Heimdall.App. Chrome colors must come
/// from the ThemeForge bridge (<c>HeimdallThemeBridge.xaml</c>) — via
/// <c>DynamicResource</c> in XAML or <c>TryFindResource</c> in code-behind.
/// Files carrying intentional domain palettes (content, not chrome) are
/// allowlisted below; everything else must stay color-literal-free.
/// </summary>
public sealed class HardcodedColorGuardTests
{
    private const string AppRelativePath = @"src\Heimdall.App";

    /// <summary>
    /// Files allowed to contain color literals. Each entry is a relative path
    /// under src/Heimdall.App with the reason the colors are intentional.
    /// Do not extend this list for UI chrome — wire new colors through the
    /// theme bridge instead.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> IntentionalColorFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Theme definition file: the bridge itself defines fixed slots
            // (overlay scrim).
            [@"Themes\HeimdallThemeBridge.xaml"] = "theme bridge definition",
            // Syntax/diff/markdown highlighting palettes — content, not chrome.
            [@"Themes\DraculaSyntaxPalette.cs"] = "fixed Dracula syntax palette",
            [@"Services\MarkdownHighlighting.cs"] = "markdown highlighting palette",
            [@"Services\MarkdownLivePreviewTransformer.cs"] = "markdown live-preview palette",
            [@"Services\MarkdownPreviewBuilder.cs"] = "markdown HTML preview palette",
            [@"Views\Tools\TextDiffView.xaml.cs"] = "diff highlighting palette",
            // Terminal color schemes (Dracula/Solarized/Monokai/Nord).
            [@"Views\EmbeddedSshView.xaml.cs"] = "terminal color schemes",
            // Exported HTML/CSS styling (rendered outside the app).
            [@"Services\EphemeralFileServer.cs"] = "exported HTML/CSS",
            [@"Services\HtmlReportGenerator.cs"] = "exported HTML/CSS",
            // Project color swatches: user-selectable data values, not chrome.
            [@"Views\Dialogs\ProjectDialog.xaml"] = "project color swatch picker (data)",
            [@"ViewModels\Dialogs\ProjectDialogViewModel.cs"] = "project color swatch data + default",
            [@"ViewModels\BadgeColorPalette.cs"] = "the one declaration of the badge and folder colour palette",
            [@"ViewModels\ProjectItemViewModel.cs"] = "project color default (data)",
            // TODO(ux-audit): the duplicated "#3B82F6" project-color default in
            // SettingsViewModel belongs in a single named constant shared with
            // ProjectDialogViewModel/ProjectItemViewModel.
            [@"ViewModels\SettingsViewModel.cs"] = "project color default (data, duplicated)",
            // System.Drawing.Color.FromArgb here converts an already-themed WPF
            // color for WebView2 interop — a type conversion, not a literal.
            [@"Views\EmbeddedVncView.xaml.cs"] = "theme color type conversion (WebView2 interop)",
        };

    private static readonly Regex ColorLiteralRegex = new(
        @"#[0-9A-Fa-f]{8}\b|#[0-9A-Fa-f]{6}\b|(?<!System)Colors\.[A-Z][A-Za-z]+|Color\.FromRgb|FromArgb",
        RegexOptions.Compiled);

    [Fact]
    public void AppSources_DoNotIntroduceHardcodedColors()
    {
        string appDir = Path.Combine(FindRepoRoot(), AppRelativePath);
        Assert.True(Directory.Exists(appDir), $"App source directory not found: {appDir}");

        List<string> violations = new();

        foreach (string file in EnumerateScannedFiles(appDir))
        {
            string relative = Path.GetRelativePath(appDir, file);
            if (IntentionalColorFiles.ContainsKey(relative))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                Match match = ColorLiteralRegex.Match(lines[i]);
                if (match.Success)
                {
                    violations.Add($"  {relative}:{i + 1} — {match.Value}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Found {violations.Count} hardcoded color literal(s) outside the intentional-palette allowlist."
            + " UI chrome colors must come from HeimdallThemeBridge.xaml"
            + " (DynamicResource in XAML, TryFindResource in code-behind):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void IntentionalColorAllowlist_OnlyListsExistingFiles()
    {
        string appDir = Path.Combine(FindRepoRoot(), AppRelativePath);

        var stale = IntentionalColorFiles.Keys
            .Where(relative => !File.Exists(Path.Combine(appDir, relative)))
            .OrderBy(relative => relative, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(
            stale.Count == 0,
            "Allowlist entries no longer exist (remove them):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, stale.Select(s => "  " + s)));
    }

    private static IEnumerable<string> EnumerateScannedFiles(string appDir)
    {
        foreach (string pattern in new[] { "*.cs", "*.xaml" })
        {
            foreach (string file in SourceFileEnumeration.EnumerateFiles(appDir, pattern))
            {
                yield return file;
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
                return dir;

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }
}
