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

using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

public sealed class SessionPaneModelTests
{
    [Fact]
    public void ProfileLookupServerId_ReturnsOriginalServerId_WhenOriginalServerIdIsNotBlank()
    {
        SessionPaneModel pane = new SessionPaneModel
        {
            ServerId = "session-1",
            OriginalServerId = "profile-1"
        };

        Assert.Equal("profile-1", pane.ProfileLookupServerId);
    }

    [Fact]
    public void ProfileLookupServerId_ReturnsServerId_WhenOriginalServerIdIsEmpty()
    {
        SessionPaneModel pane = new SessionPaneModel
        {
            ServerId = "session-1",
            OriginalServerId = ""
        };

        Assert.Equal("session-1", pane.ProfileLookupServerId);
    }

    [Fact]
    public void ProfileLookupServerId_ReturnsServerId_WhenOriginalServerIdIsWhiteSpace()
    {
        SessionPaneModel pane = new SessionPaneModel
        {
            ServerId = "session-1",
            OriginalServerId = "   "
        };

        Assert.Equal("session-1", pane.ProfileLookupServerId);
    }

    [Fact]
    public void ProfileLookupServerId_ReturnsEmpty_WhenBothIdsAreEmpty()
    {
        SessionPaneModel pane = new SessionPaneModel
        {
            ServerId = "",
            OriginalServerId = ""
        };

        Assert.Equal("", pane.ProfileLookupServerId);
    }

    [Fact]
    public void PlacingAHostOnAPane_TellsTheHostWhichPane()
    {
        SessionPaneModel first = new();
        SessionPaneModel second = new();
        RecordingPaneOwner host = new();

        first.HostControl = host;
        Assert.Same(first, host.Pane);

        // Moved by a detach or a merge: the pane it lands on is the one it reports.
        first.HostControl = null;
        second.HostControl = host;
        Assert.Same(second, host.Pane);
    }

    [Fact]
    public void PlacingAnyOtherObject_IsAccepted()
    {
        SessionPaneModel pane = new() { HostControl = new object() };

        Assert.NotNull(pane.HostControl);
    }

    private sealed class RecordingPaneOwner : ISessionPaneOwner
    {
        internal SessionPaneModel? Pane { get; private set; }

        public void SetOwningPane(SessionPaneModel pane) => Pane = pane;
    }
}
