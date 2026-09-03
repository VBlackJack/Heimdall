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
using System.Xml.Linq;

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

        // Back to thirteen: the RDP certificate prompt was the fourteenth and is no longer a
        // window at all. Its question is now asked inside the session pane that raised it, which
        // has no top-level minimum size of its own to clamp. Anything else means the population
        // moved and the lot that wires them needs re-scoping rather than a quietly updated
        // number.
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
        string apply = ExtractApply(ReadBehaviourSource());

        Assert.Contains("tracker.Resolve(smallest)", apply, StringComparison.Ordinal);

        // The shape that made every clamp permanent. Reading the window is legitimate for the
        // capture; what is forbidden is RESOLVING from it, because after the first clamp those
        // properties hold the clamped value rather than the declared one.
        Assert.DoesNotContain("Resolve(new Size(window.", apply, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WorkingAreaMinimumPolicy.Resolve(",
            apply,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The declared minimum must be read on first use, not when the opt-in is set.
    /// </summary>
    /// <remarks>
    /// <c>OnIsEnabledChanged</c> fires part-way through the XAML attribute list. Capturing there
    /// records whatever the parser has applied so far - and in every dialog wired to the clamp the
    /// opt-in precedes the root <c>MinHeight</c>, so the capture would read 0 and the first clamp
    /// would write that 0 back over the height the parser was about to set. Capturing lazily makes
    /// the attribute order irrelevant, which is the point: it must never become a contract.
    /// </remarks>
    [Fact]
    public void Behavior_CapturesOnFirstUse_NotWhenTheOptInIsSet()
    {
        string source = ReadBehaviourSource();

        string activation = ExtractBlock(source, "if ((bool)e.NewValue)");
        Assert.DoesNotContain("Capture(", activation, StringComparison.Ordinal);

        string apply = ExtractApply(source);
        Assert.Contains("tracker.Capture(", apply, StringComparison.Ordinal);
    }

    [Fact]
    public void Behavior_CapturesBeforeItResolves()
    {
        string apply = ExtractApply(ReadBehaviourSource());

        int capture = apply.IndexOf("tracker.Capture(", StringComparison.Ordinal);
        int resolve = apply.IndexOf("tracker.Resolve(", StringComparison.Ordinal);

        Assert.True(capture > 0, "Apply does not capture.");
        Assert.True(resolve > 0, "Apply does not resolve.");
        Assert.True(
            capture < resolve,
            "Apply resolves before it captures, so the first clamp would run on an empty capture.");
    }

    /// <summary>
    /// Freezes the premise that exposed the defect: some opted-in windows declare their root
    /// minimum height AFTER the opt-in, so the capture cannot be taken when the opt-in is set.
    /// </summary>
    /// <remarks>
    /// Only the <c>Window</c> element's own attributes count. A descendant control's
    /// <c>MinHeight</c> has nothing to do with <c>window.MinHeight</c>, and a document-wide text
    /// search lets those descendants answer in the root's place: two opted-in dialogs have no root
    /// <c>MinHeight</c> at all, so a text scan reported them as exposing the premise when they do
    /// not. The synthetic control below is what keeps that confusion out.
    /// </remarks>
    [Fact]
    public void AtLeastOneOptedInWindow_DeclaresItsRootMinHeightAfterTheOptIn()
    {
        string[] exposing =
        [
            .. FindAtRiskWindows()
                .Where(window => RootDeclaresMinHeightAfterOptIn(ReadXaml(window.File)))
                .Select(window => Path.GetFileName(window.File))
                .Order(StringComparer.Ordinal)
        ];

        Assert.True(
            exposing.Length > 0,
            "No window declares its root minimum height after the opt-in, so the lazy capture is no longer load-bearing.");
    }

    /// <summary>
    /// The negative control: a descendant's <c>MinHeight</c> must not stand in for the root's.
    /// </summary>
    /// <remarks>
    /// This is the assertion the previous text-based oracle could not make. It scanned the whole
    /// document, so this shape - opt-in on the root, no root minimum, a child that has one - read
    /// as "the premise holds" when the root exposes nothing at all.
    /// </remarks>
    [Fact]
    public void RootScan_IgnoresADescendantMinHeight()
    {
        const string Xaml = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:behaviors="clr-namespace:Heimdall.App.Behaviors"
                    MinWidth="700"
                    behaviors:WorkingAreaMinimumBehavior.IsEnabled="True">
              <Grid MinHeight="420"/>
            </Window>
            """;

        Assert.False(RootDeclaresMinHeightAfterOptIn(Xaml));
    }

    [Fact]
    public void RootScan_SeesARootMinHeightAfterTheOptIn()
    {
        const string Xaml = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:behaviors="clr-namespace:Heimdall.App.Behaviors"
                    MinWidth="700"
                    behaviors:WorkingAreaMinimumBehavior.IsEnabled="True"
                    MinHeight="420">
              <Grid/>
            </Window>
            """;

        Assert.True(RootDeclaresMinHeightAfterOptIn(Xaml));
    }

    [Fact]
    public void RootScan_SeesARootMinHeightBeforeTheOptIn()
    {
        const string Xaml = """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:behaviors="clr-namespace:Heimdall.App.Behaviors"
                    MinHeight="420"
                    behaviors:WorkingAreaMinimumBehavior.IsEnabled="True">
              <Grid/>
            </Window>
            """;

        Assert.False(RootDeclaresMinHeightAfterOptIn(Xaml));
    }

    /// <summary>
    /// Whether the root <c>Window</c> declares <c>MinHeight</c> after the opt-in, reading the root
    /// element's own attributes in document order and nothing else.
    /// </summary>
    private static bool RootDeclaresMinHeightAfterOptIn(string xaml)
    {
        XElement root = XDocument.Parse(xaml).Root
            ?? throw new InvalidOperationException("XAML has no root element.");

        List<string> attributes = [.. root.Attributes().Select(attribute => attribute.Name.LocalName)];

        int optIn = attributes.IndexOf("WorkingAreaMinimumBehavior.IsEnabled");
        int minHeight = attributes.IndexOf("MinHeight");

        return optIn >= 0 && minHeight > optIn;
    }

    private static string ReadBehaviourSource()
        => File.ReadAllText(Path.Combine(AppSourceRoot(), "Behaviors", "WorkingAreaMinimumBehavior.cs"));

    private static string ExtractApply(string source) => ExtractBlock(source, "private static void Apply(Window window)");

    /// <summary>The braced block that follows a marker, by brace matching.</summary>
    private static string ExtractBlock(string source, string marker)
    {
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start > 0, $"Marker not found: {marker}");

        int open = source.IndexOf('{', start);
        Assert.True(open > 0, $"Block not found after: {marker}");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced braces after: {marker}");
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
