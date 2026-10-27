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

using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace Heimdall.App.Services.HostsFile;

/// <summary>
/// How a save ended.
/// </summary>
public enum HostsFileSaveStatus
{
    Saved = 0,

    /// <summary>At least one edited or added row cannot be written; nothing was touched.</summary>
    InvalidEntries = 1,

    /// <summary>The backup could not be written, so the file was left as it was.</summary>
    BackupFailed = 2,

    /// <summary>Writing the file was refused, typically for want of elevation.</summary>
    AccessDenied = 3,

    /// <summary>Reading or writing the file failed for another I/O reason.</summary>
    WriteFailed = 4,

    /// <summary>
    /// Another program changed the file after it was loaded; writing would have dropped
    /// that change, so nothing was touched.
    /// </summary>
    ExternallyModified = 5,
}

/// <summary>
/// The outcome of <see cref="HostsFileStore.Save" />.
/// </summary>
/// <param name="Status">How the save ended.</param>
/// <param name="Problems">The rows that blocked it, for <see cref="HostsFileSaveStatus.InvalidEntries" />.</param>
/// <param name="ErrorMessage">The system's reason, for a failed backup, read or write.</param>
public sealed record HostsFileSaveResult(
    HostsFileSaveStatus Status,
    IReadOnlyList<HostsFileRowProblem> Problems,
    string? ErrorMessage);

/// <summary>
/// Reads and writes one hosts file, keeping copies of what it overwrites in a folder of
/// Heimdall's own data.
/// </summary>
/// <remarks>
/// <para>The backups do not go next to the hosts file. Saving works when Heimdall runs
/// elevated or when an organisation granted Modify on the hosts file alone; in the second
/// case the folder is not writable, and a backup there would make saving impossible where
/// it works without one. The file itself is written in place for the same reason: a
/// temporary file and a replace need write access to the folder.</para>
/// <para>Each save writes a timestamped copy and keeps the newest
/// <see cref="MaxTimestampedBackups" />. The first file ever overwritten is also kept as
/// <see cref="OriginalBackupFileName" />, written once and never replaced, so the state
/// before Heimdall touched the file survives any number of saves.</para>
/// <para>The paths are constructor arguments so tests work in a temporary folder; the tool
/// passes <see cref="SystemHostsPath" /> and the data root's backup folder.</para>
/// </remarks>
public sealed class HostsFileStore
{
    /// <summary>How many timestamped backups are kept; older ones are deleted.</summary>
    public const int MaxTimestampedBackups = 10;

    /// <summary>The copy of the file as it was before the first save, never overwritten.</summary>
    public const string OriginalBackupFileName = "hosts.original";

    private const string TimestampedBackupPrefix = "hosts-";
    private const string TimestampedBackupExtension = ".bak";
    private const string TimestampFormat = "yyyyMMdd-HHmmss-fff";
    private const string CollisionSuffixFormat = "-{0}";
    private const string HostsFileName = "hosts";
    private const string DriversFolder = "drivers";
    private const string EtcFolder = "etc";

    private readonly TimeProvider _timeProvider;

    public HostsFileStore(string filePath, string backupDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        FilePath = filePath;
        BackupDirectory = backupDirectory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The Windows hosts file.
    /// </summary>
    public static string SystemHostsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        DriversFolder, EtcFolder, HostsFileName);

    public string FilePath { get; }

    /// <summary>The folder that receives the backups.</summary>
    public string BackupDirectory { get; }

    public bool Exists => File.Exists(FilePath);

    /// <summary>
    /// Reads the file. Throws what <see cref="File.ReadAllBytes" /> throws.
    /// </summary>
    public HostsFileDocument Load() => HostsFileDocument.Parse(File.ReadAllBytes(FilePath));

    /// <summary>
    /// The timestamped backups, oldest first.
    /// </summary>
    public IReadOnlyList<string> ListTimestampedBackups() =>
        Directory.Exists(BackupDirectory)
            ? Directory.GetFiles(BackupDirectory, TimestampedBackupPrefix + "*" + TimestampedBackupExtension)
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>
    /// Writes the document in place after checking that nobody else changed the file since it
    /// was read and after copying the current file to <see cref="BackupDirectory" />.
    /// Nothing is written when a row is invalid, when the file changed, or when the backup fails.
    /// </summary>
    public HostsFileSaveResult Save(HostsFileDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var problems = document.FindProblems();
        if (problems.Count > 0)
        {
            return new HostsFileSaveResult(HostsFileSaveStatus.InvalidEntries, problems, null);
        }

        byte[]? current;
        try
        {
            current = File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;
        }
        catch (UnauthorizedAccessException ex)
        {
            return new HostsFileSaveResult(HostsFileSaveStatus.AccessDenied, [], ex.Message);
        }
        catch (IOException ex)
        {
            return new HostsFileSaveResult(HostsFileSaveStatus.WriteFailed, [], ex.Message);
        }

        if (!IsUnchangedSinceLoad(document.DiskHash, current))
        {
            return new HostsFileSaveResult(HostsFileSaveStatus.ExternallyModified, [], null);
        }

        if (current is not null)
        {
            try
            {
                WriteBackups(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new HostsFileSaveResult(HostsFileSaveStatus.BackupFailed, [], ex.Message);
            }
        }

        var bytes = document.ToBytes();
        try
        {
            File.WriteAllBytes(FilePath, bytes);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new HostsFileSaveResult(HostsFileSaveStatus.AccessDenied, [], ex.Message);
        }
        catch (IOException ex)
        {
            return new HostsFileSaveResult(HostsFileSaveStatus.WriteFailed, [], ex.Message);
        }

        document.MarkSaved(bytes);
        PruneTimestampedBackups();
        return new HostsFileSaveResult(HostsFileSaveStatus.Saved, [], null);
    }

    private static bool IsUnchangedSinceLoad(byte[]? loadedHash, byte[]? current)
    {
        if (loadedHash is null || current is null)
        {
            // Absent at load and still absent is the only match without content to hash.
            return loadedHash is null && current is null;
        }

        return CryptographicOperations.FixedTimeEquals(loadedHash, SHA256.HashData(current));
    }

    private void WriteBackups(byte[] current)
    {
        Directory.CreateDirectory(BackupDirectory);

        var original = Path.Combine(BackupDirectory, OriginalBackupFileName);
        if (!File.Exists(original))
        {
            WriteNewFile(original, current);
        }

        var stamp = _timeProvider.GetLocalNow().ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var path = Path.Combine(BackupDirectory, TimestampedBackupPrefix + stamp + TimestampedBackupExtension);
        for (int attempt = 1; File.Exists(path); attempt++)
        {
            var suffix = string.Format(CultureInfo.InvariantCulture, CollisionSuffixFormat, attempt);
            path = Path.Combine(BackupDirectory, TimestampedBackupPrefix + stamp + suffix + TimestampedBackupExtension);
        }

        WriteNewFile(path, current);
    }

    private static void WriteNewFile(string path, byte[] content)
    {
        // CreateNew: a backup is never written over another one.
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(content);
    }

    private void PruneTimestampedBackups()
    {
        var backups = ListTimestampedBackups();
        foreach (var stale in backups.Take(Math.Max(0, backups.Count - MaxTimestampedBackups)))
        {
            try
            {
                File.Delete(stale);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The save already succeeded; an old backup left behind costs only disk space.
                Core.Logging.FileLogger.Warn($"[HostsFileStore] Could not delete old backup '{stale}': {ex.Message}");
            }
        }
    }
}
