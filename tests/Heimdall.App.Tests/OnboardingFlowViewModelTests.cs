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
using Heimdall.App.ViewModels.Onboarding;
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class OnboardingFlowViewModelTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(CompletionTrigger.Skip)]
    [InlineData(CompletionTrigger.Escape)]
    [InlineData(CompletionTrigger.FinalNext)]
    public async Task Completion_WaitsForPersistenceBeforeChangingVisibleState(
        CompletionTrigger trigger)
    {
        ControlledConfigManager configManager = new();
        configManager.BlockMerge();
        AppSettings liveSettings = new();
        OnboardingFlowViewModel viewModel = CreateViewModel(configManager, liveSettings);
        int completedCount = 0;
        viewModel.Completed += (_, _) => completedCount++;

        Task completion = ExecuteCompletionAsync(viewModel, trigger);
        await configManager.MergeStarted.Task.WaitAsync(TestTimeout);

        try
        {
            Assert.False(completion.IsCompleted);
            Assert.True(viewModel.IsVisible);
            Assert.False(liveSettings.OnboardingCompleted);
            Assert.Equal(0, completedCount);
        }
        finally
        {
            configManager.ReleaseMerge();
            await completion.WaitAsync(TestTimeout);
        }

        Assert.False(viewModel.IsVisible);
        Assert.True(liveSettings.OnboardingCompleted);
        Assert.Equal(1, completedCount);
    }

    [Fact]
    public async Task Completion_WriteFailureKeepsOverlayVisibleAndCanBeRetried()
    {
        ControlledConfigManager configManager = new()
        {
            ThrowBeforeWrite = true
        };
        AppSettings liveSettings = new();
        OnboardingFlowViewModel viewModel = CreateViewModel(configManager, liveSettings);
        int completedCount = 0;
        viewModel.Completed += (_, _) => completedCount++;

        await viewModel.SkipCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsVisible);
        Assert.False(liveSettings.OnboardingCompleted);
        Assert.False(configManager.PersistedOnboardingCompleted);
        Assert.Equal(0, completedCount);
        Assert.Equal("OnboardingCompletionSaveFailed", viewModel.CompletionErrorText);
        Assert.DoesNotContain("fake-secret", viewModel.CompletionErrorText, StringComparison.Ordinal);

        configManager.ThrowBeforeWrite = false;
        await viewModel.SkipCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsVisible);
        Assert.True(liveSettings.OnboardingCompleted);
        Assert.True(configManager.PersistedOnboardingCompleted);
        Assert.Equal(1, completedCount);
        Assert.Equal(string.Empty, viewModel.CompletionErrorText);
        Assert.Equal(2, configManager.MergeCallCount);
    }

    [Fact]
    public async Task Completion_PostWriteFailureReloadsDurableStateAndCompletes()
    {
        ControlledConfigManager configManager = new()
        {
            ThrowAfterWrite = true
        };
        AppSettings liveSettings = new();
        OnboardingFlowViewModel viewModel = CreateViewModel(configManager, liveSettings);
        int completedCount = 0;
        viewModel.Completed += (_, _) => completedCount++;

        await viewModel.EscapeCommand.ExecuteAsync(null);

        Assert.Equal(1, configManager.LoadSettingsCallCount);
        Assert.True(configManager.PersistedOnboardingCompleted);
        Assert.True(liveSettings.OnboardingCompleted);
        Assert.False(viewModel.IsVisible);
        Assert.Equal(1, completedCount);
        Assert.Equal(string.Empty, viewModel.CompletionErrorText);
    }

    [Fact]
    public async Task Completion_ConcurrentCommandsMergeAndCompleteOnlyOnce()
    {
        ControlledConfigManager configManager = new();
        configManager.BlockMerge();
        AppSettings liveSettings = new();
        OnboardingFlowViewModel viewModel = CreateViewModel(configManager, liveSettings);
        int completedCount = 0;
        viewModel.Completed += (_, _) => completedCount++;

        Task skipCompletion = viewModel.SkipCommand.ExecuteAsync(null);
        await configManager.MergeStarted.Task.WaitAsync(TestTimeout);
        Task escapeCompletion = viewModel.EscapeCommand.ExecuteAsync(null);
        viewModel.CurrentStep = OnboardingFlowViewModel.StepCount - 1;
        Task nextCompletion = viewModel.NextCommand.ExecuteAsync(null);

        await Task.WhenAll(escapeCompletion, nextCompletion).WaitAsync(TestTimeout);
        Assert.Equal(1, configManager.MergeCallCount);
        Assert.Equal(0, completedCount);

        configManager.ReleaseMerge();
        await skipCompletion.WaitAsync(TestTimeout);

        Assert.Equal(1, configManager.MergeCallCount);
        Assert.Equal(1, completedCount);
        Assert.False(viewModel.IsVisible);
        Assert.True(liveSettings.OnboardingCompleted);

        await viewModel.EscapeCommand.ExecuteAsync(null);

        Assert.Equal(1, configManager.MergeCallCount);
        Assert.Equal(1, completedCount);
    }

    private static OnboardingFlowViewModel CreateViewModel(
        ControlledConfigManager configManager,
        AppSettings liveSettings)
    {
        OnboardingFlowViewModel viewModel = new(new LocalizationManager(), configManager);
        viewModel.Attach(liveSettings);
        viewModel.Start();
        return viewModel;
    }

    private static Task ExecuteCompletionAsync(
        OnboardingFlowViewModel viewModel,
        CompletionTrigger trigger)
    {
        switch (trigger)
        {
            case CompletionTrigger.Skip:
                return viewModel.SkipCommand.ExecuteAsync(null);
            case CompletionTrigger.Escape:
                return viewModel.EscapeCommand.ExecuteAsync(null);
            case CompletionTrigger.FinalNext:
                viewModel.CurrentStep = OnboardingFlowViewModel.StepCount - 1;
                return viewModel.NextCommand.ExecuteAsync(null);
            default:
                throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null);
        }
    }

    public enum CompletionTrigger
    {
        Skip,
        Escape,
        FinalNext
    }

    private sealed class ControlledConfigManager : IConfigManager
    {
        private readonly AppSettings _persistedSettings = new();
        private TaskCompletionSource<bool>? _mergeGate;

        public string ConfigPath => "mem://config";

        public string SettingsPath => "mem://settings.json";

        public string ServersPath => "mem://servers.json";

        public TaskCompletionSource<bool> MergeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ThrowBeforeWrite { get; set; }

        public bool ThrowAfterWrite { get; set; }

        public bool PersistedOnboardingCompleted => _persistedSettings.OnboardingCompleted;

        public int LoadSettingsCallCount { get; private set; }

        public int MergeCallCount { get; private set; }

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync()
        {
            LoadSettingsCallCount++;
            return Task.FromResult(CloneSettings(_persistedSettings));
        }

        public Task SaveSettingsAsync(AppSettings settings)
        {
            _persistedSettings.OnboardingCompleted = settings.OnboardingCompleted;
            SettingsChanged?.Invoke(CloneSettings(_persistedSettings));
            return Task.CompletedTask;
        }

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) =>
            Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(
            IEnumerable<KeyValuePair<string, string>> entries) =>
            Task.FromResult(0);

        public async Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            MergeCallCount++;
            MergeStarted.TrySetResult(true);
            if (_mergeGate is not null)
            {
                await _mergeGate.Task;
            }

            if (ThrowBeforeWrite)
            {
                throw new IOException("fake-secret write failure");
            }

            mutate(_persistedSettings);
            if (ThrowAfterWrite)
            {
                throw new InvalidOperationException("fake-secret subscriber failure");
            }

            SettingsChanged?.Invoke(CloneSettings(_persistedSettings));
        }

        public Task<List<ServerProfileDto>> LoadServersAsync() =>
            Task.FromResult(new List<ServerProfileDto>());

        public Task<TResult> MutateServersAsync<TResult>(
            Func<List<ServerProfileDto>, TResult> mutate) =>
            Task.FromResult(mutate([]));

        public Task SaveServersAsync(List<ServerProfileDto> servers) => Task.CompletedTask;

        public void BlockMerge()
        {
            _mergeGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseMerge()
        {
            _mergeGate?.TrySetResult(true);
        }

        private static AppSettings CloneSettings(AppSettings settings)
        {
            return new AppSettings
            {
                OnboardingCompleted = settings.OnboardingCompleted
            };
        }
    }
}
