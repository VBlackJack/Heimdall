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

using System.Collections.Specialized;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Heimdall.App.ViewModels.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the dispatcher affinity of the command library picker's load path.
/// </summary>
/// <remarks>
/// The picker exposes <see cref="CommandLibraryPickerDialogViewModel.ActionsView"/>, a
/// <c>ListCollectionView</c> created on the constructing thread and bound to a
/// <c>ListView</c> in <c>CommandLibraryPickerDialog.xaml</c>. Its load path awaits
/// <see cref="IActionService.GetAllActionsAsync"/> and then mutates the backing
/// <c>ObservableCollection</c> and refreshes that view, so the continuation must come
/// back to the thread that owns them.
///
/// Every production <see cref="IActionService"/> call chain already carries
/// <c>ConfigureAwait(false)</c> inside <c>ActionService</c>, so whether the continuation
/// lands back on the UI thread depends entirely on what the *caller* does. The fake used
/// here yields before returning, which is what makes the difference observable: a
/// <c>Task.FromResult</c> fake completes synchronously and would pass either way.
/// </remarks>
public sealed class CommandLibraryPickerThreadAffinityTests
{
    [Fact]
    public void LoadAsync_MutatesTheBoundCollectionOnTheOwningThread()
    {
        int ownerThreadId = 0;
        var mutationThreadIds = new List<int>();

        RunOnStaDispatcher(async () =>
        {
            ownerThreadId = Environment.CurrentManagedThreadId;

            var localizer = await CommandLibraryTestHelpers.CreateAppLocalizerAsync();
            var provider = CreateYieldingProvider(
                CommandLibraryTestHelpers.CreateLinuxAction("a", "Alpha", "echo ok"),
                CommandLibraryTestHelpers.CreateLinuxAction("b", "Bravo", "echo ok"));

            var vm = new CommandLibraryPickerDialogViewModel(
                localizer, provider.GetRequiredService<IServiceScopeFactory>());

            void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
                => mutationThreadIds.Add(Environment.CurrentManagedThreadId);

            vm.Actions.CollectionChanged += OnChanged;
            try
            {
                await vm.InitializeAsync();
            }
            finally
            {
                vm.Actions.CollectionChanged -= OnChanged;
            }
        });

        Assert.NotEmpty(mutationThreadIds);
        Assert.All(mutationThreadIds, id => Assert.Equal(ownerThreadId, id));
    }

    /// <summary>
    /// Builds a provider whose <see cref="IActionService"/> always completes
    /// asynchronously, so the caller's await genuinely has a continuation to schedule.
    /// </summary>
    private static ServiceProvider CreateYieldingProvider(params ActionModel[] actions)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalizationService, FakeTwinShellLocalizationService>();
        services.AddScoped<IActionService>(_ => new YieldingActionService(new FakeActionService(actions)));
        services.AddScoped<IFavoritesService, FakeFavoritesService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<ICommandGeneratorService, CommandGeneratorService>();
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a dedicated STA thread whose dispatcher is the
    /// current synchronization context, so an await without <c>ConfigureAwait(false)</c>
    /// resumes on that same thread.
    /// </summary>
    private static void RunOnStaDispatcher(Func<Task> body)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));

            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });

            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}

/// <summary>
/// Decorator forcing every <see cref="IActionService"/> call to complete asynchronously.
/// </summary>
internal sealed class YieldingActionService(IActionService inner) : IActionService
{
    public async Task<IEnumerable<ActionModel>> GetAllActionsAsync()
    {
        await Task.Yield();
        return await inner.GetAllActionsAsync();
    }

    public async Task<ActionModel?> GetActionByIdAsync(string id)
    {
        await Task.Yield();
        return await inner.GetActionByIdAsync(id);
    }

    public async Task<ActionModel?> GetActionByPublicIdAsync(Guid publicId)
    {
        await Task.Yield();
        return await inner.GetActionByPublicIdAsync(publicId);
    }

    public async Task<IEnumerable<ActionModel>> GetActionsByCategoryAsync(string category)
    {
        await Task.Yield();
        return await inner.GetActionsByCategoryAsync(category);
    }

    public async Task<IEnumerable<string>> GetAllCategoriesAsync()
    {
        await Task.Yield();
        return await inner.GetAllCategoriesAsync();
    }

    public async Task<IEnumerable<ActionModel>> FilterActionsAsync(
        IEnumerable<ActionModel> actions, Platform? platform = null, CriticalityLevel? level = null)
    {
        await Task.Yield();
        return await inner.FilterActionsAsync(actions, platform, level);
    }

    public async Task<ActionModel> CreateActionAsync(ActionModel action)
    {
        await Task.Yield();
        return await inner.CreateActionAsync(action);
    }

    public async Task UpdateActionAsync(ActionModel action)
    {
        await Task.Yield();
        await inner.UpdateActionAsync(action);
    }

    public async Task DeleteActionAsync(string id)
    {
        await Task.Yield();
        await inner.DeleteActionAsync(id);
    }

    public async Task<int> GetActionCountByCategoryAsync(string category)
    {
        await Task.Yield();
        return await inner.GetActionCountByCategoryAsync(category);
    }

    public async Task<bool> RenameCategoryAsync(string oldName, string newName)
    {
        await Task.Yield();
        return await inner.RenameCategoryAsync(oldName, newName);
    }

    public async Task<bool> DeleteCategoryAsync(string categoryName)
    {
        await Task.Yield();
        return await inner.DeleteCategoryAsync(categoryName);
    }
}
