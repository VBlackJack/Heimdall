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

using TwinShell.Core.Models;

namespace TwinShell.Core.Interfaces;

/// <summary>
/// Repository for managing Git sync history records
/// </summary>
public interface ISyncHistoryRepository
{
    /// <summary>
    /// Adds a new sync history entry
    /// </summary>
    Task AddAsync(SyncHistoryEntry entry);

    /// <summary>
    /// Gets recent sync history entries
    /// </summary>
    /// <param name="count">Maximum number of entries to return</param>
    Task<IEnumerable<SyncHistoryEntry>> GetRecentAsync(int count = 50);

    /// <summary>
    /// Gets sync history entries by operation type
    /// </summary>
    /// <param name="operationType">The type of operation (Pull, Push, FullSync, etc.)</param>
    /// <param name="count">Maximum number of entries to return</param>
    Task<IEnumerable<SyncHistoryEntry>> GetByOperationTypeAsync(string operationType, int count = 50);

    /// <summary>
    /// Gets sync history entries within a date range
    /// </summary>
    /// <param name="startDate">Start of the date range</param>
    /// <param name="endDate">End of the date range</param>
    Task<IEnumerable<SyncHistoryEntry>> GetByDateRangeAsync(DateTime startDate, DateTime endDate);

    /// <summary>
    /// Gets the last successful sync entry
    /// </summary>
    Task<SyncHistoryEntry?> GetLastSuccessfulAsync();

    /// <summary>
    /// Deletes old sync history entries
    /// </summary>
    /// <param name="olderThan">Delete entries older than this date</param>
    /// <returns>Number of entries deleted</returns>
    Task<int> DeleteOldEntriesAsync(DateTime olderThan);

    /// <summary>
    /// Deletes, for every remote that has more than <paramref name="keepPerRemote"/> entries,
    /// the oldest ones beyond that number.
    /// </summary>
    /// <param name="keepPerRemote">How many of the newest entries of each remote to keep</param>
    /// <returns>Number of entries deleted</returns>
    Task<int> DeleteBeyondNewestPerRemoteAsync(int keepPerRemote);

    /// <summary>
    /// Gets total count of sync history entries
    /// </summary>
    Task<int> GetCountAsync();
}
