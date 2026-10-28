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
using System.Security.Authentication;
using FluentFTP.Exceptions;

namespace Heimdall.App.Services.Handlers;

/// <summary>Why an FTP connect failed, as far as the user can act on it.</summary>
public enum FtpConnectFailureKind
{
    /// <summary>No class matched: the generic message and the log line are all there is.</summary>
    Generic,

    /// <summary>The server refused the credentials.</summary>
    Authentication,

    /// <summary>The host could not be reached or refused the connection.</summary>
    HostUnreachable,

    /// <summary>The server did not answer in time.</summary>
    Timeout,

    /// <summary>The TLS channel could not be established.</summary>
    Tls
}

/// <summary>
/// Maps the exception of a failed FTP connect to a class and to the locale key of its message.
/// </summary>
/// <remarks>
/// FluentFTP wraps transport errors, so the whole inner-exception chain is read, outermost first.
/// </remarks>
internal static class FtpConnectFailureClassifier
{
    /// <summary>The 530 reply code: not logged in.</summary>
    private const string NotLoggedInReplyCode = "530";

    private const string GenericMessageKey = "ErrorFtpConnectionFailed";
    private const string AuthenticationMessageKey = "ErrorFtpAuthenticationFailed";
    private const string HostUnreachableMessageKey = "ErrorFtpHostUnreachable";
    private const string TimeoutMessageKey = "ErrorFtpConnectionTimedOut";
    private const string TlsMessageKey = "ErrorFtpTlsFailed";

    /// <summary>The class of <paramref name="exception"/>, or <see cref="FtpConnectFailureKind.Generic"/>.</summary>
    internal static FtpConnectFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (Exception candidate in Chain(exception))
        {
            FtpConnectFailureKind? kind = ClassifyOne(candidate);
            if (kind is not null)
            {
                return kind.Value;
            }
        }

        return FtpConnectFailureKind.Generic;
    }

    /// <summary>The locale key of the message shown for <paramref name="kind"/>.</summary>
    internal static string MessageKey(FtpConnectFailureKind kind) => kind switch
    {
        FtpConnectFailureKind.Authentication => AuthenticationMessageKey,
        FtpConnectFailureKind.HostUnreachable => HostUnreachableMessageKey,
        FtpConnectFailureKind.Timeout => TimeoutMessageKey,
        FtpConnectFailureKind.Tls => TlsMessageKey,
        _ => GenericMessageKey
    };

    private static FtpConnectFailureKind? ClassifyOne(Exception exception) => exception switch
    {
        FtpAuthenticationException => FtpConnectFailureKind.Authentication,
        FtpCommandException command when IsNotLoggedIn(command) => FtpConnectFailureKind.Authentication,
        AuthenticationException => FtpConnectFailureKind.Tls,
        FtpInvalidCertificateException => FtpConnectFailureKind.Tls,
        FtpSecurityNotAvailableException => FtpConnectFailureKind.Tls,
        TimeoutException => FtpConnectFailureKind.Timeout,
        SocketException { SocketErrorCode: SocketError.TimedOut } => FtpConnectFailureKind.Timeout,
        SocketException => FtpConnectFailureKind.HostUnreachable,

        // Reached only when the caller did not cancel: an internal connect timeout.
        OperationCanceledException => FtpConnectFailureKind.Timeout,
        _ => null
    };

    private static bool IsNotLoggedIn(FtpCommandException exception)
        => exception.CompletionCode?.StartsWith(NotLoggedInReplyCode, StringComparison.Ordinal) == true;

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        Queue<Exception> pending = new();
        pending.Enqueue(exception);

        while (pending.Count > 0)
        {
            Exception current = pending.Dequeue();
            yield return current;

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    pending.Enqueue(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Enqueue(current.InnerException);
            }
        }
    }
}
