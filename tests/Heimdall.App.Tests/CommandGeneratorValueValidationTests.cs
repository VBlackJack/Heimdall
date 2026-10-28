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
using FluentAssertions;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the shape a hostname, an integer, a number or a drive letter must have before it is
/// put into the command bare, and that the type name is read without the operator's culture.
/// </summary>
public sealed class CommandGeneratorValueValidationTests
{
    private const string DoesNotMatterLabel = "Value";

    /// <summary>
    /// A line feed or a space around the value is not part of a hostname, an integer or a
    /// number, and these types are substituted without quotes: the line feed would submit
    /// the command on the terminal. The regex <c>$</c> and the lenient <c>TryParse</c>
    /// styles used to let them through; both validation paths must refuse them.
    /// </summary>
    [Theory]
    [InlineData(TemplateParameterTypes.Hostname, "host\n")]
    [InlineData(TemplateParameterTypes.Hostname, "host.example.com\n")]
    [InlineData(TemplateParameterTypes.DriveLetter, "C\n")]
    [InlineData(TemplateParameterTypes.Int, "5\n")]
    [InlineData(TemplateParameterTypes.Int, " 5")]
    [InlineData(TemplateParameterTypes.Int, "5 ")]
    [InlineData(TemplateParameterTypes.Integer, "5\n")]
    [InlineData(TemplateParameterTypes.Number, "1.5 ")]
    [InlineData(TemplateParameterTypes.Number, " 1.5")]
    [InlineData(TemplateParameterTypes.Number, "1.5\n")]
    public void BareType_WithWhiteSpaceAroundTheValue_IsRefusedOnBothPaths(string type, string value)
    {
        var service = CommandGeneratorValueShapingTests.CreateService();
        var template = CommandGeneratorValueShapingTests.Template(
            Platform.Linux,
            "run {v}",
            CommandLibraryTestHelpers.RequiredParameter("v", DoesNotMatterLabel, type));
        var values = CommandGeneratorValueShapingTests.Values(("v", value));

        service.ValidateParameters(template, values, out var errors).Should().BeFalse(
            $"{type} '{value}' must be refused by the validation path the dialog uses");
        errors.Should().ContainSingle();

        var generate = () => service.GenerateCommand(template, values);
        generate.Should().Throw<InvalidOperationException>(
            $"{type} '{value}' must be refused by the validation path the generator uses");
    }

    [Theory]
    [InlineData(TemplateParameterTypes.Hostname, "host")]
    [InlineData(TemplateParameterTypes.Hostname, "host.example.com")]
    [InlineData(TemplateParameterTypes.DriveLetter, "C")]
    [InlineData(TemplateParameterTypes.Int, "5")]
    [InlineData(TemplateParameterTypes.Int, "-5")]
    [InlineData(TemplateParameterTypes.Int, "+5")]
    [InlineData(TemplateParameterTypes.Number, "1.5")]
    [InlineData(TemplateParameterTypes.Number, "-1.5")]
    [InlineData(TemplateParameterTypes.Number, "1e5")]
    [InlineData(TemplateParameterTypes.Number, "7")]
    public void BareType_WithAWellFormedValue_StillGoesInBare(string type, string value)
    {
        var service = CommandGeneratorValueShapingTests.CreateService();
        var template = CommandGeneratorValueShapingTests.Template(
            Platform.Linux,
            "run {v}",
            CommandLibraryTestHelpers.RequiredParameter("v", DoesNotMatterLabel, type));
        var values = CommandGeneratorValueShapingTests.Values(("v", value));

        service.ValidateParameters(template, values, out _).Should().BeTrue();
        service.GenerateCommand(template, values).Should().Be("run " + value);
    }

    /// <summary>
    /// The operator's culture decides how "I" is lower-cased: in Turkish "IpAddress" became
    /// "ıpaddress", matched no type, and the value was neither validated as an address nor
    /// left bare. The type name is a protocol token, not text.
    /// </summary>
    [Theory]
    [InlineData("IpAddress", "10.0.0.1", "ping 10.0.0.1")]
    [InlineData("Int", "42", "ping 42")]
    [InlineData("Integer", "42", "ping 42")]
    public void TypeNameWithAnUppercaseI_UnderTurkishCulture_IsValidatedAndNotQuoted(string type, string value, string expected)
    {
        WithCulture("tr-TR", () =>
        {
            var template = CommandGeneratorValueShapingTests.Template(
                Platform.Linux,
                "ping {v}",
                CommandLibraryTestHelpers.RequiredParameter("v", DoesNotMatterLabel, type));

            CommandGeneratorValueShapingTests.CreateService()
                .GenerateCommand(template, CommandGeneratorValueShapingTests.Values(("v", value)))
                .Should().Be(expected);
        });
    }

    /// <summary>
    /// The escaper reads the type name too. A safe value is bare whatever the type, so the
    /// case that tells the two apart is one PowerShell would read as a parameter name: a
    /// negative integer stays bare only because the type is known to be one.
    /// </summary>
    [Theory]
    [InlineData("Int")]
    [InlineData("Integer")]
    public void NegativeIntegerOnWindows_UnderTurkishCulture_IsNotQuoted(string type)
    {
        WithCulture("tr-TR", () =>
        {
            var template = CommandGeneratorValueShapingTests.Template(
                Platform.Windows,
                "Start-Sleep -Seconds {v}",
                CommandLibraryTestHelpers.RequiredParameter("v", DoesNotMatterLabel, type));

            CommandGeneratorValueShapingTests.CreateService()
                .GenerateCommand(template, CommandGeneratorValueShapingTests.Values(("v", "-5")))
                .Should().Be("Start-Sleep -Seconds -5");
        });
    }

    [Theory]
    [InlineData("IpAddress", "not an address")]
    [InlineData("Int", "abc")]
    [InlineData("Integer", "abc")]
    public void TypeNameWithAnUppercaseI_UnderTurkishCulture_RefusesAValueOfTheWrongShape(string type, string value)
    {
        WithCulture("tr-TR", () =>
        {
            var template = CommandGeneratorValueShapingTests.Template(
                Platform.Linux,
                "ping {v}",
                CommandLibraryTestHelpers.RequiredParameter("v", DoesNotMatterLabel, type));
            var service = CommandGeneratorValueShapingTests.CreateService();
            var values = CommandGeneratorValueShapingTests.Values(("v", value));

            var generate = () => service.GenerateCommand(template, values);

            generate.Should().Throw<InvalidOperationException>();
            service.ValidateParameters(template, values, out _).Should().BeFalse();
        });
    }

    private static void WithCulture(string cultureName, System.Action action)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
