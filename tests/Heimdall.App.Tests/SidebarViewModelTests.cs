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

using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Heimdall.App.Services;
using Heimdall.App.Tests.TestHelpers;
using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.Sidebar;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class SidebarViewModelTests
{
    [Fact]
    public void OnFavoritesChanged_OnUiThread_RunsInline()
    {
        var dispatcher = new FakeUiDispatcher(checkAccess: true);
        using var viewModel = CreateViewModel(dispatcher);

        InvokeOnFavoritesChanged(viewModel, "PING");

        Assert.Equal(0, dispatcher.InvokeAsyncCalls);
    }

    [Fact]
    public void OnFavoritesChanged_OffUiThread_PostsToDispatcher()
    {
        var dispatcher = new FakeUiDispatcher(checkAccess: false);
        using var viewModel = CreateViewModel(dispatcher);

        InvokeOnFavoritesChanged(viewModel, "PING");

        Assert.Equal(1, dispatcher.InvokeAsyncCalls);
    }

    // A write the disk refuses must neither escape the handler (an async void: it would reach the
    // dispatcher as unhandled and surface as "an unexpected error") nor lose the user's
    // in-memory choice. The user is told once, in the status bar.
    [Fact]
    public async Task APersistedTabChoiceTheDiskRefuses_IsContainedAndReportedOnce()
    {
        var (viewModel, main) = CreatePersistingViewModel(new RefusingConfigManager());

        await viewModel.PersistTabChoiceAsync(true);

        Assert.Equal(EnglishLocalizer.Create()["ErrorSaveSettings"], main.StatusText);
        Assert.True(main.CurrentSettings!.ShowToolsPanel);

        main.StatusText = "something newer";
        await viewModel.PersistTabChoiceAsync(false);

        Assert.Equal("something newer", main.StatusText);
        viewModel.Dispose();
    }

    [Fact]
    public async Task ACategoryToggleTheDiskRefuses_IsContainedAndSharesTheSingleNotice()
    {
        var (viewModel, main) = CreatePersistingViewModel(new RefusingConfigManager());
        var category = new SidebarToolCategoryViewModel
        {
            CategoryKey = "Network",
            CategoryName = "Network",
            BrushKey = "AccentBrush",
            Tools = [],
        };

        category.IsExpanded = !category.IsExpanded;
        await viewModel.PersistCategoryExpansionAsync(
            category,
            new PropertyChangedEventArgs(nameof(SidebarToolCategoryViewModel.IsExpanded)));

        Assert.Equal(EnglishLocalizer.Create()["ErrorSaveSettings"], main.StatusText);

        main.StatusText = "something newer";
        category.IsExpanded = !category.IsExpanded;
        await viewModel.PersistCategoryExpansionAsync(
            category,
            new PropertyChangedEventArgs(nameof(SidebarToolCategoryViewModel.IsExpanded)));

        Assert.Equal("something newer", main.StatusText);
        viewModel.Dispose();
    }

    // The handler the tab switch really calls is an async void; it must reach the contained path.
    [Fact]
    public void SwitchingTabsWhileTheDiskRefuses_DoesNotThrow()
    {
        var (viewModel, main) = CreatePersistingViewModel(new RefusingConfigManager(), showToolsPanel: true);

        // Leaving the Tools tab keeps the test off the lazy population of the tools tree.
        viewModel.SetActiveTab(false);

        Assert.Equal(EnglishLocalizer.Create()["ErrorSaveSettings"], main.StatusText);
        viewModel.Dispose();
    }

    private static (SidebarViewModel ViewModel, MainViewModel Main) CreatePersistingViewModel(
        IConfigManager configManager,
        bool showToolsPanel = false)
    {
        var main = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        typeof(MainViewModel)
            .GetField("_currentSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(main, new AppSettings { ShowToolsPanel = showToolsPanel });
        var viewModel = new SidebarViewModel(
            main,
            EnglishLocalizer.Create(),
            configManager,
            new ToolsTabPopulationService(new ToolRegistry()),
            new StubToolContextProvider(),
            new FakeUiDispatcher(checkAccess: true));
        viewModel.EnablePersistence();
        return (viewModel, main);
    }

    private static SidebarViewModel CreateViewModel(FakeUiDispatcher dispatcher)
    {
        var main = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        return new SidebarViewModel(
            main,
            new LocalizationManager(),
            null!,
            new ToolsTabPopulationService(new ToolRegistry()),
            new StubToolContextProvider(),
            dispatcher);
    }

    /// <summary>A configuration store whose targeted write always fails, as a locked file does.</summary>
    private sealed class RefusingConfigManager : IConfigManager
    {
        public string ConfigPath => "mem://config";

        public string SettingsPath => "mem://settings.json";

        public string ServersPath => "mem://servers.json";

        public event Action<AppSettings>? SettingsChanged
        {
            add { }
            remove { }
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings());

        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) => Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(IEnumerable<KeyValuePair<string, string>> entries) =>
            Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate) =>
            Task.FromException(new IOException("The process cannot access the file."));

        public Task<List<ServerProfileDto>> LoadServersAsync() => Task.FromResult(new List<ServerProfileDto>());

        public Task<TResult> MutateServersAsync<TResult>(Func<List<ServerProfileDto>, TResult> mutate) =>
            Task.FromResult(mutate([]));

        public Task SaveServersAsync(List<ServerProfileDto> servers) => Task.CompletedTask;
    }

    private static void InvokeOnFavoritesChanged(SidebarViewModel viewModel, string toolId)
    {
        var method = typeof(SidebarViewModel).GetMethod("OnFavoritesChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(viewModel, [toolId]);
    }

    private sealed class StubToolContextProvider : IToolContextProvider
    {
        public event PropertyChangedEventHandler? PropertyChanged
        {
            add { }
            remove { }
        }

        public string? TargetHost => null;

        public bool HasTarget => false;

        public string ContextLabel => "Context";

        public string ContextTooltip => "Context";

        public string ContextBrushKey => "TextDisabledBrush";

        public void SetSelectedServer(ServerItemViewModel? server)
        {
        }

        public void Dispose()
        {
        }
    }
}
