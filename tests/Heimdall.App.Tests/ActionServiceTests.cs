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

using TwinShell.Core.Constants;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

public sealed class ActionServiceTests
{
    [Fact]
    public async Task CreateActionAsync_WindowsExamplesAboveCap_Throws()
    {
        ActionService service = CreateService();
        ActionModel action = CreateAction(
            bothExamples: 0,
            windowsExamples: ValidationConstants.MaxActionExamplesCount + 1,
            linuxExamples: 0);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateActionAsync(action));

        Assert.Contains("ToolCmdLibValidationExamplesMaxCount", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateActionAsync_MixedExamplesAboveCap_Throws()
    {
        ActionService service = CreateService();
        ActionModel action = CreateAction(
            bothExamples: 6,
            windowsExamples: 0,
            linuxExamples: 5);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateActionAsync(action));

        Assert.Contains("ToolCmdLibValidationExamplesMaxCount", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateActionAsync_MixedExamplesAtCap_Succeeds()
    {
        var repository = new RecordingActionRepository();
        ActionService service = CreateService(repository);
        ActionModel action = CreateAction(
            bothExamples: 4,
            windowsExamples: 3,
            linuxExamples: 3);

        ActionModel created = await service.CreateActionAsync(action);

        Assert.Same(action, created);
        Assert.Single(repository.AddedActions);
    }

    [Fact]
    public async Task CreateActionAsync_BothExamplesAboveCap_StillThrows()
    {
        ActionService service = CreateService();
        ActionModel action = CreateAction(
            bothExamples: ValidationConstants.MaxActionExamplesCount + 1,
            windowsExamples: 0,
            linuxExamples: 0);

        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateActionAsync(action));

        Assert.Contains("ToolCmdLibValidationExamplesMaxCount", exception.Message, StringComparison.Ordinal);
    }

    private static ActionService CreateService(RecordingActionRepository? repository = null)
        => new(repository ?? new RecordingActionRepository(), new FakeTwinShellLocalizationService());

    private static ActionModel CreateAction(int bothExamples, int windowsExamples, int linuxExamples)
        => new()
        {
            Title = "Valid action",
            Category = "Ops",
            Examples = CreateExamples(bothExamples, Platform.Both),
            WindowsExamples = CreateExamples(windowsExamples, Platform.Windows),
            LinuxExamples = CreateExamples(linuxExamples, Platform.Linux)
        };

    private static List<CommandExample> CreateExamples(int count, Platform platform)
        => Enumerable.Range(1, count)
            .Select(index => new CommandExample
            {
                Command = $"echo {index}",
                Description = $"Example {index}",
                Platform = platform
            })
            .ToList();

    private sealed class RecordingActionRepository : IActionRepository
    {
        public List<ActionModel> AddedActions { get; } = [];

        public Task<IEnumerable<ActionModel>> GetAllAsync() => Task.FromResult<IEnumerable<ActionModel>>(AddedActions);

        public Task<ActionModel?> GetByIdAsync(string id) =>
            Task.FromResult(AddedActions.FirstOrDefault(action => action.Id == id));

        public Task<IEnumerable<ActionModel>> GetByCategoryAsync(string category) =>
            Task.FromResult<IEnumerable<ActionModel>>(
                AddedActions.Where(action => string.Equals(action.Category, category, StringComparison.OrdinalIgnoreCase)));

        public Task<IEnumerable<string>> GetAllCategoriesAsync() =>
            Task.FromResult<IEnumerable<string>>(
                AddedActions.Select(action => action.Category).Distinct(StringComparer.OrdinalIgnoreCase));

        public Task AddAsync(ActionModel action)
        {
            AddedActions.Add(action);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(ActionModel action) => Task.CompletedTask;

        public Task DeleteAsync(string id) => Task.CompletedTask;

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task<int> CountAsync() => Task.FromResult(AddedActions.Count);

        public Task<int> CountByCategoryAsync(string category) =>
            Task.FromResult(
                AddedActions.Count(action => string.Equals(action.Category, category, StringComparison.OrdinalIgnoreCase)));

        public Task<int> UpdateCategoryForActionsAsync(string oldCategory, string? newCategory) => Task.FromResult(0);

        public Task<ActionModel?> GetByPublicIdAsync(Guid publicId) =>
            Task.FromResult(AddedActions.FirstOrDefault(action => action.PublicId == publicId));

        public Task<IEnumerable<ActionModel>> GetAllWithTemplatesAsync() =>
            Task.FromResult<IEnumerable<ActionModel>>(AddedActions);

        public Task AddRangeAsync(IEnumerable<ActionModel> actions)
        {
            AddedActions.AddRange(actions);
            return Task.CompletedTask;
        }

        public Task UpdateRangeAsync(IEnumerable<ActionModel> actions) => Task.CompletedTask;
    }
}
