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

using System.IO;
using Heimdall.App.Services;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services.WinRm;

internal sealed class WinRmBootstrapJanitor
{
    internal const int DefaultMaxAgeMinutes = 60;

    private readonly Func<string> _tempDirectory;
    private readonly Func<string, IEnumerable<string>> _enumerateScripts;
    private readonly Func<string, DateTime> _getLastWriteTimeUtc;
    private readonly Action<string> _delete;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _maxAge;

    public WinRmBootstrapJanitor(
        Func<string>? tempDirectory = null,
        Func<string, IEnumerable<string>>? enumerateScripts = null,
        Func<string, DateTime>? getLastWriteTimeUtc = null,
        Action<string>? delete = null,
        Func<DateTime>? utcNow = null,
        TimeSpan? maxAge = null)
    {
        _tempDirectory = tempDirectory ?? Path.GetTempPath;
        _enumerateScripts = enumerateScripts ?? DefaultEnumerate;
        _getLastWriteTimeUtc = getLastWriteTimeUtc ?? File.GetLastWriteTimeUtc;
        _delete = delete ?? File.Delete;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _maxAge = maxAge ?? TimeSpan.FromMinutes(DefaultMaxAgeMinutes);
    }

    public SensitiveFileJanitorSweepResult SweepStale()
    {
        DateTime threshold = _utcNow() - _maxAge;
        string directory = _tempDirectory();

        IEnumerable<string> candidates;
        try
        {
            candidates = _enumerateScripts(directory);
        }
        catch (IOException ex)
        {
            FileLogger.Warn($"[WinRmBootstrapJanitor] Enumerate failed: {ex.Message}");
            return new SensitiveFileJanitorSweepResult(0, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            FileLogger.Warn($"[WinRmBootstrapJanitor] Enumerate unauthorized: {ex.Message}");
            return new SensitiveFileJanitorSweepResult(0, null);
        }

        int removed = 0;
        DateTime? nextEligibleUtc = null;
        try
        {
            foreach (string path in candidates)
            {
                try
                {
                    DateTime lastWriteTimeUtc = _getLastWriteTimeUtc(path);
                    if (lastWriteTimeUtc > threshold)
                    {
                        DateTime eligibleUtc = lastWriteTimeUtc + _maxAge;
                        if (nextEligibleUtc is null || eligibleUtc < nextEligibleUtc)
                        {
                            nextEligibleUtc = eligibleUtc;
                        }

                        continue;
                    }

                    _delete(path);
                    removed++;
                }
                catch (IOException ex)
                {
                    FileLogger.Warn($"[WinRmBootstrapJanitor] Delete failed: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    FileLogger.Warn($"[WinRmBootstrapJanitor] Delete unauthorized: {ex.Message}");
                }
            }
        }
        catch (IOException ex)
        {
            FileLogger.Warn($"[WinRmBootstrapJanitor] Enumerate failed: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            FileLogger.Warn($"[WinRmBootstrapJanitor] Enumerate unauthorized: {ex.Message}");
        }

        if (removed > 0)
        {
            FileLogger.Info($"[WinRmBootstrapJanitor] Swept {removed} stale bootstrap script(s).");
        }

        return new SensitiveFileJanitorSweepResult(removed, nextEligibleUtc);
    }

    private static IEnumerable<string> DefaultEnumerate(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, WinRmCredentialBootstrap.ScriptSearchPattern)
            : Array.Empty<string>();
}
