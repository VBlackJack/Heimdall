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
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Services;

/// <summary>
/// Removes temporary Plink size sessions left in the PuTTY registry by a crash.
/// </summary>
/// <remarks>
/// <para>Runs once, synchronously, when the SSH handler is built and before any launch, so it can
/// never race a size session this process has just created. Only names carrying
/// <see cref="PlinkSizeSessionNaming.Prefix"/> are touched; every other saved session belongs to
/// the user and to PuTTY.</para>
/// <para>A leftover carries a copy of "Default Settings" and a size, nothing Heimdall-specific and
/// no host, so it is not launchable. It is removed for tidiness and so the PuTTY sessions list does
/// not grow, not because it exposes anything new.</para>
/// </remarks>
internal sealed class PlinkSizeSessionJanitor
{
    private readonly IPuttySessionRegistry _registry;
    private readonly Action<string> _warn;

    public PlinkSizeSessionJanitor(IPuttySessionRegistry registry, Action<string>? warn = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _warn = warn ?? FileLogger.Warn;
    }

    /// <summary>
    /// Deletes every leftover size session and returns how many were removed.
    /// </summary>
    public int SweepLeftovers()
    {
        IReadOnlyList<string> names;
        try
        {
            names = _registry.GetSessionNames();
        }
        catch (Exception ex)
        {
            _warn($"[{nameof(PlinkSizeSessionJanitor)}] Enumerate failed: {ex.Message}");
            return 0;
        }

        int removed = 0;
        foreach (string name in names)
        {
            if (!PlinkSizeSessionNaming.IsHeimdallSession(name))
            {
                continue;
            }

            try
            {
                _registry.DeleteSession(name);
                removed++;
            }
            catch (Exception ex)
            {
                _warn($"[{nameof(PlinkSizeSessionJanitor)}] Delete failed for {name}: {ex.Message}");
            }
        }

        if (removed > 0)
        {
            FileLogger.Info($"[{nameof(PlinkSizeSessionJanitor)}] Swept {removed} leftover Plink size session(s).");
        }

        return removed;
    }
}
