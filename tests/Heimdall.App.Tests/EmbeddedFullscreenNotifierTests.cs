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
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed class EmbeddedFullscreenNotifierTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotifyTree_NestedSplit_NotifiesEveryHostOnceWithRequestedState(
        bool isFullscreen)
    {
        object firstHost = new();
        object secondHost = new();
        object thirdHost = new();
        ISplitContent root = new SplitContainerModel
        {
            First = Pane(firstHost),
            Second = new SplitContainerModel
            {
                First = Pane(null),
                Second = new SplitContainerModel
                {
                    First = Pane(secondHost),
                    Second = Pane(thirdHost)
                }
            }
        };
        List<(object Host, bool IsFullscreen)> notifications = [];

        EmbeddedFullscreenNotifier.NotifyTree(
            root,
            isFullscreen,
            (host, state) => notifications.Add((host, state)));

        Assert.Collection(
            notifications,
            notification => AssertNotification(notification, firstHost, isFullscreen),
            notification => AssertNotification(notification, secondHost, isFullscreen),
            notification => AssertNotification(notification, thirdHost, isFullscreen));
    }

    private static SessionPaneModel Pane(object? hostControl) =>
        new() { HostControl = hostControl };

    private static void AssertNotification(
        (object Host, bool IsFullscreen) notification,
        object expectedHost,
        bool expectedState)
    {
        Assert.Same(expectedHost, notification.Host);
        Assert.Equal(expectedState, notification.IsFullscreen);
    }
}
