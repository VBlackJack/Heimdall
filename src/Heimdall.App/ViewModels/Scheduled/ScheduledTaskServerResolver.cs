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

namespace Heimdall.App.ViewModels.Scheduled;

/// <summary>
/// Which saved profile a scheduled task runs against.
/// </summary>
/// <remarks>
/// <para>Its own type so the rule is measured by running it. Inside
/// <c>ScheduledTasksViewModel.OnTaskDueAsync</c> it sat behind a live main view-model, a session
/// coordinator and a timer, and a rule reachable only through all of that ends up asserted by
/// reading source text instead - or not at all, which is what happened.</para>
/// <para><b>The identifier first, and the name only when it answers unambiguously.</b> The
/// fallback used to take the FIRST profile with a matching display name whenever the identifier
/// failed to resolve. Display names are not unique - two profiles are routinely both called
/// "Production" - so a task whose profile had been deleted, replaced by an import or given a new
/// identifier opened a session on a DIFFERENT machine, unattended, on a schedule, with nothing on
/// screen to notice it.</para>
/// <para><b>Refusing the fallback outright is the wrong correction, and this is the second
/// version.</b> A dangling identifier with exactly ONE profile of that name is the ordinary case -
/// a profile deleted and re-created, or re-identified by a migration - and there the name answers
/// with no ambiguity at all. Refusing it turns a task that was working into a silent no-op: the
/// scheduler stamps LastRun before it calls this, so the grid shows the task ran while nothing
/// connected. Ambiguity is what makes the fallback dangerous, not the fallback.</para>
/// <para>So: the identifier when it resolves; otherwise the name when exactly one profile carries
/// it; otherwise nothing. A task naming no identifier at all - what an older file holds - is
/// answered by the same unambiguous-name rule.</para>
/// </remarks>
internal static class ScheduledTaskServerResolver
{
    /// <summary>The profile to run, or null when the task cannot be answered safely.</summary>
    /// <param name="taskServerId">The identifier the task recorded, if any.</param>
    /// <param name="taskServerName">The display name the task recorded.</param>
    /// <param name="servers">The inventory as the application currently holds it.</param>
    public static TServer? Resolve<TServer>(
        string? taskServerId,
        string? taskServerName,
        IEnumerable<TServer> servers,
        Func<TServer, string?> identifierOf,
        Func<TServer, string?> displayNameOf)
        where TServer : class
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(identifierOf);
        ArgumentNullException.ThrowIfNull(displayNameOf);

        if (!string.IsNullOrEmpty(taskServerId))
        {
            TServer? byIdentifier = servers.FirstOrDefault(
                server => string.Equals(identifierOf(server), taskServerId, StringComparison.Ordinal));

            if (byIdentifier is not null)
            {
                return byIdentifier;
            }
        }

        // Single, or nothing. Two profiles of one name cannot say which the task meant, and
        // guessing there is what connected to the wrong machine.
        List<TServer> byName = [.. servers.Where(
            server => string.Equals(
                displayNameOf(server),
                taskServerName,
                StringComparison.OrdinalIgnoreCase))];

        return byName.Count == 1 ? byName[0] : null;
    }
}
