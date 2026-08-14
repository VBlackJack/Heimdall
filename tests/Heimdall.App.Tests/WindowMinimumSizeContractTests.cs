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
using System.Text.RegularExpressions;

namespace Heimdall.App.Tests;

/// <summary>
/// A census of the windows whose declared minimum can outgrow a small display, and of which of
/// them have opted into the clamp.
/// </summary>
/// <remarks>
/// The population is measured from the XAML rather than listed by hand: an earlier pass counted
/// five such windows where there are thirteen, because it enumerated the ones it remembered
/// instead of the ones that exist. The opted-in set is listed explicitly and grows one lot at a
/// time, so each window is a deliberate decision rather than a sweep.
/// </remarks>
public sealed class WindowMinimumSizeContractTests
{
    /// <summary>
    /// Below this, a window already fits the smallest working area this application supports, so
    /// the clamp would be inert.
    /// </summary>
    private const double AtRiskMinimumWidth = 620;

    /// <summary>
    /// Every window wired to the clamp. A window added to the codebase above the threshold shows
    /// up as a red test rather than as a silent gap, and removing an opt-in is equally visible.
    /// </summary>
    private static readonly string[] OptedIn =
    [
        "CommandLibraryPickerDialog.xaml",
        "FileConflictDialog.xaml",
        "FtpsCertificatePromptDialog.xaml",
        "GatewayOverviewDialog.xaml",
        "HostKeyPromptDialog.xaml",
        "ImportKnownHostsConflictDialog.xaml",
        "ImportKnownHostsDialog.xaml",
        "ImportSessionsPreviewDialog.xaml",
        "MacroEditorDialog.xaml",
        "MainWindow.xaml",
        "RdpImportDialog.xaml",
        "ToolPickerDialog.xaml",
        "TrustedHostKeyDetailsDialog.xaml"
    ];

    [Fact]
    public void EveryAtRiskWindow_OptsIntoTheClamp()
    {
        IReadOnlyList<(string File, double MinWidth)> atRisk = FindAtRiskWindows();

        // The finding measured thirteen; anything else means the population moved and the lot that
        // wires them needs re-scoping rather than a quietly updated number.
        Assert.Equal(13, atRisk.Count);

        string[] optedInFound = [.. atRisk
            .Where(window => ReadXaml(window.File).Contains(
                "WorkingAreaMinimumBehavior.IsEnabled=\"True\"",
                StringComparison.Ordinal))
            .Select(window => Path.GetFileName(window.File))
            .Order(StringComparer.Ordinal)];

        Assert.Equal(OptedIn.Order(StringComparer.Ordinal), optedInFound);
    }

    /// <summary>
    /// The clamp must be computed from the captured declared minimum, never from the window's own
    /// properties - which, after the first clamp, hold the clamped value.
    /// </summary>
    /// <remarks>
    /// Asserted on the source because the window-level path cannot be driven deterministically in
    /// this lane: it needs an STA host and real displays, and adding either would change the
    /// project. The arithmetic itself is covered by <c>WorkingAreaMinimumBehaviorTests</c>.
    /// </remarks>
    [Fact]
    public void Behavior_ResolvesFromTheCapturedMinimum_NotFromTheWindow()
    {
        string source = File.ReadAllText(
            Path.Combine(AppSourceRoot(), "Behaviors", "WorkingAreaMinimumBehavior.cs"));

        int applyStart = source.IndexOf("private static void Apply(Window window)", StringComparison.Ordinal);
        Assert.True(applyStart > 0, "Apply not found.");

        string apply = source[applyStart..];
        int applyEnd = apply.IndexOf("\r\n    }", StringComparison.Ordinal);
        Assert.True(applyEnd > 0, "End of Apply not found.");
        apply = apply[..applyEnd];

        Assert.Contains("tracker.Resolve(smallest)", apply, StringComparison.Ordinal);

        // The shape that made every clamp permanent.
        Assert.DoesNotContain("new Size(window.MinWidth", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_OptsIntoTheClamp()
    {
        string xaml = ReadXaml(Path.Combine(AppSourceRoot(), "MainWindow.xaml"));

        Assert.Contains(
            "behaviors:WorkingAreaMinimumBehavior.IsEnabled=\"True\"",
            xaml,
            StringComparison.Ordinal);

        // And it still declares a minimum worth clamping - an opt-in on a window with no minimum
        // would satisfy the assertion above while protecting nothing.
        Assert.Contains("MinWidth=\"800\"", xaml, StringComparison.Ordinal);
    }

    /// <summary>Every window XAML whose declared MinWidth reaches the at-risk threshold.</summary>
    private static IReadOnlyList<(string File, double MinWidth)> FindAtRiskWindows()
    {
        List<(string, double)> found = [];

        foreach (string file in Directory.EnumerateFiles(AppSourceRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            string xaml = ReadXaml(file);
            if (!xaml.Contains("<Window", StringComparison.Ordinal))
            {
                continue;
            }

            Match match = Regex.Match(xaml, @"MinWidth=""(?<value>[0-9.]+)""");
            if (!match.Success)
            {
                continue;
            }

            double minWidth = double.Parse(
                match.Groups["value"].Value,
                CultureInfo.InvariantCulture);
            if (minWidth >= AtRiskMinimumWidth)
            {
                found.Add((file, minWidth));
            }
        }

        return found;
    }

    private static string ReadXaml(string path) => File.ReadAllText(path);

    private static string AppSourceRoot() => Path.Combine(FindRepositoryRoot(), "src", "Heimdall.App");

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

        throw new DirectoryNotFoundException("Cannot find repository root containing Heimdall.slnx.");
    }
}
