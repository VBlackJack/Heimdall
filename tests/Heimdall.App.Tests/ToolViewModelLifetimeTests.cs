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

using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Heimdall.App.Services;
using Heimdall.App.Services.Import;
using Heimdall.App.Tests.Fakes;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Microsoft.Extensions.DependencyInjection;
using TwinShell.Core.Interfaces;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins who owns the lifetime of a tool's view model: the tool view, never the container.
/// </summary>
/// <remarks>
/// <para>Microsoft.Extensions.DependencyInjection keeps every disposable transient resolved from
/// the root provider until the container is disposed, so a view model resolved that way outlives
/// its tab. The oracle is a <see cref="WeakReference"/> after a full collection, with a positive
/// control: the same view model resolved from the root IS still alive, so a pass of the leak test
/// cannot come from a collection that never reached it.</para>
/// <para>The providers register the view models as transients, as the application did before the
/// factory existed: a factory that regressed to <c>GetRequiredService</c> would then resolve them
/// and be tracked, which is the failure under test.</para>
/// </remarks>
public sealed class ToolViewModelLifetimeTests
{
    private const int CollectionPasses = 3;

    [Fact]
    public async Task CommandLibrary_CreatedByTheFactoryAndDisposed_IsNotReachableFromTheRootContainer()
    {
        using ServiceProvider root = await BuildCommandLibraryProviderAsync();

        WeakReference weak = CreateAndDisposeCommandLibrary(root);
        CollectFully();

        Assert.False(weak.IsAlive, "The root container still references a disposed Command Library view model.");
        GC.KeepAlive(root);
    }

    [Fact]
    public async Task NotesTool_CreatedByTheFactoryAndDisposed_IsNotReachableFromTheRootContainer()
    {
        using ServiceProvider root = await BuildNotesProviderAsync();

        WeakReference weak = CreateAndDisposeNotesTool(root);
        CollectFully();

        Assert.False(weak.IsAlive, "The root container still references a disposed Notes view model.");
        GC.KeepAlive(root);
    }

    // Positive control: without it the two tests above could pass because the collection never
    // reached the object. Resolving the same transient from the root pins it, disposed or not.
    [Fact]
    public async Task CommandLibrary_ResolvedFromTheRoot_IsStillReachableAfterDispose()
    {
        using ServiceProvider root = await BuildCommandLibraryProviderAsync();

        WeakReference weak = ResolveFromRootAndDisposeCommandLibrary(root);
        CollectFully();

        Assert.True(weak.IsAlive, "The control no longer pins the view model: the leak tests prove nothing.");
        GC.KeepAlive(root);
    }

    [Fact]
    public async Task CommandLibrary_Dispose_ReleasesEveryCallbackTheViewInstalled()
    {
        CommandLibraryViewModel viewModel = await CreateLibraryWithHandlersAsync();

        viewModel.Dispose();

        Assert.Null(viewModel.SendCommandHandler);
        Assert.Null(viewModel.InsertTextHandler);
        Assert.Null(viewModel.CanSendToTerminalProbe);
        Assert.Null(viewModel.CommandBroadcaster);
        Assert.Null(viewModel.ShowActionDialogAsync);
        Assert.Null(viewModel.ShowRunbookDialogAsync);
        Assert.Null(viewModel.ShowSaveFileDialog);
        Assert.Null(viewModel.ShowOpenFileDialog);
        Assert.Null(viewModel.SetClipboardText);
        Assert.Null(viewModel.ShowCopyFeedback);
    }

    [Fact]
    public async Task CommandLibrary_Dispose_ReleasesTheLibraryReloadedSubscribers()
    {
        CommandLibraryViewModel viewModel = await CreateLibraryWithHandlersAsync();
        viewModel.LibraryReloaded += () => { };

        viewModel.Dispose();
        FieldInfo field = typeof(CommandLibraryViewModel).GetField(
            nameof(CommandLibraryViewModel.LibraryReloaded),
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Null(field.GetValue(viewModel));
    }

    [Fact]
    public async Task CommandLibrary_Dispose_EndsARunbookRunStillOnScreen()
    {
        var batches = new FakeBatchService();
        batches.Seed(new TwinShell.Core.Models.CommandBatch
        {
            Id = "rb-1",
            Name = "Check uptime",
            Commands = [RunbookTestHelpers.LiteralStep("uptime")]
        });
        CommandLibraryViewModel library = await RunbookTestHelpers.CreateLibraryAsync(
            batches,
            new CollectingHistoryService(),
            new SilentDialogService(),
            targetHost: "srv-01",
            connectionType: "SSH");
        await library.ToggleRunbooksAsync();
        library.SelectedRunbook = library.Runbooks.Single();
        library.RunRunbook();
        Assert.NotNull(library.ActiveRun);

        library.Dispose();

        Assert.Null(library.ActiveRun);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDisposeCommandLibrary(IServiceProvider root)
    {
        var viewModel = ToolViewModelFactory.Create<CommandLibraryViewModel>(root);
        viewModel.Dispose();
        return new WeakReference(viewModel);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ResolveFromRootAndDisposeCommandLibrary(IServiceProvider root)
    {
        var viewModel = root.GetRequiredService<CommandLibraryViewModel>();
        viewModel.Dispose();
        return new WeakReference(viewModel);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDisposeNotesTool(IServiceProvider root)
    {
        var viewModel = ToolViewModelFactory.Create<NotesToolViewModel>(root);
        viewModel.Dispose();
        return new WeakReference(viewModel);
    }

    private static void CollectFully()
    {
        for (var pass = 0; pass < CollectionPasses; pass++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }
    }

    private static async Task<ServiceProvider> BuildCommandLibraryProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton(await CommandLibraryTestHelpers.CreateAppLocalizerAsync());
        services.AddSingleton<Heimdall.App.Services.IDialogService>(new SilentDialogService());
        services.AddSingleton(InertProxy<IConfigManager>.Create());
        services.AddSingleton(InertProxy<IGitSyncService>.Create());
        services.AddSingleton(InertProxy<ICommandLibraryTransferService>.Create());
        services.AddTransient<CommandLibraryViewModel>();
        return services.BuildServiceProvider();
    }

    private static async Task<ServiceProvider> BuildNotesProviderAsync()
    {
        var localizer = new LocalizationManager();
        await localizer.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), "en");

        var services = new ServiceCollection();
        services.AddSingleton(localizer);
        services.AddSingleton<INotesStorageService>(
            new FakeNotesStorage(Path.Combine(Path.GetTempPath(), $"heimdall-notes-lifetime-{Guid.NewGuid():N}")));
        services.AddSingleton<IUiDispatcher>(new FakeUiDispatcher());
        services.AddTransient<NotesToolViewModel>();
        return services.BuildServiceProvider();
    }

    private static async Task<CommandLibraryViewModel> CreateLibraryWithHandlersAsync()
    {
        CommandLibraryViewModel viewModel = await RunbookTestHelpers.CreateLibraryAsync(
            new FakeBatchService(),
            new CollectingHistoryService(),
            new SilentDialogService(),
            targetHost: null,
            connectionType: "SSH",
            actions: Array.Empty<ActionModel>());

        viewModel.SendCommandHandler = _ => { };
        viewModel.InsertTextHandler = _ => { };
        viewModel.CanSendToTerminalProbe = () => true;
        viewModel.CommandBroadcaster = CommandLibraryBroadcastHarness.Broadcaster(("t1", "Terminal 1", true));
        viewModel.ShowActionDialogAsync = _ => Task.FromResult(true);
        viewModel.ShowRunbookDialogAsync = _ => Task.FromResult(true);
        viewModel.ShowSaveFileDialog = (_, _) => null;
        viewModel.ShowOpenFileDialog = _ => null;
        viewModel.SetClipboardText = _ => true;
        viewModel.ShowCopyFeedback = _ => { };
        return viewModel;
    }

    /// <summary>Does nothing and returns defaults: the dependencies here are never called.</summary>
    private class InertProxy<T> : DispatchProxy
        where T : class
    {
        public static T Create() => DispatchProxy.Create<T, InertProxy<T>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
