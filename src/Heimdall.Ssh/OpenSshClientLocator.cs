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

using Heimdall.Core.Configuration;

namespace Heimdall.Ssh;

/// <summary>
/// Resolves the Windows native OpenSSH client (<c>ssh.exe</c>) path with strict rejection
/// of network shares and Windows device paths.
/// </summary>
public static class OpenSshClientLocator
{
    public const string ExecutableName = "ssh.exe";

    /// <summary>
    /// Resolves <c>ssh.exe</c> from an explicit local setting, Windows System32 OpenSSH, or <c>PATH</c>.
    /// When <paramref name="configuredPath"/> is non-blank, only that explicit local file is accepted
    /// (failing closed if missing or pointing to a UNC/device path).
    /// </summary>
    public static string? ResolveExecutablePath(
        string? configuredPath,
        Func<string, bool>? fileExists = null,
        string? windowsDirectory = null,
        string? pathEnvironment = null)
    {
        Func<string, bool> exists = fileExists ?? File.Exists;

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (ImportedPathPolicy.IsNetworkOrDevicePath(expanded))
            {
                return null;
            }

            return exists(expanded) ? expanded : null;
        }

        string winDir = !string.IsNullOrWhiteSpace(windowsDirectory)
            ? windowsDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(winDir))
        {
            string system32Candidate = Path.Combine(winDir, "System32", "OpenSSH", ExecutableName);
            if (exists(system32Candidate))
            {
                return system32Candidate;
            }
        }

        string pathVar = pathEnvironment ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(dir))
            {
                continue;
            }

            string candidate = Path.Combine(dir, ExecutableName);
            if (!ImportedPathPolicy.IsNetworkOrDevicePath(candidate) && exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Same lookup as <see cref="ResolveExecutablePath"/> but every candidate must pass attestation
    /// (<c>ssh -V</c> reports an <c>OpenSSH_for_Windows</c> build). A configured path is never
    /// replaced by a fallback. PATH entries that are not fully qualified are skipped.
    /// </summary>
    public static OpenSshResolution ResolveAttested(
        string? configuredPath,
        OpenSshCapabilitiesReader? reader = null,
        Func<string, bool>? fileExists = null,
        string? windowsDirectory = null,
        string? pathEnvironment = null)
    {
        OpenSshCapabilitiesReader attestor = reader ?? OpenSshCapabilitiesReader.Shared;
        Func<string, bool> exists = fileExists ?? File.Exists;
        OpenSshRefusalReason? firstRefusal = null;

        foreach (string candidate in EnumerateCandidates(configuredPath, exists, windowsDirectory, pathEnvironment))
        {
            OpenSshResolution result = attestor.Read(candidate);
            if (result.IsAccepted)
            {
                return result;
            }

            firstRefusal ??= result.Refusal;
        }

        return new OpenSshResolution(null, firstRefusal ?? OpenSshRefusalReason.NotFound);
    }

    private static IEnumerable<string> EnumerateCandidates(
        string? configuredPath,
        Func<string, bool> exists,
        string? windowsDirectory,
        string? pathEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
            if (!ImportedPathPolicy.IsNetworkOrDevicePath(expanded)
                && Path.IsPathFullyQualified(expanded)
                && exists(expanded))
            {
                yield return expanded;
            }

            yield break;
        }

        string winDir = !string.IsNullOrWhiteSpace(windowsDirectory)
            ? windowsDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(winDir))
        {
            string system32Candidate = Path.Combine(winDir, "System32", "OpenSSH", ExecutableName);
            if (exists(system32Candidate))
            {
                yield return system32Candidate;
            }
        }

        string pathVar = pathEnvironment ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathFullyQualified(dir))
            {
                continue;
            }

            string candidate = Path.Combine(dir, ExecutableName);
            if (!ImportedPathPolicy.IsNetworkOrDevicePath(candidate) && exists(candidate))
            {
                yield return candidate;
            }
        }
    }
}
