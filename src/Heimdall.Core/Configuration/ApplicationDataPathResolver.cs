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

namespace Heimdall.Core.Configuration;

/// <summary>
/// Resolves all application-owned writable paths beneath the current user's local application data,
/// or beneath the data directory given at startup.
/// </summary>
/// <remarks>
/// The known-folder API ignores the LOCALAPPDATA variable, so without an override no second
/// instance could run beside the user's own: a hand test wrote into the live profile, and had to
/// back it up, restore it and compare hashes around every pass.
/// </remarks>
public static class ApplicationDataPathResolver
{
    /// <summary>Folder of the command library under the local application data.</summary>
    public const string TwinShellFolderName = "TwinShell";

    private static string? _overrideRoot;

    /// <summary>True when the data root was given at startup rather than derived.</summary>
    public static bool IsOverridden => _overrideRoot is not null;

    /// <summary>
    /// Uses <paramref name="dataRoot"/> as the data root for the rest of the process. Called once,
    /// at startup, before anything reads a path.
    /// </summary>
    public static void UseDataRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        if (!Path.IsPathFullyQualified(dataRoot))
        {
            throw new ArgumentException("The data directory must be an absolute path.", nameof(dataRoot));
        }

        _overrideRoot = Path.GetFullPath(dataRoot);
    }

    /// <summary>Resolves the writable application data root for the current user.</summary>
    public static string Resolve()
    {
        return _overrideRoot
            ?? ResolveFromLocalApplicationData(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>
    /// The command library's directory: under the local application data by default, inside the
    /// given data directory when one was given, so a test instance shares nothing with the user's.
    /// </summary>
    public static string ResolveTwinShellDirectory()
    {
        return ResolveTwinShellDirectory(
            _overrideRoot,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    /// <summary>The command library's directory for an override root and a local application data directory.</summary>
    public static string ResolveTwinShellDirectory(string? overrideRoot, string localApplicationData)
    {
        if (overrideRoot is not null)
        {
            return Path.Combine(overrideRoot, TwinShellFolderName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        return Path.Combine(localApplicationData, TwinShellFolderName);
    }

    /// <summary>
    /// Resolves the writable application data root from a supplied local application data directory.
    /// </summary>
    public static string ResolveFromLocalApplicationData(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        return Path.Combine(localApplicationData, AppConstants.ApplicationFolderName);
    }

    /// <summary>Resolves the diagnostic and connection-history directory.</summary>
    public static string GetLogsDirectory(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(dataRoot, AppConstants.LogsDirectoryName);
    }

    /// <summary>Resolves the terminal-macro directory.</summary>
    public static string GetMacrosDirectory(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(dataRoot, AppConstants.MacrosDirectoryName);
    }

    /// <summary>Resolves the network-scan history directory.</summary>
    public static string GetNetworkScansDirectory(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(dataRoot, AppConstants.NetworkScansDirectoryName);
    }

    /// <summary>Resolves the folder of the hosts file editor's backups.</summary>
    public static string GetHostsBackupsDirectory(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(dataRoot, AppConstants.HostsBackupsDirectoryName);
    }

    /// <summary>Resolves the restrictive staging root for application updates.</summary>
    public static string GetUpdatesDirectory(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return Path.Combine(dataRoot, AppConstants.UpdatesDirectoryName);
    }
}
