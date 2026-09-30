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

using System.ComponentModel;
using System.Diagnostics;
using Heimdall.Core.Logging;
using Heimdall.Ssh.Plink;

namespace Heimdall.App.Services;

/// <summary>
/// Removes temporary Plink size sessions left in the PuTTY registry by a crash.
/// </summary>
/// <remarks>
/// <para>Runs once, synchronously, when the SSH handler is built and before any launch. Only names
/// carrying <see cref="PlinkSizeSessionNaming.Prefix"/> are touched; every other saved session
/// belongs to the user and to PuTTY.</para>
/// <para>The PuTTY sessions hive is per user, not per Heimdall: a portable and an installed copy
/// running side by side share it, and so do two SSH handlers in one process. A size session is
/// therefore removed only when the process that created it is gone. Its name carries the owner's
/// process id and start time (<see cref="PlinkSizeSessionNaming.CreateName(PlinkSizeSessionOwner)"/>);
/// the start time is what keeps a reused process id from passing for the owner. An owner whose
/// start time cannot be read (another user's elevated process holding a reused id) counts as
/// alive: the key then waits for a later sweep rather than risk a live launch.</para>
/// <para>No age bound on top: an unattested Plink keeps its key until it exits, which can be the
/// whole length of a working day, so any age short enough to matter would delete live keys.
/// Owner-less names, written by the release that introduced these sessions, cannot be attributed
/// and are removed as before.</para>
/// <para>A leftover carries a copy of "Default Settings" and a size, nothing Heimdall-specific and
/// no host, so it is not launchable. It is removed for tidiness and so the PuTTY sessions list does
/// not grow, not because it exposes anything new.</para>
/// </remarks>
internal sealed class PlinkSizeSessionJanitor
{
    /// <summary>
    /// Difference tolerated between a recorded and a re-read process start time. Both come from
    /// the same kernel value; the slack only absorbs conversion rounding.
    /// </summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    private readonly IPuttySessionRegistry _registry;
    private readonly Action<string> _warn;
    private readonly Func<PlinkSizeSessionOwner, bool> _isOwnerAlive;

    public PlinkSizeSessionJanitor(
        IPuttySessionRegistry registry,
        Action<string>? warn = null,
        Func<PlinkSizeSessionOwner, bool>? isOwnerAlive = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _warn = warn ?? FileLogger.Warn;
        _isOwnerAlive = isOwnerAlive ?? IsProcessAlive;
    }

    /// <summary>
    /// Deletes every leftover size session whose owner has gone and returns how many were removed.
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

            if (PlinkSizeSessionNaming.TryParseOwner(name, out PlinkSizeSessionOwner owner)
                && _isOwnerAlive(owner))
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

    /// <summary>
    /// Whether the process named by <paramref name="owner"/> is still the one that created the key.
    /// </summary>
    internal static bool IsProcessAlive(PlinkSizeSessionOwner owner)
    {
        try
        {
            using Process process = Process.GetProcessById(owner.ProcessId);
            long startTicks = process.StartTime.ToUniversalTime().Ticks;
            return Math.Abs(startTicks - owner.StartTimeUtcTicks) <= StartTimeTolerance.Ticks;
        }
        catch (ArgumentException)
        {
            // No process with that id.
            return false;
        }
        catch (InvalidOperationException)
        {
            // The process exited while it was being read.
            return false;
        }
        catch (Win32Exception)
        {
            // Exists, but its start time cannot be read: keep the key.
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }
}
