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
using Heimdall.Sftp;
using Renci.SshNet.Common;

namespace Heimdall.App.Services;

/// <summary>The reason a remote directory could not be listed.</summary>
public enum SftpListingFailure
{
    /// <summary>The path does not exist on the server.</summary>
    NotFound,

    /// <summary>The server refused access to the path.</summary>
    PermissionDenied,

    /// <summary>The connection to the server is gone.</summary>
    Disconnected,

    /// <summary>The server did not answer in time.</summary>
    Timeout,

    /// <summary>Any other failure.</summary>
    Other,
}

/// <summary>
/// Classifies a failed directory listing so the message names the path and the cause, instead of
/// reporting a navigation as a failed file transfer.
/// </summary>
public static class SftpListingErrorClassifier
{
    /// <summary>Maps a listing exception to its failure reason.</summary>
    /// <param name="exception">The exception raised by the listing.</param>
    /// <param name="browserConnected">Whether the browser still reports a live connection.</param>
    public static SftpListingFailure Classify(Exception exception, bool browserConnected)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (!browserConnected)
        {
            return SftpListingFailure.Disconnected;
        }

        if (RemotePathAbsence.IsPathNotFound(exception))
        {
            return SftpListingFailure.NotFound;
        }

        return exception switch
        {
            SftpPermissionDeniedException => SftpListingFailure.PermissionDenied,
            UnauthorizedAccessException => SftpListingFailure.PermissionDenied,
            TimeoutException => SftpListingFailure.Timeout,
            SshOperationTimeoutException => SftpListingFailure.Timeout,
            SshConnectionException => SftpListingFailure.Disconnected,
            SocketException => SftpListingFailure.Disconnected,
            ObjectDisposedException => SftpListingFailure.Disconnected,
            _ => SftpListingFailure.Other,
        };
    }

    /// <summary>The catalogue key of the message for a failure reason; every message takes the path as {0}.</summary>
    public static string LocaleKey(SftpListingFailure failure) => failure switch
    {
        SftpListingFailure.NotFound => "SftpErrorListingNotFound",
        SftpListingFailure.PermissionDenied => "SftpErrorListingDenied",
        SftpListingFailure.Disconnected => "SftpErrorListingDisconnected",
        SftpListingFailure.Timeout => "SftpErrorListingTimeout",
        _ => "SftpErrorListingFailed",
    };
}
