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

namespace Heimdall.Core.Recipes;

/// <summary>
/// Resolves the program of a recipe to the absolute file that would run, or says why it may not.
/// The seam exists so that the import can be tested without the file system.
/// </summary>
public interface IRecipeProgramLocator
{
    /// <summary>Resolves a program.</summary>
    /// <param name="program">The <c>program</c> text of the recipe, as written.</param>
    /// <param name="allowedFolders">The folders the user added in Settings; may be empty.</param>
    RecipeProgramVerdict Resolve(string? program, IReadOnlyList<string> allowedFolders);
}
