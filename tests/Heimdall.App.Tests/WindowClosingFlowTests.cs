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

using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class WindowClosingFlowTests
{
    [Fact]
    public async Task Close_CleanSettings_WindowStateSaveThrows_StillCloses_NoWarning()
    {
        var promptCount = 0;
        var settingsSaveCount = 0;
        var warningCount = 0;

        bool canClose = await WindowClosingFlow.TryPrepareCloseAsync(
            settingsDirty: false,
            () =>
            {
                promptCount++;
                return Task.FromResult<bool?>(null);
            },
            () =>
            {
                settingsSaveCount++;
                return Task.FromResult(true);
            },
            () => throw new InvalidOperationException("Simulated window-state failure."),
            () => warningCount++);

        Assert.True(canClose);
        Assert.Equal(0, promptCount);
        Assert.Equal(0, settingsSaveCount);
        Assert.Equal(0, warningCount);
    }

    [Fact]
    public async Task Close_CleanSettings_NoSaveAttempted_ClosesSilently()
    {
        var promptCount = 0;
        var settingsSaveCount = 0;
        var windowStateSaveCount = 0;
        var warningCount = 0;

        bool canClose = await WindowClosingFlow.TryPrepareCloseAsync(
            settingsDirty: false,
            () =>
            {
                promptCount++;
                return Task.FromResult<bool?>(null);
            },
            () =>
            {
                settingsSaveCount++;
                return Task.FromResult(true);
            },
            () =>
            {
                windowStateSaveCount++;
                return Task.CompletedTask;
            },
            () => warningCount++);

        Assert.True(canClose);
        Assert.Equal(0, promptCount);
        Assert.Equal(0, settingsSaveCount);
        Assert.Equal(1, windowStateSaveCount);
        Assert.Equal(0, warningCount);
    }

    [Fact]
    public async Task SaveWindowBounds_NonFiniteOrDegenerate_DoesNotThrow_SkipsWrite()
    {
        var configManager = new RecordingConfigManager();
        WindowBoundsSnapshot[] invalidSnapshots =
        [
            new(double.NaN, 20, 800, 600, false),
            new(10, double.NegativeInfinity, 800, 600, false),
            new(10, 20, double.PositiveInfinity, 600, false),
            new(10, 20, 800, double.NaN, false),
            new(10, 20, 0, 600, false),
            new(10, 20, 800, -1, false)
        ];

        foreach (WindowBoundsSnapshot snapshot in invalidSnapshots)
        {
            await WindowBoundsPersistence.PersistAsync(configManager, snapshot);
        }

        Assert.Equal(0, configManager.MergeSettingCallCount);
    }

    [Fact]
    public async Task Close_DirtySettings_InvalidAndUserChoseSave_StaysOpenAndWarns()
    {
        var promptCount = 0;
        var settingsSaveCount = 0;
        var windowStateSaveCount = 0;
        var warningCount = 0;

        bool canClose = await WindowClosingFlow.TryPrepareCloseAsync(
            settingsDirty: true,
            () =>
            {
                promptCount++;
                return Task.FromResult<bool?>(true);
            },
            () =>
            {
                settingsSaveCount++;
                return Task.FromResult(false);
            },
            () =>
            {
                windowStateSaveCount++;
                return Task.CompletedTask;
            },
            () => warningCount++);

        Assert.False(canClose);
        Assert.Equal(1, promptCount);
        Assert.Equal(1, settingsSaveCount);
        Assert.Equal(0, windowStateSaveCount);
        Assert.Equal(1, warningCount);
    }

    [Fact]
    public async Task Close_DirtySettings_UserChoseDiscard_ClosesWithoutSaving()
    {
        var settingsSaveCount = 0;
        var windowStateSaveCount = 0;
        var warningCount = 0;

        bool canClose = await WindowClosingFlow.TryPrepareCloseAsync(
            settingsDirty: true,
            () => Task.FromResult<bool?>(false),
            () =>
            {
                settingsSaveCount++;
                return Task.FromResult(true);
            },
            () =>
            {
                windowStateSaveCount++;
                return Task.CompletedTask;
            },
            () => warningCount++);

        Assert.True(canClose);
        Assert.Equal(0, settingsSaveCount);
        Assert.Equal(1, windowStateSaveCount);
        Assert.Equal(0, warningCount);
    }

    private sealed class RecordingConfigManager : IConfigManager
    {
        public string ConfigPath => "memory://config";

        public string SettingsPath => "memory://settings.json";

        public string ServersPath => "memory://servers.json";

        public int MergeSettingCallCount { get; private set; }

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings());

        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) =>
            Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(
            IEnumerable<KeyValuePair<string, string>> entries) =>
            Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            MergeSettingCallCount++;
            var settings = new AppSettings();
            mutate(settings);
            SettingsChanged?.Invoke(settings);
            return Task.CompletedTask;
        }

        public Task<List<ServerProfileDto>> LoadServersAsync() =>
            Task.FromResult<List<ServerProfileDto>>([]);

        public Task<TResult> MutateServersAsync<TResult>(
            Func<List<ServerProfileDto>, TResult> mutate) =>
            Task.FromResult(mutate([]));

        public Task SaveServersAsync(List<ServerProfileDto> servers) =>
            Task.CompletedTask;
    }
}
