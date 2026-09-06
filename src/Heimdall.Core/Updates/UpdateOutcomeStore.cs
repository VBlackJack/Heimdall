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

using System.Text.Json;

namespace Heimdall.Core.Updates;

/// <summary>What a previous update attempt left behind, read as one unit.</summary>
/// <param name="Attempt">What the application intended to become.</param>
/// <param name="Failure">
/// What the relauncher recorded, when it got far enough to record anything. Null is
/// ordinary: the relauncher may have been killed, or the script may never have parsed.
/// </param>
public sealed record PendingUpdateOutcome(
    UpdateAttemptRecord Attempt,
    UpdateFailureRecord? Failure);

/// <summary>Records what an update attempt intended, across the process boundary.</summary>
public interface IUpdateOutcomeStore
{
    /// <summary>Records an attempt, replacing any previous one.</summary>
    void WriteAttempt(string attemptedVersion);

    /// <summary>Discards any pending record.</summary>
    void Clear();

    /// <summary>
    /// Returns what is pending and removes it, so a given attempt is reported once.
    /// </summary>
    PendingUpdateOutcome? TryTakePending();
}

/// <summary>
/// File-backed <see cref="IUpdateOutcomeStore"/>, living in the application's own data
/// directory rather than beside the installed binaries.
/// </summary>
/// <remarks>
/// The location matters: the installer replaces the install directory, so a record kept
/// there would not reliably survive the very event it exists to describe. The data
/// directory is a different tree and nothing in the application deletes it.
/// </remarks>
public sealed class UpdateOutcomeStore : IUpdateOutcomeStore
{
    /// <summary>
    /// Beyond this, a record is assumed to belong to some earlier session rather than to
    /// the launch now starting, and is discarded without a word.
    /// </summary>
    private static readonly TimeSpan MaxAttemptAge = TimeSpan.FromHours(6);

    /// <summary>
    /// Reading options. Case-insensitive, and that is not a convenience.
    /// </summary>
    /// <remarks>
    /// One of these two files is written by hand-built JSON inside the generated
    /// PowerShell, in camelCase, while the serializer's default output for these records
    /// is PascalCase and its default reader is case-SENSITIVE. Without this the failure
    /// record would parse to null every time and the cause would silently never be
    /// reported - a writer and a reader disagreeing about one format, which is the shape
    /// this codebase keeps rediscovering.
    /// </remarks>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private const string AttemptFileName = "update-attempt.json";

    private const string FailureFileName = "update-failure.json";

    /// <summary>Appended to the attempt file's name while it is being taken.</summary>
    private const string TakenSuffix = ".taken";

    private readonly string _directory;
    private readonly TimeProvider _timeProvider;

    public UpdateOutcomeStore(string directory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private string AttemptPath => Path.Combine(_directory, AttemptFileName);

    private string FailurePath => Path.Combine(_directory, FailureFileName);

    /// <summary>Where the relauncher writes what it recorded about a failure.</summary>
    public static string FailureRecordPathIn(string directory) =>
        Path.Combine(directory, FailureFileName);

    public void WriteAttempt(string attemptedVersion)
    {
        ArgumentNullException.ThrowIfNull(attemptedVersion);

        var record = new UpdateAttemptRecord(
            UpdateAttemptRecord.CurrentSchemaVersion,
            attemptedVersion,
            _timeProvider.GetUtcNow());

        try
        {
            Directory.CreateDirectory(_directory);

            // A previous run's failure record must never be read against this
            // attempt: it describes something else entirely. Deleted FIRST, so that a
            // delete that fails - the file held open by a relauncher from an earlier
            // attempt, or by an indexer - leaves no attempt for it to be paired with.
            // The other order wrote the attempt and then failed to delete, and the next
            // startup reported the new attempt with the old cause.
            File.Delete(FailurePath);
            File.WriteAllText(AttemptPath, JsonSerializer.Serialize(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the record costs a report, never the update itself. This runs
            // moments before the application exits to be replaced; throwing here would
            // turn a missing explanation into a failed update.
            Logging.FileLogger.Warn($"Update attempt record not written: {ex.Message}");
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(AttemptPath);
            File.Delete(FailurePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.FileLogger.Warn($"Update attempt record not cleared: {ex.Message}");
        }
    }

    public PendingUpdateOutcome? TryTakePending()
    {
        // Taken by rename, not read-then-delete. A rename is atomic on the same volume,
        // so of two instances starting together exactly one gets the file and the other
        // finds nothing; a read followed by a delete let both read before either
        // deleted, and both reported. It also removes the file before parsing, so a
        // record that cannot be parsed is not read again on every launch forever.
        string? attemptContent = TakeOrNull(AttemptPath);
        if (attemptContent is null)
        {
            return null;
        }

        // The failure record is read BEFORE the delete, and only when an attempt exists.
        // On its own it means nothing: it describes how a relauncher ended, not which
        // update it belonged to.
        string? failureContent = ReadOrNull(FailurePath);
        Clear();

        UpdateAttemptRecord? attempt = Parse<UpdateAttemptRecord>(attemptContent);
        if (attempt is null || attempt.SchemaVersion != UpdateAttemptRecord.CurrentSchemaVersion)
        {
            return null;
        }

        if (_timeProvider.GetUtcNow() - attempt.StartedUtc > MaxAttemptAge)
        {
            return null;
        }

        // A failure record that will not parse costs the CAUSE, never the report. The
        // attempt alone still supports the statement that the version did not move.
        UpdateFailureRecord? failure = failureContent is null
            ? null
            : Parse<UpdateFailureRecord>(failureContent);
        if (failure is not null && failure.SchemaVersion != UpdateFailureRecord.CurrentSchemaVersion)
        {
            failure = null;
        }

        return new PendingUpdateOutcome(attempt, failure);
    }

    /// <summary>
    /// Moves the file aside under a private name, reads it, and deletes it. The move is
    /// the claim: whoever completes it owns the record.
    /// </summary>
    /// <remarks>
    /// The private name is overwritten rather than refused, so a leftover from a run
    /// that died between the move and the delete cannot block every later take.
    /// </remarks>
    private static string? TakeOrNull(string path)
    {
        string taken = path + TakenSuffix;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            File.Move(path, taken, overwrite: true);
            string content = File.ReadAllText(taken);
            File.Delete(taken);
            return content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadOrNull(string path)
    {
        try
        {
            // ReadAllText so a byte-order mark is stripped; the byte-span deserializer
            // would throw on one.
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static T? Parse<T>(string content)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(content, ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
