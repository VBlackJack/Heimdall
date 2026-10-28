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
using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.App.ViewModels.Dialogs;
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Models;
using TwinShell.Persistence.Mappers;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

/// <summary>
/// Pins the per-parameter <see cref="TemplateParameter.Flag"/>: an optional parameter left
/// empty drops the flag that introduces it, and only a parameter that declares one does. The
/// switch in <c>fdisk -l {device}</c> belongs to the command, not to the parameter.
/// </summary>
public sealed class CommandGeneratorFlagTests
{
    private const string SetUserPattern =
        "Set-ADUser -Identity {username} -Department {department} -Title {title} -OfficePhone {phone}";

    private static TemplateParameter Flagged(string name, string flag) =>
        new() { Name = name, Label = name, Type = "string", Required = false, Flag = flag };

    private static CommandTemplate SetUserTemplate(bool declareFlags) =>
        CommandGeneratorValueShapingTests.Template(
            Platform.Windows,
            SetUserPattern,
            CommandLibraryTestHelpers.RequiredParameter("username", "Username"),
            declareFlags ? Flagged("department", "-Department") : CommandLibraryTestHelpers.OptionalParameter("department", "Department"),
            declareFlags ? Flagged("title", "-Title") : CommandLibraryTestHelpers.OptionalParameter("title", "Title"),
            declareFlags ? Flagged("phone", "-OfficePhone") : CommandLibraryTestHelpers.OptionalParameter("phone", "Phone"));

    [Fact]
    public void EmptyOptionalValue_WithADeclaredFlag_DropsTheFlagToo()
    {
        var command = CommandGeneratorValueShapingTests.CreateService().GenerateCommand(
            SetUserTemplate(declareFlags: true),
            CommandGeneratorValueShapingTests.Values(
                ("username", "jdoe"), ("department", "IT"), ("title", string.Empty), ("phone", string.Empty)));

        command.Should().Be("Set-ADUser -Identity jdoe -Department IT");
    }

    [Fact]
    public void EmptyOptionalValueInTheMiddle_WithADeclaredFlag_KeepsTheRestOfTheCommandSeparated()
    {
        var command = CommandGeneratorValueShapingTests.CreateService().GenerateCommand(
            SetUserTemplate(declareFlags: true),
            CommandGeneratorValueShapingTests.Values(
                ("username", "jdoe"), ("department", string.Empty), ("title", "Admin"), ("phone", string.Empty)));

        command.Should().Be("Set-ADUser -Identity jdoe -Title Admin");
    }

    [Fact]
    public void EveryOptionalValueEmpty_WithDeclaredFlags_LeavesOnlyTheRequiredArguments()
    {
        var command = CommandGeneratorValueShapingTests.CreateService().GenerateCommand(
            SetUserTemplate(declareFlags: true),
            CommandGeneratorValueShapingTests.Values(
                ("username", "jdoe"), ("department", string.Empty), ("title", " "), ("phone", string.Empty)));

        command.Should().Be("Set-ADUser -Identity jdoe");
    }

    [Fact]
    public void EmptyOptionalValue_WithNoDeclaredFlag_KeepsTheTokenBeforeIt()
    {
        var command = CommandGeneratorValueShapingTests.CreateService().GenerateCommand(
            SetUserTemplate(declareFlags: false),
            CommandGeneratorValueShapingTests.Values(
                ("username", "jdoe"), ("department", "IT"), ("title", string.Empty), ("phone", string.Empty)));

        command.Should().Be("Set-ADUser -Identity jdoe -Department IT -Title -OfficePhone");
    }

    [Fact]
    public void FilledOptionalValue_WithADeclaredFlag_KeepsTheFlag()
    {
        var command = CommandGeneratorValueShapingTests.CreateService().GenerateCommand(
            SetUserTemplate(declareFlags: true),
            CommandGeneratorValueShapingTests.Values(
                ("username", "jdoe"), ("department", "IT"), ("title", "Admin"), ("phone", "555")));

        command.Should().Be("Set-ADUser -Identity jdoe -Department IT -Title Admin -OfficePhone 555");
    }

    /// <summary>
    /// A switch that stands on its own before a positional argument is not the argument's
    /// flag: with no <see cref="TemplateParameter.Flag"/> declared the generator never guesses
    /// that it is.
    /// </summary>
    [Theory]
    [InlineData("sudo fdisk -l {device}", "device", "sudo fdisk -l")]
    [InlineData("du -sh {path}", "path", "du -sh")]
    public void EmptyOptionalValue_AfterASwitchOfTheCommand_KeepsTheSwitch(string pattern, string parameter, string expected)
    {
        var template = CommandGeneratorValueShapingTests.Template(
            Platform.Linux,
            pattern,
            CommandLibraryTestHelpers.OptionalParameter(parameter, parameter));

        CommandGeneratorValueShapingTests.CreateService()
            .GenerateCommand(template, CommandGeneratorValueShapingTests.Values((parameter, string.Empty)))
            .Should().Be(expected);
    }

    /// <summary>
    /// The pattern is the author's: a declared flag that is not the token written before the
    /// placeholder removes nothing, and neither does one glued to the text before it.
    /// </summary>
    [Theory]
    [InlineData("run -Other {v}", "-Title", "run -Other")]
    [InlineData("run x-Title {v}", "-Title", "run x-Title")]
    [InlineData("run -Title -Other {v}", "-Title", "run -Title -Other")]
    [InlineData("run -Title={v}", "-Title", "run -Title=")]
    public void DeclaredFlag_NotExactlyTheTokenBeforeThePlaceholder_RemovesNothing(string pattern, string flag, string expected)
    {
        var template = CommandGeneratorValueShapingTests.Template(Platform.Linux, pattern, Flagged("v", flag));

        CommandGeneratorValueShapingTests.CreateService()
            .GenerateCommand(template, CommandGeneratorValueShapingTests.Values(("v", string.Empty)))
            .Should().Be(expected);
    }

    [Fact]
    public void DeclaredFlag_FollowingAnInsertedValue_IsNotMistakenForTheFlag()
    {
        var template = CommandGeneratorValueShapingTests.Template(
            Platform.Linux,
            "echo {a}{b}",
            CommandLibraryTestHelpers.RequiredParameter("a", "A"),
            Flagged("b", "-x"));

        CommandGeneratorValueShapingTests.CreateService()
            .GenerateCommand(template, CommandGeneratorValueShapingTests.Values(("a", "-x"), ("b", string.Empty)))
            .Should().Be("echo -x");
    }

    [Fact]
    public void FlagAtTheStartOfThePattern_IsDropped()
    {
        var template = CommandGeneratorValueShapingTests.Template(
            Platform.Linux,
            "-v {v} run",
            Flagged("v", "-v"));

        CommandGeneratorValueShapingTests.CreateService()
            .GenerateCommand(template, CommandGeneratorValueShapingTests.Values(("v", string.Empty)))
            .Should().Be("run");
    }

    // The shipped catalogue, through the real seed loader, so the metadata the files carry is
    // what the generator reads.

    [Fact]
    public async Task ShippedSetUserProperties_EmptyTitle_DropsTheTitleFlag()
    {
        var template = await SeedTemplateAsync("ad-set-user-properties", windows: true);

        var command = CommandGeneratorValueShapingTests.CreateService().GenerateCommand(
            template,
            CommandGeneratorValueShapingTests.Values(
                ("username", "jdoe"), ("department", "IT"), ("title", string.Empty), ("phone", string.Empty), ("email", string.Empty)));

        command.Should().NotContain("-Title").And.NotContain("-OfficePhone").And.NotContain("-EmailAddress");
        command.Should().Contain("-Department IT");
    }

    [Theory]
    [InlineData("fdisk-partition", "sudo fdisk -l")]
    [InlineData("file-directory-size-linux", "du -sh")]
    public async Task ShippedSwitchBeforePositional_EmptyValue_KeepsTheSwitch(string seed, string expected)
    {
        var template = await SeedTemplateAsync(seed, windows: false);
        var parameter = template.Parameters.Single(p => !p.Required);

        parameter.Flag.Should().BeNull($"{seed} writes a switch of the command before the placeholder");
        CommandGeneratorValueShapingTests.CreateService()
            .GenerateCommand(template, CommandGeneratorValueShapingTests.Values((parameter.Name, string.Empty)))
            .Should().Be(expected);
    }

    /// <summary>
    /// Every flag a shipped file declares is exactly the token before its placeholder, on an
    /// optional parameter, and leaving the value empty removes that token and nothing else.
    /// A mistyped flag would otherwise sit in the catalogue doing nothing.
    /// </summary>
    [Fact]
    public async Task EveryShippedFlag_IsTheTokenBeforeItsPlaceholder_AndDropsOnlyThatToken()
    {
        var service = CommandGeneratorValueShapingTests.CreateService();
        var flagged = 0;

        foreach (var action in await ReadShippedActionsAsync())
        {
            foreach (var template in new[] { action.WindowsCommandTemplate, action.LinuxCommandTemplate })
            {
                foreach (var parameter in template?.Parameters.Where(p => !string.IsNullOrEmpty(p.Flag)) ?? [])
                {
                    flagged++;
                    var placeholder = CommandPatternQuoting.Placeholder(parameter.Name);
                    var where = $"{action.Id} / {template!.Platform} / {parameter.Name}";

                    parameter.Required.Should().BeFalse($"{where}: only an optional parameter can be left empty");
                    template.CommandPattern.Split(placeholder).Length.Should().Be(2, $"{where}: one occurrence");
                    var before = template.CommandPattern[..template.CommandPattern.IndexOf(placeholder, StringComparison.Ordinal)]
                        .TrimEnd(' ');
                    before.Should().EndWith(parameter.Flag!, $"{where}: the flag is the token before the placeholder");
                    (before.Length == parameter.Flag!.Length || before[^(parameter.Flag.Length + 1)] == ' ')
                        .Should().BeTrue($"{where}: the flag stands as a token of its own");

                    var values = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var other in template.Parameters)
                    {
                        values[other.Name] = other == parameter
                            ? string.Empty
                            : other.IsChoice ? other.AllowedValues![0] : SafeValue(other);
                    }

                    var command = service.GenerateCommand(template, values);
                    CountTokens(command, parameter.Flag).Should().Be(
                        CountTokens(template.CommandPattern, parameter.Flag) - 1,
                        $"{where}: the empty value takes its own flag with it, once");
                }
            }
        }

        flagged.Should().BeGreaterThan(20, "the catalogue declares flags on its named-value parameters");
    }

    [Fact]
    public void FlagSurvivesTheStoredFormOfATemplate()
    {
        var template = CommandGeneratorValueShapingTests.Template(Platform.Windows, SetUserPattern, Flagged("title", "-Title"));

        var stored = CommandTemplateMapper.ToModel(CommandTemplateMapper.ToEntity(template));

        stored.Parameters.Single().Flag.Should().Be("-Title");
    }

    /// <summary>
    /// The editor shows no field for the flag; saving an action it declared must not lose it,
    /// or a duplicated shipped action would regain the dangling flag on its first edit.
    /// </summary>
    [Fact]
    public void FlagSurvivesTheActionEditor()
    {
        var parameter = Flagged("title", "-Title");

        var edited = ParameterEntryVm.FromModel(parameter).ToModel();

        edited.Flag.Should().Be("-Title");
    }

    private static int CountTokens(string command, string token) =>
        command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(part => string.Equals(part, token, StringComparison.Ordinal));

    private static string SafeValue(TemplateParameter parameter) => parameter.Type.ToLowerInvariant() switch
    {
        "int" or "integer" or "number" => "1",
        "hostname" => "srv01",
        "ipaddress" => "10.0.0.1",
        "driveletter" => "C",
        "bool" or "boolean" => "true",
        _ => string.IsNullOrWhiteSpace(parameter.DefaultValue) ? "value" : parameter.DefaultValue,
    };

    private static async Task<List<ActionModel>> ReadShippedActionsAsync()
    {
        var files = Directory.GetFiles(CommandGeneratorServiceTests.FindSeedActionsDirectory(), "*.json")
            .Where(file => !Path.GetFileName(file).StartsWith('_'))
            .ToArray();

        return await TwinShellBootstrapper.ReadSeedActionsAsync(files);
    }

    private static async Task<CommandTemplate> SeedTemplateAsync(string seedId, bool windows)
    {
        var action = (await ReadShippedActionsAsync()).Single(a => a.Id == seedId);
        return (windows ? action.WindowsCommandTemplate : action.LinuxCommandTemplate)
            ?? throw new InvalidOperationException($"{seedId} has no template for the requested platform.");
    }
}
