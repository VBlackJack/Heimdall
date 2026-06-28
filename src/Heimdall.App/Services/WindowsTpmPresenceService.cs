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

using System.Diagnostics;
using System.Runtime.Versioning;
using Heimdall.Core.Logging;

namespace Heimdall.App.Services;

/// <summary>
/// TPM 2.0 presence detector using the same no-admin tpmtool path proven by the
/// spike. This gates enrollment only; it does not attest a specific credential.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTpmPresenceService : ITpmPresenceService
{
    /// <inheritdoc />
    public async Task<bool> IsTpm2PresentAsync(CancellationToken ct = default)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "tpmtool",
                    Arguments = "getdeviceinformation",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);

            var normalized = output.ToLowerInvariant();
            var hasTpm2 = normalized.Contains("2.0", StringComparison.Ordinal);
            var reportsPresent = normalized.Contains("true", StringComparison.Ordinal) ||
                normalized.Contains("vrai", StringComparison.Ordinal);
            bool present = process.ExitCode == 0 && hasTpm2 && reportsPresent;
            FileLogger.Info($"WindowsTpmPresenceService: TPM 2.0 presence check returned {present}.");
            return present;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLogger.Warn($"WindowsTpmPresenceService: TPM presence check failed: {ex.Message}");
            return false;
        }
    }
}
