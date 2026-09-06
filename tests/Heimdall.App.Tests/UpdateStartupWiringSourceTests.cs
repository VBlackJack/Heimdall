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
using Heimdall.App.Tests.Views.EmbeddedRdp;

namespace Heimdall.App.Tests;

/// <summary>
/// The main window's wiring of the update banner, read through the statement predicate:
/// the runtime settings bridge before the network round trip, the startup work cancelled
/// with the window, the banner re-localized with the rest, and suppressed in fullscreen.
/// The behaviour behind each site is tested on the view model; what is read here is only
/// that the site goes through it.
/// </summary>
public sealed class UpdateStartupWiringSourceTests
{
    private const string FinishMember = "private async Task FinishStartupUpdatesAsync(MainViewModel viewModel)";
    private const string BridgeStatement = "_settingsRuntimeBridgeInitialized = true;";
    private const string ReportStatement = "await ReportPreviousUpdateAttemptAsync(viewModel);";
    private const string CheckStatement = "await CheckForUpdatesOnStartupAsync(viewModel);";

    private const string ClosedMember = "protected override void OnClosed(EventArgs e)";
    private const string CancelStatement = "_startupUpdateCts.Cancel();";

    private const string RefreshMember = "private static void RefreshVmDrivenLocalization(MainViewModel vm)";
    private const string RefreshStatement = "vm.Update.RefreshLocalization();";

    private const string ToggleMember = "private void ToggleFullscreen()";
    private const string SuppressStatement = "vm.Update.IsSuppressedByFullscreen = _uiState.IsFullscreen;";

    /// <remarks>
    /// The Loaded handler awaited the update check (a 30 s HTTP ceiling) before setting
    /// the bridge flag; while it was false a runtime setting toggled in that window was
    /// silently not applied.
    /// </remarks>
    [Fact]
    public void StartupUpdates_InitialiseTheRuntimeBridgeBeforeTheNetworkRoundTrip()
    {
        string logic = Logic("MainWindow.xaml.cs", FinishMember);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, BridgeStatement), "the bridge flag is not a step of the startup update work");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, ReportStatement), "the previous-attempt report is not a step of the startup update work");
        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, CheckStatement), "the startup check is not a step of the startup update work");

        int bridge = logic.IndexOf(BridgeStatement, StringComparison.Ordinal);
        Assert.True(bridge < logic.IndexOf(ReportStatement, StringComparison.Ordinal), "the bridge is initialised after the report");
        Assert.True(bridge < logic.IndexOf(CheckStatement, StringComparison.Ordinal), "the bridge is initialised after the check");
    }

    [Fact]
    public void ClosingTheWindow_CancelsTheStartupUpdateWork()
    {
        string logic = Logic("MainWindow.xaml.cs", ClosedMember);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, CancelStatement), "OnClosed does not cancel the startup update work");
    }

    [Fact]
    public void LocaleChange_RefreshesTheBannerStatus()
    {
        string logic = Logic("MainWindow.xaml.cs", RefreshMember);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, RefreshStatement), "the banner status is not re-localized with the rest");
    }

    [Fact]
    public void Fullscreen_SuppressesTheBannerEitherWay()
    {
        string logic = Logic("MainWindow.WindowUI.cs", ToggleMember);

        Assert.True(ViewSource.IsStatementOfTheMethodBody(logic, SuppressStatement), "the fullscreen toggle does not update the banner suppression");
    }

    private static string Logic(string relativePath, string signature)
        => ViewSource.HandlerBody(ViewSource.WithoutCommentsAndLiterals(ReadAppSource(relativePath)), signature);

    private static string ReadAppSource(string relativePath)
    {
        string full = Path.Combine(ViewSource.RepoRoot(), "src", "Heimdall.App", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Source not found: {full}");
        return File.ReadAllText(full);
    }
}
