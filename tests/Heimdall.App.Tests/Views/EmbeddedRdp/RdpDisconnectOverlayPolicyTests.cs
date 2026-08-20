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

using System.IO;
using Heimdall.App.Views;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

public sealed class RdpDisconnectOverlayPolicyTests
{
    [Theory]
    [InlineData(true, 0, true, "user-initiated trumps any reason")]
    [InlineData(true, 516, true, "user-initiated trumps SocketConnectFailed")]
    [InlineData(true, 2055, true, "user-initiated trumps BadCredentials")]
    [InlineData(true, 3, true, "user-initiated trumps AdminDisconnect")]
    [InlineData(false, 0, true, "NoInfo is a clean exit")]
    [InlineData(false, 1, true, "LocalUser is a clean exit")]
    [InlineData(false, 2, true, "UserLogoff is a clean exit")]
    [InlineData(false, 3, false, "AdminDisconnect warrants the overlay")]
    [InlineData(false, 4, false, "code 4 is not a clean-exit code")]
    [InlineData(false, 264, false, "ConnectionTimeout warrants the overlay")]
    [InlineData(false, 516, false, "SocketConnectFailed warrants the overlay")]
    [InlineData(false, 2055, false, "BadCredentials warrants the overlay")]
    [InlineData(false, 4360, false, "ResolutionChangeTimeout warrants the overlay")]
    public void ShouldSuppressReconnectOverlay_ReturnsExpected(
        bool userInitiated,
        int reason,
        bool expected,
        string description)
    {
        _ = description;

        bool actual = EmbeddedRdpView.ShouldSuppressReconnectOverlay(userInitiated, reason);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// A reconnect that succeeds has to undo what the cancel that lost the race set up.
    /// </summary>
    /// <remarks>
    /// <para>Cancelling an auto-reconnect raises a user-initiated flag so the disconnect it causes
    /// arrives without an overlay. When the attempt already in flight succeeds instead, that
    /// disconnect never happens - and the flag is cleared nowhere but in the disconnect handler, so
    /// it outlives the race and silences the NEXT genuine drop of a live session. The theory above
    /// is what makes that consequence concrete: with the flag still raised, every reason code
    /// suppresses the overlay.</para>
    /// <para>Read from source because the handler is a COM event callback on a view that needs a
    /// desktop. Coarse on purpose: what it rules out is the reconnect path silently going back to
    /// leaving the flag and the state machine behind, which is how the defect existed in the first
    /// place.</para>
    /// </remarks>
    [Fact]
    public void TheAutoReconnectedHandlerClearsTheCancelFlagAndTellsTheStateMachine()
    {
        string handler = ExtractMethodBody(
            ReadViewSource(),
            "private void OnRdpAutoReconnected()");

        Assert.Contains("_userInitiatedDisconnect = false;", handler, StringComparison.Ordinal);
        Assert.Contains(
            "TryTransitionConnectionState(ConnectionState.Connected)",
            handler,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Guards the guard: the extraction has to be reading the real handler.
    /// </summary>
    [Fact]
    public void TheHandlerBeingReadIsTheOneThatAcceptsAReconnect()
    {
        string handler = ExtractMethodBody(
            ReadViewSource(),
            "private void OnRdpAutoReconnected()");

        Assert.Contains("UpdateSessionStatus(RdpSessionStatus.Connected)", handler, StringComparison.Ordinal);
        Assert.Contains("TransitionPhase(RdpConnectionPhase.Connected)", handler, StringComparison.Ordinal);
    }

    private static string ReadViewSource() => File.ReadAllText(Path.Combine(
        FindRepoRoot(),
        "src",
        "Heimdall.App",
        "Views",
        "EmbeddedRdpView.xaml.cs"));

    private static string ExtractMethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Signature not found: {signature}");

        int open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"No body for: {signature}");

        int depth = 0;
        for (int index = open; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[open..(index + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Unbalanced body for: {signature}");
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }
}
