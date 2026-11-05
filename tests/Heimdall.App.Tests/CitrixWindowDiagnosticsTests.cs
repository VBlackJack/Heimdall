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
    private const int IcaPid = 34372;
    private const int WorkspacePid = 18232;
    private const string SeamlessClass = CitrixApplicationWindowSelector.SeamlessWindowClassName;

    // Window titles and handles measured on Polaris, Citrix Workspace 2511, French locale.
    private const string MeasuredTitle = "MobaXterm- \\\\Distant";
    private const string MeasuredMainTitle = "MobaXterm Professional- \\\\Distant";
    private const string MeasuredLaunchTitle = "MobaXterm - Citrix Workspace";
    private const IntPtr AnchorHwnd = 0x4D09C0;
    private const IntPtr MainHwnd = 0x2F1130;
    private const IntPtr CommonOwnerHwnd = 0x190FFC;

    [Theory]
    [InlineData(MeasuredTitle)]
    [InlineData("MobaXterm- \\\\Remote")]
    [InlineData("mobaxterm- \\\\Remoto")]
    [InlineData("  MobaXterm- \\\\Distant  ")]
    public void TitleMatcher_AcceptsTheAnchorWhateverTheLocalizedSuffix(string title)
    {
        Assert.True(CitrixWindowTitleMatcher.IsApplicationWindowTitle(title, Application));
    }

    [Theory]
    [InlineData(MeasuredLaunchTitle)]
    [InlineData(MeasuredMainTitle)]
    [InlineData("MobaXtermX- \\\\Distant")]
    [InlineData("Notepad- \\\\Distant")]
    [InlineData("UsrLogon.cmd- \\\\Distant")]
    [InlineData("MobaXterm")]
    [InlineData("Citrix Workspace")]
    [InlineData("")]
    [InlineData(null)]
    public void TitleMatcher_RefusesEverythingButTheAnchor(string? title)
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
    public void Selector_MeasuredPolarisTree_EmbedsTheWindowOwnedByTheAnchor()
    {
        CitrixWindowRecord? selected = CitrixApplicationWindowSelector.Select(MeasuredTree(), new HashSet<IntPtr>(), Application);

        Assert.NotNull(selected);
        Assert.Equal(MainHwnd, selected.Value.Hwnd);
    }

    [Fact]
    public void Selector_AnchorWithoutItsWindowYet_Waits()
    {
        List<CitrixWindowRecord> tree = MeasuredTree().Where(window => window.Hwnd != MainHwnd).ToList();

        Assert.Null(CitrixApplicationWindowSelector.Select(tree, new HashSet<IntPtr>(), Application));
    }

    [Fact]
    public void Selector_TwoApplicationsInOneIcaProcess_PicksEachByItsAnchor()
    {
        List<CitrixWindowRecord> tree =
        [
            .. MeasuredTree(),
            Window(0x60, IcaPid, "Notepad- \\\\Distant", 0, 0),
            Window(0x61, IcaPid, "Untitled - Notepad- \\\\Distant", 640, 480, owner: 0x60),
        ];

        Assert.Equal(MainHwnd, CitrixApplicationWindowSelector.Select(tree, new HashSet<IntPtr>(), Application)?.Hwnd);
        Assert.Equal(0x61, CitrixApplicationWindowSelector.Select(tree, new HashSet<IntPtr>(), "Notepad")?.Hwnd);
    }

    [Fact]
    public void Selector_AnchorOpenBeforeTheLaunch_IsNotThisLaunchsApplication()
    {
        Assert.Null(CitrixApplicationWindowSelector.Select(MeasuredTree(), new HashSet<IntPtr> { AnchorHwnd }, Application));
    }

    [Fact]
    public void Selector_WorkspaceShellTitledLikeTheAnchor_IsNeverTheApplication()
    {
        CitrixWindowRecord shell = Window(0x30, WorkspacePid, MeasuredTitle, 0, 0, processName: "SelfService");
        CitrixWindowRecord shellChild = Window(0x31, WorkspacePid, "Sign in", 800, 600, owner: 0x30, processName: "SelfService");

        Assert.Null(CitrixApplicationWindowSelector.Select([shell, shellChild], new HashSet<IntPtr>(), Application));
    }

    [Fact]
    public void Selector_HiddenOwnedWindow_IsNotEmbedded()
    {
        List<CitrixWindowRecord> tree = MeasuredTree()
            .Select(window => window.Hwnd == MainHwnd ? window with { IsVisible = false } : window)
            .ToList();

        Assert.Null(CitrixApplicationWindowSelector.Select(tree, new HashSet<IntPtr>(), Application));
    }

    [Fact]
    public void Selector_WindowNeverAppeared_ReturnsNothing()
    {
        Assert.Null(CitrixApplicationWindowSelector.Select([], new HashSet<IntPtr>(), Application));
    }

    [Fact]
    public void TraceDiff_ReportsAppearedChangedAndGone_OncePerWindow()
    {
        CitrixWindowRecord stays = Window(0x10, IcaPid, MeasuredTitle);
        CitrixWindowRecord goes = Window(0x20, WorkspacePid, "Citrix Workspace", processName: "SelfService");
        CitrixWindowRecord retitled = stays with { Title = "MobaXterm- \\\\Distant (2)" };
        CitrixWindowRecord arrives = Window(0x40, IcaPid, "Dialog");

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

    // The wfica32 windows of the Polaris launch that matter to the choice, as logged.
    private static List<CitrixWindowRecord> MeasuredTree() =>
    [
        Window(0x850A9A, IcaPid, MeasuredLaunchTitle, 199, 34, className: "wMFService000406F0000"),
        Window(CommonOwnerHwnd, IcaPid, "TWI Common Owner Window", 166, 47, isVisible: false),
        Window(0x1B1068, IcaPid, "TF_FloatingLangBar_WndTitle- \\\\Distant", 190, 26, owner: CommonOwnerHwnd),
        Window(0xC71232, IcaPid, "PuTTY sessions- \\\\Distant", 142, 31, owner: CommonOwnerHwnd),
        Window(AnchorHwnd, IcaPid, MeasuredTitle, 0, 0),
        Window(0xC21232, IcaPid, string.Empty, 1, 1, owner: AnchorHwnd),
        Window(MainHwnd, IcaPid, MeasuredMainTitle, 1136, 663, owner: AnchorHwnd),
    ];

    private static CitrixWindowRecord Window(
        IntPtr hwnd,
        int pid,
        string title,
        int width = 800,
        int height = 600,
        IntPtr owner = 0,
        bool isVisible = true,
        string processName = "wfica32",
        string className = SeamlessClass)
        => new(hwnd, pid, processName, title, className, 0, 0, owner, isVisible, width, height);
}
