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
using Heimdall.App.Services;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

public sealed class WindowClosingFlowTests
{
    [Fact]
    public async Task Close_ConnectedSessions_UserDeclines_StaysOpenBeforePersistence()
    {
        int sessionPromptCount = 0;
        int windowStateSaveCount = 0;

        bool canClose = await WindowClosingFlow.TryPrepareCloseAsync(
            settingsDirty: false,
            connectedSessionCount: 2,
            () => throw new InvalidOperationException("Settings prompt must not run."),
            () => throw new InvalidOperationException("Settings save must not run."),
            () =>
            {
                sessionPromptCount++;
                return Task.FromResult(false);
            },
            () =>
            {
                windowStateSaveCount++;
                return Task.CompletedTask;
            },
            () => throw new InvalidOperationException("Warning must not run."));

        Assert.False(canClose);
        Assert.Equal(1, sessionPromptCount);
        Assert.Equal(0, windowStateSaveCount);
    }

    /// <summary>
    /// Discarding on the way out has to undo what was already applied on screen.
    /// </summary>
    /// <remarks>
    /// Theme, accent and language take effect the moment they are picked, so a discard that only
    /// drops the pending write leaves the user looking at edits they abandoned. This flow does not
    /// always end in a closed window either: declining the connected-sessions prompt keeps the
    /// application running, which is how an abandoned language survives a close that never
    /// happened.
    /// </remarks>
    [Fact]
    public async Task Close_DirtySettingsDiscarded_UndoesTheAppliedEdits()
    {
        int discardCount = 0;

        bool canClose = await WindowClosingFlow.TryPrepareCloseAsync(
            settingsDirty: true,
            connectedSessionCount: 0,
            () => Task.FromResult<bool?>(false),
            () => throw new InvalidOperationException("Save must not run on a discard."),
            () => Task.FromResult(true),
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => throw new InvalidOperationException("Warning must not run."),
            () =>
            {
                discardCount++;
                return Task.CompletedTask;
            });

        Assert.True(canClose);
        Assert.Equal(1, discardCount);
    }

    [Fact]
    public async Task Close_DirtySettingsDiscardedThenSessionsDeclined_StillUndidTheEdits()
    {
        int discardCount = 0;

        bool canClose = await WindowClosingFlow.TryPrepareCloseAsync(
            settingsDirty: true,
            connectedSessionCount: 3,
            () => Task.FromResult<bool?>(false),
            () => throw new InvalidOperationException("Save must not run on a discard."),
            () => Task.FromResult(false),
            () => Task.CompletedTask,
            () => throw new InvalidOperationException("Window state must not be saved."),
            () => throw new InvalidOperationException("Warning must not run."),
            () =>
            {
                discardCount++;
                return Task.CompletedTask;
            });

        // The window stays open, so the undo is the only thing standing between the user and a
        // settings panel showing edits they said to throw away.
        Assert.False(canClose);
        Assert.Equal(1, discardCount);
    }

    [Fact]
    public async Task Close_DirtySettingsSaved_DoesNotUndoWhatItJustSaved()
    {
        int discardCount = 0;

        bool canClose = await WindowClosingFlow.TryPrepareCloseAsync(
            settingsDirty: true,
            connectedSessionCount: 0,
            () => Task.FromResult<bool?>(true),
            () => Task.FromResult(true),
            () => Task.FromResult(true),
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => throw new InvalidOperationException("Warning must not run."),
            () =>
            {
                discardCount++;
                return Task.CompletedTask;
            });

        Assert.True(canClose);
        Assert.Equal(0, discardCount);
    }

    [Fact]
    public async Task Close_DirtySettingsAndConnectedSessions_UsesDeterministicPromptOrder()
    {
        List<string> steps = [];

        bool canClose = await WindowClosingFlow.TryPrepareCloseAsync(
            settingsDirty: true,
            connectedSessionCount: 1,
            () =>
            {
                steps.Add("settings");
                return Task.FromResult<bool?>(false);
            },
            () => throw new InvalidOperationException("Discard must not save settings."),
            () =>
            {
                steps.Add("sessions");
                return Task.FromResult(true);
            },
            () =>
            {
                steps.Add("window-state");
                return Task.CompletedTask;
            },
            () => throw new InvalidOperationException("Warning must not run."));

        Assert.True(canClose);
        Assert.Equal(["settings", "sessions", "window-state"], steps);
    }

    [Fact]
    public void UpdateShutdown_MarksConfirmedBeforeRequest_ExactlyOnce()
    {
        List<string> steps = [];

        ApplicationLifecycle.RunShutdownSequence(
            () => steps.Add("confirmed"),
            () => steps.Add("shutdown"));

        Assert.Equal(["confirmed", "shutdown"], steps);
    }

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

    [Theory]
    [InlineData(true, 0, WindowUIState.MinSidebarWidth, false, true)]
    [InlineData(false, 1000, WindowUIState.MaxSidebarWidth, true, false)]
    public void RestoreSidebarState_SeedsNormalizedCanonicalProjection(
        bool isSidebarHidden,
        int persistedWidth,
        double expectedWidth,
        bool expectedVisible,
        bool expectedRestoreButton)
    {
        WindowUIState state = new()
        {
            IsSidebarHidden = !isSidebarHidden,
            SavedSidebarWidth = WindowUIState.DefaultSidebarWidth
        };
        AppSettings settings = new()
        {
            SidebarCollapsed = isSidebarHidden,
            SidebarWidth = persistedWidth
        };

        SidebarLayoutProjection projection = WindowBoundsPersistence.RestoreSidebarState(
            state,
            settings);

        Assert.Equal(isSidebarHidden, state.IsSidebarHidden);
        Assert.Equal(expectedWidth, state.SavedSidebarWidth);
        Assert.Equal(expectedVisible, projection.IsVisible);
        Assert.Equal(expectedRestoreButton, projection.ShowRestoreButton);
        Assert.Equal(expectedWidth, projection.Width);
    }

    [Fact]
    public async Task SaveWindowBounds_VisibleSidebar_PersistsActualWidthInAtomicMerge()
    {
        RecordingConfigManager configManager = new();
        configManager.CurrentSettings.DefaultTheme = "Dracula";
        WindowUIState state = new()
        {
            SavedSidebarWidth = 360d
        };
        WindowBoundsSnapshot snapshot = WindowBoundsPersistence.CaptureSnapshot(
            left: 10d,
            top: 20d,
            width: 800d,
            height: 600d,
            isMaximized: false,
            state,
            actualSidebarWidth: 437d);

        await WindowBoundsPersistence.PersistAsync(configManager, snapshot);

        Assert.Equal(1, configManager.MergeSettingCallCount);
        Assert.False(configManager.CurrentSettings.SidebarCollapsed);
        Assert.Equal(437, configManager.CurrentSettings.SidebarWidth);
        Assert.Equal(800d, configManager.CurrentSettings.WindowWidth);
        Assert.Equal("Dracula", configManager.CurrentSettings.DefaultTheme);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SaveWindowBounds_TemporarilySuppressedSidebar_IgnoresActualWidth(
        bool isSuppressedByTab,
        bool isFullscreen)
    {
        RecordingConfigManager configManager = new();
        WindowUIState state = new()
        {
            IsSidebarSuppressedByTab = isSuppressedByTab,
            IsFullscreen = isFullscreen,
            SavedSidebarWidth = 375d
        };
        WindowBoundsSnapshot snapshot = WindowBoundsPersistence.CaptureSnapshot(
            left: 10d,
            top: 20d,
            width: 800d,
            height: 600d,
            isMaximized: false,
            state,
            actualSidebarWidth: 437d);

        await WindowBoundsPersistence.PersistAsync(configManager, snapshot);

        Assert.Equal(1, configManager.MergeSettingCallCount);
        Assert.False(configManager.CurrentSettings.SidebarCollapsed);
        Assert.Equal(375, configManager.CurrentSettings.SidebarWidth);
    }

    [Fact]
    public async Task SaveWindowBounds_CollapsedSidebar_PreservesPreferredWidth()
    {
        RecordingConfigManager configManager = new();
        WindowUIState state = new()
        {
            IsSidebarHidden = true,
            SavedSidebarWidth = 375d
        };
        WindowBoundsSnapshot snapshot = WindowBoundsPersistence.CaptureSnapshot(
            left: 10d,
            top: 20d,
            width: 800d,
            height: 600d,
            isMaximized: false,
            state,
            actualSidebarWidth: 0d);

        await WindowBoundsPersistence.PersistAsync(configManager, snapshot);

        Assert.Equal(1, configManager.MergeSettingCallCount);
        Assert.True(configManager.CurrentSettings.SidebarCollapsed);
        Assert.Equal(375, configManager.CurrentSettings.SidebarWidth);
    }

    [Fact]
    public async Task SaveWindowBounds_VisibleSidebarWithInvalidMeasurement_PersistsSavedWidth()
    {
        RecordingConfigManager configManager = new();
        WindowUIState state = new()
        {
            SavedSidebarWidth = 390d
        };
        WindowBoundsSnapshot snapshot = WindowBoundsPersistence.CaptureSnapshot(
            left: 10d,
            top: 20d,
            width: 800d,
            height: 600d,
            isMaximized: false,
            state,
            actualSidebarWidth: double.NaN);

        await WindowBoundsPersistence.PersistAsync(configManager, snapshot);

        Assert.Equal(1, configManager.MergeSettingCallCount);
        Assert.False(configManager.CurrentSettings.SidebarCollapsed);
        Assert.Equal(390, configManager.CurrentSettings.SidebarWidth);
    }

    [Fact]
    public async Task SaveWindowBounds_NonFiniteOrDegenerate_DoesNotThrow_SkipsWrite()
    {
        var configManager = new RecordingConfigManager();
        WindowBoundsSnapshot[] invalidSnapshots =
        [
            new(double.NaN, 20, 800, 600, false, false, 320),
            new(10, double.NegativeInfinity, 800, 600, false, false, 320),
            new(10, 20, double.PositiveInfinity, 600, false, false, 320),
            new(10, 20, 800, double.NaN, false, false, 320),
            new(10, 20, 0, 600, false, false, 320),
            new(10, 20, 800, -1, false, false, 320)
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

    [Fact]
    public async Task Close_AfterConfirmations_AwaitsExpandStateFlushBeforeWindowBounds()
    {
        List<string> steps = [];
        TaskCompletionSource flushStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFlush = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<bool> closing = InvokeCloseWithExpandStateFlush(
            connectedSessionCount: 1,
            () =>
            {
                steps.Add("sessions");
                return Task.FromResult(true);
            },
            async () =>
            {
                steps.Add("expand-state");
                flushStarted.TrySetResult();
                await releaseFlush.Task;
            },
            () =>
            {
                steps.Add("window-bounds");
                return Task.CompletedTask;
            });

        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(closing.IsCompleted);
        Assert.Equal(["sessions", "expand-state"], steps);

        releaseFlush.TrySetResult();
        Assert.True(await closing);
        Assert.Equal(["sessions", "expand-state", "window-bounds"], steps);
    }

    [Fact]
    public async Task Close_SessionConfirmationRefused_DoesNotFlushOrPersistBounds()
    {
        int expandStateFlushCount = 0;
        int windowBoundsSaveCount = 0;

        bool canClose = await InvokeCloseWithExpandStateFlush(
            connectedSessionCount: 1,
            () => Task.FromResult(false),
            () =>
            {
                expandStateFlushCount++;
                return Task.CompletedTask;
            },
            () =>
            {
                windowBoundsSaveCount++;
                return Task.CompletedTask;
            });

        Assert.False(canClose);
        Assert.Equal(0, expandStateFlushCount);
        Assert.Equal(0, windowBoundsSaveCount);
    }

    [Fact]
    public async Task Close_ExpandStateFlushThrows_StillPersistsBoundsAndCloses()
    {
        int windowBoundsSaveCount = 0;

        bool canClose = await InvokeCloseWithExpandStateFlush(
            connectedSessionCount: 0,
            () => throw new InvalidOperationException("Session prompt must not run."),
            () => throw new IOException("Simulated expand-state flush failure."),
            () =>
            {
                windowBoundsSaveCount++;
                return Task.CompletedTask;
            });

        Assert.True(canClose);
        Assert.Equal(1, windowBoundsSaveCount);
    }

    private static Task<bool> InvokeCloseWithExpandStateFlush(
        int connectedSessionCount,
        Func<Task<bool>> promptCloseConnectedSessionsAsync,
        Func<Task> flushExpandStateAsync,
        Func<Task> persistWindowStateAsync)
    {
        return WindowClosingFlow.TryPrepareCloseAsync(
            settingsDirty: false,
            connectedSessionCount,
            () => throw new InvalidOperationException("Settings prompt must not run."),
            () => throw new InvalidOperationException("Settings save must not run."),
            promptCloseConnectedSessionsAsync,
            flushExpandStateAsync,
            persistWindowStateAsync,
            () => throw new InvalidOperationException("Warning must not run."));
    }

    private sealed class RecordingConfigManager : IConfigManager
    {
        public string ConfigPath => "memory://config";

        public string SettingsPath => "memory://settings.json";

        public string ServersPath => "memory://servers.json";

        public int MergeSettingCallCount { get; private set; }

        public AppSettings CurrentSettings { get; } = new();

        public event Action<AppSettings>? SettingsChanged;

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(CurrentSettings);

        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;

        public Task<bool> MergeHostKeyAsync(string hostPortKey, string fingerprint) =>
            Task.FromResult(false);

        public Task<int> MergeTrustedHostKeysAsync(
            IEnumerable<KeyValuePair<string, string>> entries) =>
            Task.FromResult(0);

        public Task MergeSettingAsync(Action<AppSettings> mutate)
        {
            MergeSettingCallCount++;
            mutate(CurrentSettings);
            SettingsChanged?.Invoke(CurrentSettings);
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
