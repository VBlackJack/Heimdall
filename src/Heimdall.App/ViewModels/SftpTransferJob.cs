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

using CommunityToolkit.Mvvm.ComponentModel;
using Heimdall.Sftp;

namespace Heimdall.App.ViewModels;

/// <summary>The direction of a queued transfer.</summary>
public enum SftpTransferJobKind
{
    /// <summary>Local entries sent to a remote folder.</summary>
    Upload,

    /// <summary>Remote entries fetched to a local folder.</summary>
    Download,
}

/// <summary>Where a queued transfer stands.</summary>
public enum SftpTransferJobStatus
{
    /// <summary>Waiting for the running transfer to finish.</summary>
    Queued,

    /// <summary>Being transferred now.</summary>
    Running,

    /// <summary>Finished without error.</summary>
    Completed,

    /// <summary>Ended on an error; it can be retried.</summary>
    Failed,

    /// <summary>Stopped by the user; it can be retried.</summary>
    Cancelled,
}

/// <summary>
/// One batch handed to the transfer queue: the request that produced it, kept whole so a failed or
/// cancelled batch can be run again, and the state the queue list shows for it.
/// </summary>
public sealed partial class SftpTransferJob : ObservableObject
{
    private TaskCompletionSource _completion = NewCompletion();

    /// <summary>Initializes an upload job.</summary>
    internal SftpTransferJob(int id, string title, IReadOnlyList<string> localPaths, string remoteDirectory)
    {
        Id = id;
        Kind = SftpTransferJobKind.Upload;
        Title = title;
        LocalPaths = localPaths;
        RemoteDirectory = remoteDirectory;
        RemoteEntries = [];
        LocalDirectory = string.Empty;
    }

    /// <summary>Initializes a download job.</summary>
    internal SftpTransferJob(int id, string title, IReadOnlyList<SftpFileInfo> remoteEntries, string localDirectory)
    {
        Id = id;
        Kind = SftpTransferJobKind.Download;
        Title = title;
        LocalPaths = [];
        RemoteDirectory = string.Empty;
        RemoteEntries = remoteEntries;
        LocalDirectory = localDirectory;
    }

    /// <summary>Gets the stable identifier of the job within its pane.</summary>
    public int Id { get; }

    /// <summary>Gets the direction of the job.</summary>
    public SftpTransferJobKind Kind { get; }

    /// <summary>Gets the localized one-line description of the batch.</summary>
    public string Title { get; }

    /// <summary>Gets the local sources of an upload job.</summary>
    internal IReadOnlyList<string> LocalPaths { get; }

    /// <summary>Gets the remote destination folder of an upload job.</summary>
    internal string RemoteDirectory { get; }

    /// <summary>Gets the remote sources of a download job.</summary>
    internal IReadOnlyList<SftpFileInfo> RemoteEntries { get; }

    /// <summary>Gets the local destination folder of a download job.</summary>
    internal string LocalDirectory { get; }

    /// <summary>Gets or sets the current state of the job.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private SftpTransferJobStatus _status = SftpTransferJobStatus.Queued;

    /// <summary>Gets or sets the localized line describing the job's state or its failure.</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    /// <summary>Gets whether the job is waiting or running, so it can still be stopped.</summary>
    public bool CanCancel => Status is SftpTransferJobStatus.Queued or SftpTransferJobStatus.Running;

    /// <summary>Gets whether the job ended without completing, so it can be run again.</summary>
    public bool CanRetry => Status is SftpTransferJobStatus.Failed or SftpTransferJobStatus.Cancelled;

    /// <summary>Gets whether the job is the one being transferred now.</summary>
    public bool IsRunning => Status == SftpTransferJobStatus.Running;

    /// <summary>Gets the task that completes when the job has ended, whatever its outcome.</summary>
    internal Task Completion => _completion.Task;

    /// <summary>Gets or sets the token source cancelling the running job.</summary>
    internal CancellationTokenSource? Cancellation { get; set; }

    /// <summary>Signals the end of the current run.</summary>
    internal void SignalCompleted() => _completion.TrySetResult();

    /// <summary>Prepares the job for another run.</summary>
    internal void ResetForRetry()
    {
        _completion = NewCompletion();
        Cancellation = null;
        Detail = string.Empty;
    }

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
