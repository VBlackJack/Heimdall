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

using Heimdall.App.ViewModels;
using Heimdall.App.ViewModels.CommandLibrary;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins a runbook run started from the library: what each step's command becomes, which gestures
/// ask for confirmation, and what each gesture records in history.
/// </summary>
/// <remarks>
/// Every run goes through <see cref="CommandLibraryViewModel.RunRunbook"/>, so the wiring from
/// the library to the run (session platform, host, terminal handlers, history) is under test too.
/// History is written on a worker thread, so a test waits for the rows it expects, with a bound.
/// </remarks>
public sealed class RunbookRunTests
{
    private const string Host = "srv-01";
    private static readonly TimeSpan RecordWait = TimeSpan.FromSeconds(10);

    private static readonly ActionModel Ping = CommandLibraryTestHelpers.CreateLinuxAction(
        "ping", "Ping", "ping -c 1 {target}",
        CommandLibraryTestHelpers.RequiredParameter("target", "Target", "hostname"));

    private static readonly ActionModel Status = CommandLibraryTestHelpers.CreateLinuxAction(
        "status", "Service status", "systemctl status {unit}",
        CommandLibraryTestHelpers.RequiredParameter("unit", "Unit"));

    [Fact]
    public async Task AVariableReference_IsResolvedBeforeGeneration_WithTheHostPrefilledFromTheSession()
    {
        await using var run = await StartAsync(
            Runbook(variables: [("host", "")], RunbookTestHelpers.ActionStep("ping", ("target", "{{host}}"))),
            "SSH",
            Ping);

        Assert.Equal(Host, Assert.Single(run.Run.Variables).Value);
        Assert.Equal("ping -c 1 srv-01", run.Run.CurrentCommand);
        Assert.True(run.Run.IsCurrentCommandValid);
        Assert.Empty(run.Run.CurrentValidationError);
    }

    [Fact]
    public async Task AnUnknownVariable_IsLeftAsWritten_AndTheStepSaysSo()
    {
        await using var run = await StartAsync(
            Runbook(variables: [("unit", "nginx")], RunbookTestHelpers.ActionStep("status", ("unit", "{{x}}"))),
            "SSH",
            Status);

        var step = Assert.Single(run.Run.Steps);
        Assert.Contains("{{x}}", run.Run.CurrentCommand);
        Assert.Contains("{{x}}", step.ValidationError);
        Assert.False(run.Run.IsCurrentCommandValid);
        Assert.False(run.Run.CanSend);
    }

    [Theory]
    [InlineData("SSH", "systemctl restart nginx")]
    [InlineData("RDP", "Restart-Service -Name nginx")]
    public async Task AnActionStep_OpensOnTheTemplateOfTheSessionsPlatform(string connectionType, string expected)
    {
        var restart = RunbookTestHelpers.CreateDualAction(
            "restart", "Restart-Service -Name {unit}", "systemctl restart {unit}", "unit");

        await using var run = await StartAsync(
            Runbook(variables: [("unit", "nginx")], RunbookTestHelpers.ActionStep("restart", ("unit", "{{unit}}"))),
            connectionType,
            restart);

        Assert.Equal(expected, run.Run.CurrentCommand);
    }

    [Fact]
    public async Task SendOnADangerousStep_Refused_LeavesTheStepPendingAndSendsNothing()
    {
        var dangerous = Dangerous(Status);
        await using var run = await StartAsync(
            Runbook(variables: [("unit", "nginx")], RunbookTestHelpers.ActionStep("status", ("unit", "{{unit}}"))),
            "SSH",
            confirm: false,
            dangerous);

        await run.Run.SendAsync();

        Assert.Single(run.Dialogs.ConfirmMessages);
        Assert.Empty(run.Sent);
        Assert.Equal(RunbookStepStatus.Pending, run.Run.Steps[0].Status);
        Assert.Equal(0, run.Run.CurrentIndex);
    }

    // Positive control for the refusal above: confirmed, the same step goes.
    [Fact]
    public async Task SendOnADangerousStep_Confirmed_SendsIt()
    {
        var dangerous = Dangerous(Status);
        await using var run = await StartAsync(
            Runbook(variables: [("unit", "nginx")], RunbookTestHelpers.ActionStep("status", ("unit", "{{unit}}"))),
            "SSH",
            confirm: true,
            dangerous);

        await run.Run.SendAsync();

        Assert.Equal(["systemctl status nginx"], run.Sent);
        Assert.Equal(RunbookStepStatus.Sent, run.Run.Steps[0].Status);
    }

    [Fact]
    public async Task SendOnALiteralStep_AlwaysAsks()
    {
        await using var run = await StartAsync(
            Runbook(variables: [], RunbookTestHelpers.LiteralStep("uptime")),
            "SSH",
            confirm: true);

        await run.Run.SendAsync();

        Assert.Single(run.Dialogs.ConfirmMessages);
        Assert.Equal(["uptime"], run.Sent);
    }

    // Control for the literal rule: an action step below the dangerous level goes without a word.
    [Fact]
    public async Task SendOnARunLevelActionStep_DoesNotAsk()
    {
        await using var run = await StartAsync(
            Runbook(variables: [("unit", "nginx")], RunbookTestHelpers.ActionStep("status", ("unit", "{{unit}}"))),
            "SSH",
            confirm: false,
            Status);

        await run.Run.SendAsync();

        Assert.Empty(run.Dialogs.ConfirmMessages);
        Assert.Single(run.Sent);
    }

    [Fact]
    public async Task EachSentStep_WritesOneHistoryRow_AsExecuted_AgainstTheSessionsHost()
    {
        await using var run = await StartAsync(
            Runbook(
                variables: [("unit", "nginx")],
                RunbookTestHelpers.ActionStep("status", ("unit", "{{unit}}")),
                RunbookTestHelpers.ActionStep("status", ("unit", "sshd"))),
            "SSH",
            Status);

        await run.Run.SendAsync();
        await run.Run.SendAsync();
        await run.History.WaitForAsync(2, RecordWait);

        Assert.True(run.Run.IsFinished);
        Assert.Equal(2, run.History.Rows.Count);
        Assert.All(run.History.Rows, row =>
        {
            Assert.True(row.IsExecuted);
            Assert.Equal(Host, row.TargetHost);
            Assert.Equal("status", row.ActionId);
        });
        Assert.Equal(
            ["systemctl status nginx", "systemctl status sshd"],
            run.History.Rows.Select(row => row.Command).Order(StringComparer.Ordinal));
    }

    // Insert types without running: one row, recorded as not executed, like the library's Insert.
    [Fact]
    public async Task Insert_WritesOneRowAsNotExecuted_AndMovesOn()
    {
        await using var run = await StartAsync(
            Runbook(variables: [("unit", "nginx")], RunbookTestHelpers.ActionStep("status", ("unit", "{{unit}}"))),
            "SSH",
            Status);

        run.Run.Insert();
        await run.History.WaitForAsync(1, RecordWait);

        Assert.Equal(["systemctl status nginx"], run.Inserted);
        Assert.Empty(run.Sent);
        Assert.False(Assert.Single(run.History.Rows).IsExecuted);
        Assert.Equal(RunbookStepStatus.Inserted, run.Run.Steps[0].Status);
        Assert.True(run.Run.IsFinished);
    }

    [Fact]
    public async Task WithoutATerminal_SendIsOff_AndCopyCarriesTheRun()
    {
        await using var run = await StartAsync(
            Runbook(
                variables: [("unit", "nginx")],
                RunbookTestHelpers.ActionStep("status", ("unit", "{{unit}}")),
                RunbookTestHelpers.LiteralStep("uptime")),
            "RDP",
            confirm: false,
            hasTerminal: false,
            Status);

        Assert.False(run.Run.CanSend);
        Assert.False(run.Run.CanInsert);
        Assert.True(run.Run.ShowNoTerminalHint);

        run.Run.Copy();

        Assert.Equal(RunbookStepStatus.Copied, run.Run.Steps[0].Status);
        Assert.Equal(1, run.Run.CurrentIndex);
        Assert.Equal("uptime", run.Run.CurrentCommand);
        Assert.Single(run.Copied);
    }

    [Fact]
    public async Task SkipThenStop_MarksTheStep_AndClosesTheRun()
    {
        await using var run = await StartAsync(
            Runbook(
                variables: [("unit", "nginx")],
                RunbookTestHelpers.ActionStep("status", ("unit", "{{unit}}")),
                RunbookTestHelpers.LiteralStep("uptime")),
            "SSH",
            Status);

        run.Run.Skip();
        Assert.Equal(RunbookStepStatus.Skipped, run.Run.Steps[0].Status);
        Assert.True(run.Run.Steps[1].IsCurrent);

        run.Run.Stop();

        Assert.Null(run.Library.ActiveRun);
        Assert.True(run.Library.IsRunbookListVisible);
    }

    // Mutant: route the last run through UpdateBatchAsync, which stamps UpdatedAt as an edit.
    [Fact]
    public async Task StartingARun_SetsTheLastRun_AndLeavesUpdatedAtAlone()
    {
        var lastEdit = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var runbook = Runbook(variables: [("unit", "nginx")], RunbookTestHelpers.ActionStep("status", ("unit", "{{unit}}")));
        runbook.UpdatedAt = lastEdit;
        await using var run = await StartAsync(runbook, "SSH", Status);

        await run.Run.SendAsync();
        var deadline = DateTime.UtcNow + RecordWait;
        while (run.Batches.Stored[0].LastExecutedAt is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        var stored = run.Batches.Stored[0];
        Assert.NotNull(stored.LastExecutedAt);
        Assert.Equal(lastEdit, stored.UpdatedAt);
    }

    private static ActionModel Dangerous(ActionModel action)
    {
        action = CommandLibraryTestHelpers.CreateLinuxAction(
            action.Id, action.Title, action.LinuxCommandTemplate!.CommandPattern, [.. action.LinuxCommandTemplate.Parameters]);
        action.Level = CriticalityLevel.Dangerous;
        return action;
    }

    private static CommandBatch Runbook((string Name, string Value)[] variables, params BatchCommandItem[] steps)
    {
        for (var index = 0; index < steps.Length; index++)
        {
            steps[index].Order = index;
        }

        return new CommandBatch
        {
            Id = "rb-1",
            Name = "Restart a unit",
            Variables = variables.ToDictionary(variable => variable.Name, variable => variable.Value, StringComparer.Ordinal),
            Commands = [.. steps]
        };
    }

    private static Task<RunFixture> StartAsync(CommandBatch runbook, string connectionType, params ActionModel[] actions)
        => StartAsync(runbook, connectionType, confirm: false, hasTerminal: true, actions);

    private static Task<RunFixture> StartAsync(CommandBatch runbook, string connectionType, bool confirm, params ActionModel[] actions)
        => StartAsync(runbook, connectionType, confirm, hasTerminal: true, actions);

    private static async Task<RunFixture> StartAsync(
        CommandBatch runbook,
        string connectionType,
        bool confirm,
        bool hasTerminal,
        params ActionModel[] actions)
    {
        var batches = new FakeBatchService();
        batches.Seed(runbook);
        var history = new CollectingHistoryService();
        var dialogs = new SilentDialogService { ConfirmResult = confirm };
        var library = await RunbookTestHelpers.CreateLibraryAsync(batches, history, dialogs, Host, connectionType, actions);

        var fixture = new RunFixture(library, history, dialogs, batches);
        if (hasTerminal)
        {
            library.SendCommandHandler = fixture.Sent.Add;
            library.InsertTextHandler = fixture.Inserted.Add;
            library.CanSendToTerminalProbe = () => true;
        }

        library.SetClipboardText = text =>
        {
            fixture.Copied.Add(text);
            return true;
        };

        await library.ToggleRunbooksAsync();
        library.SelectedRunbook = library.Runbooks.Single();
        library.RunRunbook();
        return fixture;
    }

    private sealed class RunFixture(
        CommandLibraryViewModel library,
        CollectingHistoryService history,
        SilentDialogService dialogs,
        FakeBatchService batches) : IAsyncDisposable
    {
        public FakeBatchService Batches { get; } = batches;

        public CommandLibraryViewModel Library { get; } = library;

        public RunbookRunViewModel Run => Library.ActiveRun
            ?? throw new InvalidOperationException("The library started no run.");

        public CollectingHistoryService History { get; } = history;

        public SilentDialogService Dialogs { get; } = dialogs;

        public List<string> Sent { get; } = [];

        public List<string> Inserted { get; } = [];

        public List<string> Copied { get; } = [];

        public ValueTask DisposeAsync()
        {
            Library.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
