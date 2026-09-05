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
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// Names sort the way the user reads them, not by code point.
/// </summary>
/// <remarks>
/// Ordinal comparison ranks every character above U+007F after "z", so a folder whose name starts
/// with an accented capital sorted below "Zurich". The comparer is taken from the culture in
/// effect at the time of the sort.
/// </remarks>
public sealed class DisplayNameOrderingTests
{
    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void AccentedInitial_SortsWithItsBaseLetter(string cultureName)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
        try
        {
            string[] names = ["Zurich", "Études", "ecole", "Alpha"];

            string[] sorted = [.. names.OrderBy(name => name, DisplayNameOrdering.Comparer)];

            Assert.Equal(["Alpha", "ecole", "Études", "Zurich"], sorted);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Comparer_IgnoresCase()
    {
        Assert.Equal(0, DisplayNameOrdering.Comparer.Compare("Linux", "LINUX"));
    }
}
