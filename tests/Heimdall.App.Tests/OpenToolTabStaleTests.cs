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
    private sealed class StaleProbeView : IStaleToolView
    {
        public bool IsStale { get; set; }
    }

    [Fact]
    public async Task OpenToolTabAsync_ATabWhoseToolWasReplaced_IsNotReusedAndAFreshTabOpens()
    {
        using TestHarness harness = TestHarness.Create(toolAvailability: new ScriptedToolAvailability());
        StaleProbeView first = new();
        harness.EmbeddedSessionManager.CreateToolControlBehavior = (_, _, _, _) => first;
        await harness.Main.OpenToolTabAsync(AvailableToolId, "Hash", null);
        first.IsStale = true;
        StaleProbeView second = new();
        harness.EmbeddedSessionManager.CreateToolControlBehavior = (_, _, _, _) => second;

        ToolOpenOutcome outcome = await harness.Main.OpenToolTabAsync(AvailableToolId, "Hash", null);

        Assert.Equal(ToolOpenOutcome.Opened, outcome);
        Assert.Equal(2, harness.Main.Connection.ActiveSessions.Count);
        Assert.Same(second, Assert.IsType<SessionTabViewModel>(harness.Main.Connection.ActiveSession).HostControl);
        Assert.Equal(2, harness.EmbeddedSessionManager.CreateToolControlCalls);

        // The next open finds the fresh tab, not the one left behind for the user to close.
        ToolOpenOutcome third = await harness.Main.OpenToolTabAsync(AvailableToolId, "Hash", null);

        Assert.Equal(ToolOpenOutcome.FocusedExisting, third);
        Assert.Equal(2, harness.Main.Connection.ActiveSessions.Count);
        Assert.Same(second, Assert.IsType<SessionTabViewModel>(harness.Main.Connection.ActiveSession).HostControl);
    }

    [Fact]
    public async Task OpenToolTabAsync_ATabThatIsStillTheTool_IsFocusedAsBefore()
    {
        using TestHarness harness = TestHarness.Create(toolAvailability: new ScriptedToolAvailability());
        StaleProbeView view = new();
        harness.EmbeddedSessionManager.CreateToolControlBehavior = (_, _, _, _) => view;
        await harness.Main.OpenToolTabAsync(AvailableToolId, "Hash", null);

        ToolOpenOutcome outcome = await harness.Main.OpenToolTabAsync(AvailableToolId, "Hash", null);

        Assert.Equal(ToolOpenOutcome.FocusedExisting, outcome);
        Assert.Single(harness.Main.Connection.ActiveSessions);
        Assert.Equal(1, harness.EmbeddedSessionManager.CreateToolControlCalls);
    }
}
