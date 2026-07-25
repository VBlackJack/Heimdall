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
using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.CronJob;
using Heimdall.Core.Localization;

namespace Heimdall.App.Tests;

public sealed class CronJobViewModelTests
{
    [Fact]
    public async Task Initialize_PopulatesHelpText()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CronJobViewModel(new FakeCronJobService());

        vm.Initialize(localizer);

        Assert.False(string.IsNullOrWhiteSpace(vm.HelpText));
    }

    [Fact]
    public async Task Parse_EmptyInput_ShowsLocalizedError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CronJobViewModel(new FakeCronJobService());
        vm.Initialize(localizer);

        vm.ParseCommand.Execute(null);

        Assert.True(vm.HasCronError);
        Assert.Equal(localizer["ToolCronJobErrorEmpty"], vm.CronErrorText);
    }

    [Fact]
    public async Task Parse_ValidInput_LoadsEntriesAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CronJobViewModel(new FakeCronJobService());
        vm.Initialize(localizer);
        vm.CrontabInputText = "*/5 * * * * /usr/bin/echo hello";

        vm.ParseCommand.Execute(null);

        Assert.Single(vm.CronEntries);
        Assert.Equal(localizer["ToolCronJobStatusParsed"].Replace("{0}", "1", StringComparison.Ordinal), vm.StatusText);
        Assert.Equal("*/5 * * * *", vm.CronEntries[0].Schedule);
        Assert.False(vm.IsCronDetailVisible);
    }

    [Fact]
    public void Parse_NoValidEntries_ShowsError()
    {
        var vm = new CronJobViewModel(new FakeCronJobService())
        {
            CrontabInputText = "# comment only",
        };

        vm.ParseCommand.Execute(null);

        Assert.True(vm.HasCronError);
        Assert.Empty(vm.CronEntries);
    }

    [Fact]
    public async Task SelectedCronEntry_UpdatesDetailFields()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CronJobViewModel(new FakeCronJobService())
        {
            CrontabInputText = "0 12 * * 1 /backup",
        };
        vm.Initialize(localizer);
        vm.ParseCommand.Execute(null);

        vm.SelectedCronEntry = vm.CronEntries[0];

        Assert.True(vm.IsCronDetailVisible);
        Assert.Contains("0 12 * * 1", vm.DetailScheduleText, StringComparison.Ordinal);
        Assert.Contains("/backup", vm.DetailCommandText, StringComparison.Ordinal);
        Assert.Contains("202", vm.DetailNextRunsText, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearPaste_ResetsPasteState()
    {
        var vm = new CronJobViewModel(new FakeCronJobService())
        {
            CrontabInputText = "0 12 * * * /run",
        };
        vm.ParseCommand.Execute(null);

        vm.ClearPasteCommand.Execute(null);

        Assert.Equal(string.Empty, vm.CrontabInputText);
        Assert.Empty(vm.CronEntries);
        Assert.False(vm.HasCronError);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public void CopyAll_PasteMode_RaisesSanitizedPayloadWithoutHeader()
    {
        var vm = new CronJobViewModel(new FakeCronJobService())
        {
            CrontabInputText = "0 12 * * * =cmd",
        };
        vm.ParseCommand.Execute(null);

        string? payload = null;
        vm.CopyResultsRequested += (_, text) => payload = text;
        vm.CopyAllCommand.Execute(null);

        Assert.NotNull(payload);
        Assert.DoesNotContain("Schedule", payload, StringComparison.Ordinal);
        Assert.Contains("'\u003dcmd", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshTasks_Success_LoadsEntriesAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CronJobViewModel(new FakeCronJobService());
        vm.Initialize(localizer);

        await vm.RefreshTasksCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.TaskEntries.Count);
        Assert.Equal(localizer["ToolCronJobStatusTasks"].Replace("{0}", "2", StringComparison.Ordinal), vm.StatusText);
    }

    [Fact]
    public async Task RefreshTasks_IsBusyWhileLoading()
    {
        var service = new BlockingCronJobService();
        var vm = new CronJobViewModel(service);

        var task = vm.RefreshTasksCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => vm.IsBusy);

        Assert.False(vm.RefreshTasksCommand.CanExecute(null));

        service.Release([]);
        await task;

        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RefreshTasks_Timeout_ShowsLocalizedError()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CronJobViewModel(new ThrowingCronJobService(new OperationCanceledException()));
        vm.Initialize(localizer);

        await vm.RefreshTasksCommand.ExecuteAsync(null);

        Assert.True(vm.HasTasksError);
        Assert.Equal(localizer["ToolCronJobErrorTimeout"], vm.TasksErrorText);
    }

    [Fact]
    public async Task RefreshTasks_GenericError_PreservesRawMessageAcrossLocaleChange()
    {
        var en = await CreateLocalizerAsync("en");
        var fr = await CreateLocalizerAsync("fr");
        var vm = new CronJobViewModel(new ThrowingCronJobService(new InvalidOperationException("boom")));
        vm.Initialize(en);

        await vm.RefreshTasksCommand.ExecuteAsync(null);
        var errorBefore = vm.TasksErrorText;

        vm.UpdateLocalizer(fr);

        Assert.True(vm.HasTasksError);
        Assert.Contains("boom", errorBefore, StringComparison.Ordinal);
        Assert.Equal(errorBefore, vm.TasksErrorText);
    }

    [Fact]
    public async Task ModeIndex_SwitchesCopyPayloadSource()
    {
        var vm = new CronJobViewModel(new FakeCronJobService())
        {
            CrontabInputText = "0 12 * * * /run",
        };
        vm.ParseCommand.Execute(null);
        await vm.RefreshTasksCommand.ExecuteAsync(null);

        string? payload = null;
        vm.CopyResultsRequested += (_, text) => payload = text;

        vm.ModeIndex = (int)CronJobMode.Tasks;
        vm.CopyAllCommand.Execute(null);

        Assert.NotNull(payload);
        Assert.Contains(@"\TaskA", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/run", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocaleChanged_ReprojectsCronDescriptionAndStatus()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CronJobViewModel(new FakeCronJobService())
        {
            CrontabInputText = "0 12 * * 1 /backup",
        };
        vm.Initialize(localizer);
        vm.ParseCommand.Execute(null);
        vm.SelectedCronEntry = vm.CronEntries[0];
        var englishStatus = vm.StatusText;
        var englishDescription = vm.CronEntries[0].Description;
        var englishHeader = vm.DetailHeaderText;

        await localizer.SwitchLocaleAsync("fr");

        Assert.NotEqual(englishStatus, vm.StatusText);
        Assert.NotEqual(englishDescription, vm.CronEntries[0].Description);
        Assert.NotEqual(englishHeader, vm.DetailHeaderText);
    }

    [Fact]
    public void ToggleHelp_FlipsVisibility()
    {
        var vm = new CronJobViewModel(new FakeCronJobService());

        vm.ToggleHelpCommand.Execute(null);
        Assert.True(vm.IsHelpVisible);
        vm.ToggleHelpCommand.Execute(null);
        Assert.False(vm.IsHelpVisible);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromLocaleChanged()
    {
        var localizer = await CreateLocalizerAsync("en");
        var vm = new CronJobViewModel(new FakeCronJobService());
        vm.Initialize(localizer);
        var helpBefore = vm.HelpText;

        vm.Dispose();
        await localizer.SwitchLocaleAsync("fr");

        Assert.Equal(helpBefore, vm.HelpText);
    }

    private static async Task<LocalizationManager> CreateLocalizerAsync(string locale)
    {
        var manager = new LocalizationManager();
        await manager.LoadAsync(Path.Combine(AppContext.BaseDirectory, "locales"), locale);
        return manager;
    }

    /// <summary>
    /// Failure bound for a polled condition. The poll returns as soon as the
    /// predicate holds, so this is not a synchronisation point: it only has to
    /// outlast a saturated thread pool scheduling the work being observed.
    /// </summary>
    private const int PollBackstopMs = 30000;

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = PollBackstopMs)
    {
        var timeoutAt = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 > timeoutAt)
            {
                throw new TimeoutException("Condition was not met before timeout.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeCronJobService : ICronJobService
    {
        public IReadOnlyList<WindowsTaskEntry> Tasks { get; set; } =
        [
            new WindowsTaskEntry(@"\TaskA", "Ready", "4/18/2026 12:00:00", "4/17/2026 12:00:00", "0"),
            new WindowsTaskEntry(@"\TaskB", "Running", "N/A", "N/A", "267011"),
        ];

        public Task<IReadOnlyList<WindowsTaskEntry>> LoadWindowsTasksAsync(CancellationToken ct) =>
            Task.FromResult(Tasks);
    }

    private sealed class ThrowingCronJobService : ICronJobService
    {
        private readonly Exception _exception;

        public ThrowingCronJobService(Exception exception)
        {
            _exception = exception;
        }

        public Task<IReadOnlyList<WindowsTaskEntry>> LoadWindowsTasksAsync(CancellationToken ct) =>
            Task.FromException<IReadOnlyList<WindowsTaskEntry>>(_exception);
    }

    private sealed class BlockingCronJobService : ICronJobService
    {
        private readonly TaskCompletionSource<IReadOnlyList<WindowsTaskEntry>> _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<WindowsTaskEntry>> LoadWindowsTasksAsync(CancellationToken ct) => _gate.Task;

        public void Release(IReadOnlyList<WindowsTaskEntry> tasks) => _gate.TrySetResult(tasks);
    }
}
