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

namespace Heimdall.App.ViewModels;

/// <summary>
/// The eight colours a project badge or a folder icon can take, each with the key of its name.
/// </summary>
/// <remarks>
/// One list for both: the project dialog's swatches and the folder colour menu must offer the
/// same colours under the same names, and a palette declared twice drifts.
/// </remarks>
public static class BadgeColorPalette
{
    /// <summary>Hex colour and localization key of its name, in palette order.</summary>
    public static readonly IReadOnlyList<(string Color, string LabelKey)> Entries =
    [
        ("#3B82F6", "ProjectDialogColorBlue"),
        ("#22C55E", "ProjectDialogColorGreen"),
        ("#EF4444", "ProjectDialogColorRed"),
        ("#F59E0B", "ProjectDialogColorAmber"),
        ("#8B5CF6", "ProjectDialogColorPurple"),
        ("#EC4899", "ProjectDialogColorPink"),
        ("#06B6D4", "ProjectDialogColorCyan"),
        ("#F97316", "ProjectDialogColorOrange")
    ];

    /// <summary>The hex colours alone, in palette order.</summary>
    public static string[] Colors => [.. Entries.Select(entry => entry.Color)];
}
