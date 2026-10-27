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

namespace Heimdall.App.Services;

/// <summary>
/// The process facts the Citrix window scan reads, behind an interface so the filtering can be
/// exercised without Citrix processes or other users' sessions.
/// </summary>
internal interface ICitrixScanProcess : IDisposable
{
    int Id { get; }

    /// <summary>The Windows session the process runs in.</summary>
    int SessionId { get; }

    bool HasExited { get; }
}

/// <summary><see cref="Process"/> behind <see cref="ICitrixScanProcess"/>; disposing it disposes the process.</summary>
internal sealed class CitrixScanProcess : ICitrixScanProcess
{
    private readonly Process _process;

    internal CitrixScanProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
    }

    public int Id => _process.Id;

    public int SessionId => _process.SessionId;

    public bool HasExited => _process.HasExited;

    public void Dispose() => _process.Dispose();
}

/// <summary>
/// Collects the live Citrix process ids of the current Windows session.
/// </summary>
/// <remarks>
/// On a multi-user host (RDS, Citrix VDA) <see cref="Process.GetProcessesByName(string)"/> returns the
/// Citrix processes of every session, and reading <see cref="Process.HasExited"/> on another user's
/// process throws <see cref="Win32Exception"/> (access denied). The scan therefore keeps only the
/// processes of the caller's own session, treats a process whose state cannot be read as not usable
/// instead of failing the whole scan, and disposes every process it was handed.
/// </remarks>
internal static class CitrixProcessScan
{
    /// <summary>The live process ids of <paramref name="names"/> in the current Windows session.</summary>
    internal static HashSet<int> LivePidsInCurrentSession(IEnumerable<string> names)
    {
        using Process self = Process.GetCurrentProcess();
        return LivePids(names, GetProcessesByName, self.SessionId);
    }

    /// <summary>
    /// The ids of the processes named in <paramref name="names"/> that run in
    /// <paramref name="currentSessionId"/> and have not exited.
    /// </summary>
    /// <param name="names">The process names to look up.</param>
    /// <param name="getByName">Opens the processes of one name; the scan disposes each of them.</param>
    /// <param name="currentSessionId">The Windows session to keep.</param>
    internal static HashSet<int> LivePids(
        IEnumerable<string> names,
        Func<string, IEnumerable<ICitrixScanProcess>> getByName,
        int currentSessionId)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(getByName);

        HashSet<int> pids = [];
        foreach (string name in names)
        {
            IEnumerable<ICitrixScanProcess> processes;
            try
            {
                processes = getByName(name);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                Core.Logging.FileLogger.Warn($"Citrix: process lookup for '{name}' failed: {ex.Message}");
                continue;
            }

            foreach (ICitrixScanProcess process in processes)
            {
                using (process)
                {
                    if (IsLiveInSession(process, currentSessionId))
                    {
                        pids.Add(process.Id);
                    }
                }
            }
        }

        return pids;
    }

    private static bool IsLiveInSession(ICitrixScanProcess process, int currentSessionId)
    {
        try
        {
            if (process.SessionId != currentSessionId)
            {
                return false;
            }

            return !process.HasExited;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // Access denied, or the process went away between the lookup and the read.
            return false;
        }
    }

    private static IEnumerable<ICitrixScanProcess> GetProcessesByName(string name)
    {
        // Materialised eagerly so every Process is wrapped, and so disposed by the scan.
        return Process.GetProcessesByName(name).Select(p => (ICitrixScanProcess)new CitrixScanProcess(p)).ToList();
    }
}
