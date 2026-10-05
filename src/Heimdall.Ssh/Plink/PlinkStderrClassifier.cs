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

namespace Heimdall.Ssh.Plink;

/// <summary>
/// Names the failure behind a plink.exe that exited before it opened the tunnel, from what it
/// wrote to stderr.
/// </summary>
/// <remarks>
/// A plink that refused the sign-in or could not reach the gateway used to be waited on for the
/// whole port check, about half a minute, and then reported as a port that some other process
/// owned. Its own words name the cause; these are the sentences PuTTY's plink prints for the
/// failures a tunnel meets. Anything else stays <see cref="SshFailureCode.Unknown"/>, and the
/// caller still shows plink's last line.
/// </remarks>
internal static class PlinkStderrClassifier
{
    private static readonly (string Fragment, SshFailureCode Code)[] Rules =
    [
        ("WARNING - POTENTIAL SECURITY BREACH", SshFailureCode.HostKeyMismatch),
        ("host key does not match", SshFailureCode.HostKeyMismatch),
        ("Host key did not appear in manually configured list", SshFailureCode.HostKeyMismatch),
        ("Wrong passphrase", SshFailureCode.PassphraseRejected),
        ("Unable to use key file", SshFailureCode.KeyFileInvalid),
        ("Server refused our key", SshFailureCode.KeyRejected),

        // A refused password prints "Access denied" and then, as plink gives up, "No supported
        // authentication methods available". Only without the first is it the server offering
        // nothing plink can use.
        ("Access denied", SshFailureCode.AuthRejected),
        ("No supported authentication methods", SshFailureCode.NoSupportedAuth),
        ("Connection refused", SshFailureCode.NetworkRefused),
        ("timed out", SshFailureCode.NetworkTimedOut),
        ("Connection reset", SshFailureCode.NetworkReset),
        ("Host does not exist", SshFailureCode.NetworkUnreachable),
        ("Network is unreachable", SshFailureCode.NetworkUnreachable),
        ("No route to host", SshFailureCode.NetworkUnreachable),
    ];

    /// <summary>
    /// Returns the failure the lines name, the most specific first, or
    /// <see cref="SshFailureCode.Unknown"/> when none does.
    /// </summary>
    public static SshFailureCode Classify(IEnumerable<string> stderrLines)
    {
        ArgumentNullException.ThrowIfNull(stderrLines);

        List<string> lines = stderrLines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        foreach ((string fragment, SshFailureCode code) in Rules)
        {
            if (lines.Any(line => line.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                return code;
            }
        }

        return SshFailureCode.Unknown;
    }
}
