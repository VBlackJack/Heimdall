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
using Heimdall.App.ViewModels.Session;
using Heimdall.App.Views;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// A host's own Reconnect and Close used to name only the tab it was created for. In a split
/// they acted on the whole tab, and after a merge or a detach on a tab the host was no longer in.
/// </summary>
public sealed class SessionHostOwnerResolutionTests
{
    // Proved against the mutant that answers with the creating tab: the merged-into tab is lost.
    [Fact]
    public void ResolveHostOwner_APaneMergedIntoAnotherTab_IsFoundThereAsOneOfSeveral()
    {
        SessionPaneModel moved = Pane("moved");
        SessionTabViewModel createdFor = new();
        SessionTabViewModel mergedInto = Split(Pane("rdp"), moved);

        (SessionTabViewModel owner, bool oneOfSeveral) =
            SessionCoordinator.ResolveHostOwner([mergedInto], createdFor, moved);

        Assert.Same(mergedInto, owner);
        Assert.True(oneOfSeveral);
    }

    [Fact]
    public void ResolveHostOwner_TheSecondaryPaneOfItsOwnSplit_IsOneOfSeveral()
    {
        SessionPaneModel secondary = Pane("ssh");
        SessionTabViewModel tab = Split(Pane("rdp"), secondary);

        (SessionTabViewModel owner, bool oneOfSeveral) =
            SessionCoordinator.ResolveHostOwner([tab], tab, secondary);

        Assert.Same(tab, owner);
        Assert.True(oneOfSeveral);
    }

    // A pane detached to a tab of its own is that tab's only pane: the whole tab is its to act on.
    [Fact]
    public void ResolveHostOwner_APaneDetachedToItsOwnTab_IsThatTabAlone()
    {
        SessionPaneModel detached = Pane("ssh");
        SessionTabViewModel original = Split(Pane("rdp"), Pane("other"));
        SessionTabViewModel own = new() { RootContent = detached };

        (SessionTabViewModel owner, bool oneOfSeveral) =
            SessionCoordinator.ResolveHostOwner([original, own], original, detached);

        Assert.Same(own, owner);
        Assert.False(oneOfSeveral);
    }

    [Fact]
    public void ResolveHostOwner_WithoutAPaneOrWithAPaneFoundNowhere_KeepsTheCreatingTab()
    {
        SessionTabViewModel createdFor = new();

        Assert.Same(createdFor, SessionCoordinator.ResolveHostOwner([], createdFor, null).Owner);
        (SessionTabViewModel owner, bool oneOfSeveral) =
            SessionCoordinator.ResolveHostOwner([], createdFor, Pane("gone"));
        Assert.Same(createdFor, owner);
        Assert.False(oneOfSeveral);
    }

    // The reconnect request an SSH host recorded travels with it to the tab it now sits in.
    [Fact]
    public void MoveReconnectRequest_HandsTheRecordedRequestToTheOtherTab()
    {
        SessionTabViewModel from = new();
        SessionTabViewModel to = new();
        ReconnectRequestContext recorded = new(true, 2, 5);
        EmbeddedSessionManager.ForwardReconnectRequest(from, recorded, (_, _, _) => { });

        EmbeddedSessionManager.MoveReconnectRequest(from, to);

        Assert.Equal(recorded, EmbeddedSessionManager.TakeReconnectRequest(to));
        Assert.Equal(ReconnectRequestContext.Manual, EmbeddedSessionManager.TakeReconnectRequest(from));
    }

    [Fact]
    public void ProfileLookupServerIdFor_ASecondaryPane_IsItsOwnProfileNotThePrimarys()
    {
        SessionPaneModel primary = Pane("primary");
        primary.OriginalServerId = "profile-primary";
        SessionPaneModel secondary = Pane("secondary");
        secondary.OriginalServerId = "profile-secondary";
        SessionTabViewModel tab = Split(primary, secondary);

        Assert.Equal("profile-secondary", tab.ProfileLookupServerIdFor(secondary));
        Assert.Equal("profile-primary", tab.ProfileLookupServerIdFor(primary));
        Assert.Equal("profile-primary", tab.ProfileLookupServerIdFor(null));
    }

    [Fact]
    public void TerminalRoutingPane_ATerminalWithOnlyItsSftpCompanion_ActsOnTheWholeTab()
    {
        SessionPaneModel terminal = new() { PaneId = "ssh", ServerId = "s1", OriginalServerId = "srv", ConnectionType = "SSH" };
        SessionPaneModel companion = new() { PaneId = "sftp", ServerId = "s2", OriginalServerId = "srv", ConnectionType = "SFTP" };
        SessionTabViewModel tab = Split(terminal, companion);

        Assert.Null(EmbeddedSessionManager.TerminalRoutingPane(tab, terminal));
    }

    [Fact]
    public void TerminalRoutingPane_ATerminalBesideAnotherServer_ActsOnItsOwnPane()
    {
        SessionPaneModel terminal = new() { PaneId = "ssh", ServerId = "s1", OriginalServerId = "srv", ConnectionType = "SSH" };
        SessionPaneModel other = new() { PaneId = "rdp", ServerId = "s2", OriginalServerId = "other", ConnectionType = "RDP" };
        SessionTabViewModel tab = Split(terminal, other);

        Assert.Same(terminal, EmbeddedSessionManager.TerminalRoutingPane(tab, terminal));
    }

    [Fact]
    public void TerminalRoutingPane_ATerminalNoLongerPrimaryInItsTab_ActsOnItsOwnPane()
    {
        SessionPaneModel companion = new() { PaneId = "sftp", OriginalServerId = "srv", ConnectionType = "SFTP" };
        SessionPaneModel terminal = new() { PaneId = "ssh", OriginalServerId = "srv", ConnectionType = "SSH" };

        // Swapped: the terminal is now the second pane.
        SessionTabViewModel tab = Split(companion, terminal);

        Assert.Same(terminal, EmbeddedSessionManager.TerminalRoutingPane(tab, terminal));
    }

    [Fact]
    public void TerminalRoutingPane_ATerminalAloneInItsTab_ActsOnTheTab()
    {
        SessionPaneModel terminal = Pane("ssh");
        SessionTabViewModel tab = new() { RootContent = terminal };

        Assert.Null(EmbeddedSessionManager.TerminalRoutingPane(tab, terminal));
    }

    private static SessionPaneModel Pane(string id) => new() { PaneId = id, ServerId = id, ConnectionType = "SSH" };

    private static SessionTabViewModel Split(SessionPaneModel first, SessionPaneModel second) => new()
    {
        RootContent = new SplitContainerModel
        {
            First = first,
            Second = second,
            Orientation = SplitOrientation.Vertical
        }
    };
}
