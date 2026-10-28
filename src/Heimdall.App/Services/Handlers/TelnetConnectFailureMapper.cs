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

using System.Net.Sockets;

namespace Heimdall.App.Services.Handlers;

/// <summary>How a failed Telnet connect is reported to the user.</summary>
internal enum TelnetConnectOutcome
{
    /// <summary>The user cancelled: not a failure, nothing is shown.</summary>
    Cancelled,

    /// <summary>The connect timeout elapsed or the network reported a timeout.</summary>
    TimedOut,

    /// <summary>Any other failure: the generic message carries the exception text.</summary>
    Failed
}

/// <summary>
/// Maps the exception of a failed Telnet connect to an outcome and to the locale key of its message.
/// </summary>
/// <remarks>
/// The connect timeout cancels a linked token, so a timeout surfaces as an
/// <see cref="OperationCanceledException"/> exactly like a user cancel. Only the user's own token
/// tells them apart.
/// </remarks>
internal static class TelnetConnectFailureMapper
{
    internal const string TimedOutMessageKey = "ErrorTelnetConnectionTimedOut";
    internal const string FailedMessageKey = "ErrorTelnetConnectionFailed";

    /// <summary>The outcome of <paramref name="exception"/> given the user's cancellation token.</summary>
    internal static TelnetConnectOutcome Classify(Exception exception, CancellationToken userToken)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException)
        {
            return userToken.IsCancellationRequested
                ? TelnetConnectOutcome.Cancelled
                : TelnetConnectOutcome.TimedOut;
        }

        return exception switch
        {
            TimeoutException => TelnetConnectOutcome.TimedOut,
            SocketException { SocketErrorCode: SocketError.TimedOut } => TelnetConnectOutcome.TimedOut,
            _ => TelnetConnectOutcome.Failed
        };
    }

    /// <summary>The locale key of the message shown for <paramref name="outcome"/>, or null when nothing is shown.</summary>
    internal static string? MessageKey(TelnetConnectOutcome outcome) => outcome switch
    {
        TelnetConnectOutcome.Cancelled => null,
        TelnetConnectOutcome.TimedOut => TimedOutMessageKey,
        _ => FailedMessageKey
    };
}
