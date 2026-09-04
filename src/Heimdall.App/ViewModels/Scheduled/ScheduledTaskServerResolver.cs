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
/// <para><b>A task that names an identifier is answered by that identifier or not at all.</b> The
/// fallback used to take the FIRST profile with a matching display name whenever the identifier
/// failed to resolve. Display names are not unique - two profiles are routinely both called
/// "Production" - so a task whose profile had been deleted opened a session on a DIFFERENT
/// machine, unattended, on a schedule.</para>
/// <para><b>Requiring the name to be unique does not fix that, which is the correction this
/// version makes.</b> Delete the profile a task names and the remaining same-named profile becomes
/// the unique match - and it is a different machine. "Deleted and re-created under a new
/// identifier" and "deleted, and an unrelated profile happens to share the name" produce the same
/// inventory; nothing recorded anywhere can separate them. Uniqueness is a property of the list,
/// not evidence about the destination.</para>
/// <para><b>The cost of refusing is real and is the lesser one.</b> A task whose profile was
/// deleted and re-created stops running, and the scheduler stamps its last-run time before calling
/// this, so the grid says it ran. That is bad and it is discoverable - the log names the task and
/// the identifier it could not find. Connecting to a machine nobody chose is neither.</para>
/// <para>A task naming no identifier at all - what an older file holds - is still answered by
/// display name, and still only when exactly one profile carries it. There the name is the only
/// thing recorded, so refusing it would break every such task rather than protect anything.</para>
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
            // That identifier or nothing. No name is consulted here: a name that has become
            // unique because the task's own profile was deleted names a different machine.
            return servers.FirstOrDefault(
                server => string.Equals(identifierOf(server), taskServerId, StringComparison.Ordinal));
        }

        // Only for a task that recorded no identifier, and only when the name is unambiguous.
        List<TServer> byName = [.. servers.Where(
            server => string.Equals(
                displayNameOf(server),
                taskServerName,
                StringComparison.OrdinalIgnoreCase))];

        return byName.Count == 1 ? byName[0] : null;
    }
}
