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

using System.Runtime.CompilerServices;
using Heimdall.App.ViewModels;
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

/// <summary>
/// One failure overlay per pane: the generic one stands aside for a host that draws its own.
/// </summary>
/// <remarks>
/// A disconnected SFTP pane showed two overlays stacked: the pane's own (X icon, Reconnect, Edit
/// profile, Copy error, Close) drawn over the SFTP view's (red "Disconnected", the reason the
/// listing is stale, and its Reconnect showing through behind). The SFTP view publishes
/// Disconnected to its pane, and the pane exempted only the RDP and SSH hosts. The SFTP view keeps
/// its overlay, as RDP and SSH keep theirs: its header already offers Edit profile, Reconnect and
/// Close while disconnected, routed through the pane like the others, and its message says what
/// the stale listing behind it is.
///
/// The views are allocated without running their constructors: the decision reads only the host's
/// type, and building a real view here needs a running App and its host services.
/// </remarks>
public sealed class SessionPaneDisconnectedOverlayTests
{
    public static TheoryData<Type> HostsWithTheirOwnOverlay => new()
    {
        typeof(EmbeddedRdpView),
        typeof(EmbeddedSshView),
        typeof(EmbeddedSftpView),

        // Its overlay carries the password entry of a shared session, which the pane's cannot.
        typeof(EmbeddedVncView),
    };

    [Theory]
    [MemberData(nameof(HostsWithTheirOwnOverlay))]
    public void ADisconnectedHost_WithItsOwnOverlay_GetsNoSecondOne(Type hostType)
    {
        object host = AllocateWithoutConstructor(hostType);

        Assert.False(
            SessionPaneControl.ShowsDisconnectedOverlay(host, hasFailureDetails: false, SessionStatusTokens.Disconnected),
            $"The pane's Disconnected overlay covers {hostType.Name}, which draws its own: two overlays stack.");
        Assert.False(
            SessionPaneControl.ShowsDisconnectedOverlay(host, hasFailureDetails: false, SessionStatusTokens.Error),
            $"The pane's Disconnected overlay covers {hostType.Name} on an error, which it reports itself.");
    }

    // The control that keeps the theory above from passing on an overlay that never shows: a host
    // with no overlay of its own still gets the pane's when it reports a disconnect.
    [Theory]
    [InlineData(SessionStatusTokens.Disconnected)]
    [InlineData(SessionStatusTokens.Error)]
    public void ADisconnectedHost_WithoutItsOwnOverlay_GetsThePanes(string status)
    {
        Assert.True(SessionPaneControl.ShowsDisconnectedOverlay(new object(), hasFailureDetails: false, status));
    }

    [Fact]
    public void AConnectedHost_GetsNoOverlay()
    {
        Assert.False(
            SessionPaneControl.ShowsDisconnectedOverlay(new object(), hasFailureDetails: false, SessionStatusTokens.Connected));
    }

    // A reconnect that fails clears the host first, so the failure is the pane's to show whatever
    // the protocol: this is where Copy error and the failure detail live.
    [Fact]
    public void AFailedConnection_WithoutAHost_GetsThePanes()
    {
        Assert.True(SessionPaneControl.ShowsDisconnectedOverlay(null, hasFailureDetails: true, SessionStatusTokens.Error));
    }

    // An external client (mstsc as a separate process) has no host control: its pane used to spin
    // forever because "no host and no failure" meant "still loading". Once it reads Disconnected, the
    // pane shows its Disconnected overlay and the spinner goes away, never both.
    [Fact]
    public void AnExternalClientPane_ThatDisconnected_ShowsTheDisconnectedOverlayNotTheSpinner()
    {
        Assert.True(SessionPaneControl.ShowsDisconnectedOverlay(null, hasFailureDetails: false, SessionStatusTokens.Disconnected));
        Assert.False(SessionPaneControl.ShowsLoadingOverlay(null, hasFailureDetails: false, SessionStatusTokens.Disconnected));
    }

    [Theory]
    [InlineData(SessionStatusTokens.Connecting)]
    [InlineData(SessionStatusTokens.LaunchedExternalClient)]
    public void AHostlessPane_StillLaunching_KeepsTheLoadingOverlay(string status)
    {
        Assert.True(SessionPaneControl.ShowsLoadingOverlay(null, hasFailureDetails: false, status));
        Assert.False(SessionPaneControl.ShowsDisconnectedOverlay(null, hasFailureDetails: false, status));
    }

    [Theory]
    [InlineData(SessionStatusTokens.Connecting)]
    [InlineData(SessionStatusTokens.Connected)]
    [InlineData(SessionStatusTokens.Disconnected)]
    public void APaneWithAHost_NeverShowsTheLoadingOverlay(string status)
    {
        Assert.False(SessionPaneControl.ShowsLoadingOverlay(new object(), hasFailureDetails: false, status));
    }

    // The split service skips a pane with no host and no failure as "already reconnecting", so
    // Reconnect on an exited external client inside a split would do nothing: it is not offered.
    [Theory]
    [InlineData(true, false, SessionStatusTokens.Disconnected, true, false)]
    [InlineData(true, false, SessionStatusTokens.Disconnected, false, true)]
    [InlineData(false, false, SessionStatusTokens.Disconnected, true, true)]
    [InlineData(true, true, SessionStatusTokens.Error, true, true)]
    [InlineData(true, false, SessionStatusTokens.Connecting, true, true)]
    public void Reconnect_IsHiddenOnlyForAHostlessDisconnectedPaneInASplit(
        bool hostless, bool hasFailure, string status, bool isSplit, bool expected)
    {
        object? host = hostless ? null : new object();

        Assert.Equal(expected, SessionPaneControl.OffersReconnect(host, hasFailure, status, isSplit));
    }

    private static object AllocateWithoutConstructor(Type type)
    {
        object instance = RuntimeHelpers.GetUninitializedObject(type);
        GC.SuppressFinalize(instance);
        return instance;
    }
}
