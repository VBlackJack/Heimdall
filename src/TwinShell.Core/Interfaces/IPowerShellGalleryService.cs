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
/// Service for interacting with the PowerShell Gallery
/// </summary>
public interface IPowerShellGalleryService
{
    /// <summary>
    /// Searches for modules in the PowerShell Gallery
    /// </summary>
    /// <param name="query">Search query</param>
    /// <param name="maxResults">Maximum number of results (default: 50)</param>
    /// <returns>List of matching modules</returns>
    Task<IEnumerable<PowerShellModule>> SearchModulesAsync(string query, int maxResults = 50);

    /// <summary>
    /// Gets details for a specific module
    /// </summary>
    /// <param name="moduleName">Module name</param>
    /// <returns>Module details, or null if not found</returns>
    Task<PowerShellModule?> GetModuleDetailsAsync(string moduleName);

    /// <summary>
    /// Gets commands from a locally installed module
    /// </summary>
    /// <param name="moduleName">Module name</param>
    /// <returns>List of commands in the module</returns>
    Task<IEnumerable<PowerShellCommand>> GetModuleCommandsAsync(string moduleName);

    /// <summary>
    /// Gets help information for a specific command
    /// </summary>
    /// <param name="commandName">Command name</param>
    /// <returns>Command help information</returns>
    Task<PowerShellCommand?> GetCommandHelpAsync(string commandName);

    /// <summary>
    /// Installs a module from the PowerShell Gallery
    /// </summary>
    /// <param name="moduleName">Module name to install</param>
    /// <param name="scope">Installation scope (CurrentUser or AllUsers)</param>
    /// <returns>True if installation succeeded</returns>
    Task<bool> InstallModuleAsync(string moduleName, string scope = "CurrentUser");

    /// <summary>
    /// Checks if a module is installed
    /// </summary>
    /// <param name="moduleName">Module name</param>
    /// <returns>True if module is installed</returns>
    Task<bool> IsModuleInstalledAsync(string moduleName);

    /// <summary>
    /// Imports a command as a custom Action
    /// </summary>
    /// <param name="command">PowerShell command to import</param>
    /// <returns>The created Action</returns>
    Task<TwinShell.Core.Models.Action> ImportCommandAsActionAsync(PowerShellCommand command);
}
