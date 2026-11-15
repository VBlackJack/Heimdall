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

namespace Heimdall.App.Services;

/// <summary>
/// The one funnel every act the SFTP pane runs as root goes through. Each act is journaled exactly
/// as before by the <see cref="SessionOperationEmitter"/> it wraps, and is also recorded in the
/// pane's <see cref="SftpRootActivityLog"/> so the operator can see afterwards that root was used.
/// </summary>
/// <remarks>
/// <para>This file is the only place that marks an operation record as privileged. A new escalation
/// site that bypasses it is a site the indicator would not show, and the funnel guards read the
/// source for exactly that.</para>
/// <para>Nothing here changes what runs: the operation is awaited as it was, the journal receives
/// the same single record, and an exception leaves through <c>throw;</c> so the caller sees the
/// same object and its filters keep matching. The indicator records whether or not the journal's
/// live gate is open: the two are independent.</para>
/// </remarks>
internal sealed class SftpPrivilegedActRecorder
{
    private readonly SessionOperationEmitter _journal;
    private readonly SftpRootActivityLog _log;

    /// <summary>Creates a recorder over the session journal and the pane's root activity log.</summary>
    public SftpPrivilegedActRecorder(SessionOperationEmitter journal, SftpRootActivityLog log)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Runs a privileged upload: journaled, and recorded as <paramref name="kind"/>.</summary>
    public Task RunUploadAsync(
        SftpRootActKind kind,
        string localPath,
        string remotePath,
        Func<Task> operation,
        Func<long> bytesOnSuccess)
        => TrackAsync(
            kind,
            remotePath,
            targetPath: null,
            () => _journal.RunUploadAsync(localPath, remotePath, operation, bytesOnSuccess, privileged: true));

    /// <summary>Runs a privileged download: journaled, and recorded.</summary>
    public Task RunDownloadAsync(
        string remotePath,
        string localPath,
        Func<Task> operation,
        Func<long> bytesOnSuccess)
        => TrackAsync(
            SftpRootActKind.Download,
            remotePath,
            targetPath: null,
            () => _journal.RunDownloadAsync(remotePath, localPath, operation, bytesOnSuccess, privileged: true));

    /// <summary>Runs a privileged mkdir: journaled, and recorded.</summary>
    public Task RunMkdirAsync(string path, Func<Task> operation)
        => TrackAsync(
            SftpRootActKind.NewFolder,
            path,
            targetPath: null,
            () => _journal.RunMkdirAsync(path, operation, privileged: true));

    /// <summary>Runs a privileged delete: journaled, and recorded.</summary>
    public Task RunDeleteAsync(string path, Func<Task> operation)
        => TrackAsync(
            SftpRootActKind.Delete,
            path,
            targetPath: null,
            () => _journal.RunDeleteAsync(path, operation, privileged: true));

    /// <summary>Runs a privileged chmod: journaled, and recorded.</summary>
    public Task RunChmodAsync(string path, Func<Task> operation)
        => TrackAsync(
            SftpRootActKind.PermissionChange,
            path,
            targetPath: null,
            () => _journal.RunChmodAsync(path, operation, privileged: true));

    /// <summary>Runs a privileged rename: journaled, and recorded.</summary>
    public Task RunRenameAsync(string oldPath, string newPath, Func<Task> operation)
        => TrackAsync(
            SftpRootActKind.Rename,
            oldPath,
            newPath,
            () => _journal.RunRenameAsync(oldPath, newPath, operation, privileged: true));

    /// <summary>
    /// Records a save the editor already completed as root, and journals it as before. The pane
    /// learns of such a save from the editor's signal, so there is no operation to run here.
    /// </summary>
    public void RecordEditorSave(
        string localPath,
        string remotePath,
        bool success,
        Func<long> bytesOnSuccess)
    {
        try
        {
            _journal.EmitUploadCompleted(localPath, remotePath, success, bytesOnSuccess, privileged: true);
        }
        finally
        {
            _log.Record(
                SftpRootActKind.ExternalEditorSave,
                remotePath,
                targetPath: null,
                success ? SftpRootActOutcome.Succeeded : SftpRootActOutcome.Failed);
        }
    }

    /// <summary>
    /// Runs a privileged directory listing and records it. A listing is not journaled, as before,
    /// and is counted apart from the operations.
    /// </summary>
    public async Task<T> RunListingAsync<T>(string path, Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        T result = default!;
        await TrackAsync(
                SftpRootActKind.Listing,
                path,
                targetPath: null,
                async () => result = await operation().ConfigureAwait(false))
            .ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Runs the privileged opening of a file for editing and records it. It is not journaled, as
    /// before: the later saves are, through <see cref="RecordEditorSave"/>.
    /// </summary>
    public Task RunEditOpenAsync(string path, Func<Task> operation)
        => TrackAsync(SftpRootActKind.EditOpen, path, targetPath: null, operation);

    // Runs the operation, then records how it ended. The exception is rethrown by the same
    // statement that caught it, so the caller sees the same object with its stack, and a filter
    // on its type keeps matching.
    private async Task TrackAsync(
        SftpRootActKind kind,
        string path,
        string? targetPath,
        Func<Task> run)
    {
        ArgumentNullException.ThrowIfNull(run);

        try
        {
            await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _log.Record(kind, path, targetPath, SftpRootActOutcome.Cancelled);
            throw;
        }
        catch
        {
            _log.Record(kind, path, targetPath, SftpRootActOutcome.Failed);
            throw;
        }

        _log.Record(kind, path, targetPath, SftpRootActOutcome.Succeeded);
    }
}
