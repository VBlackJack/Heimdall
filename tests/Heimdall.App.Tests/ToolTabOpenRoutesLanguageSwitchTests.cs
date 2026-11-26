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

using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandPalette;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// A tool tab follows an interface language switch whichever route opened it (the palette, another
/// tool), a tab named after user data is left alone, and the session tabs' own accessible names are
/// worded again by the shell refresh.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    private const string RouteOpenToolMethodName = "OpenToolFromPaletteAsync";
    private const string RoutePaletteArgument = "10.0.0.1";
    private const string RouteServerName = "web-01";
    private const string RouteCloseNameKey = "A11yCloseSessionTabNamed";

    private static async Task RouteOpenFromPaletteAsync(MainViewModel main, ServerItemViewModel item)
    {
        MethodInfo? method = typeof(CommandPaletteViewModel).GetMethod(
            RouteOpenToolMethodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        await (Task)method!.Invoke(main.CommandPalette, [item])!;
    }

    [Fact]
    public async Task PaletteToolTab_PlainLabel_IsRetitledAfterALanguageSwitch()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        ToolDescriptor ping = SwitchPingDescriptor(main);
        string english = SwitchPingLabel(main);

        await RouteOpenFromPaletteAsync(main, new ServerItemViewModel { Id = "tool-ping", DisplayName = english });
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);
        Assert.Equal(english, tab.Title);

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        string french = ping.ResolveLabel(localizer.GetString);
        Assert.NotEqual(english, french);
        Assert.Equal(french, tab.Title);
    }

    [Fact]
    public async Task PaletteToolTab_WithATypedArgument_IsRetitledWithThatArgumentAfterALanguageSwitch()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        ToolDescriptor ping = SwitchPingDescriptor(main);
        string english = localizer.Format(ping.LabelWithArgKey!, RoutePaletteArgument);

        await RouteOpenFromPaletteAsync(
            main,
            new ServerItemViewModel { Id = $"tool-ping|{RoutePaletteArgument}", DisplayName = english });
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);
        Assert.Equal(english, tab.Title);

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        string french = localizer.Format(ping.LabelWithArgKey!, RoutePaletteArgument);
        Assert.NotEqual(english, french);
        Assert.Equal(french, tab.Title);
    }

    [Fact]
    public async Task CommandLibraryTab_OpenedFromASnippet_IsRetitledAfterALanguageSwitch()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        FieldInfo? field = typeof(CommandPaletteViewModel).GetField(
            "_detailAction",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(main.CommandPalette, new TwinShell.Core.Models.Action { Id = "act-xyz", Title = "Demo" });
        harness.EmbeddedSessionManager.CreateToolControlBehavior = (_, _, _, _) => new object();
        await main.CommandPalette.OpenSnippetInLibraryCommand.ExecuteAsync(null);
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);
        string english = tab.Title;
        Assert.Equal(localizer["PaletteToolCmdLib"], english);

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        Assert.NotEqual(english, tab.Title);
        Assert.Equal(localizer["PaletteToolCmdLib"], tab.Title);
    }

    [Fact]
    public async Task ToolTab_OpenedFromAnotherTool_IsRetitledAfterALanguageSwitch()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        ToolDescriptor ping = SwitchPingDescriptor(main);
        string english = SwitchPingLabel(main);

        await harness.EmbeddedSessionManager.OpenToolCallback!(SwitchPingToolId, english, new ToolContext(TargetHost: RoutePaletteArgument));
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        string french = ping.ResolveLabel(localizer.GetString);
        Assert.NotEqual(english, french);
        Assert.Equal(french, tab.Title);
    }

    [Fact]
    public async Task ToolTab_OpenedForAServerEntry_KeepsTheServerName()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();

        await main.OpenToolTabAsync(SwitchPingToolId, RouteServerName, new ToolContext(TargetHost: RoutePaletteArgument));
        SessionTabViewModel tab = Assert.Single(main.Connection.ActiveSessions);

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        Assert.Equal(RouteServerName, tab.Title);
    }

    [Fact]
    public async Task SessionTab_CloseAccessibleName_IsReworded_AfterALanguageSwitch()
    {
        using TestHarness harness = TestHarness.Create();
        MainViewModel main = harness.Main;
        LocalizationManager localizer = main.GetLocalizer();
        SessionTabViewModel tab = main.Connection.AddSession("srv-1", RouteServerName, "SSH");
        string english = localizer.Format(RouteCloseNameKey, RouteServerName);
        Assert.Equal(english, tab.CloseAccessibleName);

        await localizer.SwitchLocaleAsync(SwitchFrenchLocale);

        string french = localizer.Format(RouteCloseNameKey, RouteServerName);
        Assert.NotEqual(english, french);
        Assert.Equal(french, tab.CloseAccessibleName);
    }
}
