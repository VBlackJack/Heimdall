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

using TwinShell.Core.Models;

namespace TwinShell.Core.Helpers;

/// <summary>
/// Maps the category strings the catalogue shipped before its categories became keys to
/// those keys.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-10 a shipped category was an emoji followed by an English label, and that
/// string is what an export or a sync folder taken then carries. Run at the doors such a
/// file comes in through, this lands its actions in the group the shipped ones now use
/// instead of a second group whose name only looks the same.
/// </para>
/// <para>
/// The old strings are kept verbatim, emoji and variation selector included, because the
/// match is exact after a trim: a category an operator typed that merely resembles one of
/// them is theirs, and stays as they typed it.
/// </para>
/// </remarks>
public static class CategoryMigration
{
    private static readonly IReadOnlyDictionary<string, string> LegacyCategories =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["🔒 Security & Encryption"] = CommandCategories.Security,
            ["🏢 Active Directory & GPO"] = CommandCategories.ActiveDirectory,
            ["⚙️ Services & Automation"] = CommandCategories.ServicesAutomation,
            ["🔄 Windows Updates"] = CommandCategories.WindowsUpdates,
            ["📊 Monitoring & Logs"] = CommandCategories.MonitoringLogs,
            ["📁 Files & Storage"] = CommandCategories.FilesStorage,
            ["🌐 Network & DNS"] = CommandCategories.NetworkDns,
            ["📦 Package Management"] = CommandCategories.PackageManagement,
            ["🔧 Development"] = CommandCategories.Development,
            ["⚡ Performance"] = CommandCategories.Performance,
            ["🐳 Containers & VMs"] = CommandCategories.ContainersVms,
            ["💽 Database"] = CommandCategories.Database,
            ["🧹 Windows Debloat"] = CommandCategories.WindowsDebloat,
        };

    /// <summary>The old shipped category strings and the key each maps to.</summary>
    public static IReadOnlyDictionary<string, string> Legacy => LegacyCategories;

    /// <summary>
    /// Returns the key for one of the old shipped category strings, or
    /// <paramref name="category"/> unchanged for anything else.
    /// </summary>
    public static string Normalize(string category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return category;
        }

        return LegacyCategories.TryGetValue(category.Trim(), out string? key) ? key : category;
    }
}
