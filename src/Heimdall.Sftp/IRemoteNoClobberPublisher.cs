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
/// Publishes a remote file or directory that must not replace anything already at the destination.
/// </summary>
/// <remarks>
/// Opt-in capability rather than a member of <see cref="IRemoteBrowser"/>. An upload carries no
/// existence contract and legitimately replaces its destination, so a caller that needs the opposite
/// cannot express it through that seam. Widening the browser interface would also force every
/// implementation to answer for a guarantee only some transports can make.
/// <para>
/// Declared <c>public</c> deliberately: the consumers are the clipboard view model and the logging
/// decorator, both in the application assembly, which is not granted access to this assembly's
/// internals. An internal interface would still satisfy the test assemblies, so a prototype would
/// compile and pass while the application build failed.
/// </para>
/// <para>
/// A transport that cannot honour the contract simply does not implement this interface, and the
/// caller must refuse before mutating the destination. Implementing it while falling back to a
/// replacing primitive is the one thing this contract forbids.
/// </para>
/// </remarks>
public interface IRemoteNoClobberPublisher
{
    /// <summary>
    /// Publishes <paramref name="localPath"/> at <paramref name="remotePath"/>, failing if anything
    /// already exists there.
    /// </summary>
    /// <remarks>
    /// An <b>existing</b> destination is never replaced, and no partial content is ever observable at
    /// the destination: bytes land on a staging path reserved exclusively, and the destination only
    /// appears once it already holds the complete content.
    /// <para>
    /// What is <b>not</b> promised is that a failure leaves the destination absent. A transport
    /// failure or a cancellation can arrive after the publish has already taken effect but before its
    /// answer comes back, so a <b>new</b> destination may exist even though the call threw. The
    /// outcome is then simply unconfirmed, and an unconfirmed outcome must never be promoted to
    /// success. In particular, the caller must not probe the destination afterwards and treat its
    /// presence as proof the publication was its own.
    /// </para>
    /// <para>
    /// A cut therefore keeps its source and asks for its listing to be reloaded rather than deleting
    /// anything, since "the file is there" does not establish that this call put it there or that the
    /// content is complete from this call's perspective. The reload is an attempt, not a promise: if the
    /// session or the listing is gone it simply does not happen, and that changes nothing about the
    /// outcome, which stays unconfirmed.
    /// </para>
    /// <para>
    /// Cancellation raises <see cref="OperationCanceledException"/> and is never reported as a
    /// refusal: the caller withdrawing and the server being unable to publish safely are different
    /// outcomes, and a cut must not treat the first as a completed transfer.
    /// </para>
    /// <para>
    /// This is per-file atomicity, not transactional atomicity over a tree. See
    /// <see cref="CreateDirectoryExclusiveAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="localPath">Local file whose content is published.</param>
    /// <param name="remotePath">Remote destination that must not already exist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="RemoteDestinationExistsException">The destination already exists.</exception>
    /// <exception cref="RemoteNoClobberPublishUnavailableException">
    /// The publication could not be confirmed. It may not have been attempted, or it may have taken
    /// effect with its answer lost; either way this call cannot claim it published.
    /// </exception>
    Task PublishFileIfAbsentAsync(string localPath, string remotePath, CancellationToken ct = default);

    /// <summary>
    /// Creates <paramref name="remotePath"/> as a new directory, failing if anything already exists
    /// there.
    /// </summary>
    /// <remarks>
    /// Reserves the directory exclusively; it never merges into an existing one. Every node of a
    /// pasted tree goes through this, root and descendants alike, because a merge is how a stale
    /// listing turns into an overwritten subtree.
    /// <para>
    /// A tree that fails partway may therefore be left incomplete on the server. That is deliberate:
    /// a recursive cleanup of a directory this call created could delete entries another party added
    /// inside it in the meantime, so an incomplete tree is preferred to a destructive rollback. The
    /// caller stops the failing root and publishes none of its remaining entries.
    /// </para>
    /// <para>
    /// As with a file publish, a transport failure or cancellation can arrive after the directory was
    /// already created. The reservation is then unconfirmed rather than known to have failed, and it is
    /// never promoted to success.
    /// </para>
    /// </remarks>
    /// <param name="remotePath">Remote directory path that must not already exist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="RemoteDestinationExistsException">The path already exists.</exception>
    /// <exception cref="RemoteNoClobberPublishUnavailableException">
    /// The reservation could not be confirmed. It may not have been attempted, or the directory may
    /// have been created with its answer lost.
    /// </exception>
    Task CreateDirectoryExclusiveAsync(string remotePath, CancellationToken ct = default);
}

/// <summary>
/// Exposes whether a browser can publish without replacing an existing destination.
/// </summary>
/// <remarks>
/// A decorator must answer for the browser it wraps rather than for itself, so the capability is
/// asked for through this seam instead of a type test. Type tests silently give the wrong answer
/// once a browser is decorated, and the operations-log decorator is applied only on some paths,
/// which would make the same probe behave differently depending on unrelated configuration.
/// </remarks>
public interface IRemoteNoClobberCapability
{
    /// <summary>
    /// Returns the publisher to use, or <c>null</c> when no-clobber publication is unsupported.
    /// </summary>
    /// <remarks>
    /// Returning <c>null</c> obliges the caller to refuse before mutating the destination. A
    /// decorator must delegate to its inner browser and must not claim the capability on its own
    /// behalf: claiming it while the inner cannot honour it produces a guard that refuses everything
    /// and a feature that is silently dead.
    /// </remarks>
    IRemoteNoClobberPublisher? NoClobberPublisher { get; }
}

/// <summary>
/// Represents a publication refused because the destination already exists.
/// </summary>
/// <remarks>
/// Distinct from <see cref="RemoteNoClobberPublishUnavailableException"/>: here the guarantee held
/// and stopped the publication, rather than the outcome being unconfirmed. Derives from <see cref="IOException"/> so
/// callers that only catch that keep working.
/// </remarks>
public sealed class RemoteDestinationExistsException : IOException
{
    /// <summary>
    /// Initializes a new instance for a destination that was already present.
    /// </summary>
    /// <param name="remotePath">Destination that already exists.</param>
    /// <param name="innerException">Underlying transport failure, when one was observed.</param>
    public RemoteDestinationExistsException(string remotePath, Exception? innerException = null)
        : base($"Refused to publish: destination already exists: {remotePath}", innerException)
    {
        RemotePath = remotePath;
    }

    /// <summary>Gets the destination that already exists.</summary>
    public string RemotePath { get; }
}

/// <summary>
/// Represents a publication whose outcome could not be confirmed.
/// </summary>
/// <remarks>
/// Covers every outcome the call cannot confirm as a publication: no pinned connection context, a
/// refused host key, a transport failure, a lost response, or a non-zero exit whose cause cannot be
/// distinguished.
/// <para>
/// It does <b>not</b> assert that the primitive was impossible, nor that the destination is absent.
/// A response can be lost after the link or the directory creation already took effect, so a new
/// destination may exist. What the exception states is only that this call cannot claim to have
/// published. An existing destination is still never replaced, and the caller must not retry through
/// a replacing path, nor delete a cut source, nor read the destination's presence as its own success.
/// </para>
/// </remarks>
public sealed class RemoteNoClobberPublishUnavailableException : IOException
{
    /// <summary>
    /// Initializes an exception for an unconfirmed publication outcome.
    /// </summary>
    /// <param name="remotePath">Requested destination.</param>
    /// <param name="reason">Short, non-sensitive reason the outcome is unconfirmed.</param>
    /// <param name="innerException">Underlying transport failure, when one was observed.</param>
    public RemoteNoClobberPublishUnavailableException(
        string remotePath,
        string reason,
        Exception? innerException = null)
        : base(
            $"Publication of '{remotePath}' could not be confirmed ({reason}). The destination may or " +
            "may not have been created, an existing destination was not replaced, and Heimdall does " +
            "not fall back to a transfer that could replace one.",
            innerException)
    {
        RemotePath = remotePath;
        Reason = reason;
    }

    /// <summary>Gets the requested destination.</summary>
    public string RemotePath { get; }

    /// <summary>Gets the short, non-sensitive reason the outcome is unconfirmed.</summary>
    public string Reason { get; }
}
