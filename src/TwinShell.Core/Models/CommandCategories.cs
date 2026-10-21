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

namespace TwinShell.Core.Models;

/// <summary>
/// The categories the shipped catalogue uses, as stable ASCII keys.
/// </summary>
/// <remarks>
/// A key is what the seeds, the database, an export and a sync folder carry; the label an
/// operator reads is resolved from it at presentation, in their language. Any other category
/// string is one an operator typed, and is shown as typed.
/// </remarks>
public static class CommandCategories
{
    /// <summary>The "security" category.</summary>
    public const string Security = "security";

    /// <summary>The "active-directory" category.</summary>
    public const string ActiveDirectory = "active-directory";

    /// <summary>The "services-automation" category.</summary>
    public const string ServicesAutomation = "services-automation";

    /// <summary>The "windows-updates" category.</summary>
    public const string WindowsUpdates = "windows-updates";

    /// <summary>The "monitoring-logs" category.</summary>
    public const string MonitoringLogs = "monitoring-logs";

    /// <summary>The "files-storage" category.</summary>
    public const string FilesStorage = "files-storage";

    /// <summary>The "network-dns" category.</summary>
    public const string NetworkDns = "network-dns";

    /// <summary>The "package-management" category.</summary>
    public const string PackageManagement = "package-management";

    /// <summary>The "development" category.</summary>
    public const string Development = "development";

    /// <summary>The "performance" category.</summary>
    public const string Performance = "performance";

    /// <summary>The "containers-vms" category.</summary>
    public const string ContainersVms = "containers-vms";

    /// <summary>The "database" category.</summary>
    public const string Database = "database";

    /// <summary>The "windows-debloat" category.</summary>
    public const string WindowsDebloat = "windows-debloat";

    /// <summary>Every key the shipped catalogue knows.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Security,
        ActiveDirectory,
        ServicesAutomation,
        WindowsUpdates,
        MonitoringLogs,
        FilesStorage,
        NetworkDns,
        PackageManagement,
        Development,
        Performance,
        ContainersVms,
        Database,
        WindowsDebloat,
    ];
}
