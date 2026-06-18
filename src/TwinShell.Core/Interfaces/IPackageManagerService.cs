/*
 * Copyright 2025 Julien Bombled
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

using TwinShell.Core.Models;

namespace TwinShell.Core.Interfaces;

/// <summary>
/// Service for managing package operations (Winget and Chocolatey)
/// </summary>
public interface IPackageManagerService
{
    /// <summary>
    /// Search for packages using Winget
    /// </summary>
    /// <param name="searchTerm">The search term</param>
    /// <returns>List of package search results</returns>
    Task<IEnumerable<PackageSearchResult>> SearchWingetPackagesAsync(string searchTerm);

    /// <summary>
    /// Search for packages using Chocolatey
    /// </summary>
    /// <param name="searchTerm">The search term</param>
    /// <returns>List of package search results</returns>
    Task<IEnumerable<PackageSearchResult>> SearchChocolateyPackagesAsync(string searchTerm);

    /// <summary>
    /// Get detailed information about a Winget package
    /// </summary>
    /// <param name="packageId">The package ID</param>
    /// <returns>Detailed package information</returns>
    Task<PackageInfo?> GetWingetPackageInfoAsync(string packageId);

    /// <summary>
    /// Get detailed information about a Chocolatey package
    /// </summary>
    /// <param name="packageId">The package ID</param>
    /// <returns>Detailed package information</returns>
    Task<PackageInfo?> GetChocolateyPackageInfoAsync(string packageId);

    /// <summary>
    /// List all installed Winget packages
    /// </summary>
    /// <returns>List of installed packages</returns>
    Task<IEnumerable<PackageSearchResult>> ListWingetInstalledPackagesAsync();

    /// <summary>
    /// List all installed Chocolatey packages
    /// </summary>
    /// <returns>List of installed packages</returns>
    Task<IEnumerable<PackageSearchResult>> ListChocolateyInstalledPackagesAsync();

    /// <summary>
    /// Check if Winget is available on the system
    /// </summary>
    /// <returns>True if Winget is available</returns>
    Task<bool> IsWingetAvailableAsync();

    /// <summary>
    /// Check if Chocolatey is available on the system
    /// </summary>
    /// <returns>True if Chocolatey is available</returns>
    Task<bool> IsChocolateyAvailableAsync();
}
