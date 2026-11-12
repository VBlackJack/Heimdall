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

using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>A pane's hosted content and the title the user knows it by.</summary>
/// <param name="Control">The hosted content; may be a close guard, a tool view, both or neither.</param>
/// <param name="Title">The pane title, the single placeholder of the close-guard messages.</param>
public readonly record struct ExitCloseHost(object? Control, string Title);

/// <summary>
/// Asks every guarded pane whether the application may exit, the way closing its tab would.
/// </summary>
/// <remarks>
/// <para>Closing the main window used to tear every session down silently, dropping unsaved editor
/// buffers, hosts editor edits, dirty remote edits and transfers in flight without a question.
/// This sweep runs the shared close protocol once for the whole window: one interactive request,
/// each host polled, the guards that deferred resolved, and the poll retried exactly once.</para>
/// <para>A tool that refuses with <see cref="ToolCloseRefusal.Busy"/> is not an obstacle: exit has
/// always stopped running tools and keeps doing so. A declined prompt ends the sweep in silence,
/// the user having just answered it; a failed save is reported.</para>
/// </remarks>
internal static class ExitCloseGuardSweep
{
    /// <summary>
    /// Whether the application may exit.
    /// </summary>
    /// <param name="hosts">Every leaf host of every open session, detached windows included.</param>
    /// <param name="arbiter">The shared arbiter, so grants and in-flight prompts are the tabs' own.</param>
    /// <param name="report">Says a refusal: the locale key of the reason and the pane title it takes.</param>
    internal static async Task<bool> ConfirmAsync(
        IReadOnlyList<ExitCloseHost> hosts,
        IPaneCloseArbiter arbiter,
        Action<string, string> report)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(report);

        CloseRequest request = CloseRequest.Interactive(DisconnectReason.UserAction);
        try
        {
            foreach (ExitCloseHost host in hosts)
            {
                if (!await ConfirmGuardAsync(host, request, arbiter, report).ConfigureAwait(true))
                {
                    return false;
                }
            }

            foreach (ExitCloseHost host in hosts)
            {
                // A tool whose unsaved work comes back at the next start is not asked: its prompt
                // could only hold the exit for work that is not lost.
                if (host.Control is not IToolView tool
                    || host.Control is IRecoverableUnsavedWork
                    || tool.CanClose())
                {
                    continue;
                }

                switch (tool.CloseRefusal)
                {
                    case ToolCloseRefusal.Busy:
                        // Running tools are stopped by an exit; only a prompt can hold it.
                        break;

                    case ToolCloseRefusal.SaveFailed:
                        report(CloseGuardLocaleKeys.BlockedSaveFailed, host.Title);
                        return false;

                    default:
                        return false;
                }
            }

            return true;
        }
        finally
        {
            arbiter.Release(request);
        }
    }

    /// <summary>Poll, resolve what deferred, retry exactly once, for one host.</summary>
    private static async Task<bool> ConfirmGuardAsync(
        ExitCloseHost host,
        CloseRequest request,
        IPaneCloseArbiter arbiter,
        Action<string, string> report)
    {
        IReadOnlyList<object?> single = [host.Control];

        CloseDecision decision = arbiter.Poll(request, single);
        if (decision.Verdict == CloseVerdict.Allow)
        {
            return true;
        }

        if (decision.Verdict == CloseVerdict.Deny)
        {
            report(decision.ReasonKey ?? CloseGuardLocaleKeys.BlockedGeneric, host.Title);
            return false;
        }

        if (!await arbiter.ResolveAsync(request, single).ConfigureAwait(true))
        {
            // The guard asked its own question and the user answered it: a box repeating the
            // question would only contradict the answer. Only a guard that refuses without having
            // asked (a deny, a second deferral) is reported.
            return false;
        }

        CloseDecision retry = arbiter.Poll(request, single);
        if (retry.Verdict == CloseVerdict.Allow)
        {
            return true;
        }

        report(retry.ReasonKey ?? CloseGuardLocaleKeys.BlockedGeneric, host.Title);
        return false;
    }
}
