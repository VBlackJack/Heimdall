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

using Heimdall.Core.Logging;
using Heimdall.Core.Models;

namespace Heimdall.App.Services.PostConnect;

public sealed class PostConnectSequenceRunner : IPostConnectSequenceRunner
{
    public async Task<PostConnectRunResult> RunAsync(
        IReadOnlyList<PostConnectStep> steps,
        Action<string> writeCallback,
        IProgress<PostConnectRunProgress>? progress,
        CancellationToken ct,
        IPostConnectStepResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(writeCallback);

        FileLogger.Info($"Post-connect: running {steps.Count} step(s).");

        var stepsExecuted = 0;
        var stepsSkippedDisabled = 0;
        var stepsFailed = 0;
        var stepsBroken = 0;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (ct.IsCancellationRequested)
            {
                Report(progress, step, index, steps.Count, PostConnectStepStatus.Cancelled);
                return Finish(stepsExecuted, stepsSkippedDisabled, stepsFailed, stepsBroken, wasCancelled: true, wasStoppedByFailurePolicy: false);
            }

            var hasLibraryLink = !string.IsNullOrWhiteSpace(step.CommandLibraryId);
            var hasLiteralInput = !string.IsNullOrWhiteSpace(step.Input);
            if (!step.Enabled || (!hasLibraryLink && !hasLiteralInput))
            {
                Report(progress, step, index, steps.Count, PostConnectStepStatus.Skipped);
                stepsSkippedDisabled++;
                continue;
            }

            Report(progress, step, index, steps.Count, PostConnectStepStatus.Pending);
            Report(progress, step, index, steps.Count, PostConnectStepStatus.Running);

            try
            {
                var inputToSend = step.Input;
                if (hasLibraryLink)
                {
                    if (resolver is null)
                    {
                        Report(progress, step, index, steps.Count, PostConnectStepStatus.Broken);
                        stepsBroken++;
                        continue;
                    }

                    var resolveResult = await resolver.ResolveAsync(step, ct).ConfigureAwait(false);
                    switch (resolveResult.Status)
                    {
                        case PostConnectResolveStatus.Resolved:
                            inputToSend = resolveResult.ResolvedInput ?? string.Empty;
                            break;
                        case PostConnectResolveStatus.Literal:
                            FileLogger.Warn($"Post-connect: resolver returned Literal for linked step {index}.");
                            Report(progress, step, index, steps.Count, PostConnectStepStatus.Broken);
                            stepsBroken++;
                            continue;
                        default:
                            FileLogger.Warn($"Post-connect: linked step {index} broken ({resolveResult.ReasonKey}).");
                            Report(progress, step, index, steps.Count, PostConnectStepStatus.Broken);
                            stepsBroken++;
                            continue;
                    }
                }

                if (step.DelayMs > 0)
                {
                    await Task.Delay(step.DelayMs, ct).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
                writeCallback(inputToSend);
                Report(progress, step, index, steps.Count, PostConnectStepStatus.Completed);
                stepsExecuted++;
            }
            catch (OperationCanceledException)
            {
                Report(progress, step, index, steps.Count, PostConnectStepStatus.Cancelled);
                return Finish(stepsExecuted, stepsSkippedDisabled, stepsFailed, stepsBroken, wasCancelled: true, wasStoppedByFailurePolicy: false);
            }
            catch (Exception ex)
            {
                // Log and honor the per-step failure policy so later hooks still run when allowed.
                FileLogger.Error(
                    $"Post-connect: step '{Summarize(GetDisplayText(step))}' failed at {index + 1}/{steps.Count} ({ex.GetType().Name})",
                    ex);
                Report(progress, step, index, steps.Count, PostConnectStepStatus.Failed);
                stepsFailed++;
                if (step.OnFailure == PostConnectFailurePolicy.Stop)
                {
                    return Finish(stepsExecuted, stepsSkippedDisabled, stepsFailed, stepsBroken, wasCancelled: false, wasStoppedByFailurePolicy: true);
                }
            }
        }

        return Finish(stepsExecuted, stepsSkippedDisabled, stepsFailed, stepsBroken, wasCancelled: false, wasStoppedByFailurePolicy: false);
    }

    private static PostConnectRunResult Finish(
        int stepsExecuted,
        int stepsSkippedDisabled,
        int stepsFailed,
        int stepsBroken,
        bool wasCancelled,
        bool wasStoppedByFailurePolicy)
    {
        FileLogger.Info(
            "Post-connect: completed "
            + $"executed={stepsExecuted}, "
            + $"skipped={stepsSkippedDisabled}, "
            + $"failed={stepsFailed}, "
            + $"broken={stepsBroken}, "
            + $"cancelled={wasCancelled}, "
            + $"stopped={wasStoppedByFailurePolicy}.");
        return new PostConnectRunResult
        {
            StepsExecuted = stepsExecuted,
            StepsSkippedDisabled = stepsSkippedDisabled,
            StepsFailed = stepsFailed,
            StepsBroken = stepsBroken,
            WasCancelled = wasCancelled,
            WasStoppedByFailurePolicy = wasStoppedByFailurePolicy
        };
    }

    private static void Report(
        IProgress<PostConnectRunProgress>? progress,
        PostConnectStep step,
        int index,
        int totalSteps,
        PostConnectStepStatus status)
    {
        progress?.Report(new PostConnectRunProgress
        {
            CurrentStepIndex = index,
            TotalSteps = totalSteps,
            CurrentStepDisplayText = Summarize(GetDisplayText(step)),
            Status = status
        });
    }

    private static string GetDisplayText(PostConnectStep step)
    {
        if (!string.IsNullOrWhiteSpace(step.CommandLibraryId))
        {
            return step.CommandLibraryId;
        }

        if (!string.IsNullOrWhiteSpace(step.Input))
        {
            return step.Input;
        }

        return string.Empty;
    }

    private static string Summarize(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length <= 80)
        {
            return trimmed;
        }

        return trimmed[..77] + "...";
    }
}
