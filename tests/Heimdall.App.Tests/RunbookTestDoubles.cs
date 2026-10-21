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

using System.Collections.Concurrent;
using Heimdall.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// In-memory runbook store with the real service's numbering rules: a create numbers the steps
/// from their position, an update renumbers them, and every read returns copies, so a test sees
/// what was stored and not the instance it handed in.
/// </summary>
internal sealed class FakeBatchService : IBatchService
{
    private readonly List<CommandBatch> _store = [];

    public IReadOnlyList<CommandBatch> Stored => _store;

    public Task<IEnumerable<CommandBatch>> GetAllBatchesAsync()
        => Task.FromResult<IEnumerable<CommandBatch>>(_store.Select(Copy).ToList());

    public Task<CommandBatch?> GetBatchByIdAsync(string id)
        => Task.FromResult(_store.Where(batch => batch.Id == id).Select(Copy).FirstOrDefault());

    public Task<CommandBatch?> GetBatchByPublicIdAsync(Guid publicId)
        => Task.FromResult(_store.Where(batch => batch.PublicId == publicId).Select(Copy).FirstOrDefault());

    public Task<CommandBatch> CreateBatchAsync(CommandBatch batch)
    {
        batch.Id = Guid.NewGuid().ToString();
        batch.IsUserCreated = true;
        for (var index = 0; index < batch.Commands.Count; index++)
        {
            batch.Commands[index].Order = index;
            batch.Commands[index].BatchId = batch.Id;
        }

        _store.Add(Copy(batch));
        return Task.FromResult(batch);
    }

    public Task UpdateBatchAsync(CommandBatch batch)
    {
        for (var index = 0; index < batch.Commands.Count; index++)
        {
            batch.Commands[index].Order = index;
        }

        var position = _store.FindIndex(stored => stored.Id == batch.Id);
        if (position < 0)
        {
            throw new InvalidOperationException($"No batch {batch.Id}");
        }

        _store[position] = Copy(batch);
        return Task.CompletedTask;
    }

    public Task DeleteBatchAsync(string id)
    {
        _store.RemoveAll(batch => batch.Id == id);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<CommandBatch>> SearchBatchesAsync(string query) => GetAllBatchesAsync();

    public string ExportBatchToJson(CommandBatch batch) => throw new NotSupportedException();

    public CommandBatch ImportBatchFromJson(string json) => throw new NotSupportedException();

    public void Seed(CommandBatch batch) => _store.Add(Copy(batch));

    private static CommandBatch Copy(CommandBatch batch) => new()
    {
        Id = batch.Id,
        PublicId = batch.PublicId,
        Name = batch.Name,
        Description = batch.Description,
        ExecutionMode = batch.ExecutionMode,
        CreatedAt = batch.CreatedAt,
        UpdatedAt = batch.UpdatedAt,
        LastExecutedAt = batch.LastExecutedAt,
        IsUserCreated = batch.IsUserCreated,
        Tags = [.. batch.Tags],
        Variables = new Dictionary<string, string>(batch.Variables, StringComparer.Ordinal),
        Commands = batch.Commands
            .OrderBy(command => command.Order)
            .Select(command => new BatchCommandItem
            {
                Id = command.Id,
                BatchId = command.BatchId,
                Order = command.Order,
                ActionId = command.ActionId,
                ActionTitle = command.ActionTitle,
                Command = command.Command,
                Platform = command.Platform,
                Description = command.Description,
                Parameters = new Dictionary<string, string>(command.Parameters, StringComparer.Ordinal)
            })
            .ToList()
    };
}

/// <summary>
/// History service that keeps every row written to it, for a run that writes several.
/// </summary>
internal sealed class CollectingHistoryService : ICommandHistoryService
{
    private readonly ConcurrentQueue<(string ActionId, string Command, string? TargetHost, bool IsExecuted)> _rows = new();
    private readonly SemaphoreSlim _written = new(0);

    public IReadOnlyCollection<(string ActionId, string Command, string? TargetHost, bool IsExecuted)> Rows => _rows;

    /// <summary>Waits, with a bound, until <paramref name="count"/> rows have been written.</summary>
    public async Task WaitForAsync(int count, TimeSpan timeout)
    {
        for (var index = 0; index < count; index++)
        {
            if (!await _written.WaitAsync(timeout))
            {
                throw new TimeoutException($"Only {_rows.Count} of {count} history rows were written.");
            }
        }
    }

    public Task<string> AddCommandAsync(
        string actionId, string generatedCommand, Dictionary<string, string> parameters,
        Platform platform, string actionTitle, string category,
        string? targetHost, bool isExecuted)
    {
        _rows.Enqueue((actionId, generatedCommand, targetHost, isExecuted));
        _written.Release();
        return Task.FromResult(Guid.NewGuid().ToString());
    }

    public Task UpdateWithExecutionResultsAsync(string historyId, int exitCode, TimeSpan duration, bool success)
        => Task.CompletedTask;

    public Task<IEnumerable<CommandHistory>> GetRecentAsync(int count = 50)
        => Task.FromResult<IEnumerable<CommandHistory>>([]);

    public Task<IEnumerable<CommandHistory>> SearchAsync(
        string? searchText = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        Platform? platform = null,
        string? category = null)
        => Task.FromResult<IEnumerable<CommandHistory>>([]);

    public Task<CommandHistory?> GetByIdAsync(string id) => Task.FromResult<CommandHistory?>(null);

    public Task DeleteAsync(string id) => Task.CompletedTask;

    public Task DeleteRangeAsync(IEnumerable<string> ids) => Task.CompletedTask;

    public Task CleanupOldEntriesAsync(int daysToKeep = 90) => Task.CompletedTask;

    public Task<int> GetCountAsync() => Task.FromResult(_rows.Count);

    public Task ClearAllAsync() => Task.CompletedTask;
}

internal static class RunbookTestHelpers
{
    /// <summary>
    /// A loaded library over <paramref name="actions"/>, with the runbook store and the history
    /// service given, opened for <paramref name="targetHost"/> on a session of
    /// <paramref name="connectionType"/>.
    /// </summary>
    public static async Task<CommandLibraryViewModel> CreateLibraryAsync(
        IBatchService batches,
        ICommandHistoryService history,
        SilentDialogService dialogs,
        string? targetHost,
        string connectionType,
        params ActionModel[] actions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new FakeActionService(actions));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();
        services.AddScoped(_ => batches);
        services.AddScoped(_ => history);

        var viewModel = new CommandLibraryViewModel(
            services.BuildServiceProvider(),
            configManager: null!,
            await CommandLibraryTestHelpers.CreateAppLocalizerAsync(),
            dialogs,
            gitSyncService: null!,
            transferService: null!);

        viewModel.AutoSelectPlatform(connectionType);
        await viewModel.InitializeAsync(targetHost, initialActionId: null, username: "ops");
        return viewModel;
    }

    /// <summary>An action carrying both a Windows and a Linux template over one parameter.</summary>
    public static ActionModel CreateDualAction(
        string id,
        string windowsPattern,
        string linuxPattern,
        string parameterName,
        CriticalityLevel level = CriticalityLevel.Run)
        => new()
        {
            Id = id,
            Title = id,
            Category = "Ops",
            Platform = Platform.Both,
            Level = level,
            WindowsCommandTemplate = new CommandTemplate
            {
                Id = id + "-win",
                Name = id,
                Platform = Platform.Windows,
                CommandPattern = windowsPattern,
                Parameters = [CommandLibraryTestHelpers.RequiredParameter(parameterName, parameterName)]
            },
            LinuxCommandTemplate = new CommandTemplate
            {
                Id = id + "-linux",
                Name = id,
                Platform = Platform.Linux,
                CommandPattern = linuxPattern,
                Parameters = [CommandLibraryTestHelpers.RequiredParameter(parameterName, parameterName)]
            }
        };

    /// <summary>An action step over <paramref name="actionId"/> with the given values.</summary>
    public static BatchCommandItem ActionStep(string actionId, params (string Name, string Value)[] values)
        => new()
        {
            ActionId = actionId,
            ActionTitle = actionId,
            Parameters = values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal)
        };

    /// <summary>A literal step.</summary>
    public static BatchCommandItem LiteralStep(string command, Platform platform = Platform.Linux)
        => new() { Command = command, Platform = platform };
}
