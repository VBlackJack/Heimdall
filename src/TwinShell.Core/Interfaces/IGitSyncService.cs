/*
 * Copyright 2025 Julien Bombled
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

namespace TwinShell.Core.Interfaces;

/// <summary>
/// Error codes for Git synchronization operations.
/// Enables programmatic error handling and user-friendly messages.
/// </summary>
public enum GitSyncErrorCode
{
    /// <summary>No error occurred</summary>
    None = 0,

    /// <summary>Network connectivity issue</summary>
    NetworkError = 1,

    /// <summary>Authentication failed (invalid token or SSH key)</summary>
    AuthenticationFailed = 2,

    /// <summary>Remote repository not found or inaccessible</summary>
    RepositoryNotFound = 3,

    /// <summary>Git repository configuration is invalid or missing</summary>
    InvalidConfiguration = 4,

    /// <summary>Push rejected by remote (need to pull first)</summary>
    PushRejected = 5,

    /// <summary>Merge conflict detected during pull</summary>
    MergeConflict = 6,

    /// <summary>Local repository is not initialized</summary>
    RepositoryNotInitialized = 7,

    /// <summary>File I/O error during sync operations</summary>
    FileSystemError = 8,

    /// <summary>Data validation error during import</summary>
    ValidationError = 9,

    /// <summary>Operation was cancelled by user</summary>
    Cancelled = 10,

    /// <summary>Operation timed out</summary>
    Timeout = 11,

    /// <summary>Data conflict detected (local changes would be overwritten)</summary>
    DataConflict = 12,

    /// <summary>Unknown or unclassified error</summary>
    Unknown = 99
}

/// <summary>
/// Service for Git-based synchronization of TwinShell data.
/// Handles clone, pull, push operations with remote repositories.
/// </summary>
public interface IGitSyncService
{
    /// <summary>
    /// Gets whether a Git repository is configured and ready.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Gets whether a sync operation is currently in progress.
    /// Used to prevent concurrent operations.
    /// </summary>
    bool IsOperationInProgress { get; }

    /// <summary>
    /// Gets or sets the current synchronization status message.
    /// </summary>
    string StatusMessage { get; }

    /// <summary>
    /// Event raised when sync status changes.
    /// </summary>
    event EventHandler<GitSyncStatusEventArgs>? StatusChanged;

    /// <summary>
    /// Initializes the local repository. Clones if not exists, or validates existing.
    /// </summary>
    /// <returns>Result of the initialization</returns>
    Task<GitOperationResult> InitializeRepositoryAsync();

    /// <summary>
    /// Pulls latest changes from remote and imports into database.
    /// </summary>
    /// <returns>Result of the pull operation</returns>
    Task<GitOperationResult> PullAndImportAsync();

    /// <summary>
    /// Exports database to YAML, commits and pushes to remote.
    /// </summary>
    /// <param name="commitMessage">Optional commit message</param>
    /// <returns>Result of the push operation</returns>
    Task<GitOperationResult> ExportAndPushAsync(string? commitMessage = null);

    /// <summary>
    /// Performs a full sync: pull, merge, export changes, push.
    /// </summary>
    /// <returns>Result of the sync operation</returns>
    Task<GitOperationResult> FullSyncAsync();

    /// <summary>
    /// Tests the connection to the remote repository.
    /// </summary>
    /// <returns>True if connection is successful</returns>
    Task<GitOperationResult> TestConnectionAsync();

    /// <summary>
    /// Gets the current local repository status (branch, ahead/behind, etc.)
    /// </summary>
    /// <returns>Repository status information</returns>
    Task<GitRepositoryStatus> GetRepositoryStatusAsync();

    /// <summary>
    /// Cancels any currently running sync operation.
    /// </summary>
    void CancelOperation();
}

/// <summary>
/// Result of a Git operation
/// </summary>
public sealed class GitOperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ErrorDetails { get; set; }
    public GitSyncErrorCode ErrorCode { get; set; } = GitSyncErrorCode.None;
    public int ItemsImported { get; set; }
    public int ItemsExported { get; set; }
    public int ItemsUpdated { get; set; }
    public int ItemsSkipped { get; set; }
    public int ConflictsDetected { get; set; }
    public int CommitsMerged { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<SyncConflict> Conflicts { get; set; } = new();

    /// <summary>List of files with Git merge conflicts</summary>
    public List<string> ConflictedFiles { get; set; } = new();

    public static GitOperationResult Ok(string message = "Operation completed successfully")
        => new() { Success = true, Message = message, ErrorCode = GitSyncErrorCode.None };

    public static GitOperationResult Fail(string message, GitSyncErrorCode errorCode = GitSyncErrorCode.Unknown, string? details = null)
        => new() { Success = false, Message = message, ErrorCode = errorCode, ErrorDetails = details };

    /// <summary>
    /// Creates a result indicating data conflicts were detected
    /// </summary>
    public static GitOperationResult WithConflicts(string message, List<SyncConflict> conflicts)
        => new()
        {
            Success = false,
            Message = message,
            ErrorCode = GitSyncErrorCode.DataConflict,
            ConflictsDetected = conflicts.Count,
            Conflicts = conflicts
        };

    /// <summary>
    /// Creates a result indicating Git merge conflicts were detected
    /// </summary>
    public static GitOperationResult WithMergeConflicts(string message, List<string> conflictedFiles)
        => new()
        {
            Success = false,
            Message = message,
            ErrorCode = GitSyncErrorCode.MergeConflict,
            ConflictsDetected = conflictedFiles.Count,
            ConflictedFiles = conflictedFiles
        };
}

/// <summary>
/// Represents a sync conflict between local and remote data
/// </summary>
public sealed class SyncConflict
{
    /// <summary>Entity type (Action, Template, Category, Batch)</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>PublicId of the conflicting entity</summary>
    public Guid EntityId { get; set; }

    /// <summary>Display name/title of the entity</summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>Local modification timestamp</summary>
    public DateTime LocalModifiedAt { get; set; }

    /// <summary>Remote modification timestamp</summary>
    public DateTime RemoteModifiedAt { get; set; }

    /// <summary>Resolution chosen by user (null if not yet resolved)</summary>
    public ConflictResolution? Resolution { get; set; }
}

/// <summary>
/// Resolution options for sync conflicts
/// </summary>
public enum ConflictResolution
{
    /// <summary>Keep local version, discard remote</summary>
    KeepLocal,

    /// <summary>Use remote version, discard local</summary>
    UseRemote,

    /// <summary>Skip this entity, don't sync</summary>
    Skip
}

/// <summary>
/// Status of the local Git repository
/// </summary>
public sealed class GitRepositoryStatus
{
    public bool IsInitialized { get; set; }
    public string? CurrentBranch { get; set; }
    public int CommitsAhead { get; set; }
    public int CommitsBehind { get; set; }
    public bool HasLocalChanges { get; set; }
    public DateTime? LastSyncTime { get; set; }
    public string? LastCommitMessage { get; set; }
    public string? RemoteUrl { get; set; }
}

/// <summary>
/// Event args for sync status changes
/// </summary>
public sealed class GitSyncStatusEventArgs : EventArgs
{
    /// <summary>Current status message</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Whether an operation is currently in progress</summary>
    public bool IsOperationInProgress { get; set; }

    /// <summary>Overall progress (0-100)</summary>
    public double? Progress { get; set; }

    /// <summary>Current phase of the operation</summary>
    public SyncPhase Phase { get; set; } = SyncPhase.Idle;

    /// <summary>Current file being processed (if applicable)</summary>
    public string? CurrentFile { get; set; }

    /// <summary>Total number of files to process</summary>
    public int TotalFiles { get; set; }

    /// <summary>Number of files processed so far</summary>
    public int ProcessedFiles { get; set; }

    /// <summary>Current entity type being processed</summary>
    public string? CurrentEntityType { get; set; }
}

/// <summary>
/// Phases of a sync operation
/// </summary>
public enum SyncPhase
{
    /// <summary>No operation in progress</summary>
    Idle,

    /// <summary>Initializing repository</summary>
    Initializing,

    /// <summary>Fetching from remote</summary>
    Fetching,

    /// <summary>Merging changes</summary>
    Merging,

    /// <summary>Importing data from files</summary>
    Importing,

    /// <summary>Exporting data to files</summary>
    Exporting,

    /// <summary>Staging changes</summary>
    Staging,

    /// <summary>Committing changes</summary>
    Committing,

    /// <summary>Pushing to remote</summary>
    Pushing,

    /// <summary>Validating data</summary>
    Validating,

    /// <summary>Detecting conflicts</summary>
    DetectingConflicts,

    /// <summary>Completed</summary>
    Completed,

    /// <summary>Failed</summary>
    Failed
}
