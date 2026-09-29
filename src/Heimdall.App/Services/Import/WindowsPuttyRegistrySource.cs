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

using Heimdall.Core.Ssh;
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Services.Import;

/// <summary>
/// Reads PuTTY sessions from HKCU in strict read-only mode.
/// </summary>
/// <remarks>
/// Heimdall's own temporary Plink size sessions (<see cref="PlinkSizeSessionNaming.Prefix"/>) are
/// skipped: one left behind by a crash is not a session the user saved and must never be offered
/// for import.
/// </remarks>
public sealed class WindowsPuttyRegistrySource : IPuttySessionRegistrySource
{
    private readonly IPuttySessionRegistry _registry;

    public WindowsPuttyRegistrySource(IPuttySessionRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public Task<IReadOnlyList<RawPuttySession>> ReadSessionsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sessions = new List<RawPuttySession>();
        foreach (var subKeyName in _registry.GetSessionNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            if (PlinkSizeSessionNaming.IsHeimdallSession(subKeyName))
            {
                continue;
            }

            var sessionValues = _registry.ReadSession(subKeyName);
            if (sessionValues is null)
            {
                continue;
            }

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in sessionValues)
            {
                values[value.Name] = value.Value;
            }

            sessions.Add(new RawPuttySession(subKeyName, values));
        }

        return Task.FromResult<IReadOnlyList<RawPuttySession>>(sessions);
    }
}
