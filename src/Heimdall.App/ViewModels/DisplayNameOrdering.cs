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

using System.Globalization;

namespace Heimdall.App.ViewModels;

/// <summary>
/// The one comparer every user-facing name list in the sessions view is ordered by.
/// </summary>
/// <remarks>
/// It compares under the current culture rather than ordinally. Ordinal comparison ranks every
/// character above U+007F after "z", so a folder whose name starts with an accented capital sorted
/// below "Zurich", and two spellings of one word that differ only by an accent landed far apart.
/// One comparer, taken at the moment of sorting, keeps the tree, the folder pickers and the project
/// list in the same order and lets a language change take effect at the next rebuild.
/// </remarks>
public static class DisplayNameOrdering
{
    /// <summary>A case-insensitive comparer for the culture in effect right now.</summary>
    /// <remarks>
    /// Read once per sort and reuse the instance: a comparison delegate that reads this property
    /// on every call would build a comparer per comparison.
    /// </remarks>
    public static StringComparer Comparer =>
        StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
}
