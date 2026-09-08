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
using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Configuration;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

[Collection(CredentialProtectorAppCollection.Name)]
public sealed class GatewayDiagnosticViewModelTests
{
    [Fact]
    public void Preview_ContainsAllAncestorsAndUnsavedDraft()
    {
        GatewayDialogViewModel vm = Create();
        vm.ConfigureDiagnostics(Inventory(), (_, _, _, _, _) => Task.FromResult<IReadOnlyList<GatewayDiagnosticStep>>([]));
        vm.SelectedParentGatewayId = "child";
        vm.Name = "edited";
        Assert.Contains("root", vm.FullDiagnosticRoute);
        Assert.Contains("child", vm.FullDiagnosticRoute);
        Assert.Contains("edited", vm.FullDiagnosticRoute);
        Assert.Equal(new[] { "root", "child", "edited" }, vm.BuildDiagnosticRoute(false).Select(g => g.Name));
    }

    [Fact]
    public void Preview_RejectsCycleThroughCurrentGateway()
    {
        SshGatewayDto current = new() { Id = "root", Name = "root", Host = "root.invalid", User = "audit" };
        GatewayDialogViewModel vm = GatewayDialogViewModel.FromDto(current);
        vm.ConfigureDiagnostics(Inventory(), (_, _, _, _, _) => Task.FromResult<IReadOnlyList<GatewayDiagnosticStep>>([]));
        vm.SelectedParentGatewayId = "child";
        Assert.Equal("GatewayDiagnosticInvalidRoute", vm.FullDiagnosticRoute);
    }

    [Fact]
    public async Task Run_CopiesOnlyTypedResultsAndClearsAfterEditing()
    {
        GatewayDialogViewModel vm = Create();
        vm.Host = "private-host-secret.invalid";
        vm.User = "private-account-secret";
        vm.KeyPath = "private-key-path-secret";
        vm.ConfigureDiagnostics([], (_, _, _, _, _) =>
            Task.FromResult<IReadOnlyList<GatewayDiagnosticStep>>([new(1, false, false, SshFailureCode.AuthRejected, 12)]));
        await vm.TestGatewayRouteCommand.ExecuteAsync(null);
        Assert.Contains("GatewayDiagnosticAuth", vm.DiagnosticReport);
        Assert.DoesNotContain("private-", vm.DiagnosticReport);
        RecordingClipboard clipboard = new();
        vm.CopyDiagnosticReport(clipboard);
        Assert.Equal(vm.DiagnosticReport, clipboard.Text);
        vm.Host = "changed.invalid";
        Assert.Empty(vm.DiagnosticReport);
        Assert.False(vm.HasDiagnosticReport);
    }

    [Fact]
    public async Task Close_CancelsRunningTestAndSuppressesLateResult()
    {
        GatewayDialogViewModel vm = Create();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<IReadOnlyList<GatewayDiagnosticStep>> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        vm.ConfigureDiagnostics([], (_, _, _, _, ct) =>
        {
            observed = ct;
            started.SetResult();
            return completed.Task;
        });
        Task running = vm.TestGatewayRouteCommand.ExecuteAsync(null);
        await started.Task;
        Assert.False(vm.CanEditGateway);
        Assert.False(vm.CanStartDiagnostic);
        vm.CloseDiagnostics();
        Assert.True(observed.IsCancellationRequested);
        completed.SetResult([new(1, false, true, null, 1)]);
        await running;
        Assert.Empty(vm.DiagnosticReport);
        Assert.False(vm.CanStartDiagnostic);
    }

    [Fact]
    public async Task Stop_ProducesCancelledResultAndAllowsRetry()
    {
        GatewayDialogViewModel vm = Create();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.ConfigureDiagnostics([], async (_, _, _, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [];
        });
        Task running = vm.TestGatewayRouteCommand.ExecuteAsync(null);
        await started.Task;
        vm.CancelGatewayDiagnosticCommand.Execute(null);
        await running;
        Assert.Equal("GatewayDiagnosticCancelled", vm.DiagnosticStatus);
        Assert.True(vm.CanStartDiagnostic);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("")]
    public async Task InvalidTargetPort_DoesNotUsePreviousValueOrRun(string port)
    {
        GatewayDialogViewModel vm = Create();
        bool called = false;
        vm.ConfigureDiagnostics([], (_, _, _, _, _) =>
        {
            called = true;
            return Task.FromResult<IReadOnlyList<GatewayDiagnosticStep>>([]);
        });
        vm.DiagnosticTargetHost = "destination.invalid";
        vm.DiagnosticTargetPort = "443";
        vm.DiagnosticTargetPort = port;
        await vm.TestGatewayRouteCommand.ExecuteAsync(null);
        Assert.False(called);
        Assert.Equal("GatewayDiagnosticInvalidTarget", vm.DiagnosticStatus);
    }

    [Fact]
    public async Task RawFailureText_NeverEntersStatusOrReport()
    {
        GatewayDialogViewModel vm = Create();
        vm.ConfigureDiagnostics([], (_, _, _, _, _) =>
            Task.FromException<IReadOnlyList<GatewayDiagnosticStep>>(new IOException("password=must-never-appear")));
        await vm.TestGatewayRouteCommand.ExecuteAsync(null);
        Assert.Equal("GatewayDiagnosticUnavailable", vm.DiagnosticStatus);
        Assert.DoesNotContain("must-never-appear", vm.DiagnosticReport);
    }

    [Fact]
    public void ConfigureAfterClose_DoesNotEnableTest()
    {
        GatewayDialogViewModel vm = Create();
        vm.CloseDiagnostics();
        vm.ConfigureDiagnostics([], (_, _, _, _, _) => Task.FromResult<IReadOnlyList<GatewayDiagnosticStep>>([]));
        Assert.False(vm.DiagnosticsReady);
    }

    private static GatewayDialogViewModel Create() => new() { Name = "draft", Host = "draft.invalid", User = "audit" };
    private static SshGatewayDto[] Inventory() =>
    [
        new() { Id = "root", Name = "root", Host = "root.invalid", User = "audit" },
        new() { Id = "child", Name = "child", Host = "child.invalid", User = "audit", ParentGatewayId = "root" }
    ];
    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }
        public void SetText(string text) => Text = text;
    }
}
