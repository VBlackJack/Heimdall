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

using TwinShell.Core.Enums;

namespace TwinShell.Core.Helpers;

/// <summary>
/// The names a risk level is written under in a sync file: the names of
/// <see cref="CriticalityLevel"/>, plus two legacy spellings an older schema declared.
/// </summary>
/// <remarks>
/// <para>
/// The sync schema listed "Info", "Warning" and "Danger" while export writes the enum
/// names, so every Run or Dangerous action an export wrote failed validation on the way
/// back in. The schema now takes its list from <see cref="AcceptedNames"/>, which is
/// derived from the enum, so the two cannot drift apart again.
/// </para>
/// <para>
/// "Warning" and "Danger" are still accepted, mapped to Run and Dangerous, because a file
/// written by hand against the old schema says them. An unknown name still fails
/// validation rather than becoming Info.
/// </para>
/// </remarks>
public static class CriticalityLevelNames
{
    private static readonly IReadOnlyDictionary<string, CriticalityLevel> LegacyAliases =
        new Dictionary<string, CriticalityLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["Warning"] = CriticalityLevel.Run,
            ["Danger"] = CriticalityLevel.Dangerous,
        };

    /// <summary>Every name a sync file may use for a level: the enum names, then the aliases.</summary>
    public static IReadOnlyList<string> AcceptedNames { get; } =
        Enum.GetNames<CriticalityLevel>().Concat(LegacyAliases.Keys).ToList();

    /// <summary>
    /// Reads a level written by name, an enum name or a legacy alias, without regard to case.
    /// </summary>
    public static bool TryParse(string? name, out CriticalityLevel level)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            string trimmed = name.Trim();
            if (LegacyAliases.TryGetValue(trimmed, out level))
            {
                return true;
            }

            if (Enum.TryParse(trimmed, ignoreCase: true, out level)
                && Enum.IsDefined(level)
                && !int.TryParse(trimmed, out _))
            {
                return true;
            }
        }

        level = default;
        return false;
    }
}
