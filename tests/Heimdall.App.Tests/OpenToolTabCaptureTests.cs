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
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// A command captured while a Command Library tab is open lands in that tab: the capture carries
/// the session's host, which would otherwise open a library tab per capture.
/// </summary>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public async Task OpenToolTabAsync_CaptureWithALibraryOpen_PrefillsThatTab()
    {
        using TestHarness harness = TestHarness.Create();
        var library = new RecordingCaptureTarget();
        harness.EmbeddedSessionManager.CreateToolControlBehavior = (_, _, _, _) => library;
        await harness.Main.OpenToolTabAsync(ToolRegistry.CommandLibraryToolId, "Command Library", null);
        SessionTabViewModel libraryTab = Assert.Single(harness.Main.Connection.ActiveSessions);

        var capture = new ToolContext(
            TargetHost: "srv-01",
            ConnectionType: "SSH",
            RemoteOs: RemoteOs.Auto,
            InitialCommandText: "cd /tmp && ls");
        await harness.Main.OpenToolTabAsync(ToolRegistry.CommandLibraryToolId, "Command Library", capture);

        Assert.Same(libraryTab, Assert.Single(harness.Main.Connection.ActiveSessions));
        Assert.Same(libraryTab, harness.Main.Connection.ActiveSession);
        Assert.Equal(1, harness.EmbeddedSessionManager.CreateToolControlCalls);
        Assert.Equal(new CapturedCommand("cd /tmp && ls", RemoteOs.Linux, "ssh"), Assert.Single(library.Prefills));
    }

    [Fact]
    public async Task OpenToolTabAsync_CaptureWithNoLibraryOpen_OpensOneCarryingTheText()
    {
        using TestHarness harness = TestHarness.Create();
        ToolContext? created = null;
        harness.EmbeddedSessionManager.CreateToolControlBehavior = (_, _, context, _) =>
        {
            created = context;
            return new RecordingCaptureTarget();
        };

        var capture = new ToolContext(TargetHost: "srv-01", ConnectionType: "SSH", InitialCommandText: "uptime");
        await harness.Main.OpenToolTabAsync(ToolRegistry.CommandLibraryToolId, "Command Library", capture);

        Assert.Single(harness.Main.Connection.ActiveSessions);
        Assert.Equal("uptime", created?.InitialCommandText);
    }

    private sealed class RecordingCaptureTarget : ICommandCaptureTarget
    {
        public List<CapturedCommand> Prefills { get; } = [];

        public void PrefillNewAction(CapturedCommand capture) => Prefills.Add(capture);
    }
}
