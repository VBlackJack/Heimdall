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
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;

namespace TwinShell.Core.Services;

/// <summary>
/// Service for managing command history
/// </summary>
public sealed class CommandHistoryService : ICommandHistoryService
{
    private readonly ICommandHistoryRepository _repository;

    public CommandHistoryService(ICommandHistoryRepository repository)
    {
        _repository = repository;
    }

    public async Task<string> AddCommandAsync(
        string actionId,
        string generatedCommand,
        Dictionary<string, string> parameters,
        Platform platform,
        string actionTitle,
        string category)
    {
        var history = new CommandHistory
        {
            Id = Guid.NewGuid().ToString(),
            ActionId = actionId,
            GeneratedCommand = generatedCommand,
            Parameters = parameters,
            Platform = platform,
            ActionTitle = actionTitle,
            Category = category,
            CreatedAt = DateTime.UtcNow
        };

        await _repository.AddAsync(history).ConfigureAwait(false);
        return history.Id;
    }

    public async Task UpdateWithExecutionResultsAsync(
        string historyId,
        int exitCode,
        TimeSpan duration,
        bool success)
    {
        var history = await _repository.GetByIdAsync(historyId).ConfigureAwait(false);
        if (history != null)
        {
            history.IsExecuted = true;
            history.ExitCode = exitCode;
            history.ExecutionDuration = duration;
            history.ExecutionSuccess = success;
            await _repository.UpdateAsync(history).ConfigureAwait(false);
        }
    }

    public async Task<IEnumerable<CommandHistory>> GetRecentAsync(int count = 50)
    {
        return await _repository.GetRecentAsync(count).ConfigureAwait(false);
    }

    public async Task<IEnumerable<CommandHistory>> SearchAsync(
        string? searchText = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Platform? platform = null,
        string? category = null)
    {
        return await _repository.SearchAsync(searchText, fromDate, toDate, platform, category).ConfigureAwait(false);
    }

    public async Task<CommandHistory?> GetByIdAsync(string id)
    {
        return await _repository.GetByIdAsync(id).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id)
    {
        await _repository.DeleteAsync(id).ConfigureAwait(false);
    }

    public async Task DeleteRangeAsync(IEnumerable<string> ids)
    {
        await _repository.DeleteRangeAsync(ids).ConfigureAwait(false);
    }

    public async Task CleanupOldEntriesAsync(int daysToKeep = 90)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-daysToKeep);
        await _repository.DeleteOlderThanAsync(cutoffDate).ConfigureAwait(false);
    }

    public async Task<int> GetCountAsync()
    {
        return await _repository.CountAsync().ConfigureAwait(false);
    }

    public async Task ClearAllAsync()
    {
        await _repository.ClearAllAsync().ConfigureAwait(false);
    }
}
