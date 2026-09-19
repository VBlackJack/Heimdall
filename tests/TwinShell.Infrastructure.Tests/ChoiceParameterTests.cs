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

using TwinShell.Core.Enums;
using TwinShell.Core.Interfaces;
using TwinShell.Core.Models;
using TwinShell.Core.Services;

namespace TwinShell.Infrastructure.Tests;

/// <summary>
/// Pins the parameter type that offers a fixed set of values.
/// </summary>
/// <remarks>
/// What makes a choice's value right is membership, not shape, so it is checked before the
/// type switch rather than inside it. The two ways that could go wrong are refusing a value
/// that is offered, and accepting one that is not.
/// </remarks>
public sealed class ChoiceParameterTests
{
    [Fact]
    public void AnOfferedValueIsAccepted()
    {
        var template = ChoiceTemplate("restart", "stop", "start");

        var valid = Generator().ValidateParameters(
            template, Values(("action", "restart")), out var errors);

        Assert.True(valid);
        Assert.Empty(errors);
    }

    [Fact]
    public void AValueThatIsNotOfferedIsRefused()
    {
        var template = ChoiceTemplate("restart", "stop", "start");

        var valid = Generator().ValidateParameters(
            template, Values(("action", "obliterate")), out var errors);

        Assert.False(valid || errors.Count == 0);
        Assert.Contains(errors, error => error.Contains("restart", StringComparison.Ordinal));
    }

    /// <summary>
    /// The offered values were typed by a person into an editor, twice.
    /// </summary>
    [Theory]
    [InlineData("RESTART")]
    [InlineData("  restart  ")]
    public void AnOfferedValueIsMatchedWithoutRegardToCaseOrSpace(string typed)
    {
        var template = ChoiceTemplate("restart", "stop");

        Generator().ValidateParameters(template, Values(("action", typed)), out var errors);

        Assert.Empty(errors);
    }

    /// <summary>
    /// Whether a value may be absent is what Required decides. Two rules answering the
    /// same question is how they end up disagreeing.
    /// </summary>
    [Fact]
    public void AnEmptyValueIsLeftToTheRequiredRule()
    {
        var optional = ChoiceTemplate("restart", "stop");
        optional.Parameters[0].Required = false;

        Generator().ValidateParameters(optional, Values(("action", "")), out var errors);

        Assert.Empty(errors);
    }

    [Fact]
    public void AnEmptyValueStillFailsWhenTheParameterIsRequired()
    {
        var required = ChoiceTemplate("restart", "stop");
        required.Parameters[0].Required = true;

        Generator().ValidateParameters(required, Values(("action", "")), out var errors);

        Assert.NotEmpty(errors);
    }

    /// <summary>
    /// A choice with nothing left to offer behaves like free text rather than refusing
    /// everything: an action that arrived that way is worth less, not worth nothing.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AChoiceWithNothingToOfferAcceptsAnyValue(bool emptyRatherThanNull)
    {
        var template = ChoiceTemplate();
        template.Parameters[0].AllowedValues = emptyRatherThanNull ? [] : null;

        Generator().ValidateParameters(template, Values(("action", "anything")), out var errors);

        Assert.Empty(errors);
    }

    /// <summary>
    /// Generation has to refuse what validation refuses, or the panel reports an error and
    /// then produces the command anyway.
    /// </summary>
    [Fact]
    public void GeneratingWithAValueThatIsNotOfferedThrows()
    {
        var template = ChoiceTemplate("restart", "stop");

        Assert.Throws<InvalidOperationException>(
            () => Generator().GenerateCommand(template, Values(("action", "obliterate"))));
    }

    [Fact]
    public void GeneratingWithAnOfferedValueSubstitutesIt()
    {
        var template = ChoiceTemplate("restart", "stop");

        var command = Generator().GenerateCommand(template, Values(("action", "restart")));

        Assert.Contains("restart", command, StringComparison.Ordinal);
    }

    /// <summary>
    /// IsChoice is the one predicate four places read, so it has to be exact about which
    /// parameters qualify.
    /// </summary>
    [Theory]
    [InlineData("choice", true, true)]
    [InlineData("CHOICE", true, true)]
    [InlineData("choice", false, false)]
    [InlineData("string", true, false)]
    public void IsChoiceNeedsBothTheTypeAndSomethingToOffer(
        string type, bool hasValues, bool expected)
    {
        var parameter = new TemplateParameter
        {
            Name = "action",
            Label = "Action",
            Type = type,
            AllowedValues = hasValues ? ["restart"] : null
        };

        Assert.Equal(expected, parameter.IsChoice);
    }

    private static CommandTemplate ChoiceTemplate(params string[] offered) => new()
    {
        Id = "t",
        Name = "Service control",
        Platform = Platform.Linux,
        CommandPattern = "systemctl {action} nginx",
        Parameters =
        [
            new TemplateParameter
            {
                Name = "action",
                Label = "Action",
                Type = TemplateParameter.ChoiceTypeName,
                AllowedValues = offered.Length == 0 ? null : [.. offered]
            }
        ]
    };

    private static Dictionary<string, string> Values(params (string Name, string Value)[] values)
        => values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    private static ICommandGeneratorService Generator()
        => new CommandGeneratorService(new EchoLocalizationService());

    /// <summary>
    /// Localization stand-in that formats the key with its arguments, so a test can assert
    /// the offered values reached the message without depending on any translation.
    /// </summary>
    private sealed class EchoLocalizationService : ILocalizationService
    {
        public System.Globalization.CultureInfo CurrentCulture
            => System.Globalization.CultureInfo.InvariantCulture;

        public System.Globalization.CultureInfo[] SupportedCultures => [CurrentCulture];

        public string GetString(string key) => key;

        public string GetString(string key, string fallback) => fallback;

        public string GetFormattedString(string key, params object[] args)
            => key + ": " + string.Join(" | ", args);

        public void ChangeLanguage(System.Globalization.CultureInfo culture) { }

        public void ChangeLanguage(string cultureCode) { }

#pragma warning disable CS0067
        public event EventHandler? LanguageChanged;
#pragma warning restore CS0067
    }
}
