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
/// Detailed information about a package
/// </summary>
public sealed class PackageInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Homepage { get; set; }
    public string? License { get; set; }
    public string? LicenseUrl { get; set; }
    public string Source { get; set; } = string.Empty;
    public PackageManager PackageManager { get; set; }
    public DateTime? PublishedDate { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool IsInstalled { get; set; }
}
