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

using Heimdall.App.Services.PostConnect;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed class PostConnectSequenceRunnerTests
{
    private readonly PostConnectSequenceRunner _runner = new();

    [Fact]
    public async Task RunAsync_ExecutesEnabledStepsInOrder()
    {
        var writes = new List<string>();

        var result = await _runner.RunAsync(
            [
                new PostConnectStep { Input = "pwd", DelayMs = 0 },
                new PostConnectStep { Input = "whoami", DelayMs = 0 }
            ],
            writes.Add,
            progress: null,
            CancellationToken.None);

        Assert.Equal(["pwd", "whoami"], writes);
        Assert.Equal(2, result.StepsExecuted);
        Assert.Equal(0, result.StepsFailed);
    }

    [Fact]
    public async Task RunAsync_SkipsDisabledAndBlankSteps()
    {
        var writes = new List<string>();

        var result = await _runner.RunAsync(
            [
                new PostConnectStep { Input = "pwd", DelayMs = 0, Enabled = false },
                new PostConnectStep { Input = "   ", DelayMs = 0, Enabled = true },
                new PostConnectStep { Input = "whoami", DelayMs = 0 }
            ],
            writes.Add,
            progress: null,
            CancellationToken.None);

        Assert.Equal(["whoami"], writes);
        Assert.Equal(1, result.StepsExecuted);
        Assert.Equal(2, result.StepsSkippedDisabled);
    }

    [Fact]
    public async Task RunAsync_StopPolicy_StopsAfterFirstFailure()
    {
        var writes = new List<string>();

        var result = await _runner.RunAsync(
            [
                new PostConnectStep { Input = "pwd", DelayMs = 0 },
                new PostConnectStep { Input = "boom", DelayMs = 0, OnFailure = PostConnectFailurePolicy.Stop },
                new PostConnectStep { Input = "hostname", DelayMs = 0 }
            ],
            input =>
            {
                if (input == "boom")
                {
                    throw new InvalidOperationException("broken");
                }

                writes.Add(input);
            },
            progress: null,
            CancellationToken.None);

        Assert.Equal(["pwd"], writes);
        Assert.Equal(1, result.StepsExecuted);
        Assert.Equal(1, result.StepsFailed);
        Assert.True(result.WasStoppedByFailurePolicy);
    }

    [Fact]
    public async Task RunAsync_ContinuePolicy_ContinuesAfterFailure()
    {
        var writes = new List<string>();

        var result = await _runner.RunAsync(
            [
                new PostConnectStep { Input = "boom", DelayMs = 0, OnFailure = PostConnectFailurePolicy.Continue },
                new PostConnectStep { Input = "hostname", DelayMs = 0 }
            ],
            input =>
            {
                if (input == "boom")
                {
                    throw new InvalidOperationException("broken");
                }

                writes.Add(input);
            },
            progress: null,
            CancellationToken.None);

        Assert.Equal(["hostname"], writes);
        Assert.Equal(1, result.StepsExecuted);
        Assert.Equal(1, result.StepsFailed);
        Assert.False(result.WasStoppedByFailurePolicy);
    }

    [Fact]
    public async Task RunAsync_LogsAndContinues_WhenStepThrows()
    {
        var writes = new List<string>();
        var recorder = new ProgressRecorder<PostConnectRunProgress>();

        var result = await _runner.RunAsync(
            [
                new PostConnectStep { Input = "boom", DelayMs = 0, OnFailure = PostConnectFailurePolicy.Continue },
                new PostConnectStep { Input = "hostname", DelayMs = 0 }
            ],
            input =>
            {
                if (input == "boom")
                {
                    throw new InvalidOperationException("broken");
                }

                writes.Add(input);
            },
            recorder,
            CancellationToken.None);

        Assert.Equal(["hostname"], writes);
        Assert.Equal(1, result.StepsExecuted);
        Assert.Equal(1, result.StepsFailed);
        Assert.Contains(recorder.Items, item => item.Status == PostConnectStepStatus.Failed);
        Assert.Contains(recorder.Items, item => item.Status == PostConnectStepStatus.Completed);
    }

    [Fact]
    public async Task RunAsync_CancelledDuringDelay_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(30);

        var result = await _runner.RunAsync(
            [new PostConnectStep { Input = "pwd", DelayMs = 5_000 }],
            _ => { },
            progress: null,
            cts.Token);

        Assert.True(result.WasCancelled);
        Assert.Equal(0, result.StepsExecuted);
    }

    [Fact]
    public async Task RunAsync_ReportsProgressStatuses()
    {
        var recorder = new ProgressRecorder<PostConnectRunProgress>();

        await _runner.RunAsync(
            [new PostConnectStep { Input = "pwd", DelayMs = 0 }],
            _ => { },
            recorder,
            CancellationToken.None);

        Assert.Contains(recorder.Items, e => e.Status == PostConnectStepStatus.Pending);
        Assert.Contains(recorder.Items, e => e.Status == PostConnectStepStatus.Running);
        Assert.Contains(recorder.Items, e => e.Status == PostConnectStepStatus.Completed);
    }

    [Fact]
    public async Task RunAsync_TruncatesDisplayTextToEightyCharacters()
    {
        var longInput = new string('a', 90);
        var recorder = new ProgressRecorder<PostConnectRunProgress>();

        await _runner.RunAsync(
            [new PostConnectStep { Input = longInput, DelayMs = 0 }],
            _ => { },
            recorder,
            CancellationToken.None);

        var pending = Assert.Single(recorder.Items, progress => progress.Status == PostConnectStepStatus.Pending);
        Assert.NotNull(pending);
        Assert.Equal(80, pending.CurrentStepDisplayText.Length);
        Assert.EndsWith("...", pending.CurrentStepDisplayText);
    }

    [Fact]
    public async Task RunAsync_LinkedStep_UsesResolvedCommandInsteadOfDormantInput()
    {
        var writes = new List<string>();
        var recorder = new ProgressRecorder<PostConnectRunProgress>();
        var resolver = new FakeResolver(new PostConnectResolveResult
        {
            Status = PostConnectResolveStatus.Resolved,
            ResolvedInput = "tail -f /var/log/app.log"
        });

        var result = await _runner.RunAsync(
            [
                new PostConnectStep
                {
                    Input = "dormant",
                    CommandLibraryId = "tail-log",
                    CommandLibraryParams = new Dictionary<string, string>(StringComparer.Ordinal) { ["path"] = "/var/log/app.log" },
                    DelayMs = 0
                }
            ],
            writes.Add,
            progress: recorder,
            CancellationToken.None,
            resolver);

        Assert.Equal(["tail -f /var/log/app.log"], writes);
        Assert.Equal(1, result.StepsExecuted);
        Assert.Equal(0, result.StepsBroken);
        Assert.Equal("tail-log", Assert.Single(recorder.Items, item => item.Status == PostConnectStepStatus.Pending).CurrentStepDisplayText);
    }

    [Fact]
    public async Task RunAsync_BrokenLinkedStep_DoesNotExecuteDormantInput_AndContinues()
    {
        var writes = new List<string>();
        var resolver = new FakeResolver(
            new PostConnectResolveResult
            {
                Status = PostConnectResolveStatus.BrokenInvalidParams,
                ReasonKey = "LogPostConnectResolveBrokenInvalidParams"
            },
            new PostConnectResolveResult
            {
                Status = PostConnectResolveStatus.Literal,
                ResolvedInput = "hostname"
            });

        var result = await _runner.RunAsync(
            [
                new PostConnectStep
                {
                    Input = "dormant",
                    CommandLibraryId = "tail-log",
                    DelayMs = 0,
                    OnFailure = PostConnectFailurePolicy.Stop
                },
                new PostConnectStep { Input = "hostname", DelayMs = 0 }
            ],
            writes.Add,
            progress: null,
            CancellationToken.None,
            resolver);

        Assert.Equal(["hostname"], writes);
        Assert.Equal(1, result.StepsExecuted);
        Assert.Equal(1, result.StepsBroken);
        Assert.False(result.WasStoppedByFailurePolicy);
    }

    [Fact]
    public async Task RunAsync_ResolverReturningLiteralForLinkedStep_TreatsItAsBroken()
    {
        var writes = new List<string>();
        var resolver = new FakeResolver(new PostConnectResolveResult
        {
            Status = PostConnectResolveStatus.Literal,
            ResolvedInput = "should-not-run"
        });

        var result = await _runner.RunAsync(
            [
                new PostConnectStep
                {
                    Input = "dormant",
                    CommandLibraryId = "tail-log",
                    DelayMs = 0
                }
            ],
            writes.Add,
            progress: null,
            CancellationToken.None,
            resolver);

        Assert.Empty(writes);
        Assert.Equal(0, result.StepsExecuted);
        Assert.Equal(1, result.StepsBroken);
    }

    private sealed class ProgressRecorder<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];

        public void Report(T value)
        {
            Items.Add(value);
        }
    }

    private sealed class FakeResolver(params PostConnectResolveResult[] results) : IPostConnectStepResolver
    {
        private readonly Queue<PostConnectResolveResult> _results = new(results);

        public Task<PostConnectResolveResult> ResolveAsync(PostConnectStep step, CancellationToken ct)
        {
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No resolve result queued.");
            }

            return Task.FromResult(_results.Dequeue());
        }
    }
}
