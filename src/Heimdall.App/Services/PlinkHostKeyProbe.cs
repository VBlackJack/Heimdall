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
            var probeParts = BuildProbeArguments(port, probeTarget);
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

            // Nothing is ever typed into the probe: closing the input at once means a session that
            // did open would find its stream ended instead of waiting on us.
            process.StandardInput.Close();

            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            string stderr;
            bool exitedByItself = true;
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
                exitedByItself = false;
            }

            PlinkHostKeyPresentation? presentation = TryParseRejectedPresentation(stderr, exitedByItself);
            if (presentation is null)
            {
                // The transcript is not logged: it is the server's text, and nothing in it is needed
                // to know that the probe saw no rejected key.
                Core.Logging.FileLogger.Warn(
                    $"Host key probe read no rejected host key ({stderr.Length} chars, exited by itself: {exitedByItself}).");
                if (HasUnexpectedFatalError(stderr))
                {
                    Core.Logging.FileLogger.Warn(
                        "Host key probe ended with a fatal error: the plink in use may be too old for a SHA256 -hostkey.");
                }
            }

            return presentation;
        }
        catch (Exception ex)
        {
            Core.Logging.FileLogger.Warn($"[PlinkHostKeyProbe] probe failed: {ex.Message}");
        }

        return null;
    }

    /// <summary>The line plink prints before the key it was shown.</summary>
    private const string FingerprintHeaderLine = "Host key fingerprint is:";

    /// <summary>The line plink prints when it refuses a key that is not the manually configured one.</summary>
    private const string RejectionLine = "Host key not in manually configured list";

    private const string FatalErrorPrefix = "FATAL ERROR";

    private const string FatalRejectionLine = "FATAL ERROR: Host key not in manually configured list";

    /// <summary>A GSSAPI key exchange sends no host key, so the manual key is never checked.</summary>
    private const string GssapiMarker = "GSSAPI";

    private const string KeyExchangeMarker = "key exchange";

    /// <summary>The line plink prints when no GSSAPI credentials exist; it says nothing about the exchange.</summary>
    private const string BenignGssapiLine = "No GSSAPI security context available";

    private const string UnsatisfiableFingerprintPrefix = "SHA256:";

    /// <summary>The length of an unpadded base64 SHA-256 digest.</summary>
    private const int FingerprintBase64Length = 43;

    private const char UnsatisfiableFingerprintFill = 'A';

    /// <summary>
    /// A well-formed fingerprint no server key can have. Given as the manual host key, it makes
    /// plink ignore the registry cache, print the key it is shown and refuse it before any user
    /// authentication.
    /// </summary>
    internal static readonly string UnsatisfiableFingerprint =
        UnsatisfiableFingerprintPrefix + new string(UnsatisfiableFingerprintFill, FingerprintBase64Length);

    /// <summary>Output that shows the connection went on past the key exchange.</summary>
    private static readonly string[] ProgressMarkers =
    [
        "Pageant",
        "Using username",
        "Access granted",
        "Authenticating",
        "Started a shell",
        "Opening",
    ];

    private static readonly Regex KeyLinePattern = new(
        @"^(?<type>[A-Za-z0-9@.-]+) \d+ (?<fingerprint>SHA256:[A-Za-z0-9+/]{43})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The plink arguments of the probe: verbose, no prompt, no agent, no connection sharing, no
    /// shell, and an unsatisfiable manual host key so the connection ends at the key check. Only the
    /// port and the target are added.
    /// </summary>
    /// <remarks>
    /// Residual risk: plink has no option to turn GSSAPI off. A GSSAPI key exchange sends no host
    /// key, so the manual key is never checked, and on a domain-joined machine talking to a server
    /// that enables it plink can still authenticate with Kerberos during the probe. The parser
    /// returns nothing for such a transcript, but closing the gap needs a dedicated probe session,
    /// which is not done here.
    /// </remarks>
    internal static string[] BuildProbeArguments(int port, string probeTarget) =>
    [
        "-v",
        "-batch",
        "-noagent",
        "-noshare",
        "-N",
        "-ssh",
        "-P",
        port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-hostkey",
        UnsatisfiableFingerprint,
        probeTarget,
    ];

    /// <summary>
    /// Reads the key a probe was shown and refused. A key is accepted only when the line right after
    /// the fingerprint header is a type, a size and a SHA256 fingerprint, the rejection line follows,
    /// nothing shows the connection going further, and the process ended by itself.
    /// </summary>
    internal static PlinkHostKeyPresentation? TryParseRejectedPresentation(string? stderr, bool exitedByItself)
    {
        if (!exitedByItself || string.IsNullOrWhiteSpace(stderr))
        {
            return null;
        }

        string[] lines = SplitLines(stderr);
        if (lines.Any(IsProgressLine))
        {
            return null;
        }

        // Exactly one header, the key right after it, the rejection right after the key, and
        // plink's own fatal error as the last line: a server-controlled message that imitates the
        // block earlier in the output leaves a second header or a different last line.
        int header = Array.IndexOf(lines, FingerprintHeaderLine);
        if (header < 0
            || Array.IndexOf(lines, FingerprintHeaderLine, header + 1) >= 0
            || header + 2 >= lines.Length
            || !string.Equals(lines[header + 2], RejectionLine, StringComparison.Ordinal)
            || !string.Equals(lines[^1], FatalRejectionLine, StringComparison.Ordinal))
        {
            return null;
        }

        Match key = KeyLinePattern.Match(lines[header + 1]);
        if (!key.Success)
        {
            return null;
        }

        return new PlinkHostKeyPresentation(key.Groups["type"].Value, key.Groups["fingerprint"].Value);
    }

    /// <summary>The lines of a transcript without line terminators or trailing blank lines.</summary>
    private static string[] SplitLines(string text)
    {
        string[] lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        int last = lines.Length;
        while (last > 0 && string.IsNullOrWhiteSpace(lines[last - 1]))
        {
            last--;
        }

        return lines[..last];
    }

    private static bool IsProgressLine(string line) =>
        ProgressMarkers.Any(marker => line.Contains(marker, StringComparison.Ordinal))
        || (line.Contains(GssapiMarker, StringComparison.OrdinalIgnoreCase)
            && line.Contains(KeyExchangeMarker, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(line, BenignGssapiLine, StringComparison.Ordinal));

    /// <summary>
    /// Whether the transcript holds a fatal error other than the expected key rejection, which
    /// points at a plink that does not understand the probe's arguments.
    /// </summary>
    private static bool HasUnexpectedFatalError(string stderr) =>
        SplitLines(stderr).Any(line =>
            line.StartsWith(FatalErrorPrefix, StringComparison.Ordinal)
            && !string.Equals(line, FatalRejectionLine, StringComparison.Ordinal));

    /// <summary>
    /// Algorithm reported when plink printed a fingerprint but no recognisable key type.
    /// </summary>
    internal const string UnknownAlgorithm = "ssh-unknown";

    /// <summary>
    /// Extracts the presented host key algorithm and SHA256 fingerprint from the
    /// verbose stderr of a plink probe. Returns null when no fingerprint is present.
    /// </summary>
    /// <remarks>
    /// Production no longer calls this: its loose reading takes the first fingerprint found anywhere,
    /// including a server's version string, and cannot tell a refused key from an accepted one.
    /// <see cref="TryParseRejectedPresentation"/> replaces it. It is kept, with its tests, as the
    /// record of the format plink prints.
    /// </remarks>
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
