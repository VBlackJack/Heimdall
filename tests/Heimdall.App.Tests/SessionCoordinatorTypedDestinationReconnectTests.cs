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

namespace Heimdall.App.Tests;

/// <summary>
/// A reconnected typed destination is still a typed destination.
/// </summary>
/// <remarks>
/// The reconnect dials a copy of the tab's snapshot, not the snapshot itself. A copy that lost
/// the mark would file the reconnected session's RDP certificate approval under the profile
/// scope, under an identifier a saved profile may share - the collision back, on the second
/// connection only, which is the kind of defect a first look does not reach.
/// </remarks>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public async Task AdHocReconnect_DialsACopyThatIsStillATypedDestination()
    {
        using TestHarness harness = TestHarness.Create();
        ServerProfileDto snapshot = harness.CreateServer("SSH");
        snapshot.MarkAsTypedDestination();
        SessionTabViewModel tab = harness.Main.Connection.AddSession(
            "adhoc-runtime",
            snapshot.DisplayName,
            "SSH");
        tab.MarkAsAdHoc(snapshot);
        ControlledProtocolHandler handler = harness.GetHandler("SSH");
        handler.Result.SetResult(new ConnectionResult(false, "reconnect declined", null));

        harness.Main.Session.ReconnectSession(tab);

        await WaitUntilAsync(() => handler.LastServer is not null);
        ServerProfileDto dialled = handler.LastServer!;

        // A copy, so the snapshot stays immutable for the next reconnect - and a marked one.
        Assert.NotSame(snapshot, dialled);
        Assert.True(dialled.IsTypedDestination);

        await WaitUntilAsync(() => harness.Main.Session.ActiveReconnectChainCount == 0);
    }
}
