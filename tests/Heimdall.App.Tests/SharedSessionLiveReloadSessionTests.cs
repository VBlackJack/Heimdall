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

using System.Windows;
using System.Windows.Controls;
using Heimdall.App.Controls;
using Heimdall.App.Services;
using Heimdall.App.Services.SharedSessions;
using Heimdall.App.ViewModels;
using Heimdall.App.Views;
using Heimdall.Core.Configuration;
using Size = System.Windows.Size;

namespace Heimdall.App.Tests;

/// <summary>
/// An open session against a shared source that reloads underneath it, and the row layout that
/// keeps a session's name readable in a narrow sidebar.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public async Task SharedProfileGone_WhileItsSessionIsOpen_LeavesTheSession_AndAReconnectSaysNotFound()
    {
        SharedSessionTestData shared = SharedSessionTestData.Create();
        var provider = new StubSharedSessionProvider(
            [shared.DirectProfile],
            [shared.Gateway],
            [StubSharedSessionProvider.Loaded(SharedSessionTestData.SourceId, "Team", 1)]);
        using TestHarness harness = TestHarness.Create(
            profileLookupFactory: configManager => new ProfileLookup(configManager, provider));
        harness.Main.ServerList.LoadServers([], new AppSettings());
        Assert.Contains(harness.Main.ServerList.Servers, server => server.Id == shared.DirectProfile.Id);
        SessionTabViewModel tab = harness.Main.Connection.AddSession(
            "source-runtime",
            shared.DirectProfile.DisplayName,
            "SSH");
        tab.OriginalServerId = shared.DirectProfile.Id;
        ControlledProtocolHandler handler = harness.GetHandler("SSH");

        // The file changed and no longer holds the session.
        provider.SetContents([], [], [StubSharedSessionProvider.Loaded(SharedSessionTestData.SourceId, "Team", 0)]);
        await provider.LoadAsync(new AppSettings());

        DateTime refreshed = DateTime.UtcNow + TestTimeout;
        while (harness.Main.ServerList.Servers.Any(server => server.Id == shared.DirectProfile.Id))
        {
            Assert.True(DateTime.UtcNow < refreshed, "the tree kept the row of a session gone from its source");
            await Task.Delay(20);
        }

        Assert.Contains(tab, harness.Main.Connection.ActiveSessions);

        Action<SessionTabViewModel, string, string> callback = Assert.IsType<Action<SessionTabViewModel, string, string>>(
            harness.EmbeddedSessionManager.ReconnectRequestedCallback);
        EmbeddedSessionManager.ForwardReconnectRequest(tab, ReconnectRequestContext.Manual, callback);

        string notFound = harness.Main.GetLocalizer()["ErrorServerNotFound"];
        DateTime deadline = DateTime.UtcNow + TestTimeout;
        while (harness.Main.StatusText != notFound)
        {
            Assert.True(DateTime.UtcNow < deadline, $"status stayed '{harness.Main.StatusText}'");
            await Task.Delay(20);
        }

        Assert.False(handler.Started.Task.IsCompleted);
    }

    [Fact]
    public void NameFirstPanel_GivesTheNameTheWidth_AndHidesTheBadgesBelowTheirMinimum()
    {
        RunOnStaThread(() =>
        {
            TextBlock name = new() { Text = "srv-database-primary-01", TextTrimming = TextTrimming.CharacterEllipsis };
            TextBlock badge = new()
            {
                Text = "via someone.with.a.long.login@gateway-one.example.test",
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var panel = new NameFirstPanel { MinTrailingWidth = 28 };
            panel.Children.Add(name);
            panel.Children.Add(badge);

            name.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double nameWidth = name.DesiredSize.Width;
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double badgeWidth = badge.DesiredSize.Width;
            Assert.True(badgeWidth > 100, "the badge is long enough to have squeezed the name before");

            // Room for the name and part of the badge: the name keeps all of it, the badge the rest.
            double partial = nameWidth + 60;
            Layout(panel, partial);
            Assert.Equal(nameWidth, Slot(name).Width, 1);
            Assert.True(panel.IsTrailingShown);
            Assert.InRange(Slot(badge).Width, 1, 60.5);

            // Room for the name alone: the badge goes, the name is whole.
            Layout(panel, nameWidth + 10);
            Assert.Equal(nameWidth, Slot(name).Width, 1);
            Assert.False(panel.IsTrailingShown);
            Assert.Equal(0, Slot(badge).Width, 1);

            // Narrower than the name: the name takes everything and ellipsizes; the badge stays out.
            Layout(panel, nameWidth / 2);
            // An ellipsized name asks for slightly less than it is offered: trimming stops on a glyph.
            Assert.InRange(Slot(name).Width, (nameWidth / 2) - 10, (nameWidth / 2) + 0.01);
            Assert.Equal(0, Slot(badge).Width, 1);

            // The control: a grid with the name in a star column and the badge in an auto column,
            // the layout this replaced, leaves the name nothing at the same width.
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock gridName = new() { Text = name.Text, TextTrimming = TextTrimming.CharacterEllipsis };
            TextBlock gridBadge = new() { Text = badge.Text };
            Grid.SetColumn(gridBadge, 1);
            grid.Children.Add(gridName);
            grid.Children.Add(gridBadge);
            Layout(grid, partial);
            Assert.True(gridName.RenderSize.Width < nameWidth / 2, "the old layout did not squeeze the name");
        });
    }

    // The width an element was given. Its RenderSize stays at what it asked for when it is given
    // less: WPF clips it to the slot rather than shrinking it.
    private static Rect Slot(UIElement element) =>
        System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot((FrameworkElement)element);

    private static void Layout(FrameworkElement element, double width)
    {
        element.Measure(new Size(width, 40));
        element.Arrange(new Rect(0, 0, width, 40));
        element.UpdateLayout();
    }
}
