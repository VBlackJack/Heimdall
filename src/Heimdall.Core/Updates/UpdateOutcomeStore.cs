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

/// <summary>Records what an update attempt intended, across the process boundary.</summary>
public interface IUpdateOutcomeStore
{
    /// <summary>Records an attempt, replacing any previous one.</summary>
    void WriteAttempt(string attemptedVersion);

    /// <summary>Discards any pending record.</summary>
    void Clear();

    /// <summary>
    /// Returns the pending record and removes it, so a given attempt is reported once.
    /// </summary>
    UpdateAttemptRecord? TryTakePending();
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

    private const string AttemptFileName = "update-attempt.json";

    private readonly string _directory;
    private readonly TimeProvider _timeProvider;

    public UpdateOutcomeStore(string directory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private string AttemptPath => Path.Combine(_directory, AttemptFileName);

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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.FileLogger.Warn($"Update attempt record not cleared: {ex.Message}");
        }
    }

    public UpdateAttemptRecord? TryTakePending()
    {
        string path = AttemptPath;
        string content;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Read with ReadAllText so a byte-order mark is stripped; the byte-span
            // deserializer would throw on one.
            content = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // Deleted BEFORE parsing, deliberately. A record that cannot be parsed would
        // otherwise be read again on every launch forever, and two instances starting
        // together would both report the same attempt.
        Clear();

        UpdateAttemptRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<UpdateAttemptRecord>(content);
        }
        catch (JsonException)
        {
            return null;
        }

        if (record is null || record.SchemaVersion != UpdateAttemptRecord.CurrentSchemaVersion)
        {
            return null;
        }

        if (_timeProvider.GetUtcNow() - record.StartedUtc > MaxAttemptAge)
        {
            return null;
        }

        return record;
    }
}
