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

    [Theory]
    [InlineData("8080")]
    [InlineData("/var/log/syslog")]
    [InlineData("id_ed25519")]
    [InlineData("user@host:/srv/data")]
    public void GenerateCommand_SafeValue_ArrivesBareOnBothPlatforms(string value)
    {
        foreach (var platform in new[] { Platform.Linux, Platform.Windows })
        {
            var template = Template(platform, "run {v}", CommandLibraryTestHelpers.RequiredParameter("v", "Value"));

            var command = CreateService().GenerateCommand(template, Values(("v", value)));

            command.Should().Be("run " + value, $"{value} holds nothing {platform} reads specially");
        }
    }

    [Fact]
    public void GenerateCommand_SafeValuesAroundLiteralText_StayOneToken()
    {
        var template = Template(
            Platform.Linux,
            "firewall-cmd --add-port={port}/{protocol} && cat ~/.ssh/{key}.pub",
            CommandLibraryTestHelpers.RequiredParameter("port", "Port"),
            CommandLibraryTestHelpers.RequiredParameter("protocol", "Protocol"),
            CommandLibraryTestHelpers.RequiredParameter("key", "Key"));

        var command = CreateService().GenerateCommand(
            template,
            Values(("port", "8080"), ("protocol", "tcp"), ("key", "id_ed25519")));

        command.Should().Be("firewall-cmd --add-port=8080/tcp && cat ~/.ssh/id_ed25519.pub");
    }

    [Theory]
    [InlineData(Platform.Linux, "a b", "'a b'")]
    [InlineData(Platform.Linux, "x;y", "'x;y'")]
    [InlineData(Platform.Linux, "it's", "'it'\\''s'")]
    [InlineData(Platform.Linux, "$HOME", "'$HOME'")]
    [InlineData(Platform.Windows, "a b", "'a b'")]
    [InlineData(Platform.Windows, "x;y", "'x;y'")]
    [InlineData(Platform.Windows, "it's", "'it''s'")]
    [InlineData(Platform.Windows, "$env:TEMP", "'$env:TEMP'")]
    public void GenerateCommand_ValueTheShellWouldRead_StaysQuoted(Platform platform, string value, string expected)
    {
        var template = Template(platform, "run {v}", CommandLibraryTestHelpers.RequiredParameter("v", "Value"));

        var command = CreateService().GenerateCommand(template, Values(("v", value)));

        command.Should().Be("run " + expected);
    }

    /// <summary>
    /// PowerShell reads a leading dash as a parameter name, a leading at sign as a splat and a
    /// comma as an array separator; bash gives none of them a meaning.
    /// </summary>
    [Theory]
    [InlineData("-Force")]
    [InlineData("@args")]
    [InlineData("a,b")]
    public void GenerateCommand_ValuePowerShellReadsUnquoted_StaysQuotedOnWindowsOnly(string value)
    {
        var service = CreateService();
        var windows = Template(Platform.Windows, "run {v}", CommandLibraryTestHelpers.RequiredParameter("v", "Value"));
        var linux = Template(Platform.Linux, "run {v}", CommandLibraryTestHelpers.RequiredParameter("v", "Value"));

        service.GenerateCommand(windows, Values(("v", value))).Should().Be("run '" + value + "'");
        service.GenerateCommand(linux, Values(("v", value))).Should().Be("run " + value);
    }

    [Fact]
    public void GenerateCommand_InlineInQuotesSafeValue_KeepsThePatternsQuotesOnly()
    {
        var template = Template(
            Platform.Linux,
            "grep '{v}' file",
            new TemplateParameter { Name = "v", Label = "Value", Type = "string", Required = true, Quoting = QuotingMode.InlineInQuotes });

        var command = CreateService().GenerateCommand(template, Values(("v", "8080")));

        command.Should().Be("grep '8080' file");
    }

    /// <summary>
    /// "text" is not a type the generator declares ("string" is): it used to fall through
    /// both switches with no check at all, so a line feed went into the command and
    /// submitted it on the terminal.
    /// </summary>
    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    public void UnknownType_ControlCharacter_IsRefusedOnBothPaths(string value)
    {
        var service = CreateService();
        var template = Template(Platform.Linux, "echo {v}", CommandLibraryTestHelpers.RequiredParameter("v", "Value", "text"));

        service.ValidateParameters(template, Values(("v", value)), out var errors).Should().BeFalse();
        errors.Should().Equal(MessageKeys.ValidationParameterDangerousCharacters);
        var generate = () => service.GenerateCommand(template, Values(("v", value)));
        generate.Should().Throw<InvalidOperationException>()
            .WithMessage(MessageKeys.ValidationParameterDangerousCharacters);
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
