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
using System.Text.RegularExpressions;
using Heimdall.Core.Security;

namespace Heimdall.App.Services;

/// <summary>
/// Parsed host key presentation extracted from a <c>plink -batch -v</c> probe.
/// </summary>
internal sealed record PlinkHostKeyPresentation(string Algorithm, string Fingerprint);

/// <summary>
/// Shared plink host key probing helper used by embedded SSH and tunnel fallbacks.
/// </summary>
internal static class PlinkHostKeyProbe
{
    /// <summary>
    /// Probe the target host and extract the presented host key algorithm and
    /// SHA256 fingerprint from plink verbose output.
    /// Returns null when the key is already cached or the fingerprint cannot be parsed.
    /// </summary>
    internal static async Task<PlinkHostKeyPresentation?> ProbeAsync(
        string plinkPath,
        string host,
        int port,
        string? username,
        int timeoutMs,
        CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(username) &&
                !InputValidator.Validate(username, "SshUser"))
            {
                return null;
            }

            if (!InputValidator.IsValidSshHost(host))
            {
                return null;
            }

            if (!InputValidator.ValidatePortRange(port))
            {
                return null;
            }

            var userPrefix = string.IsNullOrWhiteSpace(username) ? string.Empty : $"{username}@";
            var probeTarget = $"{userPrefix}{host}";
            var probeParts = new[] { "-v", "-batch", "-ssh", "-P", port.ToString(), probeTarget };
            var probeArgs = string.Join(' ', probeParts);

            Core.Logging.FileLogger.Info($"Host key probe: {plinkPath} {probeArgs}");

            var psi = new ProcessStartInfo
            {
                FileName = plinkPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };

            foreach (var part in probeParts)
            {
                psi.ArgumentList.Add(part);
            }

            using var process = new Process { StartInfo = psi };
            process.Start();

            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            string stderr;
            try
            {
                stderr = await process.StandardError.ReadToEndAsync(linked.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch (Exception ex)
                {
                    Core.Logging.FileLogger.Warn($"[PlinkHostKeyProbe] probe kill: {ex.Message}");
                }

                stderr = string.Empty;
            }

            Core.Logging.FileLogger.Info(
                $"Host key probe stderr ({stderr.Length} chars): {stderr.Trim().Replace('\n', ' ')}");

            return TryParsePresentation(stderr);
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[PlinkHostKeyProbe] probe failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>
    /// Algorithm reported when plink printed a fingerprint but no recognisable key type.
    /// </summary>
    internal const string UnknownAlgorithm = "ssh-unknown";

    /// <summary>
    /// Extracts the presented host key algorithm and SHA256 fingerprint from the
    /// verbose stderr of a plink probe. Returns null when no fingerprint is present.
    /// </summary>
    internal static PlinkHostKeyPresentation? TryParsePresentation(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return null;
        }

        // Key type names plink prints: ssh-ed25519, ssh-rsa, ecdsa-sha2-nistpNNN and the
        // security-key variants sk-ssh-ed25519@openssh.com / sk-ecdsa-sha2-nistp256@openssh.com.
        // Anchored on a word boundary so "sk-ssh-..." is not read from its "ssh-" suffix.
        Match fullMatch = Regex.Match(
            stderr,
            @"(?<![\w@.-])((?:ssh|ecdsa|sk)-[\w@.-]+)\s+\d+\s+(SHA256:\S+)",
            RegexOptions.Multiline);

        if (fullMatch.Success)
        {
            string algorithm = fullMatch.Groups[1].Value;
            string fingerprint = fullMatch.Groups[2].Value;
            Core.Logging.FileLogger.Info(
                $"Extracted host key presentation: algorithm={algorithm} fingerprint={fingerprint}");
            return new PlinkHostKeyPresentation(algorithm, fingerprint);
        }

        Match sha256Match = Regex.Match(stderr, @"SHA256:(\S+)");

        if (sha256Match.Success)
        {
            string fingerprint = $"SHA256:{sha256Match.Groups[1].Value}";
            Core.Logging.FileLogger.Info(
                $"Extracted host key fingerprint (fallback): {fingerprint}");
            return new PlinkHostKeyPresentation(UnknownAlgorithm, fingerprint);
        }

        return null;
    }
}
