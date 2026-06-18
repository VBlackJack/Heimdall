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

using TwinShell.Core.Enums;
using TwinShell.Core.Models;

namespace TwinShell.Core.Interfaces;

/// <summary>
/// Repository interface for CommandHistory persistence
/// </summary>
public interface ICommandHistoryRepository
{
    /// <summary>
    /// Add a new command history entry
    /// </summary>
    Task AddAsync(CommandHistory history);

    /// <summary>
    /// PERFORMANCE: Add multiple command history entries at once
    /// </summary>
    Task AddRangeAsync(IEnumerable<CommandHistory> histories);

    /// <summary>
    /// Update an existing command history entry
    /// </summary>
    Task UpdateAsync(CommandHistory history);

    /// <summary>
    /// Get the most recent command history entries
    /// </summary>
    /// <param name="count">Number of entries to retrieve</param>
    Task<IEnumerable<CommandHistory>> GetRecentAsync(int count = 50);

    /// <summary>
    /// Search command history with filters
    /// </summary>
    /// <param name="searchText">Text to search in command or action title</param>
    /// <param name="fromDate">Filter by start date</param>
    /// <param name="toDate">Filter by end date</param>
    /// <param name="platform">Filter by platform</param>
    /// <param name="category">Filter by category</param>
    Task<IEnumerable<CommandHistory>> SearchAsync(
        string? searchText = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Platform? platform = null,
        string? category = null);

    /// <summary>
    /// Get command history by ID
    /// </summary>
    Task<CommandHistory?> GetByIdAsync(string id);

    /// <summary>
    /// Delete a command history entry
    /// </summary>
    Task DeleteAsync(string id);

    /// <summary>
    /// Delete multiple command history entries
    /// </summary>
    Task DeleteRangeAsync(IEnumerable<string> ids);

    /// <summary>
    /// Delete all command history entries older than specified date
    /// </summary>
    Task DeleteOlderThanAsync(DateTime date);

    /// <summary>
    /// Get total count of history entries
    /// </summary>
    Task<int> CountAsync();

    /// <summary>
    /// Clear all command history
    /// </summary>
    Task ClearAllAsync();
}
