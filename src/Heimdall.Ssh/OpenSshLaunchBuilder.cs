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

using System.Globalization;
using System.Text;
using Heimdall.Core.Configuration;
using Heimdall.Core.Security;
using Heimdall.Core.Ssh;

namespace Heimdall.Ssh;

/// <summary>
/// Input parameters for constructing an argument vector for native Windows OpenSSH (<c>ssh.exe</c>).
/// </summary>
public sealed record OpenSshLaunchSpec
{
    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public string? Username { get; init; }

    public required string EphemeralKnownHostsPath { get; init; }

    /// <summary>
    /// Alias ssh looks the server up under in the ephemeral <c>known_hosts</c> file. It must match
    /// the alias that file was written with.
    /// </summary>
    public string HostKeyAlias { get; init; } = OpenSshEphemeralKnownHosts.HostKeyAlias;

    /// <summary>
    /// Key types (see <see cref="OpenSshHostKeyTypes"/>) of the keys written to the ephemeral
    /// file. When present, ssh is restricted to host key algorithms of those types so it never
    /// negotiates a key Heimdall did not decide. Empty leaves the algorithm choice to ssh: the
    /// file still holds only decided keys, so any other key fails the strict check.
    /// </summary>
    public IReadOnlyList<string> PinnedKeyTypes { get; init; } = [];

    /// <summary>
    /// True when the host presented an OpenSSH certificate: the matching certificate algorithms
    /// are offered after the plain ones.
    /// </summary>
    public bool HostCertificatePresented { get; init; }

    public string? KeyPath { get; init; }

    public string? CertificatePath { get; init; }

    public bool AgentForwarding { get; init; }

    public bool X11Forwarding { get; init; }

    public bool Compression { get; init; }

    public SshCryptoPolicy CryptoPolicy { get; init; } = SshCryptoPolicy.Standard;

    public IReadOnlyList<string> LocalForwards { get; init; } = [];

    public int SocksProxyPort { get; init; }

    public int RemoteBindPort { get; init; }

    public int RemoteLocalPort { get; init; }

    public bool AllowStartupExecution { get; init; }

    public string? StartupCommand { get; init; }

    public IReadOnlyList<string> EnvironmentVariables { get; init; } = [];

    public int ServerAliveIntervalSeconds { get; init; }
}

/// <summary>
/// Builds hardened argument lists and Win32 ConPTY command lines for native <c>ssh.exe</c>.
/// Never enables user config files and always separates target/command with <c>--</c>.
/// </summary>
public static class OpenSshLaunchBuilder
{
    public const string StrictModernKexAlgorithms =
        "mlkem768x25519-sha256,sntrup761x25519-sha512@openssh.com,curve25519-sha256,curve25519-sha256@libssh.org,ecdh-sha2-nistp521,ecdh-sha2-nistp384,ecdh-sha2-nistp256,diffie-hellman-group18-sha512,diffie-hellman-group16-sha512";

    public const string StrictModernHostKeyAlgorithms =
        "ssh-ed25519-cert-v01@openssh.com,ecdsa-sha2-nistp521-cert-v01@openssh.com,ecdsa-sha2-nistp384-cert-v01@openssh.com,ecdsa-sha2-nistp256-cert-v01@openssh.com,rsa-sha2-512-cert-v01@openssh.com,rsa-sha2-256-cert-v01@openssh.com,ssh-ed25519,ecdsa-sha2-nistp521,ecdsa-sha2-nistp384,ecdsa-sha2-nistp256,rsa-sha2-512,rsa-sha2-256";

    public const string StrictModernCiphers =
        "chacha20-poly1305@openssh.com,aes256-gcm@openssh.com,aes128-gcm@openssh.com,aes256-ctr,aes192-ctr,aes128-ctr";

    public const string StrictModernMacs =
        "hmac-sha2-512-etm@openssh.com,hmac-sha2-256-etm@openssh.com,umac-128-etm@openssh.com,hmac-sha2-512,hmac-sha2-256";

    public static IReadOnlyList<string> BuildArguments(OpenSshLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        string trimmedHost = spec.Host?.Trim() ?? string.Empty;
        if (!InputValidator.IsValidSshHost(trimmedHost))
        {
            throw new ArgumentException("Invalid SSH target host.", nameof(spec));
        }

        if (!InputValidator.ValidatePortRange(spec.Port))
        {
            throw new ArgumentOutOfRangeException(nameof(spec), spec.Port, "SSH port must be in range 1..65535.");
        }

        string? trimmedUser = string.IsNullOrWhiteSpace(spec.Username) ? null : spec.Username.Trim();
        if (trimmedUser is not null && !InputValidator.Validate(trimmedUser, "SshUser"))
        {
            throw new ArgumentException("Invalid SSH username.", nameof(spec));
        }

        if (string.IsNullOrWhiteSpace(spec.EphemeralKnownHostsPath)
            || ImportedPathPolicy.IsNetworkOrDevicePath(spec.EphemeralKnownHostsPath))
        {
            throw new ArgumentException("Valid local ephemeral known_hosts path is required.", nameof(spec));
        }

        if (string.IsNullOrWhiteSpace(spec.HostKeyAlias) || spec.HostKeyAlias.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new ArgumentException("A host key alias without whitespace is required.", nameof(spec));
        }

        var args = new List<string>
        {
            "-F", "none",
            "-o", "GlobalKnownHostsFile=none",
            "-o", $"UserKnownHostsFile={spec.EphemeralKnownHostsPath}",
            "-o", "StrictHostKeyChecking=yes",
            "-o", "CheckHostIP=no",
            "-o", "UpdateHostKeys=no",
            "-o", $"HostKeyAlias={spec.HostKeyAlias}",
            "-p", spec.Port.ToString(CultureInfo.InvariantCulture)
        };

        if (spec.ServerAliveIntervalSeconds > 0)
        {
            args.Add("-o");
            args.Add($"ServerAliveInterval={spec.ServerAliveIntervalSeconds.ToString(CultureInfo.InvariantCulture)}");
        }

        if (spec.X11Forwarding)
        {
            args.Add("-X");
        }

        if (spec.Compression)
        {
            args.Add("-C");
        }

        if (spec.AgentForwarding)
        {
            args.Add("-A");
        }

        if (!string.IsNullOrWhiteSpace(spec.KeyPath))
        {
            string trimmedKey = spec.KeyPath.Trim();
            if (!ImportedPathPolicy.IsNetworkOrDevicePath(trimmedKey))
            {
                args.Add("-i");
                args.Add(trimmedKey);
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.CertificatePath))
        {
            string trimmedCert = spec.CertificatePath.Trim();
            if (!ImportedPathPolicy.IsNetworkOrDevicePath(trimmedCert))
            {
                args.Add("-o");
                args.Add($"CertificateFile={trimmedCert}");
            }
        }

        // ssh keeps the first value of a repeated option: the host key algorithms are emitted once,
        // here, whatever the policy.
        string? pinnedHostKeyAlgorithms = BuildPinnedHostKeyAlgorithms(spec);
        if (spec.CryptoPolicy != SshCryptoPolicy.StrictModern && pinnedHostKeyAlgorithms is not null)
        {
            args.Add("-o");
            args.Add($"HostKeyAlgorithms={pinnedHostKeyAlgorithms}");
        }

        if (spec.CryptoPolicy == SshCryptoPolicy.StrictModern)
        {
            args.Add("-o");
            args.Add($"KexAlgorithms={StrictModernKexAlgorithms}");
            args.Add("-o");
            args.Add($"HostKeyAlgorithms={pinnedHostKeyAlgorithms ?? StrictModernHostKeyAlgorithms}");
            args.Add("-o");
            args.Add($"Ciphers={StrictModernCiphers}");
            args.Add("-o");
            args.Add($"MACs={StrictModernMacs}");
        }

        foreach (string rawForward in spec.LocalForwards)
        {
            if (SshLocalForwardSpec.TryParse(rawForward, out var parsed) && parsed is not null)
            {
                args.Add("-L");
                args.Add($"{parsed.BindAddress}:{parsed.LocalPort}:{parsed.RemoteHost}:{parsed.RemotePort}");
            }
        }

        if (InputValidator.ValidatePortRange(spec.SocksProxyPort))
        {
            args.Add("-D");
            args.Add($"127.0.0.1:{spec.SocksProxyPort.ToString(CultureInfo.InvariantCulture)}");
        }

        if (InputValidator.ValidatePortRange(spec.RemoteBindPort))
        {
            int remoteLocal = InputValidator.ValidatePortRange(spec.RemoteLocalPort)
                ? spec.RemoteLocalPort
                : spec.RemoteBindPort;
            args.Add("-R");
            args.Add(
                $"{spec.RemoteBindPort.ToString(CultureInfo.InvariantCulture)}:127.0.0.1:{remoteLocal.ToString(CultureInfo.InvariantCulture)}");
        }

        string? normalizedStartupCmd = null;
        if (spec.AllowStartupExecution)
        {
            int envCount = 0;
            foreach (string rawEnv in spec.EnvironmentVariables)
            {
                if (envCount >= SshStartupCommandPolicy.MaxEnvironmentVariables)
                {
                    break;
                }

                if (SshStartupCommandPolicy.TryParseEnvAssignment(rawEnv, out string envName, out string envVal))
                {
                    args.Add("-o");
                    args.Add($"SetEnv={envName}={envVal}");
                    envCount++;
                }
            }

            if (SshStartupCommandPolicy.TryNormalizeStartupCommand(spec.StartupCommand, out string cmd))
            {
                normalizedStartupCmd = cmd;
                args.Add("-t");
            }
        }

        args.Add("--");
        args.Add(trimmedUser is null ? trimmedHost : $"{trimmedUser}@{trimmedHost}");

        if (normalizedStartupCmd is not null)
        {
            args.Add(normalizedStartupCmd);
        }

        return args;
    }

    /// <summary>
    /// The <c>HostKeyAlgorithms</c> value for the pinned key types: their plain algorithms, then
    /// the matching certificate algorithms when the host presented a certificate. Under
    /// <see cref="SshCryptoPolicy.StrictModern"/> it replaces the StrictModern list and is filtered
    /// to the algorithms that list allows. Null when no key type is pinned.
    /// </summary>
    private static string? BuildPinnedHostKeyAlgorithms(OpenSshLaunchSpec spec)
    {
        if (spec.PinnedKeyTypes.Count == 0)
        {
            return null;
        }

        bool strictModern = spec.CryptoPolicy == SshCryptoPolicy.StrictModern;
        bool allowSha1Rsa = !strictModern;
        var plain = new List<string>();
        var certificates = new List<string>();
        foreach (string keyType in spec.PinnedKeyTypes)
        {
            if (!OpenSshHostKeyTypes.IsSupported(keyType))
            {
                throw new ArgumentException("Unsupported pinned host key type.", nameof(spec));
            }

            plain.AddRange(OpenSshHostKeyTypes.PlainAlgorithms(keyType, allowSha1Rsa));
            if (spec.HostCertificatePresented)
            {
                certificates.AddRange(OpenSshHostKeyTypes.CertificateAlgorithms(keyType, allowSha1Rsa));
            }
        }

        IEnumerable<string> ordered = plain.Concat(certificates).Distinct(StringComparer.Ordinal);
        if (strictModern)
        {
            var allowed = new HashSet<string>(StrictModernHostKeyAlgorithms.Split(','), StringComparer.Ordinal);
            ordered = ordered.Where(allowed.Contains);
        }

        string joined = string.Join(',', ordered);
        if (joined.Length == 0)
        {
            throw new ArgumentException("No host key algorithm is allowed for the pinned key types.", nameof(spec));
        }

        return joined;
    }

    /// <summary>
    /// Formats an argument vector into a properly escaped Windows command line string for ConPTY
    /// (<c>CreateProcessW</c> / <c>CommandLineToArgvW</c> rules).
    /// </summary>
    public static string JoinWindowsCommandLine(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return string.Empty;
        }

        var parts = new string[arguments.Count];
        for (int i = 0; i < arguments.Count; i++)
        {
            parts[i] = QuoteWindowsArgument(arguments[i]);
        }

        return string.Join(' ', parts);
    }

    private static string QuoteWindowsArgument(string arg)
    {
        if (arg.Length == 0)
        {
            return "\"\"";
        }

        bool needsQuotes = false;
        foreach (char c in arg)
        {
            if (char.IsWhiteSpace(c) || c == '"')
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes)
        {
            return arg;
        }

        var sb = new StringBuilder(arg.Length + 4);
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', (backslashes * 2) + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }

                sb.Append(c);
            }
        }

        if (backslashes > 0)
        {
            sb.Append('\\', backslashes * 2);
        }

        sb.Append('"');
        return sb.ToString();
    }
}
