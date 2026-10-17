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
using Heimdall.Core.SessionDiagnostics;

namespace Heimdall.App.Tests;

/// <summary>
/// The tab strip marks the sessions that need a look, so thirty healthy tabs stay quiet and the
/// six that dropped overnight stand out without clicking through all of them.
/// </summary>
public sealed class SessionTabHeaderStateTests
{
    [Theory]
    [InlineData("", SessionTabHeaderState.Established)]
    [InlineData(SessionStatusTokens.Connected, SessionTabHeaderState.Established)]
    [InlineData(SessionStatusTokens.RemoteSessionHandedOff, SessionTabHeaderState.Established)]
    [InlineData(SessionStatusTokens.LaunchedExternalClient, SessionTabHeaderState.Established)]
    [InlineData(SessionStatusTokens.Connecting, SessionTabHeaderState.Transitioning)]
    [InlineData(SessionStatusTokens.Reconnecting, SessionTabHeaderState.Transitioning)]
    [InlineData(SessionStatusTokens.Disconnecting, SessionTabHeaderState.Transitioning)]
    [InlineData(SessionStatusTokens.Disconnected, SessionTabHeaderState.Disconnected)]
    [InlineData(SessionStatusTokens.Error, SessionTabHeaderState.Failed)]
    [InlineData("Authentication failed: permission denied", SessionTabHeaderState.Failed)]
    public void From_FoldsEachStatusIntoTheStateTheHeaderMarks(string status, SessionTabHeaderState expected)
    {
        Assert.Equal(expected, SessionTabHeaderStates.From(status, hasFailureDetails: false));
    }

    [Fact]
    public void From_ARecordedFailure_WinsOverAConnectedToken()
    {
        Assert.Equal(
            SessionTabHeaderState.Failed,
            SessionTabHeaderStates.From(SessionStatusTokens.Connected, hasFailureDetails: true));
    }

    [Fact]
    public void ResolveKey_AnEstablishedTabIsNotAnnounced()
    {
        Assert.Null(SessionTabHeaderStates.ResolveKey(SessionTabHeaderState.Established, SessionStatusTokens.Connected));
        Assert.Equal(
            "SessionStatusDisconnected",
            SessionTabHeaderStates.ResolveKey(SessionTabHeaderState.Disconnected, SessionStatusTokens.Disconnected));
        Assert.Equal(
            "SessionStatusError",
            SessionTabHeaderStates.ResolveKey(SessionTabHeaderState.Failed, "free text reason"));
    }

    /// <summary>
    /// A tool pane's status is free text by design ("Ready"), and free text on a session reads as
    /// a failure reason, so every tool tab carried the failure mark and was announced as an error.
    /// </summary>
    [Fact]
    public void From_AToolPaneIsNotMarkedForItsFreeTextStatus()
    {
        Assert.Equal(
            SessionTabHeaderState.Established,
            SessionTabHeaderStates.From("Ready", hasFailureDetails: false, connectionType: "TOOL:REGEX"));
        Assert.Equal(
            SessionTabHeaderState.Failed,
            SessionTabHeaderStates.From("Ready", hasFailureDetails: false, connectionType: "SSH"));
    }

    [Fact]
    public void From_AToolPaneWithARecordedFailureIsStillMarked()
    {
        Assert.Equal(
            SessionTabHeaderState.Failed,
            SessionTabHeaderStates.From("Ready", hasFailureDetails: true, connectionType: "TOOL:REGEX"));
    }

    [Fact]
    public void HeaderState_AnOpenToolTabIsNeitherMarkedNorAnnounced()
    {
        SessionTabViewModel tab = new() { Title = "Regex Tester", ConnectionType = "TOOL:REGEX", Status = "Ready" };

        Assert.Equal(SessionTabHeaderState.Established, tab.HeaderState);
        Assert.Null(SessionTabHeaderStates.ResolveKey(tab.HeaderState, tab.Status));
    }

    [Fact]
    public void HeaderState_FollowsAStatusWrittenOnThePrimaryPane()
    {
        SessionTabViewModel tab = new() { Title = "srv01" };
        List<string?> raised = [];
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.PrimaryPane.Status = SessionStatusTokens.Disconnected;

        Assert.Equal(SessionTabHeaderState.Disconnected, tab.HeaderState);
        Assert.Contains(nameof(SessionTabViewModel.HeaderState), raised);
    }

    [Fact]
    public void HeaderState_IsRaisedWhenAFailureIsRecorded()
    {
        SessionTabViewModel tab = new() { Title = "srv01", Status = SessionStatusTokens.Connected };
        List<string?> raised = [];
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.PrimaryPane.FailureDetails = new SessionDiagnostic(SessionFailureStage.Unknown, "SessionStatusError");

        Assert.Equal(SessionTabHeaderState.Failed, tab.HeaderState);
        Assert.Contains(nameof(SessionTabViewModel.HeaderState), raised);
    }
}
