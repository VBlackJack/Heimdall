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
using TwinShell.Core.Constants;
using TwinShell.Core.Enums;
using TwinShell.Core.Models;
using TwinShell.Core.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins how the generator shapes a value on its way into the command: an empty optional
/// value leaves the argument out instead of quoting nothing.
/// </summary>
public sealed class CommandGeneratorValueShapingTests
{
    [Fact]
    public void GenerateCommand_EmptyOptionalValue_LeavesTheArgumentOut()
    {
        var template = Template(Platform.Linux, "hostname {name}", CommandLibraryTestHelpers.OptionalParameter("name", "Name"));

        var command = CreateService().GenerateCommand(template, Values(("name", string.Empty)));

        command.Should().Be("hostname");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GenerateCommand_EmptyOptionalValueBetweenWords_CollapsesTheSpaces(string empty)
    {
        var template = Template(Platform.Windows, "a {x} b", CommandLibraryTestHelpers.OptionalParameter("x", "X"));

        var command = CreateService().GenerateCommand(template, Values(("x", empty)));

        command.Should().Be("a b");
    }

    [Fact]
    public void GenerateCommand_EmptyOptionalValue_LeavesTheAuthorsOwnSpacesAlone()
    {
        var template = Template(
            Platform.Linux,
            "grep 'a  b' {file}",
            CommandLibraryTestHelpers.OptionalParameter("file", "File"));

        var command = CreateService().GenerateCommand(template, Values(("file", string.Empty)));

        command.Should().Be("grep 'a  b'");
    }

    [Fact]
    public void GenerateCommand_EmptyRequiredValue_IsStillAnError()
    {
        var template = Template(Platform.Linux, "ping {host}", CommandLibraryTestHelpers.RequiredParameter("host", "Host"));
        var service = CreateService();

        var generate = () => service.GenerateCommand(template, Values(("host", string.Empty)));

        generate.Should().Throw<InvalidOperationException>()
            .WithMessage(string.Format(CultureInfo.InvariantCulture, MessageKeys.ValidationParameterRequired, "Host"));
        service.ValidateParameters(template, Values(("host", " ")), out var errors).Should().BeFalse();
        errors.Should().ContainSingle();
    }

    internal static CommandGeneratorService CreateService() =>
        new(new FakeTwinShellLocalizationService());

    internal static CommandTemplate Template(Platform platform, string pattern, params TemplateParameter[] parameters) =>
        new()
        {
            Id = "shaping",
            Name = "shaping",
            Platform = platform,
            CommandPattern = pattern,
            Parameters = [.. parameters],
        };

    internal static Dictionary<string, string> Values(params (string Name, string Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
}
