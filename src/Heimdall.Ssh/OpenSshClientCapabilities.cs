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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Heimdall.Ssh;

/// <summary>Why a candidate <c>ssh.exe</c> was not accepted.</summary>
public enum OpenSshRefusalReason
{
    /// <summary>No candidate file was found, or the configured path is unusable.</summary>
    NotFound,

    /// <summary>The binary is not an <c>OpenSSH_for_Windows</c> build (MSYS, Cygwin, Git for Windows...).</summary>
    NotWindowsBuild,

    /// <summary><c>ssh -V</c> failed, timed out or printed nothing recognizable.</summary>
    VersionUnreadable,
}

/// <summary>A parsed OpenSSH version such as <c>8.1p1</c>.</summary>
public readonly record struct OpenSshVersion(int Major, int Minor, int Patch) : IComparable<OpenSshVersion>
{
    public int CompareTo(OpenSshVersion other)
    {
        int byMajor = Major.CompareTo(other.Major);
        if (byMajor != 0)
        {
            return byMajor;
        }

        int byMinor = Minor.CompareTo(other.Minor);
        return byMinor != 0 ? byMinor : Patch.CompareTo(other.Patch);
    }

    public static bool operator >=(OpenSshVersion left, OpenSshVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(OpenSshVersion left, OpenSshVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >(OpenSshVersion left, OpenSshVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(OpenSshVersion left, OpenSshVersion right) => left.CompareTo(right) < 0;

    public override string ToString() => $"{Major}.{Minor}p{Patch}";
}

/// <summary>Algorithm names reported by <c>ssh -Q</c>; <see cref="IsKnown"/> is false when the query failed.</summary>
public sealed record OpenSshAlgorithmList(IReadOnlyList<string> Names, bool IsKnown)
{
    public static OpenSshAlgorithmList Unknown { get; } = new(Array.Empty<string>(), false);
}

/// <summary>The requested algorithms kept by the binary; <see cref="Verified"/> is false when the binary's list is unknown.</summary>
public sealed record OpenSshAlgorithmSelection(IReadOnlyList<string> Algorithms, bool Verified);

/// <summary>Result of running one external ssh.exe command.</summary>
public sealed record OpenSshProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs <c>ssh.exe</c> with arguments and returns its output, or null when it could not run or timed out.</summary>
internal interface IOpenSshProcessRunner
{
    OpenSshProcessResult? Run(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout);
}

/// <summary>Real runner: no shell, no window, redirected streams, kill on timeout.</summary>
internal sealed class OpenSshProcessRunner : IOpenSshProcessRunner
{
    public OpenSshProcessResult? Run(string executablePath, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = new Process { StartInfo = info };
            if (!process.Start())
            {
                return null;
            }

            process.StandardInput.Close();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // The process already ended.
                }

                return null;
            }

            process.WaitForExit();
            return new OpenSshProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }
}

/// <summary>
/// What a verified Windows <c>ssh.exe</c> build can do: version, feature gates and supported algorithms.
/// </summary>
public sealed class OpenSshClientCapabilities
{
    /// <summary>First version that accepts <c>SetEnv</c>.</summary>
    public static readonly OpenSshVersion SetEnvMinimumVersion = new(7, 8, 0);

    /// <summary>First version whose configuration tokenizer handles quoted values the new way.</summary>
    public static readonly OpenSshVersion QuotedTokenizerMinimumVersion = new(8, 7, 0);

    public OpenSshClientCapabilities(
        string executablePath,
        OpenSshVersion version,
        OpenSshAlgorithmList kex,
        OpenSshAlgorithmList ciphers,
        OpenSshAlgorithmList macs,
        OpenSshAlgorithmList keyTypes)
    {
        ExecutablePath = executablePath;
        Version = version;
        Kex = kex;
        Ciphers = ciphers;
        Macs = macs;
        KeyTypes = keyTypes;
    }

    public string ExecutablePath { get; }

    public OpenSshVersion Version { get; }

    public OpenSshAlgorithmList Kex { get; }

    public OpenSshAlgorithmList Ciphers { get; }

    public OpenSshAlgorithmList Macs { get; }

    public OpenSshAlgorithmList KeyTypes { get; }

    public bool SupportsSetEnv => Version >= SetEnvMinimumVersion;

    public bool UsesQuotedTokenizer => Version >= QuotedTokenizerMinimumVersion;

    public OpenSshAlgorithmSelection IntersectKex(IEnumerable<string> requested) => Intersect(requested, Kex);

    public OpenSshAlgorithmSelection IntersectCiphers(IEnumerable<string> requested) => Intersect(requested, Ciphers);

    public OpenSshAlgorithmSelection IntersectMacs(IEnumerable<string> requested) => Intersect(requested, Macs);

    public OpenSshAlgorithmSelection IntersectKeyTypes(IEnumerable<string> requested) => Intersect(requested, KeyTypes);

    private static OpenSshAlgorithmSelection Intersect(IEnumerable<string> requested, OpenSshAlgorithmList supported)
    {
        var requestedList = requested.ToList();
        if (!supported.IsKnown)
        {
            return new OpenSshAlgorithmSelection(requestedList, false);
        }

        var known = new HashSet<string>(supported.Names, StringComparer.Ordinal);
        return new OpenSshAlgorithmSelection(requestedList.Where(known.Contains).ToList(), true);
    }
}

/// <summary>Outcome of an attested ssh.exe resolution: capabilities on success, a typed reason otherwise.</summary>
public sealed record OpenSshResolution(OpenSshClientCapabilities? Capabilities, OpenSshRefusalReason? Refusal)
{
    public bool IsAccepted => Capabilities is not null;

    public string? Path => Capabilities?.ExecutablePath;
}

/// <summary>
/// Attests an <c>ssh.exe</c> (Windows build only) and reads its capabilities, cached per
/// full path and last write time.
/// </summary>
public sealed partial class OpenSshCapabilitiesReader
{
    public const string WindowsBuildMarker = "OpenSSH_for_Windows_";
    public const string VersionArgument = "-V";
    public const string QueryArgument = "-Q";
    public const string KexQuery = "kex";
    public const string CipherQuery = "cipher";
    public const string MacQuery = "mac";
    public const string KeyQuery = "key";

    /// <summary>Upper bound for each external ssh.exe call.</summary>
    public static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(10);

    private readonly IOpenSshProcessRunner _runner;
    private readonly Func<string, DateTime> _lastWriteUtc;
    private readonly ConcurrentDictionary<(string Path, DateTime LastWriteUtc), Lazy<OpenSshResolution>> _cache = new();

    public OpenSshCapabilitiesReader()
        : this(new OpenSshProcessRunner(), File.GetLastWriteTimeUtc)
    {
    }

    internal OpenSshCapabilitiesReader(IOpenSshProcessRunner runner, Func<string, DateTime> lastWriteUtc)
    {
        _runner = runner;
        _lastWriteUtc = lastWriteUtc;
    }

    /// <summary>Process-wide reader used by default.</summary>
    public static OpenSshCapabilitiesReader Shared { get; } = new();

    /// <summary>Attests <paramref name="fullPath"/> and returns its capabilities, or the refusal reason.</summary>
    public OpenSshResolution Read(string fullPath)
    {
        DateTime stamp = _lastWriteUtc(fullPath);
        var key = (fullPath, stamp);
        OpenSshResolution result = _cache.GetOrAdd(
            key,
            _ => new Lazy<OpenSshResolution>(() => Probe(fullPath), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (!result.IsAccepted)
        {
            // A refusal is never kept: the user may fix the binary in place.
            _cache.TryRemove(key, out _);
        }

        return result;
    }

    /// <summary>Parses the <c>ssh -V</c> banner: a version on success, a refusal reason otherwise.</summary>
    public static (OpenSshVersion? Version, OpenSshRefusalReason? Refusal) ParseBanner(string banner)
    {
        Match windows = WindowsBannerRegex().Match(banner);
        if (windows.Success)
        {
            int major = int.Parse(windows.Groups["major"].Value, CultureInfo.InvariantCulture);
            int minor = int.Parse(windows.Groups["minor"].Value, CultureInfo.InvariantCulture);
            int patch = windows.Groups["patch"].Success
                ? int.Parse(windows.Groups["patch"].Value, CultureInfo.InvariantCulture)
                : 0;
            return (new OpenSshVersion(major, minor, patch), null);
        }

        if (banner.Contains(WindowsBuildMarker, StringComparison.Ordinal))
        {
            return (null, OpenSshRefusalReason.VersionUnreadable);
        }

        return GenericBannerRegex().IsMatch(banner)
            ? (null, OpenSshRefusalReason.NotWindowsBuild)
            : (null, OpenSshRefusalReason.VersionUnreadable);
    }

    private OpenSshResolution Probe(string fullPath)
    {
        OpenSshProcessResult? versionRun = _runner.Run(fullPath, new[] { VersionArgument }, ProcessTimeout);
        if (versionRun is null)
        {
            return new OpenSshResolution(null, OpenSshRefusalReason.VersionUnreadable);
        }

        string banner = string.IsNullOrWhiteSpace(versionRun.StandardError)
            ? versionRun.StandardOutput
            : versionRun.StandardError;
        (OpenSshVersion? version, OpenSshRefusalReason? refusal) = ParseBanner(banner);
        if (version is null)
        {
            return new OpenSshResolution(null, refusal);
        }

        return new OpenSshResolution(
            new OpenSshClientCapabilities(
                fullPath,
                version.Value,
                Query(fullPath, KexQuery),
                Query(fullPath, CipherQuery),
                Query(fullPath, MacQuery),
                Query(fullPath, KeyQuery)),
            null);
    }

    private OpenSshAlgorithmList Query(string fullPath, string kind)
    {
        OpenSshProcessResult? run = _runner.Run(fullPath, new[] { QueryArgument, kind }, ProcessTimeout);
        if (run is null || run.ExitCode != 0)
        {
            return OpenSshAlgorithmList.Unknown;
        }

        string[] names = run.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length == 0 ? OpenSshAlgorithmList.Unknown : new OpenSshAlgorithmList(names, true);
    }

    [GeneratedRegex(@"OpenSSH_for_Windows_(?<major>\d+)\.(?<minor>\d+)(?:p(?<patch>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsBannerRegex();

    [GeneratedRegex(@"OpenSSH_\d+\.\d+", RegexOptions.CultureInvariant)]
    private static partial Regex GenericBannerRegex();
}
