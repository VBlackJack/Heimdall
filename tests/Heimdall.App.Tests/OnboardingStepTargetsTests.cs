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
using System.Text.Json;
using System.Xml.Linq;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using Heimdall.App.ViewModels.Onboarding;
using Heimdall.App.ViewModels.Shell;

namespace Heimdall.App.Tests;

/// <summary>
/// Every step of the tour must point at a control that still exists.
/// </summary>
/// <remarks>
/// The tour spotlights real controls by <c>x:Name</c>, which buys a dependency: rename or delete
/// a named element and the step silently stops highlighting anything. The overlay degrades to a
/// centred card rather than drawing a ring around empty space, so nothing throws, nothing fails,
/// and the only symptom is a step that quietly stops teaching - visible to a pair of eyes and to
/// nothing else.
///
/// That is the same shape as three defects found by hand this week: text clipped instead of
/// wrapped, translated keys referenced nowhere, and a style key that resolves in one window and
/// not another. All three compiled, all three passed every test. This is the cheap guard that
/// class deserves, and it is the reason the step table names its targets as data rather than
/// burying them in a switch.
/// </remarks>
public sealed class OnboardingStepTargetsTests
{
    [Fact]
    public void EveryTargetedStep_NamesAnElementThatExists()
    {
        // Parsed rather than matched as text, so a target left behind in an XML comment does not
        // count as existing.
        XDocument xaml = XDocument.Load(MainWindowXamlPath());

        foreach (OnboardingFlowViewModel.Step step in OnboardingFlowViewModel.Steps)
        {
            if (step.TargetElementName is null)
            {
                continue;
            }

            Assert.True(
                xaml.Descendants().Count(e => (string?)e.Attribute(XamlNamespace + "Name") == step.TargetElementName) == 1,
                $"Onboarding step targets '{step.TargetElementName}', which no longer exists in "
                + "MainWindow.xaml. The step would show a centred card and teach nothing.");
        }
    }

    /// <summary>
    /// Existing is not enough: the target also has to be on screen when its step is shown.
    /// </summary>
    /// <remarks>
    /// The first three steps named controls that existed and still drew no ring on a fresh
    /// profile, from the day the spotlight shipped: they live in the sidebar's session list, the
    /// shipped settings open that sidebar on its Tools tab, and the step only opened the shell's
    /// Sessions tab. So every visibility gate between a target and the window is read from the
    /// XAML, and each one must be a gate the step declares it opens. A gate the tour does not know
    /// how to open fails here rather than as a centred card nobody reports.
    /// </remarks>
    [Fact]
    public void EveryTargetedStep_OpensEveryGateThatCanHideItsTarget()
    {
        XDocument xaml = XDocument.Load(MainWindowXamlPath());

        foreach (OnboardingFlowViewModel.Step step in OnboardingFlowViewModel.Steps)
        {
            if (step.TargetElementName is null)
            {
                continue;
            }

            foreach (string gate in GatesAbove(xaml, step.TargetElementName))
            {
                bool opened = gate switch
                {
                    SessionSidebarGate => step.InSessionSidebar,
                    SessionsShellTabGate => string.Equals(step.ShellTab, ShellTab.Sessions, StringComparison.Ordinal),
                    ToolsShellTabGate => string.Equals(step.ShellTab, ShellTab.Tools, StringComparison.Ordinal),
                    _ => false,
                };

                Assert.True(
                    opened,
                    $"Onboarding step '{step.TitleKey}' targets '{step.TargetElementName}', which "
                    + $"is hidden behind {gate} and the step does not open it. The step would show a "
                    + "centred card and teach nothing.");
            }
        }
    }

    // Keeps the walk above from passing by finding nothing: the session list is behind both
    // sidebar gates, the sidebar switch and the column Ctrl+B collapses, and the walk has to see
    // each of them. A renamed switch or column would otherwise drop its gate without a sound.
    [Fact]
    public void TheGateWalk_SeesBothWaysTheSessionListIsHidden()
    {
        XDocument xaml = XDocument.Load(MainWindowXamlPath());

        IReadOnlyList<string> gates = GatesAbove(xaml, "SessionTreeView");

        Assert.Contains(SessionsShellTabGate, gates);
        Assert.Equal(2, gates.Count(g => g == SessionSidebarGate));
    }

    [Fact]
    public void TheSessionListSteps_AreTheOnesThatOpenTheSidebar()
    {
        string[] inSidebar = OnboardingFlowViewModel.Steps
            .Where(s => s.InSessionSidebar)
            .Select(s => s.TargetElementName!)
            .ToArray();

        Assert.Equal(["SessionTreeView", "AddButton", "Mw_FilterBox"], inSidebar);
    }

    [Theory]
    [InlineData(false, 1, 0)]
    [InlineData(true, 1, 1)]
    public void ASessionListStep_OpensTheSessionsSidebarAndShowsAHiddenSidebar(
        bool sidebarHidden, int expectedTabOpens, int expectedShows)
    {
        int tabOpens = 0;
        int shows = 0;

        MainWindow.RevealOnboardingSessionSidebar(
            OnboardingFlowViewModel.Steps[0], sidebarHidden, () => tabOpens++, () => shows++);

        Assert.Equal(expectedTabOpens, tabOpens);

        // Showing is a toggle underneath, so a visible sidebar must be left alone: toggling it
        // would hide the very list the step is about to point at.
        Assert.Equal(expectedShows, shows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStepOutsideTheSessionList_LeavesTheSidebarAsTheUserSetIt(bool sidebarHidden)
    {
        OnboardingFlowViewModel.Step toolbarStep = Assert.Single(
            OnboardingFlowViewModel.Steps, s => s.TargetElementName == "QuickConnectButton");
        int calls = 0;

        MainWindow.RevealOnboardingSessionSidebar(toolbarStep, sidebarHidden, () => calls++, () => calls++);
        MainWindow.RevealOnboardingSessionSidebar(null, sidebarHidden, () => calls++, () => calls++);

        Assert.Equal(0, calls);
    }

    // The reveal has to run before the spotlight is measured, or the first measure still finds the
    // list collapsed and the step falls back to the centred card.
    [Fact]
    public void TheShell_RevealsTheSidebarBeforeMeasuringTheTarget()
    {
        string logic = ViewSource.HandlerBody(
            ViewSource.WithoutCommentsAndLiterals(File.ReadAllText(
                Path.Combine(RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml.cs"))),
            ApplyStepMember);

        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, RevealStatement),
            "revealing the session sidebar is not a step of ApplyOnboardingStep");
        Assert.True(
            ViewSource.IsStatementOfTheMethodBody(logic, MeasureStatement),
            "measuring the target is not a step of ApplyOnboardingStep");
        Assert.True(
            logic.IndexOf(RevealStatement, StringComparison.Ordinal)
                < logic.IndexOf(MeasureStatement, StringComparison.Ordinal),
            "ApplyOnboardingStep measures the target before it reveals the session sidebar.");
    }

    [Fact]
    public void EveryStep_HasBothItsStringsInBothLocales()
    {
        foreach (string locale in new[] { "en", "fr" })
        {
            using JsonDocument document = ReadLocale(locale);

            foreach (OnboardingFlowViewModel.Step step in OnboardingFlowViewModel.Steps)
            {
                foreach (string key in new[] { step.TitleKey, step.BodyKey })
                {
                    Assert.True(
                        document.RootElement.TryGetProperty(key, out JsonElement value),
                        $"Onboarding step key '{key}' is missing from {locale}.json");
                    Assert.False(
                        string.IsNullOrWhiteSpace(value.GetString()),
                        $"Onboarding step key '{key}' is empty in {locale}.json");
                }
            }
        }
    }

    // A tab name the shell does not handle would leave the step pointing into a tab that was
    // never opened, which is the failure the navigate-first ordering exists to prevent.
    [Fact]
    public void EveryDeclaredShellTab_IsOneTheShellCanOpen()
    {
        string[] known = [ShellTab.Sessions, ShellTab.Tools];

        foreach (OnboardingFlowViewModel.Step step in OnboardingFlowViewModel.Steps)
        {
            if (step.ShellTab is null)
            {
                continue;
            }

            Assert.Contains(step.ShellTab, known);
        }
    }

    [Fact]
    public void TheTour_HasMoreThanTheThreeStepsItStartedWith()
    {
        // Guards the theories above against becoming vacuous, and pins the decision that the
        // tour goes further into the product than the original three generic cards.
        Assert.True(
            OnboardingFlowViewModel.StepCount > 3,
            $"The tour has {OnboardingFlowViewModel.StepCount} steps.");
        Assert.Contains(OnboardingFlowViewModel.Steps, s => s.TargetElementName is not null);
    }

    private const string SessionSidebarGate = "the session sidebar";
    private const string SessionsShellTabGate = "the Sessions tab";
    private const string ToolsShellTabGate = "the Tools tab";

    private const string ApplyStepMember = "private void ApplyOnboardingStep()";
    private const string RevealStatement = "RevealOnboardingSessionSidebar(";
    private const string MeasureStatement =
        "Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => SpotlightOnboardingTarget(step)));";

    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Every gate that can collapse the named element: a bound or fixed Visibility on it or on any
    /// ancestor, and the sidebar column that Ctrl+B collapses to zero width.
    /// </summary>
    private static IReadOnlyList<string> GatesAbove(XDocument xaml, string elementName)
    {
        XElement target = Assert.Single(
            xaml.Descendants(), e => (string?)e.Attribute(XamlNamespace + "Name") == elementName);
        List<string> gates = [];

        // Property elements such as <Grid.ColumnDefinitions> are not part of the visual chain.
        foreach (XElement element in target.AncestorsAndSelf().Where(e => !e.Name.LocalName.Contains('.')))
        {
            if ((string?)element.Attribute("Visibility") is { } visibility
                && !string.Equals(visibility, "Visible", StringComparison.Ordinal))
            {
                gates.Add(ClassifyVisibility(visibility));
            }

            if (IsInSessionTreeColumn(element))
            {
                gates.Add(SessionSidebarGate);
            }
        }

        return gates;
    }

    private static string ClassifyVisibility(string visibility)
    {
        if (visibility.Contains("ElementName=SidebarTabSessions", StringComparison.Ordinal))
        {
            return SessionSidebarGate;
        }

        if (visibility.Contains("IsSessionsTabSelected", StringComparison.Ordinal))
        {
            return SessionsShellTabGate;
        }

        if (visibility.Contains("IsToolsTabSelected", StringComparison.Ordinal))
        {
            return ToolsShellTabGate;
        }

        // Unknown to the tour, so no step can declare it opens it.
        return $"Visibility=\"{visibility}\"";
    }

    private static bool IsInSessionTreeColumn(XElement element)
    {
        if (element.Parent is not { } grid || grid.Name.LocalName != "Grid")
        {
            return false;
        }

        XElement[] columns = grid
            .Elements(grid.Name.Namespace + "Grid.ColumnDefinitions")
            .Elements()
            .ToArray();
        int column = int.TryParse((string?)element.Attribute("Grid.Column"), out int parsed) ? parsed : 0;

        return column < columns.Length
            && (string?)columns[column].Attribute(XamlNamespace + "Name") == "SessionTreeColumn";
    }

    private static string MainWindowXamlPath() =>
        Path.Combine(RepoRoot(), "src", "Heimdall.App", "MainWindow.xaml");

    private static JsonDocument ReadLocale(string locale) =>
        JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "locales", $"{locale}.json")));

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
