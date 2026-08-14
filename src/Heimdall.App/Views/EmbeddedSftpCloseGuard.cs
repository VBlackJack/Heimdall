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

    public CloseDecision PollClose(CloseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        SftpCloseGuardSnapshot snapshot = _sample();

        // A save in flight is refused outright rather than confirmed. Tearing the pane down mid-save
        // would leave a half-written file on the server, and no answer the user could give would
        // make that acceptable - so this one is terminal, and it has to be tested first.
        if (snapshot.IsEditorSaveInProgress)
        {
            return CloseDecision.Deny(SftpCloseGuardLocaleKeys.EditorSaveBlocked, snapshot.Epoch);
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

        if (snapshot.IsEditorSaveInProgress)
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
