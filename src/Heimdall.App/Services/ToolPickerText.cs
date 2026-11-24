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

using Heimdall.Core.Models;

namespace Heimdall.App.Services;

/// <summary>
/// The words of one entry of the tool picker. The name given to a recipe is the one its tab and its
/// card carry, with the word "Recipe" and the author marked as unverified, and it becomes the default
/// name of the saved tree entry: that entry is then the only place the author is named. The
/// description of a recipe is the text of the file, never looked up as a locale key.
/// </summary>
internal static class ToolPickerText
{
    /// <summary>The name an entry shows, and the saved entry takes by default.</summary>
    /// <param name="descriptor">The tool.</param>
    /// <param name="localize">Resolves a locale key to its text.</param>
    internal static string DisplayName(ToolDescriptor descriptor, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(localize);
        return descriptor.IsRecipe ? RecipeToolText.TabTitle(descriptor, localize) : descriptor.ResolveLabel(localize);
    }

    /// <summary>The description an entry shows; empty when the tool has none.</summary>
    /// <param name="descriptor">The tool.</param>
    /// <param name="localize">Resolves a locale key to its text.</param>
    internal static string Description(ToolDescriptor descriptor, Func<string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(localize);

        if (descriptor.IsRecipe)
        {
            return descriptor.RecipeDescription ?? string.Empty;
        }

        string key = $"ToolDesc{descriptor.Id}";
        string description = localize(key);
        return string.Equals(description, key, StringComparison.Ordinal) ? string.Empty : description;
    }
}
