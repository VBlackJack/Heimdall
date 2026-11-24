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
/// Compares what the program of a recipe resolves to now with what it resolved to at import. The
/// seam exists so that a view model can be tested without the file system.
/// </summary>
public interface IRecipeProgramResolver
{
    /// <summary>Resolves the program again and compares the result with the path stored at import.</summary>
    /// <param name="program">The <c>program</c> text of the recipe.</param>
    /// <param name="allowedFolders">The folders the user added in Settings.</param>
    /// <param name="storedPath">The resolved path stored when the recipe was imported.</param>
    RecipeProgramComparison CompareWithStored(string? program, IReadOnlyList<string> allowedFolders, string storedPath);
}
