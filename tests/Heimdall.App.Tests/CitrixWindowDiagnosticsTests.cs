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

using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The Polaris spike: an application launched from the SelfService cache opens in a wfica32
/// window titled after it, which may share its process with other applications. These tests pin
/// how that window is recognized and how the diagnostic trace reports window changes.
/// </summary>
public sealed class CitrixWindowDiagnosticsTests
{
    private const string Application = "MobaXterm";
    private const int IcaPid = 5150;
    private const int WorkspacePid = 6160;

    // The title measured on the test workstation, Citrix Workspace 2511, French locale.
    private const string MeasuredTitle = "MobaXterm- \\\\Distant";

    [Theory]
    [InlineData(MeasuredTitle)]
    [InlineData("MobaXterm - \\\\Remote")]
    [InlineData("mobaxterm- \\\\Remoto")]
    [InlineData("  MobaXterm  ")]
    public void TitleMatcher_AcceptsTheApplicationWhateverTheLocalizedSuffix(string title)
    {
        Assert.True(CitrixWindowTitleMatcher.IsApplicationWindowTitle(title, Application));
    }

    [Theory]
    [InlineData("MobaXterm Pro- \\\\Distant")]
    [InlineData("MobaXtermX- \\\\Distant")]
    [InlineData("Notepad- \\\\Distant")]
    [InlineData("Citrix Workspace")]
    [InlineData("")]
    [InlineData(null)]
    public void TitleMatcher_RefusesAnotherApplication(string? title)
    {
        Assert.False(CitrixWindowTitleMatcher.IsApplicationWindowTitle(title, Application));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TitleMatcher_WithoutAnApplicationName_MatchesNothing(string? applicationName)
    {
        Assert.False(CitrixWindowTitleMatcher.IsApplicationWindowTitle(MeasuredTitle, applicationName));
    }

    [Fact]
    public void Selector_TwoApplicationsInOneIcaProcess_PicksEachByItsTitle()
    {
        CitrixWindowRecord moba = Window(0x10, IcaPid, "wfica32", MeasuredTitle);
        CitrixWindowRecord notepad = Window(0x20, IcaPid, "wfica32", "Notepad- \\\\Distant");

        IReadOnlyList<CitrixWindowRecord> forMoba = CitrixApplicationWindowSelector.Select([moba, notepad], new HashSet<IntPtr>(), Application);
        IReadOnlyList<CitrixWindowRecord> forNotepad = CitrixApplicationWindowSelector.Select([moba, notepad], new HashSet<IntPtr>(), "Notepad");

        Assert.Equal(0x10, Assert.Single(forMoba).Hwnd);
        Assert.Equal(0x20, Assert.Single(forNotepad).Hwnd);
    }

    [Fact]
    public void Selector_WindowOpenBeforeTheLaunch_IsNotThisLaunchsWindow()
    {
        CitrixWindowRecord earlier = Window(0x10, IcaPid, "wfica32", MeasuredTitle);

        IReadOnlyList<CitrixWindowRecord> matches = CitrixApplicationWindowSelector.Select(
            [earlier],
            new HashSet<IntPtr> { 0x10 },
            Application);

        Assert.Empty(matches);
    }

    [Fact]
    public void Selector_WorkspaceShellWindowNamedLikeTheApplication_IsNeverTheSession()
    {
        // The Workspace sign-in window is the one the default capture wrongly embedded. Even
        // titled like the application, it is not an ICA window.
        CitrixWindowRecord shell = Window(0x30, WorkspacePid, "SelfService", MeasuredTitle);

        Assert.Empty(CitrixApplicationWindowSelector.Select([shell], new HashSet<IntPtr>(), Application));
    }

    [Fact]
    public void Selector_HiddenWindow_IsNotEmbedded()
    {
        CitrixWindowRecord hidden = Window(0x10, IcaPid, "wfica32", MeasuredTitle, isVisible: false);

        Assert.Empty(CitrixApplicationWindowSelector.Select([hidden], new HashSet<IntPtr>(), Application));
    }

    [Fact]
    public void Selector_WindowNeverAppeared_ReturnsNothing()
    {
        Assert.Empty(CitrixApplicationWindowSelector.Select([], new HashSet<IntPtr>(), Application));
    }

    [Fact]
    public void TraceDiff_ReportsAppearedChangedAndGone_OncePerWindow()
    {
        CitrixWindowRecord stays = Window(0x10, IcaPid, "wfica32", MeasuredTitle);
        CitrixWindowRecord goes = Window(0x20, WorkspacePid, "SelfService", "Citrix Workspace");
        CitrixWindowRecord retitled = stays with { Title = "MobaXterm- \\\\Distant (2)" };
        CitrixWindowRecord arrives = Window(0x40, IcaPid, "wfica32", "Dialog");

        IReadOnlyList<string> first = CitrixWindowTraceDiff.Compare(
            new Dictionary<IntPtr, CitrixWindowRecord>(),
            [stays, goes],
            out Dictionary<IntPtr, CitrixWindowRecord> afterFirst);
        IReadOnlyList<string> unchanged = CitrixWindowTraceDiff.Compare(afterFirst, [stays, goes], out afterFirst);
        IReadOnlyList<string> second = CitrixWindowTraceDiff.Compare(afterFirst, [retitled, arrives], out _);

        Assert.Equal(2, first.Count);
        Assert.All(first, line => Assert.StartsWith(CitrixWindowTraceDiff.AppearedTag, line, StringComparison.Ordinal));
        Assert.Empty(unchanged);
        Assert.Equal(3, second.Count);
        Assert.Contains(second, line => line.StartsWith(CitrixWindowTraceDiff.ChangedTag, StringComparison.Ordinal) && line.Contains("hwnd=0x10", StringComparison.Ordinal));
        Assert.Contains(second, line => line.StartsWith(CitrixWindowTraceDiff.AppearedTag, StringComparison.Ordinal) && line.Contains("hwnd=0x40", StringComparison.Ordinal));
        Assert.Contains(second, line => line.StartsWith(CitrixWindowTraceDiff.GoneTag, StringComparison.Ordinal) && line.Contains("hwnd=0x20", StringComparison.Ordinal));
    }

    [Fact]
    public void Describe_CarriesEveryFieldTheSpikeAsksFor()
    {
        CitrixWindowRecord window = new(0x10, IcaPid, "wfica32", MeasuredTitle, "Transparent Windows Client", 0x16CF0000, 0x00040100, 0x99, true, 800, 600);

        string line = window.Describe();

        Assert.Contains("hwnd=0x10", line, StringComparison.Ordinal);
        Assert.Contains($"pid={IcaPid}", line, StringComparison.Ordinal);
        Assert.Contains("process=wfica32", line, StringComparison.Ordinal);
        Assert.Contains($"title=\"{MeasuredTitle}\"", line, StringComparison.Ordinal);
        Assert.Contains("class=\"Transparent Windows Client\"", line, StringComparison.Ordinal);
        Assert.Contains("style=0x16CF0000", line, StringComparison.Ordinal);
        Assert.Contains("exStyle=0x00040100", line, StringComparison.Ordinal);
        Assert.Contains("owner=0x99", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Options_DefaultSettings_LeaveDiagnosticsOff()
    {
        Assert.False(CitrixWindowDiagnosticsOptions.FromSettings(new AppSettings()).Enabled);
        Assert.False(CitrixWindowDiagnosticsOptions.FromSettings(null).Enabled);
        Assert.False(CitrixWindowDiagnosticsOptions.Disabled.Enabled);
    }

    [Fact]
    public void Options_ReadTheSettingsAndClampToTheirRanges()
    {
        SettingRange waitRange = SettingRanges.Of(nameof(AppSettings.CitrixSessionWindowWaitSeconds));
        SettingRange intervalRange = SettingRanges.Of(nameof(AppSettings.CitrixWindowDiagnosticsIntervalMs));
        AppSettings settings = new()
        {
            CitrixWindowDiagnosticsEnabled = true,
            CitrixWindowDiagnosticsDurationSeconds = 120,
            CitrixWindowDiagnosticsIntervalMs = intervalRange.Min - 1,
            CitrixSessionWindowWaitSeconds = waitRange.Max + 1,
        };

        CitrixWindowDiagnosticsOptions options = CitrixWindowDiagnosticsOptions.FromSettings(settings);

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(120), options.TraceDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(intervalRange.Min), options.TraceInterval);
        Assert.Equal(TimeSpan.FromSeconds(waitRange.Max), options.SessionWindowWait);
    }

    private static CitrixWindowRecord Window(
        IntPtr hwnd,
        int pid,
        string processName,
        string title,
        bool isVisible = true)
        => new(hwnd, pid, processName, title, "Transparent Windows Client", 0, 0, IntPtr.Zero, isVisible, 800, 600);
}
