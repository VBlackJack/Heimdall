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

namespace TwinShell.Core.Models;

/// <summary>
/// Represents a PowerShell module from the PowerShell Gallery
/// </summary>
public sealed class PowerShellModule
{
    /// <summary>
    /// Module name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Module version
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Module description
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Module author
    /// </summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// Company name
    /// </summary>
    public string? CompanyName { get; set; }

    /// <summary>
    /// Download count
    /// </summary>
    public long DownloadCount { get; set; }

    /// <summary>
    /// Published date
    /// </summary>
    public DateTime PublishedDate { get; set; }

    /// <summary>
    /// Tags
    /// </summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Project URI
    /// </summary>
    public string? ProjectUri { get; set; }

    /// <summary>
    /// License URI
    /// </summary>
    public string? LicenseUri { get; set; }

    /// <summary>
    /// Whether the module is installed locally
    /// </summary>
    public bool IsInstalled { get; set; }
}
