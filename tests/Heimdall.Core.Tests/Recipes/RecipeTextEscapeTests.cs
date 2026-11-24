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

using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>Showing text from a recipe whole, with whatever could hide something made visible.</summary>
public sealed class RecipeTextEscapeTests
{
    [Fact]
    public void AnOrdinaryTextIsUnchanged()
    {
        Assert.Equal("Ping {Host} -n 4", RecipeText.Escape("Ping {Host} -n 4", escapeNonAscii: true));
        Assert.Equal(string.Empty, RecipeText.Escape(null, escapeNonAscii: true));
        Assert.Equal(string.Empty, RecipeText.Escape(string.Empty, escapeNonAscii: false));
    }

    [Fact]
    public void AControlAndAnOverrideCharacterAreEscapedEvenWhenAccentsAreKept()
    {
        string text = "a\u0007b\u202Ec";

        string escaped = RecipeText.Escape(text, escapeNonAscii: false);

        Assert.Equal("a\\u0007b\\u202Ec", escaped);
    }

    [Fact]
    public void AnAccentIsKeptInANameAndEscapedInAProgramOrAnArgument()
    {
        Assert.Equal("caf\u00e9", RecipeText.Escape("caf\u00e9", escapeNonAscii: false));
        Assert.Equal("caf\\u00E9", RecipeText.Escape("caf\u00e9", escapeNonAscii: true));
    }

    [Fact]
    public void ALookAlikeLetterCannotPassForTheAsciiOneInAnArgument()
    {
        string cyrillicA = "\u0430";

        Assert.Equal("ping.exe", RecipeText.Escape("ping.exe", escapeNonAscii: true));
        Assert.NotEqual("ping.exe", RecipeText.Escape("ping.exe".Replace("p", cyrillicA, StringComparison.Ordinal), escapeNonAscii: true));
        Assert.Contains("\\u0430", RecipeText.Escape(cyrillicA, escapeNonAscii: true), StringComparison.Ordinal);
    }

    [Fact]
    public void ALongTextIsNeverCut()
    {
        string text = new('x', RecipeLimits.MaxFragmentLength * 10);

        Assert.Equal(text, RecipeText.Escape(text, escapeNonAscii: true));
    }
}
