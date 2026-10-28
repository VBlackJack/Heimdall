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

using System.Reflection;
using FluentAssertions;
using Heimdall.App.ViewModels.Dialogs;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Opening an action in the editor and saving it must hand back every field of every
/// parameter. The editor rebuilds each parameter from its own view model, so a field the
/// view model does not carry is dropped on save without a word (the quoting mode once was,
/// and the generated command changed with it).
/// </summary>
public sealed class ParameterEntryVmRoundTripTests
{
    /// <summary>
    /// The text properties <see cref="ParameterEntryVm.ToModel"/> trims on purpose. Listed
    /// rather than excluded: they still travel, and the test checks they come back trimmed.
    /// </summary>
    private static readonly HashSet<string> TrimmedOnPurpose = new(StringComparer.Ordinal)
    {
        nameof(TemplateParameter.Name),
        nameof(TemplateParameter.Label),
        nameof(TemplateParameter.DefaultValue),
        nameof(TemplateParameter.Description),
    };

    private const string Padding = "  ";
    private const string SampleValuePrefix = "sample-";
    private static readonly string[] SampleListValues = ["alpha", "beta"];

    private static IEnumerable<PropertyInfo> SettableProperties() =>
        typeof(TemplateParameter)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true } && p.SetMethod is { IsPublic: true });

    /// <summary>A value that differs from the property's default, as the user could have saved it.</summary>
    private static object NonDefaultValue(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        if (type == typeof(string))
        {
            var text = SampleValuePrefix + property.Name;
            return TrimmedOnPurpose.Contains(property.Name) ? Padding + text + Padding : text;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type.IsEnum)
        {
            return Enum.GetValues(type).Cast<object>().Last();
        }

        if (type == typeof(List<string>))
        {
            return SampleListValues.ToList();
        }

        throw new InvalidOperationException(
            $"TemplateParameter.{property.Name} has the type {property.PropertyType.Name}, which this guard "
            + "cannot fill with a non-default value. Teach NonDefaultValue the type and make sure the "
            + "editor view model carries the property.");
    }

    /// <summary>What the property should hold after the round trip, normalisations included.</summary>
    private static object ExpectedValue(PropertyInfo property, object input) =>
        TrimmedOnPurpose.Contains(property.Name) ? ((string)input).Trim() : input;

    [Fact]
    public void Guard_FindsTheSettableProperties()
    {
        var names = SettableProperties().Select(p => p.Name).ToList();

        names.Should().Contain(
            [nameof(TemplateParameter.Name), nameof(TemplateParameter.Quoting), nameof(TemplateParameter.Flag)]);
        names.Should().NotContain(nameof(TemplateParameter.IsChoice), "a computed property has no setter");
        TrimmedOnPurpose.Should().OnlyContain(
            name => names.Contains(name), "a stale entry would hide a renamed property from the trim check");
    }

    [Fact]
    public void FromModelThenToModel_KeepsEverySettablePropertyOfTheParameter()
    {
        var source = new TemplateParameter();
        var inputs = new Dictionary<PropertyInfo, object>();
        foreach (var property in SettableProperties())
        {
            var value = NonDefaultValue(property);
            property.SetValue(source, value);
            inputs[property] = value;
        }

        var blank = new TemplateParameter();
        foreach (var property in inputs.Keys)
        {
            property.GetValue(source).Should().NotBeEquivalentTo(
                property.GetValue(blank),
                $"{property.Name} must be set to a non-default value for this guard to mean anything");
        }

        var roundTripped = ParameterEntryVm.FromModel(source).ToModel();

        foreach (var (property, input) in inputs)
        {
            property.GetValue(roundTripped).Should().BeEquivalentTo(
                ExpectedValue(property, input),
                $"saving an action in the editor must not drop TemplateParameter.{property.Name}");
        }
    }

    [Fact]
    public void FromModelThenToModel_KeepsTheQuotingMode()
    {
        var source = new TemplateParameter { Name = "path", Quoting = QuotingMode.InlineInQuotes };

        var roundTripped = ParameterEntryVm.FromModel(source).ToModel();

        roundTripped.Quoting.Should().Be(QuotingMode.InlineInQuotes);
    }

    [Fact]
    public void FromModelThenToModel_LeavesAnUndeclaredQuotingModeUndeclared()
    {
        var source = new TemplateParameter { Name = "path" };

        var roundTripped = ParameterEntryVm.FromModel(source).ToModel();

        roundTripped.Quoting.Should().BeNull();
    }
}
