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

    /// <summary>
    /// What the attested <c>ssh.exe</c> can do. Null means unknown: algorithm lists are then passed
    /// unchanged and no version gate is applied.
    /// </summary>
    public OpenSshClientCapabilities? Capabilities { get; init; }
}

/// <summary>Why the launch of native OpenSSH was refused before any process started.</summary>
public enum OpenSshLaunchFailure
{
    /// <summary>The target host is not a valid SSH host.</summary>
    InvalidHost,

    /// <summary>The port is out of range.</summary>
    InvalidPort,

    /// <summary>The username is not valid.</summary>
    InvalidUsername,

    /// <summary>The ephemeral known_hosts path is missing or not local.</summary>
    InvalidKnownHostsPath,

    /// <summary>The host key alias is missing or contains whitespace.</summary>
    InvalidHostKeyAlias,

    /// <summary>A pinned host key type is not supported.</summary>
    UnsupportedPinnedKeyType,

    /// <summary>No host key algorithm is allowed for the pinned key types.</summary>
    NoAllowedHostKeyAlgorithm,

    /// <summary>A file path contains characters ssh would expand or misread.</summary>
    PathNotAllowed,

    /// <summary>Environment variables are requested but the binary predates <c>SetEnv</c>.</summary>
    SetEnvUnsupported,

    /// <summary>StrictModern and the binary share no key exchange algorithm.</summary>
    NoCommonKexAlgorithm,

    /// <summary>StrictModern and the binary share no cipher.</summary>
    NoCommonCipher,

    /// <summary>StrictModern and the binary share no MAC.</summary>
    NoCommonMac,
}

/// <summary>
/// Raised by <see cref="OpenSshLaunchBuilder"/> with a typed <see cref="Reason"/>. The message is
/// for logs only: the caller maps the reason to a localized text.
/// </summary>
public sealed class OpenSshLaunchException : ArgumentException
{
    public OpenSshLaunchException(OpenSshLaunchFailure reason, string logMessage)
        : base(logMessage)
    {
        Reason = reason;
    }

    public OpenSshLaunchFailure Reason { get; }
}

/// <summary>An argument vector plus what was left out of it because it could not be applied.</summary>
/// <param name="Arguments">The arguments for <c>ssh.exe</c>.</param>
/// <param name="DroppedLocalForwards">Local forward entries that did not parse.</param>
/// <param name="DroppedEnvironmentVariables">Environment entries that were invalid, refused or beyond the limit.</param>
public sealed record OpenSshLaunchPlan(
    IReadOnlyList<string> Arguments,
    int DroppedLocalForwards,
    int DroppedEnvironmentVariables)
{
    /// <summary>Total number of entries that were not applied.</summary>
    public int DroppedEntryCount => DroppedLocalForwards + DroppedEnvironmentVariables;
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

    /// <summary>Quote written around the two <c>-o</c> path values and the <c>SetEnv</c> value.</summary>
    private const char ConfigQuote = '"';

    /// <summary>Characters refused inside an environment value because they cannot be quoted for both ssh config tokenizers.</summary>
    private const string RefusedEnvironmentValueCharacters = "\"'\\";

    /// <summary>Convenience wrapper returning only the argument vector.</summary>
    public static IReadOnlyList<string> BuildArguments(OpenSshLaunchSpec spec) => Build(spec).Arguments;

    /// <summary>
    /// Builds the argument vector and reports the entries that could not be applied. Throws
    /// <see cref="OpenSshLaunchException"/> with a typed reason when the launch must be refused.
    /// </summary>
    public static OpenSshLaunchPlan Build(OpenSshLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        OpenSshClientCapabilities? capabilities = spec.Capabilities;
        int droppedForwards = 0;
        int droppedEnvironment = 0;

        string trimmedHost = spec.Host?.Trim() ?? string.Empty;
        if (!InputValidator.IsValidSshHost(trimmedHost))
        {
            throw new OpenSshLaunchException(OpenSshLaunchFailure.InvalidHost, "Invalid SSH target host.");
        }

        if (!InputValidator.ValidatePortRange(spec.Port))
        {
            throw new OpenSshLaunchException(OpenSshLaunchFailure.InvalidPort, "SSH port must be in range 1..65535.");
        }

        string? trimmedUser = string.IsNullOrWhiteSpace(spec.Username) ? null : spec.Username.Trim();
        if (trimmedUser is not null && !InputValidator.Validate(trimmedUser, "SshUser"))
        {
            throw new OpenSshLaunchException(OpenSshLaunchFailure.InvalidUsername, "Invalid SSH username.");
        }

        if (string.IsNullOrWhiteSpace(spec.EphemeralKnownHostsPath)
            || ImportedPathPolicy.IsNetworkOrDevicePath(spec.EphemeralKnownHostsPath))
        {
            throw new OpenSshLaunchException(
                OpenSshLaunchFailure.InvalidKnownHostsPath,
                "Valid local ephemeral known_hosts path is required.");
        }

        if (string.IsNullOrWhiteSpace(spec.HostKeyAlias) || spec.HostKeyAlias.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw new OpenSshLaunchException(
                OpenSshLaunchFailure.InvalidHostKeyAlias,
                "A host key alias without whitespace is required.");
        }

        string knownHostsPath = NormalizeLocalPath(spec.EphemeralKnownHostsPath);
        var args = new List<string>
        {
            "-F", "none",
            "-o", "GlobalKnownHostsFile=none",
            "-o", $"UserKnownHostsFile={Quote(knownHostsPath)}",
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
                // A separate argv element: no ssh config tokenizer sees it. IdentitiesOnly keeps
                // the agent and the default key files from being offered before the chosen key.
                args.Add("-i");
                args.Add(NormalizeLocalPath(trimmedKey));
                args.Add("-o");
                args.Add("IdentitiesOnly=yes");
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.CertificatePath))
        {
            string trimmedCert = spec.CertificatePath.Trim();
            if (!ImportedPathPolicy.IsNetworkOrDevicePath(trimmedCert))
            {
                args.Add("-o");
                args.Add($"CertificateFile={Quote(NormalizeLocalPath(trimmedCert))}");
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
            args.Add($"KexAlgorithms={SelectSupported(StrictModernKexAlgorithms, capabilities, static (c, r) => c.IntersectKex(r), OpenSshLaunchFailure.NoCommonKexAlgorithm)}");
            args.Add("-o");
            args.Add($"HostKeyAlgorithms={pinnedHostKeyAlgorithms ?? StrictModernHostKeyAlgorithms}");
            args.Add("-o");
            args.Add($"Ciphers={SelectSupported(StrictModernCiphers, capabilities, static (c, r) => c.IntersectCiphers(r), OpenSshLaunchFailure.NoCommonCipher)}");
            args.Add("-o");
            args.Add($"MACs={SelectSupported(StrictModernMacs, capabilities, static (c, r) => c.IntersectMacs(r), OpenSshLaunchFailure.NoCommonMac)}");
        }

        foreach (string rawForward in spec.LocalForwards)
        {
            if (string.IsNullOrWhiteSpace(rawForward))
            {
                continue;
            }

            if (SshLocalForwardSpec.TryParse(rawForward, out var parsed) && parsed is not null)
            {
                args.Add("-L");
                args.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{FormatForwardHost(parsed.BindAddress)}:{parsed.LocalPort}:{FormatForwardHost(parsed.RemoteHost)}:{parsed.RemotePort}"));
            }
            else
            {
                droppedForwards++;
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
                if (string.IsNullOrWhiteSpace(rawEnv))
                {
                    continue;
                }

                if (envCount >= SshStartupCommandPolicy.MaxEnvironmentVariables
                    || !SshStartupCommandPolicy.TryParseEnvAssignment(rawEnv, out string envName, out string envVal)
                    || !IsQuotableEnvironmentValue(envVal))
                {
                    droppedEnvironment++;
                    continue;
                }

                if (capabilities is { SupportsSetEnv: false })
                {
                    throw new OpenSshLaunchException(
                        OpenSshLaunchFailure.SetEnvUnsupported,
                        "The OpenSSH client predates SetEnv (7.8).");
                }

                args.Add("-o");
                args.Add($"SetEnv={envName}={Quote(envVal)}");
                envCount++;
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

        return new OpenSshLaunchPlan(args, droppedForwards, droppedEnvironment);
    }

    /// <summary>
    /// Makes a file path absolute and refuses what ssh would expand or misread. ssh percent-expands
    /// these keywords and not every inbox build expands <c>%%</c>, so refusing is the only rule that
    /// holds on every version. The path is written inside double quotes, so a quote, a backslash
    /// before a quote and a trailing backslash are refused too.
    /// </summary>
    private static string NormalizeLocalPath(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new OpenSshLaunchException(OpenSshLaunchFailure.PathNotAllowed, "The path cannot be made absolute.");
        }

        bool refused = full.Contains('%', StringComparison.Ordinal)
            || full.Contains("${", StringComparison.Ordinal)
            || full.Contains(ConfigQuote, StringComparison.Ordinal)
            || full.Contains("\\'", StringComparison.Ordinal)
            || full.EndsWith('\\')
            || full.Any(char.IsControl);
        if (refused)
        {
            throw new OpenSshLaunchException(
                OpenSshLaunchFailure.PathNotAllowed,
                "The path contains a character that ssh would expand or misread.");
        }

        return full;
    }

    private static string Quote(string value) => $"{ConfigQuote}{value}{ConfigQuote}";

    private static bool IsQuotableEnvironmentValue(string value)
        => !value.Any(static c => RefusedEnvironmentValueCharacters.Contains(c) || char.IsControl(c));

    private static string FormatForwardHost(string host)
        => host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;

    /// <summary>
    /// Keeps, in order, the entries of <paramref name="requested"/> the binary supports. An unknown
    /// binary list leaves the request unchanged; an empty intersection refuses the launch.
    /// </summary>
    private static string SelectSupported(
        string requested,
        OpenSshClientCapabilities? capabilities,
        Func<OpenSshClientCapabilities, IEnumerable<string>, OpenSshAlgorithmSelection> intersect,
        OpenSshLaunchFailure failure)
    {
        if (capabilities is null)
        {
            return requested;
        }

        OpenSshAlgorithmSelection selection = intersect(capabilities, requested.Split(','));
        if (!selection.Verified)
        {
            return requested;
        }

        if (selection.Algorithms.Count == 0)
        {
            throw new OpenSshLaunchException(failure, "The OpenSSH client supports none of the required algorithms.");
        }

        return string.Join(',', selection.Algorithms);
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
                throw new OpenSshLaunchException(
                    OpenSshLaunchFailure.UnsupportedPinnedKeyType,
                    "Unsupported pinned host key type.");
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
            throw new OpenSshLaunchException(
                OpenSshLaunchFailure.NoAllowedHostKeyAlgorithm,
                "No host key algorithm is allowed for the pinned key types.");
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
