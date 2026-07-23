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
/// Resolves all application-owned writable paths beneath the current user's local application data.
/// </summary>
public static class ApplicationDataPathResolver
{
    /// <summary>Resolves the writable application data root for the current user.</summary>
    public static string Resolve()
    {
        string localApplicationData =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return ResolveFromLocalApplicationData(localApplicationData);
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
}
