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

using System.Runtime.CompilerServices;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Ephemeral per-connection trust decision carried from <see cref="ConnectionService"/> to
/// <see cref="SshHandler"/> so an SSH startup command / <c>SetEnv</c> approved at connect time
/// executes immediately on the first connection without mutating <see cref="ServerProfileDto"/>'s
/// serialized schema.
/// </summary>
internal static class SshStartupTrustGate
{
    internal sealed record Decision(bool Approved);

    private static readonly ConditionalWeakTable<ServerProfileDto, Decision> Decisions = new();

    public static void Set(ServerProfileDto profile, bool approved)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Decisions.AddOrUpdate(profile, new Decision(approved));
    }

    public static Decision? Take(ServerProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Decisions.TryGetValue(profile, out Decision? decision))
        {
            Decisions.Remove(profile);
            return decision;
        }

        return null;
    }
}
