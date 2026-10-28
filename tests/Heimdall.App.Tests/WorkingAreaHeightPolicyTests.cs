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
using System.Windows;
using System.Xml.Linq;
using Heimdall.App.Behaviors;
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// A dialog taller than the display must not put its buttons under the taskbar.
/// </summary>
/// <remarks>
/// The session dialog declares a height of 750, the macro editor 760 and the runbook editor 720,
/// on a 768 px display whose working area is about 728 once the taskbar is out. Lowering a
/// <em>minimum</em> does nothing for that; the ceiling and the top edge are what move. The rule is
/// arithmetic, so it is tested as such; attaching it to a live window needs a desktop.
/// </remarks>
public sealed class WorkingAreaHeightPolicyTests
{
    // 1366x768 at 100%: a 40 px taskbar at the bottom.
    private static readonly Rect SmallDisplay = new(0, 0, 1366, 728);

    [Fact]
    public void ADialogTallerThanTheWorkingArea_GetsACeilingAtTheWorkingArea_AndStartsAtItsTop()
    {
        // CenterOwner puts a 750 px window at (728 - 750) / 2 = -11 on this display.
        var resolved = WorkingAreaHeightPolicy.Resolve(
            top: -11,
            height: 750,
            declaredMaxHeight: double.PositiveInfinity,
            SmallDisplay);

        Assert.Equal(728, resolved.MaxHeight);
        Assert.Equal(0, resolved.Top);
    }

    [Fact]
    public void ADialogThatFits_IsLeftWhereItIs()
    {
        var resolved = WorkingAreaHeightPolicy.Resolve(
            top: 100,
            height: 500,
            declaredMaxHeight: double.PositiveInfinity,
            SmallDisplay);

        Assert.Equal(728, resolved.MaxHeight);
        Assert.Equal(100, resolved.Top);
    }

    [Fact]
    public void ADialogWhoseBottomEdgeIsUnderTheTaskbar_IsMovedUpJustFarEnough()
    {
        // 700 tall at top 100 ends at 800, 72 below the working area's bottom edge.
        var resolved = WorkingAreaHeightPolicy.Resolve(
            top: 100,
            height: 700,
            declaredMaxHeight: double.PositiveInfinity,
            SmallDisplay);

        Assert.Equal(28, resolved.Top);
    }

    [Fact]
    public void ADialogSizedToItsContent_GetsTheCeiling_ButIsNotSlidUpForAWorstCase()
    {
        // Height is NaN for a SizeToContent window when this runs. Sliding it up to make room for
        // the tallest it could become would drag every small dialog to the top of the screen.
        var resolved = WorkingAreaHeightPolicy.Resolve(
            top: 214,
            height: double.NaN,
            declaredMaxHeight: double.PositiveInfinity,
            SmallDisplay);

        Assert.Equal(728, resolved.MaxHeight);
        Assert.Equal(214, resolved.Top);
    }

    [Fact]
    public void ADialogSizedToItsContent_IsNeverLeftAboveTheDisplay()
    {
        var resolved = WorkingAreaHeightPolicy.Resolve(
            top: -40,
            height: double.NaN,
            declaredMaxHeight: double.PositiveInfinity,
            SmallDisplay);

        Assert.Equal(0, resolved.Top);
    }

    [Fact]
    public void ADeclaredCeilingBelowTheWorkingArea_StandsAsItIs()
    {
        var resolved = WorkingAreaHeightPolicy.Resolve(
            top: 50,
            height: 400,
            declaredMaxHeight: 500,
            SmallDisplay);

        Assert.Equal(500, resolved.MaxHeight);
    }

    [Fact]
    public void TheWorkingAreaOfASecondDisplay_IsUsedWithItsOwnOrigin()
    {
        // The owner sits on a monitor stacked below the primary: its working area starts at 768.
        var below = new Rect(0, 768, 1920, 1040);

        var resolved = WorkingAreaHeightPolicy.Resolve(
            top: 700,
            height: 750,
            declaredMaxHeight: double.PositiveInfinity,
            below);

        // 700 is above this display, so the window is lowered onto it.
        Assert.Equal(768, resolved.Top);
        Assert.Equal(1040, resolved.MaxHeight);
    }

    [Fact]
    public void AWindowThatIsNotOnTheDisplayVertically_IsNotMoved()
    {
        // A manual position on a monitor far to the right and above: not this clamp's to move.
        var resolved = WorkingAreaHeightPolicy.Resolve(
            top: -1200,
            height: 750,
            declaredMaxHeight: double.PositiveInfinity,
            SmallDisplay);

        Assert.Equal(-1200, resolved.Top);
        Assert.Equal(728, resolved.MaxHeight);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AWorkingAreaThatCouldNotBeMeasured_ChangesNothing(double unusable)
    {
        var resolved = WorkingAreaHeightPolicy.Resolve(
            top: 20,
            height: 750,
            declaredMaxHeight: 900,
            new Rect(0, 0, 1366, unusable));

        Assert.Equal(900, resolved.MaxHeight);
        Assert.Equal(20, resolved.Top);
    }

    [Fact]
    public void AnEmptyWorkingArea_ChangesNothing()
    {
        var resolved = WorkingAreaHeightPolicy.Resolve(
            top: 20,
            height: 750,
            declaredMaxHeight: double.PositiveInfinity,
            Rect.Empty);

        Assert.True(double.IsPositiveInfinity(resolved.MaxHeight));
        Assert.Equal(20, resolved.Top);
    }

    [Fact]
    public void ApplyingTheResultAgain_ChangesNothing()
    {
        // The behavior runs at SourceInitialized and again at Loaded; the second pass sees the
        // first one's window and must leave it where it is.
        var first = WorkingAreaHeightPolicy.Resolve(-11, 750, double.PositiveInfinity, SmallDisplay);
        var second = WorkingAreaHeightPolicy.Resolve(
            first.Top,
            Math.Min(750, first.MaxHeight),
            double.PositiveInfinity,
            SmallDisplay);

        Assert.Equal(first, second);
    }
}

/// <summary>
/// The population of dialogs the height clamp is for, measured from the XAML rather than listed
/// from memory.
/// </summary>
public sealed class WindowHeightClampContractTests
{
    /// <summary>Below this a fixed height fits the smallest display the application supports.</summary>
    private const double AtRiskHeight = 700;

    /// <summary>
    /// Windows that declare a tall height and are deliberately not clamped: the main window restores
    /// its own placement and is not a dialog, and the floating session window is a user-sized window
    /// that remembers where it was put.
    /// </summary>
    private static readonly string[] NotDialogs = ["MainWindow.xaml", "FloatingSessionWindow.xaml"];

    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void EveryDialogWithAFixedHeightOfSevenHundredOrMore_OptsIntoTheHeightClamp()
    {
        List<string> unclamped = [];
        List<string> seen = [];

        foreach (string file in Directory.EnumerateFiles(AppRoot(), "*.xaml", SearchOption.AllDirectories))
        {
            XElement root = XDocument.Load(file).Root!;
            if (root.Name != Presentation + "Window")
            {
                continue;
            }

            string name = Path.GetFileName(file);
            if (!double.TryParse(
                    (string?)root.Attribute("Height"),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double height)
                || height < AtRiskHeight
                || NotDialogs.Contains(name, StringComparer.Ordinal))
            {
                continue;
            }

            seen.Add(name);
            bool clamps = root.Attributes().Any(attribute =>
                attribute.Name.LocalName == "WorkingAreaMinimumBehavior.ClampHeight"
                && attribute.Value == "True");
            if (!clamps)
            {
                unclamped.Add(name);
            }
        }

        // A sweep that found none checked nothing: the three dialogs the finding names are in it.
        Assert.Contains("ServerDialog.xaml", seen);
        Assert.Contains("MacroEditorDialog.xaml", seen);
        Assert.Contains("RunbookDialog.xaml", seen);

        Assert.True(
            unclamped.Count == 0,
            "These dialogs declare a fixed height of " + AtRiskHeight + " or more and do not opt into"
            + " WorkingAreaMinimumBehavior.ClampHeight, so on a 768 px display their buttons sit under"
            + " the taskbar: " + string.Join(", ", unclamped));
    }

    private static string AppRoot() => Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App");
}
