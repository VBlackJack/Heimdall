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

namespace Heimdall.Sftp;

/// <summary>
/// Why a server-side copy attempt did or did not produce a safe copy.
/// </summary>
/// <remarks>
/// Named rather than boolean because the caller must tell a decline apart from a transport failure.
/// Cancellation is deliberately absent: it is not an outcome of the attempt but the caller
/// withdrawing, and it leaves as <see cref="OperationCanceledException"/>.
/// </remarks>
public enum ServerSideCopyOutcome
{
    /// <summary>The server copied and reserved the destination exclusively.</summary>
    Succeeded,

    /// <summary>No pinned connection context was retained, so no verifiable channel could be opened.</summary>
    NoPinnedContext,

    /// <summary>The command ran and failed: a collision, a cross-device link, or a missing tool.</summary>
    NonZeroExit,

    /// <summary>The command outran its own cap while the caller was still waiting.</summary>
    CommandTimedOut,

    /// <summary>The freshly opened exec channel presented an unexpected host key.</summary>
    HostKeyRejected,

    /// <summary>The channel, or the transport underneath it, failed.</summary>
    TransportFailed,
}

/// <summary>
/// Represents a remote copy refused because the transport cannot publish a destination without
/// risking an existing one.
/// </summary>
/// <remarks>
/// Derives from <see cref="IOException"/> so the documented failure contract of
/// <c>IRemoteBrowser.CopyAsync</c> still holds for callers that only catch that type. Callers
/// that can localize the reason match on this type instead.
/// </remarks>
public sealed class RemoteCopyUnsupportedException : IOException
{
    /// <summary>
    /// Initializes a new instance for a copy the transport cannot perform safely.
    /// </summary>
    /// <param name="sourcePath">Requested copy source.</param>
    /// <param name="destinationPath">Requested copy destination.</param>
    /// <param name="transport">Transport that cannot guarantee the destination is untouched.</param>
    public RemoteCopyUnsupportedException(
        string sourcePath,
        string destinationPath,
        string transport)
        : base(
            $"{transport} cannot copy '{sourcePath}' to '{destinationPath}' without risking an " +
            "existing destination: the protocol offers no commit that fails when the destination " +
            "already exists.")
    {
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        Transport = transport;
    }

    /// <summary>
    /// Initializes a new instance for an SFTP copy whose safe server-side path was unavailable.
    /// </summary>
    /// <remarks>
    /// A distinct reason from the protocol-level one above. SFTP does have a commit that fails on an
    /// existing destination; what failed here is reaching it, so saying "the protocol offers no such
    /// commit" would be factually wrong for this transport.
    /// </remarks>
    /// <param name="sourcePath">Requested copy source.</param>
    /// <param name="destinationPath">Requested copy destination.</param>
    /// <param name="outcome">Outcome that denied the safe path; must not be a success.</param>
    public RemoteCopyUnsupportedException(
        string sourcePath,
        string destinationPath,
        ServerSideCopyOutcome outcome)
        : base(
            $"SFTP cannot copy '{sourcePath}' to '{destinationPath}' safely: the server-side copy " +
            $"was not performed ({outcome}), and no other route reserves the destination " +
            "exclusively.")
    {
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        Transport = "SFTP";
        Outcome = outcome;
    }

    /// <summary>Gets the requested copy source.</summary>
    public string SourcePath { get; }

    /// <summary>Gets the requested copy destination.</summary>
    public string DestinationPath { get; }

    /// <summary>Gets the transport that cannot guarantee an untouched destination.</summary>
    public string Transport { get; }

    /// <summary>
    /// Gets the server-side outcome that denied the safe path, or <c>null</c> when the transport
    /// offers no safe commit at all rather than having failed to reach one.
    /// </summary>
    public ServerSideCopyOutcome? Outcome { get; }
}
