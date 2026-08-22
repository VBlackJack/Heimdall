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

using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// The status bar states the displayed session's condition, and revises it when that
/// condition changes.
/// </summary>
/// <remarks>
/// It used to be written once, at connection, and never again. A session that dropped left
/// "Connected to: X" standing in the bar while the panel above it announced the
/// disconnection: two surfaces asserting opposite things about the same session, with
/// nothing to reconcile them. Measured on a VNC session on 2026-08-21.
/// </remarks>
public sealed partial class SessionCoordinatorPreMountTests
{
    [Fact]
    public void ActiveSession_WhenConnected_StatesItInTheStatusBar()
    {
        using var harness = TestHarness.Create();

        harness.Main.Connection.ActiveSession = new SessionTabViewModel
        {
            Title = "vnc-5901",
            Status = SessionStatusTokens.Connected
        };

        Assert.Contains("vnc-5901", harness.Main.StatusText, StringComparison.Ordinal);
        Assert.Equal(
            harness.Main.GetLocalizer().Format("StatusConnected", "vnc-5901"),
            harness.Main.StatusText);
    }

    /// <summary>
    /// The defect itself. Fails against any build where the bar is written once.
    /// </summary>
    [Fact]
    public void ActiveSession_WhenItDrops_StopsClaimingItIsConnected()
    {
        using var harness = TestHarness.Create();
        var session = new SessionTabViewModel
        {
            Title = "vnc-5901",
            Status = SessionStatusTokens.Connected
        };
        harness.Main.Connection.ActiveSession = session;

        string whileConnected = harness.Main.StatusText;
        session.Status = SessionStatusTokens.Disconnected;

        Assert.NotEqual(whileConnected, harness.Main.StatusText);
        Assert.DoesNotContain(
            harness.Main.GetLocalizer().Format("StatusConnected", "vnc-5901"),
            harness.Main.StatusText,
            StringComparison.Ordinal);
        Assert.Contains("vnc-5901", harness.Main.StatusText, StringComparison.Ordinal);
        Assert.Contains(
            harness.Main.GetLocalizer()["SessionStatusDisconnected"],
            harness.Main.StatusText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every condition is named with the label the tab header already uses, through the
    /// shared resolver, so the two surfaces cannot drift apart.
    /// </summary>
    [Theory]
    [InlineData("Connecting", "SessionStatusConnecting")]
    [InlineData("Reconnecting", "SessionStatusReconnecting")]
    [InlineData("Disconnecting", "SessionStatusDisconnecting")]
    [InlineData("Error", "SessionStatusError")]
    public void ActiveSession_NamesEveryConditionWithTheSharedLabel(string token, string expectedKey)
    {
        using var harness = TestHarness.Create();

        harness.Main.Connection.ActiveSession = new SessionTabViewModel
        {
            Title = "host",
            Status = token
        };

        Assert.Contains(
            harness.Main.GetLocalizer()[expectedKey],
            harness.Main.StatusText,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A pane that failed carries the reason as free text. Showing "Error" instead would
    /// hide the only useful part.
    /// </summary>
    [Fact]
    public void ActiveSession_WithAFreeFormFailure_ShowsTheReason()
    {
        using var harness = TestHarness.Create();

        harness.Main.Connection.ActiveSession = new SessionTabViewModel
        {
            Title = "host",
            Status = "Name or service not known"
        };

        Assert.Contains(
            "Name or service not known",
            harness.Main.StatusText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoActiveSession_ReturnsToTheReadyMessage()
    {
        using var harness = TestHarness.Create();
        harness.Main.Connection.ActiveSession = new SessionTabViewModel
        {
            Title = "host",
            Status = SessionStatusTokens.Connected
        };

        harness.Main.Connection.ActiveSession = null;

        Assert.Equal(harness.Main.GetLocalizer()["StatusReady"], harness.Main.StatusText);
    }

    /// <summary>
    /// The bar is still the application's message line. A message written by anything else
    /// must survive until the session it sits over actually changes, or every transient
    /// notice would be erased the moment it appeared.
    /// </summary>
    [Fact]
    public void AMessageFromElsewhere_IsNotOverwrittenWhileTheSessionIsUnchanged()
    {
        using var harness = TestHarness.Create();
        harness.Main.Connection.ActiveSession = new SessionTabViewModel
        {
            Title = "host",
            Status = SessionStatusTokens.Connected
        };

        harness.Main.StatusText = "Copied 3 files";

        Assert.Equal("Copied 3 files", harness.Main.StatusText);
    }
}
