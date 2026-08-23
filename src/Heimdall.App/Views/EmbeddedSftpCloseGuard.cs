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

namespace Heimdall.App.Views;

/// <summary>Locale keys this guard surfaces.</summary>
/// <remarks>
/// Constants rather than literals so the localization test can reflect over the class: a key added
/// later without a translation is then a red test rather than silent drift.
/// </remarks>
public static class SftpCloseGuardLocaleKeys
{
    public const string Title = "SftpCloseGuardTitle";

    public const string TransferMessage = "SftpCloseGuardTransferMessage";

    public const string TransferBlocked = "SftpCloseGuardTransferBlocked";

    public const string EditorSaveBlocked = "SftpCloseGuardEditorSaveBlocked";

    public const string EditorDirtyMessage = "SftpCloseGuardEditorDirtyMessage";

    /// <summary>Title of the offer the editor overlay makes when a save will not end.</summary>
    public const string EditorSaveEscapeTitle = "SftpCloseGuardEditorSaveEscapeTitle";

    /// <summary>The offer itself. Operands: the pane label, then the local copy's folder.</summary>
    public const string EditorSaveEscapeMessage = "SftpCloseGuardEditorSaveEscapeMessage";

    /// <summary>Label of the answer that drops the connection.</summary>
    public const string EditorSaveEscapeConfirm = "SftpCloseGuardEditorSaveEscapeConfirm";

    /// <summary>Label of the answer that does nothing. Takes both Enter and Escape.</summary>
    public const string EditorSaveEscapeKeepWaiting = "SftpCloseGuardEditorSaveEscapeKeepWaiting";

    /// <summary>Reported when dropping the connection ended the save.</summary>
    public const string EditorSaveEscapeDropped = "SftpCloseGuardEditorSaveEscapeDropped";

    /// <summary>Reported when even dropping the connection did not end it.</summary>
    public const string EditorSaveEscapeStuck = "SftpCloseGuardEditorSaveEscapeStuck";
}

/// <summary>
/// What an SFTP pane has to protect when someone tries to close it, and what to do about it.
/// </summary>
/// <remarks>
/// It is a separate, WPF-free class rather than logic inside the view for two reasons: the view is
/// a <c>UserControl</c> that cannot be built without a desktop, and the decision here is the part
/// worth testing. The view keeps only the wiring - it reads its own fields into the delegates below
/// and forwards the three <see cref="ICloseGuard"/> members.
/// <para>
/// Note what this class deliberately does NOT do: it never cancels a transfer. The teardown that
/// follows a granted close cancels anyway, and cancelling here would destroy work for a close the
/// user may still abandon at a later prompt.
/// </para>
/// </remarks>
public sealed class EmbeddedSftpCloseGuard : ICloseGuard
{
    private readonly Func<SftpCloseGuardSnapshot> _sample;
    private readonly Func<string, string, Task<bool>> _confirmAsync;
    private readonly Func<string> _describePane;

    /// <param name="sample">
    /// Reads the pane's protectable state. Must be a single atomic read: the protocol compares the
    /// epoch across its two phases, and a torn read would compare an epoch against a busy flag
    /// sampled at a different instant.
    /// </param>
    /// <param name="confirmAsync">Raises a confirmation, given a title key and a message key.</param>
    /// <param name="describePane">The pane label to interpolate into a message.</param>
    public EmbeddedSftpCloseGuard(
        Func<SftpCloseGuardSnapshot> sample,
        Func<string, string, Task<bool>> confirmAsync,
        Func<string> describePane)
    {
        _sample = sample ?? throw new ArgumentNullException(nameof(sample));
        _confirmAsync = confirmAsync ?? throw new ArgumentNullException(nameof(confirmAsync));
        _describePane = describePane ?? throw new ArgumentNullException(nameof(describePane));
    }

    public CloseGuardState SampleCloseGuardState()
    {
        SftpCloseGuardSnapshot snapshot = _sample();
        return new CloseGuardState(snapshot.IsBusy, snapshot.Epoch);
    }

    /// <summary>
    /// The one place that decides whether a save in flight refuses a close, and under which
    /// sentence. Returns the locale key to show, or <see langword="null"/> when nothing refuses.
    /// </summary>
    /// <remarks>
    /// Extracted so every surface that can close this editor asks the same question and quotes the
    /// same answer. The pane, the split pane, the floating window and the editor overlay's own
    /// Close button all route here; a second copy of the condition is how the two surfaces would
    /// drift into disagreeing about one save.
    /// <para>
    /// Why the refusal is terminal, rather than a question the user could answer: NOT because a
    /// teardown would leave a half-written file on the server. It would not - all three write paths
    /// stage to a temporary name and publish by an atomic rename, so the destination is never
    /// touched before the write has completed. The real reason is that the write cannot be stopped.
    /// The upload sits in a synchronous stream write that takes no cancellation token, so granting
    /// the close would hand the user back a pane while the upload thread stays wedged inside the
    /// SSH library holding the browser's client lock, on a browser the teardown is about to
    /// dispose - and the save the user believed had been accepted would silently never happen.
    /// </para>
    /// </remarks>
    /// <param name="snapshot">One atomic read of the pane's protectable state.</param>
    public static string? DescribeEditorSaveRefusal(SftpCloseGuardSnapshot snapshot)
    {
        return snapshot.IsEditorSaveInProgress ? SftpCloseGuardLocaleKeys.EditorSaveBlocked : null;
    }

    public CloseDecision PollClose(CloseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        SftpCloseGuardSnapshot snapshot = _sample();

        // The refusal is terminal, so it has to be tested first.
        if (DescribeEditorSaveRefusal(snapshot) is { } reasonKey)
        {
            return CloseDecision.Deny(reasonKey, snapshot.Epoch);
        }

        // The rest is losable work the user is entitled to abandon knowingly, so it becomes a
        // question rather than a refusal - which is what needs the asynchronous phase.
        if (snapshot.IsTransferInProgress)
        {
            return CloseDecision.Defer(SftpCloseGuardLocaleKeys.TransferBlocked, snapshot.Epoch);
        }

        if (snapshot.HasUnsavedEditorChanges)
        {
            return CloseDecision.Defer(SftpCloseGuardLocaleKeys.EditorDirtyMessage, snapshot.Epoch);
        }

        return CloseDecision.Allow(snapshot.Epoch);
    }

    public async Task<bool> ResolveCloseAsync(CloseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        SftpCloseGuardSnapshot snapshot = _sample();

        // Re-checked rather than trusted from the poll: the work may have finished while an earlier
        // pane in the same gesture was being confirmed, and asking about a transfer that already
        // ended would be a prompt with no subject.
        if (!snapshot.IsBusy)
        {
            return true;
        }

        if (DescribeEditorSaveRefusal(snapshot) is not null)
        {
            return false;
        }

        string messageKey = snapshot.IsTransferInProgress
            ? SftpCloseGuardLocaleKeys.TransferMessage
            : SftpCloseGuardLocaleKeys.EditorDirtyMessage;

        return await _confirmAsync(SftpCloseGuardLocaleKeys.Title, messageKey).ConfigureAwait(true);
    }

    /// <summary>Pane label for a message, so the user knows which pane is being asked about.</summary>
    public string DescribePane() => _describePane();
}

/// <summary>
/// One atomic read of everything an SFTP pane protects.
/// </summary>
/// <param name="IsTransferInProgress">A file transfer is running.</param>
/// <param name="IsEditorSaveInProgress">The inline editor is writing a file back to the server.</param>
/// <param name="HasUnsavedEditorChanges">The inline editor holds edits that were never saved.</param>
/// <param name="Epoch">
/// Change stamp over the three flags above. Never read as a quantity: only equality across the
/// protocol's two phases matters.
/// </param>
public readonly record struct SftpCloseGuardSnapshot(
    bool IsTransferInProgress,
    bool IsEditorSaveInProgress,
    bool HasUnsavedEditorChanges,
    long Epoch)
{
    /// <summary>True when tearing the pane down right now would destroy work.</summary>
    public bool IsBusy => IsTransferInProgress || IsEditorSaveInProgress || HasUnsavedEditorChanges;
}
