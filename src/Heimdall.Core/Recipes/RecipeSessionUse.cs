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

/// <summary>Whether a recipe reads the host or the port of the selected session.</summary>
/// <param name="UsesHost">True when a token or a <c>whenTrue</c> token holds <c>{Host}</c>.</param>
/// <param name="UsesPort">True when a token or a <c>whenTrue</c> token holds <c>{Port}</c>.</param>
public sealed record RecipeSessionUse(bool UsesHost, bool UsesPort)
{
    /// <summary>True when the recipe needs a session at all.</summary>
    public bool UsesSession => UsesHost || UsesPort;

    /// <summary>Reads the placeholders a validated recipe uses.</summary>
    /// <param name="recipe">A recipe that passed <see cref="RecipeValidator"/>.</param>
    public static RecipeSessionUse Of(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        bool host = false;
        bool port = false;

        foreach (string token in EnumerateTokens(recipe))
        {
            TemplateParse parse = RecipeTemplate.Parse(token);
            foreach (TemplateSegment segment in parse.Segments)
            {
                host |= segment.Kind == TemplateSegmentKind.Host;
                port |= segment.Kind == TemplateSegmentKind.Port;
            }
        }

        return new RecipeSessionUse(host, port);
    }

    private static IEnumerable<string> EnumerateTokens(Recipe recipe)
    {
        foreach (RecipeArgument argument in recipe.Arguments)
        {
            foreach (string token in argument.Tokens)
            {
                yield return token;
            }
        }

        foreach (RecipeInput input in recipe.Inputs)
        {
            foreach (string token in input.WhenTrue ?? [])
            {
                yield return token;
            }
        }
    }
}
