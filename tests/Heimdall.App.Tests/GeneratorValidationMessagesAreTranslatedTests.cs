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

using System.IO;
using System.Reflection;
using System.Text.Json;
using Heimdall.App.Tests.Views.EmbeddedRdp;
using TwinShell.Core.Constants;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins that the command generator's parameter-validation messages reach the operator as
/// sentences.
/// </summary>
/// <remarks>
/// <para>
/// <b>They did not.</b> The bridge from TwinShell's <c>ILocalizationService</c> to Heimdall's
/// localizer returns the key itself when it is missing, and none of these keys existed in any
/// locale file. So a missing required parameter produced the literal text
/// <c>Validation.ParameterRequired</c> in the Command Library's error panel, and had done since
/// the tool shipped. Nothing failed, because a key is a perfectly good string.
/// </para>
/// <para>
/// That is what makes this worth a guard rather than a fix alone: the failure mode is a message
/// that looks like a message.
/// </para>
/// <para>
/// The set is read off <see cref="MessageKeys"/> rather than written out here, so adding a tenth
/// message fails this file until it is translated. What that does not cover is a generator
/// message named outside the <c>Validation.Parameter</c> namespace; the namespace is the boundary
/// this guard claims, and <c>CommandGeneratorService</c> is its only producer.
/// </para>
/// </remarks>
public sealed class GeneratorValidationMessagesAreTranslatedTests
{
    private const string ParameterMessagePrefix = "Validation.Parameter";

    private static readonly string[] Locales = ["en", "fr", "es"];

    /// <summary>
    /// Every parameter-validation key <see cref="MessageKeys"/> declares, each value once:
    /// several of them are declared twice under a long name and a short alias.
    /// </summary>
    private static IReadOnlyList<string> ParameterMessageKeys { get; } =
        typeof(MessageKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string?)field.GetRawConstantValue())
            .Where(value => value is not null && value.StartsWith(ParameterMessagePrefix, StringComparison.Ordinal))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The three tests below all iterate the set, so a set that came back empty would leave
    /// every one of them green over nothing.
    /// </summary>
    /// <remarks>
    /// The floor is one-sided on purpose: the failure this guards against is the reflection
    /// enumerating less than it should, and nine is what the generator produced on the day the
    /// missing translations were found. Enumerating more is the tenth message arriving, which is
    /// the case the rest of the file is here to catch.
    /// </remarks>
    [Fact]
    public void TheParameterMessagesAreFoundByReflection()
    {
        Assert.True(
            ParameterMessageKeys.Count >= 9,
            $"Expected at least the nine known parameter messages, found {ParameterMessageKeys.Count}: "
                + string.Join(", ", ParameterMessageKeys));
    }

    [Fact]
    public void EveryGeneratorMessageIsTranslatedInEveryLocale()
    {
        var missing = new List<string>();

        foreach (var locale in Locales)
        {
            var strings = LoadLocale(locale);
            foreach (var key in ParameterMessageKeys)
            {
                if (!strings.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
                {
                    missing.Add($"{locale}: {key}");
                }
            }
        }

        Assert.Empty(missing);
    }

    /// <summary>
    /// A translation that is just the key back again would satisfy the test above while showing
    /// the operator exactly what it showed before.
    /// </summary>
    [Fact]
    public void NoTranslationIsJustTheKeyRepeated()
    {
        foreach (var locale in Locales)
        {
            var strings = LoadLocale(locale);
            foreach (var key in ParameterMessageKeys)
            {
                Assert.NotEqual(key, strings[key]);
            }
        }
    }

    /// <summary>
    /// Every one of these messages names the parameter it is about, so every one of them has to
    /// carry the placeholder that puts the name in.
    /// </summary>
    [Fact]
    public void EveryTranslationCarriesItsParameterPlaceholder()
    {
        foreach (var locale in Locales)
        {
            var strings = LoadLocale(locale);
            foreach (var key in ParameterMessageKeys)
            {
                Assert.Contains("{0}", strings[key], StringComparison.Ordinal);
            }
        }
    }

    private static Dictionary<string, string> LoadLocale(string locale)
    {
        var path = Path.Combine(ViewSource.RepoRoot(), "locales", locale + ".json");
        Assert.True(File.Exists(path), $"Locale file not found: {path}");

        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"Locale {locale} did not parse.");
    }
}
