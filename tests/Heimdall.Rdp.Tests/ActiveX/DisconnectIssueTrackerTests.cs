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

using Heimdall.Rdp.ActiveX;

namespace Heimdall.Rdp.Tests.ActiveX;

public sealed class DisconnectIssueTrackerTests
{
    [Fact]
    public void Fresh_ShouldIssueDisconnect()
    {
        Assert.True(new DisconnectIssueTracker().ShouldIssueDisconnect);
    }

    [Fact]
    public void AfterDisconnectReturned_SkipsTheRepeat()
    {
        var tracker = new DisconnectIssueTracker();

        tracker.OnDisconnectReturned();

        Assert.False(tracker.ShouldIssueDisconnect);
    }

    [Fact]
    public void AfterDisconnectReturnedThenConnect_IssuesAgain()
    {
        var tracker = new DisconnectIssueTracker();
        tracker.OnDisconnectReturned();

        tracker.OnConnectStarted();

        Assert.True(tracker.ShouldIssueDisconnect);
    }

    [Fact]
    public void AfterDisconnectReturnedThenReset_IssuesAgain()
    {
        var tracker = new DisconnectIssueTracker();
        tracker.OnDisconnectReturned();

        tracker.OnResetForReuse();

        Assert.True(tracker.ShouldIssueDisconnect);
    }

    [Fact]
    public void WhenDisconnectThrew_NothingIsRecordedAndItIssuesAgain()
    {
        var tracker = new DisconnectIssueTracker();
        tracker.OnConnectStarted();

        // The host calls OnDisconnectReturned only after the OCX call returns, so a throw skips it.

        Assert.True(tracker.ShouldIssueDisconnect);
    }
}
