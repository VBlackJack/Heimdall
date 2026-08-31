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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>One TextBox in the settings markup, and the Settings property its Text is bound to.</summary>
internal sealed record SettingsTextBoxBinding(string Property, bool CommitsOnEveryKeystroke);

/// <summary>
/// The settings fields that edit a whole number through a text property, censused from the view
/// model and from the markup rather than listed again here.
/// </summary>
/// <remarks>
/// Two oracles rest on the view-model census - that a load reseeds every text, and that a reset
/// does - so both read the same one. Written out twice, a field could be dropped from one list and
/// kept in the other, and each test would go on reporting success over a different set of fields.
/// </remarks>
internal static class SettingsNumericFields
{
    /// <summary>Every &lt;X&gt; int property that has a matching &lt;X&gt;Text string property.</summary>
    internal static IReadOnlyList<(PropertyInfo Number, PropertyInfo Text)> Pairs()
    {
        Dictionary<string, PropertyInfo> byName = ByName();

        const string suffix = "Text";
        List<(PropertyInfo Number, PropertyInfo Text)> pairs = [];

        foreach (PropertyInfo text in byName.Values)
        {
            if (text.PropertyType != typeof(string)
                || !text.Name.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            if (byName.TryGetValue(text.Name[..^suffix.Length], out PropertyInfo? number)
                && number.PropertyType == typeof(int))
            {
                pairs.Add((number, text));
            }
        }

        return pairs;
    }

    /// <summary>Every public property of the settings view model, by name.</summary>
    internal static Dictionary<string, PropertyInfo> ByName()
    {
        Dictionary<string, PropertyInfo> byName = new(StringComparer.Ordinal);
        foreach (PropertyInfo property in typeof(SettingsViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            byName.TryAdd(property.Name, property);
        }

        return byName;
    }

    /// <summary>
    /// Every editable box in the window whose text is bound to a settings property, read from the
    /// markup.
    /// </summary>
    /// <remarks>
    /// The census has to start here rather than at the view model. A field that was never converted
    /// has no text property, so a census of the view model's number/text pairs cannot contain it,
    /// and the guard that walks those pairs reports success over a set the defect is not in. The
    /// markup, by contrast, holds one entry per box the user can type into, whether or not anyone
    /// remembered to give that box a text property.
    /// </remarks>
    internal static IReadOnlyList<SettingsTextBoxBinding> TextBoxBindings()
    {
        string markup = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "src", "Heimdall.App", "MainWindow.xaml"));

        List<SettingsTextBoxBinding> bindings = [];
        foreach (Match box in Regex.Matches(markup, @"<TextBox\b[^>]*>", RegexOptions.Singleline))
        {
            Match bound = Regex.Match(box.Value, @"Text=""\{Binding\s+Settings\.([A-Za-z0-9_.]+)");
            if (!bound.Success)
            {
                continue;
            }

            string path = bound.Groups[1].Value;
            if (path.Contains('.', StringComparison.Ordinal))
            {
                // A nested view model of its own, with its own fields and its own validation.
                continue;
            }

            bindings.Add(new SettingsTextBoxBinding(
                path,
                box.Value.Contains("UpdateSourceTrigger=PropertyChanged", StringComparison.Ordinal)));
        }

        return bindings;
    }

    internal static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root from test binary directory: {AppContext.BaseDirectory}");
    }
}

/// <summary>
/// Ties every number field on the settings screen to the text property it has to be bound through.
/// </summary>
/// <remarks>
/// <para>A TextBox bound straight to an int is where the defect lives: when the text does not
/// convert, the binding drops it before the setter runs, so no error is recorded, nothing is marked
/// dirty, the banner and the tab badge stay empty, and the save guard has nothing to refuse. The
/// box goes on showing what the user typed while the stored number is still the old one.</para>
/// <para>Nothing else catches a field left behind. Every view-model oracle stays green when one of
/// these bindings is missed, because the text property exists and works - it is simply not what the
/// box on screen is bound to.</para>
/// <para>The direction of the census is the whole point. Walking the view model's number/text pairs
/// asks "were the fields that were converted converted", which four unconverted fields answered
/// yes to while shipping the defect. Walking the markup asks "is every box the user can type a
/// number into bound through a text", which is the claim.</para>
/// </remarks>
public sealed class SettingsNumericFieldBindingTests
{
    [Fact]
    public void EveryNumberSettingIsBoundThroughItsTextProperty()
    {
        IReadOnlyList<SettingsTextBoxBinding> bindings = SettingsNumericFields.TextBoxBindings();
        Dictionary<string, PropertyInfo> byName = SettingsNumericFields.ByName();

        // Guarding the guard: a markup scan that matched nothing would report success having
        // compared nothing at all.
        Assert.True(
            bindings.Count >= 35,
            $"only {bindings.Count} settings text boxes were found in the markup, so the scan is "
                + "no longer reading what it thinks it is");

        HashSet<string> boundThroughText = new(StringComparer.Ordinal);
        List<string> problems = [];

        foreach (SettingsTextBoxBinding binding in bindings)
        {
            if (byName.TryGetValue(binding.Property, out PropertyInfo? bound)
                && bound.PropertyType == typeof(int))
            {
                problems.Add(
                    $"a field is bound straight to Settings.{binding.Property}. A text that does "
                        + "not convert is dropped before the setter runs, so the save is refused "
                        + "by nothing and reported by nothing: add a "
                        + $"{binding.Property}Text property and bind the box to that.");
                continue;
            }

            const string suffix = "Text";
            if (binding.Property.EndsWith(suffix, StringComparison.Ordinal)
                && byName.TryGetValue(binding.Property[..^suffix.Length], out PropertyInfo? number)
                && number.PropertyType == typeof(int))
            {
                boundThroughText.Add(number.Name);
            }
        }

        foreach ((PropertyInfo number, PropertyInfo text) in SettingsNumericFields.Pairs())
        {
            if (!boundThroughText.Contains(number.Name))
            {
                problems.Add(
                    $"{text.Name} exists on the view model but no settings text box binds to it, "
                        + $"so nothing on screen edits {number.Name} through it.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>
    /// Every number box commits on each keystroke rather than when it loses focus.
    /// </summary>
    /// <remarks>
    /// The default LostFocus trigger reopens the defect for one gesture: type into a box, press the
    /// Save shortcut without tabbing away, and the text never reaches the view model, so the save
    /// guard validates the number the field no longer shows and reports nothing.
    /// </remarks>
    [Fact]
    public void EveryNumberFieldCommitsOnEveryKeystroke()
    {
        Dictionary<string, PropertyInfo> byName = SettingsNumericFields.ByName();
        List<SettingsTextBoxBinding> numeric =
        [
            .. SettingsNumericFields.TextBoxBindings()
                .Where(binding =>
                    binding.Property.EndsWith("Text", StringComparison.Ordinal)
                    && byName.TryGetValue(binding.Property[..^"Text".Length], out PropertyInfo? number)
                    && number.PropertyType == typeof(int))
        ];

        Assert.True(
            numeric.Count >= 21,
            $"only {numeric.Count} number fields were found in the markup, so nothing was checked");

        List<string> problems =
        [
            .. numeric
                .Where(binding => !binding.CommitsOnEveryKeystroke)
                .Select(binding =>
                    $"Settings.{binding.Property} is bound without "
                        + "UpdateSourceTrigger=PropertyChanged, so what the user typed does not "
                        + "reach the view model until the box loses focus.")
        ];

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
