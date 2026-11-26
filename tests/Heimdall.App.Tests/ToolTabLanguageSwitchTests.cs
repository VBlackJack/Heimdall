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
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// An open tool tab follows an interface language switch: its title and its ready status are worded
/// again, while a name somebody gave it is left alone.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    private const string SwitchPingToolId = "PING";
    private const string SwitchFrenchLocale = "fr";
    private const string SwitchUserGivenName = "My own tab";
    private const string SwitchLaterStatus = "Written by another feature";
    private const string SwitchRecipeToolId = ToolDescriptor.RecipeIdPrefix + "acme.ping";

    private static ToolDescriptor SwitchPingDescriptor(MainViewModel main) => main.ToolRegistry.GetById(SwitchPingToolId)!;

    private static string SwitchPingLabel(MainViewModel main) => SwitchPingDescriptor(main).ResolveLabel(main.GetLocalizer().GetString);

    private static void SwitchRegisterProbeRecipe(MainViewModel main)
        => main.ToolRegistry.RegisterRecipeTools(
            [RecipeTestFiles.Recipe("acme.ping", "Ping")],
            _ => throw new InvalidOperationException("never built"));

    [Fact]
    public async Task ToolTab_OpenedWithATitleWording_IsRetitledAfterALanguageSwitch()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        ToolDescriptor ping = SwitchPingDescriptor(main);
        string english = SwitchPingLabel(main);
        await main.OpenToolTabAsync(SwitchPingToolId, english, null, titleWording: () => ping.ResolveLabel(localizer.GetString));
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);
        List<string?> raised = [];
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        string french = ping.ResolveLabel(localizer.GetString);
        Assert.NotEqual(english, french);
        Assert.Equal(french, tab.Title);
        Assert.Equal(french, tab.DisplayTitle);
        Assert.Contains(nameof(SessionTabViewModel.Title), raised);
    }

    [Fact]
    public async Task RecipeToolTab_WithProvenance_IsRetitledAfterALanguageSwitch()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        SwitchRegisterProbeRecipe(main);
        ToolDescriptor recipe = main.ToolRegistry.GetById(SwitchRecipeToolId)!;
        await main.OpenToolTabAsync(recipe.Id, "a requested title", null);
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);
        string english = RecipeToolText.TabTitle(recipe, localizer.GetString);
        Assert.Equal(english, tab.Title);

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        string french = RecipeToolText.TabTitle(recipe, localizer.GetString);
        Assert.NotEqual(english, french);
        Assert.Equal(french, tab.Title);
    }

    [Fact]
    public async Task ToolTab_OpenedWithAUserGivenNameEqualToTheToolLabel_IsNotRetitled()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        string english = SwitchPingLabel(main);
        await main.OpenToolTabAsync(SwitchPingToolId, english, null);
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        Assert.Equal(english, tab.Title);
    }

    [Fact]
    public async Task ToolTab_ThatTheUserRenamed_KeepsTheirName()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        ToolDescriptor ping = SwitchPingDescriptor(main);
        await main.OpenToolTabAsync(SwitchPingToolId, SwitchPingLabel(main), null, titleWording: () => ping.ResolveLabel(localizer.GetString));
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);
        tab.CustomTitle = SwitchUserGivenName;

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        Assert.Equal(SwitchUserGivenName, tab.DisplayTitle);
        Assert.Equal(ping.ResolveLabel(localizer.GetString), tab.Title);
    }

    [Fact]
    public async Task ToolTab_WhoseTitleWasSetLaterByOtherCode_IsNotOverwritten()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        ToolDescriptor ping = SwitchPingDescriptor(main);
        await main.OpenToolTabAsync(SwitchPingToolId, SwitchPingLabel(main), null, titleWording: () => ping.ResolveLabel(localizer.GetString));
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);
        tab.Title = SwitchUserGivenName;

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        Assert.Equal(SwitchUserGivenName, tab.Title);
    }

    [Fact]
    public async Task Split_WhereAUserSessionBecomesPrimary_KeepsTheServerNameAndRewordsTheToolPane()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        ToolDescriptor ping = SwitchPingDescriptor(main);
        await main.OpenToolTabAsync(SwitchPingToolId, SwitchPingLabel(main), null, titleWording: () => ping.ResolveLabel(localizer.GetString));
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);
        SessionPaneModel toolPane = tab.PrimaryPane;
        SessionPaneModel serverPane = new()
        {
            ServerId = "srv-1",
            ConnectionType = "SSH",
            Title = "web-01",
            Status = SessionStatusTokens.Connected
        };
        tab.SetRootContent(new SplitContainerModel
        {
            First = serverPane,
            Second = toolPane,
            Orientation = SplitOrientation.Vertical,
            SplitRatio = SplitContainerModel.DefaultRatio
        });
        Assert.Same(serverPane, tab.PrimaryPane);

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        Assert.Equal("web-01", serverPane.Title);
        Assert.Equal(SessionStatusTokens.Connected, serverPane.Status);
        Assert.Equal(ping.ResolveLabel(localizer.GetString), toolPane.Title);
        Assert.Equal(localizer["StatusReady"], toolPane.Status);
    }

    [Fact]
    public async Task ToolTab_ReadyStatus_IsReworded_AndTheStatusBarFollows()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        ToolDescriptor ping = SwitchPingDescriptor(main);
        await main.OpenToolTabAsync(SwitchPingToolId, SwitchPingLabel(main), null, titleWording: () => ping.ResolveLabel(localizer.GetString));
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);
        main.Connection.ActiveSession = tab;
        string englishReady = tab.Status;
        Assert.Contains(englishReady, main.StatusText, StringComparison.Ordinal);

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        Assert.NotEqual(englishReady, tab.Status);
        Assert.Equal(localizer["StatusReady"], tab.Status);
        Assert.Equal(localizer.Format("StatusSessionState", tab.Title, tab.Status), main.StatusText);
    }

    [Fact]
    public async Task ToolTab_StatusWrittenLaterByOtherCode_IsNotOverwritten()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        await main.OpenToolTabAsync(SwitchPingToolId, SwitchPingLabel(main), null);
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);
        tab.Status = SwitchLaterStatus;

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        Assert.Equal(SwitchLaterStatus, tab.Status);
    }

    [Fact]
    public async Task ApplicationStatus_SetBeforeALanguageSwitch_SurvivesItWhileAToolTabIsDisplayed()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        ToolDescriptor ping = SwitchPingDescriptor(main);
        await main.OpenToolTabAsync(SwitchPingToolId, SwitchPingLabel(main), null, titleWording: () => ping.ResolveLabel(localizer.GetString));
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);
        main.Connection.ActiveSession = tab;
        const string refusal = "A feature refused something";
        main.StatusText = refusal;

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        Assert.Equal(ping.ResolveLabel(localizer.GetString), tab.Title);
        Assert.Equal(refusal, main.StatusText);
    }

    [Fact]
    public async Task SplitToolPane_CreatedBySplitService_IsRetitledAfterALanguageSwitch()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        SwitchRegisterProbeRecipe(main);
        SessionTabViewModel tab = main.Connection.AddSession("srv-1", "web-01", "SSH");

        Assert.True(main.Split.SplitSessionWithTool(tab, SwitchPingToolId, SplitOrientation.Vertical));
        Assert.True(main.Split.SplitSessionWithTool(tab, SwitchRecipeToolId, SplitOrientation.Vertical));
        List<SessionPaneModel> toolPanes = SplitTreeHelper.EnumerateLeaves(tab.RootContent)
            .Where(p => p.ConnectionType.StartsWith("TOOL:", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, toolPanes.Count);

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        ToolDescriptor ping = SwitchPingDescriptor(main);
        ToolDescriptor recipe = main.ToolRegistry.GetById(SwitchRecipeToolId)!;
        Assert.Contains(toolPanes, p => p.Title == RecipeToolText.TabTitle(ping, localizer.GetString));
        Assert.Contains(toolPanes, p => p.Title == RecipeToolText.TabTitle(recipe, localizer.GetString));
        Assert.All(toolPanes, p => Assert.Equal(localizer["StatusReady"], p.Status));
        Assert.Equal("web-01", tab.Title);
    }

    [Fact]
    public async Task ToolTab_WhoseTitleWordingThrows_DoesNotStopTheOtherTabsNorTheStatusRefresh()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        ToolDescriptor ping = SwitchPingDescriptor(main);
        bool armed = false;
        await main.OpenToolTabAsync(SwitchPingToolId, SwitchPingLabel(main), null, titleWording: () => armed
            ? throw new InvalidOperationException("broken wording")
            : ping.ResolveLabel(localizer.GetString));
        SessionTabViewModel broken = Assert.Single(main.Connection.ActiveSessions);
        string brokenTitle = broken.Title;
        await main.OpenToolTabAsync(SwitchPingToolId, SwitchPingLabel(main), null, titleWording: () => ping.ResolveLabel(localizer.GetString));
        SessionTabViewModel healthy = main.Connection.ActiveSessions.Single(t => !ReferenceEquals(t, broken));
        main.Connection.ActiveSession = healthy;
        armed = true;

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        Assert.Equal(brokenTitle, broken.Title);
        Assert.Equal(ping.ResolveLabel(localizer.GetString), healthy.Title);
        Assert.Equal(localizer["StatusReady"], healthy.Status);
        Assert.Equal(localizer.Format("StatusSessionState", healthy.Title, healthy.Status), main.StatusText);
    }
}
