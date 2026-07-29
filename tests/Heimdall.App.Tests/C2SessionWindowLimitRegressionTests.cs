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
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public void UnsplitSession_AtEmbeddedLimit_ReintroducesSecondaryAsIndependentTab()
    {
        using TestHarness harness = TestHarness.Create();
        var service = new SessionWindowService();
        SessionTabViewModel session = harness.Main.Connection.AddSession(
            "srv-primary",
            "Primary",
            "SSH");
        var primaryHost = new object();
        var secondaryHost = new object();
        session.RootContent = new SplitContainerModel
        {
            First = new SessionPaneModel
            {
                PaneId = "primary",
                ServerId = "srv-primary",
                ConnectionType = "SSH",
                HostControl = primaryHost,
            },
            Second = new SessionPaneModel
            {
                PaneId = "secondary",
                ServerId = "srv-secondary",
                ConnectionType = "RDP",
                Title = "Secondary",
                HostControl = secondaryHost,
            },
        };

        Assert.Null(harness.Main.Connection.AddSession(
            "blocked",
            "Blocked",
            "SSH",
            maxEmbeddedSessions: 1));

        service.UnsplitSession(session, harness.Main);

        Assert.False(session.IsSplit);
        Assert.Equal(2, harness.Main.Connection.ActiveSessions.Count);
        Assert.Same(primaryHost, session.HostControl);
        Assert.Same(
            secondaryHost,
            harness.Main.Connection.ActiveSessions[^1].HostControl);
    }
}
