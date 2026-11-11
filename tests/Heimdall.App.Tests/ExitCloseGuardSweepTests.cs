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

using System.Reflection;
using Heimdall.App.Services;
using Heimdall.App.ViewModels;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed class ExitCloseGuardSweepTests
{
    private const string GuardReason = "FakeGuardBusy";

    [Fact]
    public async Task NothingGuarded_ExitsWithoutAWord()
    {
        List<(string Key, string Title)> reports = [];

        bool allowed = await ExitCloseGuardSweep.ConfirmAsync(
            [new ExitCloseHost(new object(), "Plain")], new PaneCloseArbiter(), (key, title) => reports.Add((key, title)));

        Assert.True(allowed);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task EachDeferredGuard_IsAskedOnce()
    {
        FakeGuard first = new() { Consent = true };
        FakeGuard second = new() { Consent = true };
        ExitCloseHost[] hosts = [new(first, "One"), new(second, "Two")];

        bool allowed = await ExitCloseGuardSweep.ConfirmAsync(hosts, new PaneCloseArbiter(), (_, _) => { });

        Assert.True(allowed);
        Assert.Equal(1, first.ResolveCount);
        Assert.Equal(1, second.ResolveCount);
    }

    [Fact]
    public async Task ARefusalAfterTheGuardsOwnPrompt_StopsTheSweepInSilence()
    {
        FakeGuard refusing = new() { Consent = false };
        FakeGuard never = new() { Consent = true };
        List<(string Key, string Title)> reports = [];

        bool allowed = await ExitCloseGuardSweep.ConfirmAsync(
            [new ExitCloseHost(refusing, "Editor"), new ExitCloseHost(never, "Other")],
            new PaneCloseArbiter(),
            (key, title) => reports.Add((key, title)));

        Assert.False(allowed);
        Assert.Equal(0, never.ResolveCount);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task ADeny_ReportsItsReasonAndStops()
    {
        FakeGuard denying = new() { Verdict = CloseVerdict.Deny };
        List<(string Key, string Title)> reports = [];

        bool allowed = await ExitCloseGuardSweep.ConfirmAsync(
            [new ExitCloseHost(denying, "Locked")], new PaneCloseArbiter(), (key, title) => reports.Add((key, title)));

        Assert.False(allowed);
        Assert.Equal((GuardReason, "Locked"), Assert.Single(reports));
        Assert.Equal(0, denying.ResolveCount);
    }

    [Fact]
    public async Task WorkThatChangesDuringThePrompt_IsReportedNotWavedThrough()
    {
        FakeGuard guard = new() { Consent = true };
        guard.OnResolve = () => guard.Epoch++;
        List<(string Key, string Title)> reports = [];

        bool allowed = await ExitCloseGuardSweep.ConfirmAsync(
            [new ExitCloseHost(guard, "Busy")], new PaneCloseArbiter(), (key, title) => reports.Add((key, title)));

        Assert.False(allowed);
        Assert.Equal(1, guard.ResolveCount);
        Assert.Equal((CloseGuardLocaleKeys.BlockedStale, "Busy"), Assert.Single(reports));
    }

    [Fact]
    public async Task ABusyTool_DoesNotHoldTheExit()
    {
        FakeTool busy = new(ToolCloseRefusal.Busy);
        List<(string Key, string Title)> reports = [];

        bool allowed = await ExitCloseGuardSweep.ConfirmAsync(
            [new ExitCloseHost(busy, "Scanner")], new PaneCloseArbiter(), (key, title) => reports.Add((key, title)));

        Assert.True(allowed);
        Assert.Equal(1, busy.CanCloseCount);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task ADeclinedToolPrompt_KeepsTheWindowOpenInSilence()
    {
        FakeTool declined = new(ToolCloseRefusal.UserDeclined);
        FakeTool after = new(ToolCloseRefusal.UserDeclined);
        List<(string Key, string Title)> reports = [];

        bool allowed = await ExitCloseGuardSweep.ConfirmAsync(
            [new ExitCloseHost(declined, "Notes"), new ExitCloseHost(after, "Hosts")],
            new PaneCloseArbiter(),
            (key, title) => reports.Add((key, title)));

        Assert.False(allowed);
        Assert.Empty(reports);
        Assert.Equal(0, after.CanCloseCount);
    }

    [Fact]
    public async Task AFailedSave_IsReported()
    {
        FakeTool failed = new(ToolCloseRefusal.SaveFailed);
        List<(string Key, string Title)> reports = [];

        bool allowed = await ExitCloseGuardSweep.ConfirmAsync(
            [new ExitCloseHost(failed, "Hosts")], new PaneCloseArbiter(), (key, title) => reports.Add((key, title)));

        Assert.False(allowed);
        Assert.Equal((CloseGuardLocaleKeys.BlockedSaveFailed, "Hosts"), Assert.Single(reports));
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("refuse")]
    [InlineData("deny")]
    [InlineData("declined-tool")]
    public async Task TheRequestIsReleased_InEveryOutcome(string outcome)
    {
        RecordingArbiter arbiter = new();
        object host = outcome switch
        {
            "refuse" => new FakeGuard { Consent = false },
            "deny" => new FakeGuard { Verdict = CloseVerdict.Deny },
            "declined-tool" => new FakeTool(ToolCloseRefusal.UserDeclined),
            _ => new FakeGuard { Consent = true },
        };

        _ = await ExitCloseGuardSweep.ConfirmAsync([new ExitCloseHost(host, "H")], arbiter, (_, _) => { });

        Assert.Equal(1, arbiter.ReleaseCount);
        Assert.Equal(0, arbiter.Inner.TrackedDeferredRequestCount);
    }

    [Fact]
    public async Task AGuardThatThrowsWhileResolving_IsARefusalAndStillReleased()
    {
        RecordingArbiter arbiter = new();
        FakeGuard throwing = new() { ResolveThrows = true };

        bool allowed = await ExitCloseGuardSweep.ConfirmAsync(
            [new ExitCloseHost(throwing, "H")], arbiter, (_, _) => { });

        Assert.False(allowed);
        Assert.Equal(1, arbiter.ReleaseCount);
    }

    [Fact]
    public async Task ConnectionViewModel_AsksDetachedWindowsToo()
    {
        FakeGuard detachedGuard = new() { Consent = false };
        SessionTabViewModel detached = new() { ServerId = "d", Title = "Detached", ConnectionType = "SSH" };
        detached.HostControl = detachedGuard;
        IDialogService dialogService = DispatchProxy.Create<IDialogService, InfoRecordingDialogProxy>();
        ISplitService splitService = DispatchProxy.Create<ISplitService, NoopSplitProxy>();
        SessionWindowService windows = new(static (_, _) => { }, () => [detached]);
        ConnectionViewModel viewModel = new(
            new LocalizationManager(), dialogService, splitService, new PaneCloseArbiter(), windows);

        bool allowed = await viewModel.ConfirmCloseForExitAsync();

        Assert.False(allowed);
        Assert.Equal(1, detachedGuard.ResolveCount);
        Assert.Equal(0, ((InfoRecordingDialogProxy)(object)dialogService).InfoCount);
    }

    [Fact]
    public async Task ConnectionViewModel_WorkspaceLocked_AsksNothing()
    {
        FakeGuard detachedGuard = new() { Consent = false };
        FakeTool tool = new(ToolCloseRefusal.UserDeclined);
        SessionTabViewModel detached = new() { ServerId = "d", Title = "Detached", ConnectionType = "SSH" };
        detached.HostControl = detachedGuard;
        SessionTabViewModel toolTab = new() { ServerId = "t", Title = "Tool", ConnectionType = "SSH" };
        toolTab.HostControl = tool;
        IDialogService dialogService = DispatchProxy.Create<IDialogService, InfoRecordingDialogProxy>();
        ISplitService splitService = DispatchProxy.Create<ISplitService, NoopSplitProxy>();
        SessionWindowService windows = new(static (_, _) => { }, () => [detached, toolTab]);
        ConnectionViewModel viewModel = new(
            new LocalizationManager(), dialogService, splitService, new PaneCloseArbiter(), windows);

        bool allowed = await viewModel.ConfirmCloseForExitAsync(workspaceLocked: true);

        Assert.True(allowed);
        Assert.Equal(0, detachedGuard.ResolveCount);
        Assert.Equal(0, tool.CanCloseCount);
        Assert.Equal(0, ((InfoRecordingDialogProxy)(object)dialogService).InfoCount);
    }

    [Fact]
    public async Task AToolWithRecoverableWork_IsNotAskedAtExit()
    {
        FakeRecoverableTool recoverable = new();
        List<(string Key, string Title)> reports = [];

        bool allowed = await ExitCloseGuardSweep.ConfirmAsync(
            [new ExitCloseHost(recoverable, "Diagram")], new PaneCloseArbiter(), (key, title) => reports.Add((key, title)));

        Assert.True(allowed);
        Assert.Equal(0, recoverable.CanCloseCount);
        Assert.Empty(reports);

        // The tool's own answer is untouched: a single tab close still asks it.
        Assert.False(recoverable.CanClose());
        Assert.Equal(1, recoverable.CanCloseCount);
    }

    private sealed class FakeGuard : ICloseGuard
    {
        public CloseVerdict Verdict { get; set; } = CloseVerdict.Defer;

        public bool Consent { get; set; }

        public bool ResolveThrows { get; set; }

        public long Epoch { get; set; } = 1;

        public Action? OnResolve { get; set; }

        public int ResolveCount { get; private set; }

        public CloseGuardState SampleCloseGuardState() => new(true, Epoch);

        public CloseDecision PollClose(CloseRequest request) => Verdict switch
        {
            CloseVerdict.Deny => CloseDecision.Deny(GuardReason, Epoch),
            CloseVerdict.Allow => CloseDecision.Allow(Epoch),
            _ => CloseDecision.Defer(GuardReason, Epoch),
        };

        public Task<bool> ResolveCloseAsync(CloseRequest request, CancellationToken cancellationToken)
        {
            ResolveCount++;
            OnResolve?.Invoke();
            return ResolveThrows
                ? throw new InvalidOperationException("boom")
                : Task.FromResult(Consent);
        }
    }

    private sealed class FakeTool(ToolCloseRefusal refusal) : IToolView
    {
        public int CanCloseCount { get; private set; }

        public ToolCloseRefusal CloseRefusal => refusal;

        public void Initialize(ToolContext? context, LocalizationManager? localizer)
        {
        }

        public bool CanClose()
        {
            CanCloseCount++;
            return false;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeRecoverableTool : IToolView, IRecoverableUnsavedWork
    {
        public int CanCloseCount { get; private set; }

        public ToolCloseRefusal CloseRefusal => ToolCloseRefusal.UserDeclined;

        public void Initialize(ToolContext? context, LocalizationManager? localizer)
        {
        }

        public bool CanClose()
        {
            CanCloseCount++;
            return false;
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingArbiter : IPaneCloseArbiter
    {
        public PaneCloseArbiter Inner { get; } = new();

        public int ReleaseCount { get; private set; }

        public CloseDecision Poll(CloseRequest request, IReadOnlyList<object?> hosts) => Inner.Poll(request, hosts);

        public Task<bool> ResolveAsync(CloseRequest request, IReadOnlyList<object?> hosts)
            => Inner.ResolveAsync(request, hosts);

        public void Release(CloseRequest request)
        {
            ReleaseCount++;
            Inner.Release(request);
        }
    }

    private class InfoRecordingDialogProxy : DispatchProxy
    {
        public int InfoCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDialogService.ShowInfo))
            {
                InfoCount++;
                return null;
            }

            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    private class NoopSplitProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new NotSupportedException(targetMethod?.Name);
    }
}
