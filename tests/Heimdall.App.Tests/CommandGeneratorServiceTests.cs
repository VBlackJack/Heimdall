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
using System.Text.Json;
using FluentAssertions;
using TwinShell.Core.Constants;
using TwinShell.Core.Enums;
using TwinShell.Core.Helpers;
using TwinShell.Core.Models;
using TwinShell.Core.Services;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.Tests;

public sealed class CommandGeneratorServiceTests
{
    // Regression for seed defect D2 (Lot A): seed patterns used to double-quote the
    // placeholder (e.g. -Identity "{groupName}"), so the generator wrapped the already
    // single-quoted value into a literal "'...'". After Lot A unwrapped the seed patterns,
    // the producer must emit a single-quoted value with no surrounding double quotes.
    // Producer: CommandGeneratorService.GenerateCommand (CommandGeneratorService.cs:29)
    //           -> QuoteForShell (CommandGeneratorService.cs:332).
    [Fact]
    public void GenerateCommand_WindowsStringParameters_SingleQuotesWithoutDoubleWrap()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = new CommandTemplate
        {
            Id = "ad-add-user-to-group-cmd",
            Name = "Add-ADGroupMember",
            Platform = Platform.Windows,
            CommandPattern = "Add-ADGroupMember -Identity {groupName} -Members {username}",
            Parameters =
            [
                CommandLibraryTestHelpers.RequiredParameter("groupName", "Group name"),
                CommandLibraryTestHelpers.RequiredParameter("username", "User name")
            ]
        };
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["groupName"] = "Domain Admins",
            ["username"] = "jdupont"
        };

        string command = service.GenerateCommand(template, values);

        // F-12: a value that reads the same without quotes arrives bare.
        command.Should().Be("Add-ADGroupMember -Identity 'Domain Admins' -Members jdupont");
        command.Should().NotContain("\"'", "the generator must not double-wrap the single-quoted value (D2 Lot A)");
    }

    // Bash counterpart of the same contract, using the corrected seed pattern
    // git-commit.json (linuxCommandTemplate: "git commit -m {message}").
    [Fact]
    public void GenerateCommand_LinuxStringParameter_SingleQuotesWithoutDoubleWrap()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = new CommandTemplate
        {
            Id = "git-commit-linux",
            Name = "git commit",
            Platform = Platform.Linux,
            CommandPattern = "git commit -m {message}",
            Parameters = [CommandLibraryTestHelpers.RequiredParameter("message", "Message")]
        };
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["message"] = "initial commit"
        };

        string command = service.GenerateCommand(template, values);

        command.Should().Be("git commit -m 'initial commit'");
        command.Should().NotContain("\"'", "the generator must not double-wrap the single-quoted value (D2 Lot A)");
    }

    [Fact]
    public void GenerateCommand_NoParameters_PatternWithinLimit_ReturnsPattern()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = CreateTemplate("short", "Short", "uptime");
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

        string command = service.GenerateCommand(template, values);

        Assert.Equal("uptime", command);
    }

    [Fact]
    public void GenerateCommand_NoParameters_PatternExceedsMaxLength_Throws()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = CreateTemplate("long-pattern", "Long pattern", new string('a', 1100));
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));

        Assert.Contains(ValidationConstants.MaxCommandLength.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCommand_GeneratedResultExceedsMaxLength_Throws()
    {
        CommandGeneratorService service = CreateService();
        TemplateParameter parameter = CommandLibraryTestHelpers.RequiredParameter("val", "Value");
        string pattern = new string('a', 800) + "{val}" + new string('b', 100);
        CommandTemplate template = CreateTemplate("expanded-command", "Expanded command", pattern, parameter);
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["val"] = new string('c', ValidationConstants.MaxParameterLength)
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));

        Assert.Contains(ValidationConstants.MaxCommandLength.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCommand_TooManyParameters_Throws()
    {
        CommandGeneratorService service = CreateService();
        TemplateParameter[] parameters = CreateParameters(ValidationConstants.MaxParametersPerTemplate + 1);
        CommandTemplate template = CreateTemplate("too-many", "Too many", "echo ok", parameters);
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));

        Assert.Contains(ValidationConstants.MaxParametersPerTemplate.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateCommand_ParameterCountAtLimit_Succeeds()
    {
        CommandGeneratorService service = CreateService();
        TemplateParameter[] parameters = CreateParameters(ValidationConstants.MaxParametersPerTemplate);
        CommandTemplate template = CreateTemplate("at-limit", "At limit", "echo ok", parameters);
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

        string command = service.GenerateCommand(template, values);

        Assert.Equal("echo ok", command);
    }

    [Fact]
    public void ValidateParameters_TooManyParameters_ReturnsFalseWithCountError()
    {
        CommandGeneratorService service = CreateService();
        TemplateParameter[] parameters = CreateParameters(ValidationConstants.MaxParametersPerTemplate + 1);
        CommandTemplate template = CreateTemplate("too-many-validation", "Too many validation", "echo ok", parameters);
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);

        bool isValid = service.ValidateParameters(template, values, out List<string> errors);

        Assert.False(isValid);
        Assert.Contains(errors, error => error.Contains(ValidationConstants.MaxParametersPerTemplate.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateParameters_WithinBounds_ReturnsTrue()
    {
        CommandGeneratorService service = CreateService();
        TemplateParameter parameter = CommandLibraryTestHelpers.RequiredParameter("name", "Name");
        CommandTemplate template = CreateTemplate("valid", "Valid", "echo {name}", parameter);
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = "alpha"
        };

        bool isValid = service.ValidateParameters(template, values, out List<string> errors);

        Assert.True(isValid);
        Assert.Empty(errors);
    }

    // ===== TEST-01: shell escaping / quoting =====

    // Windows apostrophe escaping: PowerShell doubles the single quote ('' ), and the value
    // is never double-wrapped (ties back to D2: no "'...'" output).
    [Fact]
    public void GenerateCommand_WindowsStringWithApostrophe_DoublesQuoteWithoutDoubleWrap()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "Get-ADUser -Identity {name}",
            CommandLibraryTestHelpers.RequiredParameter("name", "Name"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["name"] = "O'Brien" };

        string command = service.GenerateCommand(template, values);

        command.Should().Be("Get-ADUser -Identity 'O''Brien'");
        command.Should().NotContain("\"'", "the generator must not double-wrap the single-quoted value (D2)");
    }

    // Linux apostrophe escaping: close-quote, escaped literal quote, reopen ('\'').
    [Fact]
    public void GenerateCommand_LinuxStringWithApostrophe_UsesCloseEscapeReopen()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = LinuxTemplate(
            "echo {msg}",
            CommandLibraryTestHelpers.RequiredParameter("msg", "Message"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["msg"] = "it's" };

        string command = service.GenerateCommand(template, values);

        command.Should().Be("echo 'it'\\''s'");
    }

    // Whitelisted types (hostname/ipaddress/int) are substituted bare (no surrounding quotes).
    [Theory]
    [InlineData("hostname", "srv01", "-ComputerName srv01")]
    [InlineData("ipaddress", "10.0.0.1", "-ComputerName 10.0.0.1")]
    [InlineData("int", "42", "-ComputerName 42")]
    [InlineData("integer", "42", "-ComputerName 42")]
    public void GenerateCommand_WhitelistedType_SubstitutesValueWithoutQuotes(
        string type,
        string value,
        string expected)
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "-ComputerName {h}",
            CommandLibraryTestHelpers.RequiredParameter("h", "Host", type));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["h"] = value };

        string command = service.GenerateCommand(template, values);

        command.Should().Be(expected);
    }

    // A-03: a ShellQuote value is wrapped in single quotes, where both shells read every
    // metacharacter literally, so the blacklist no longer refuses them. They have to arrive
    // intact inside the quotes, on both platforms and through both validation paths.
    [Theory]
    [InlineData("a&b")]
    [InlineData("a|b")]
    [InlineData("a;b")]
    [InlineData("a`b")]
    [InlineData("a$b")]
    [InlineData("a(b")]
    [InlineData("a)b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("(objectClass=user)")]
    [InlineData("error|warn")]
    [InlineData("$env:TEMP")]
    [InlineData("-Confirm:$false")]
    public void GenerateCommand_ShellQuoteStringWithShellCharacters_ArrivesLiterallyInsideQuotes(string value)
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate windows = WindowsTemplate(
            "Write-Output {v}",
            CommandLibraryTestHelpers.RequiredParameter("v", "Value"));
        CommandTemplate linux = LinuxTemplate(
            "printf %s {v}",
            CommandLibraryTestHelpers.RequiredParameter("v", "Value"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["v"] = value };

        service.ValidateParameters(windows, values, out List<string> windowsErrors);
        service.ValidateParameters(linux, values, out List<string> linuxErrors);

        windowsErrors.Should().BeEmpty();
        linuxErrors.Should().BeEmpty();
        service.GenerateCommand(windows, values).Should().Be("Write-Output '" + value + "'");
        service.GenerateCommand(linux, values).Should().Be("printf %s '" + value + "'");
    }

    // A-03: with the blacklist gone for ShellQuote, the quote escaping is the only thing that
    // keeps a value inside its quotes. An attempt to close the quote has to stay data.
    [Fact]
    public void GenerateCommand_ShellQuoteStringClosingTheQuote_StaysInsideTheQuotes()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate windows = WindowsTemplate(
            "Write-Output {v}",
            CommandLibraryTestHelpers.RequiredParameter("v", "Value"));
        CommandTemplate linux = LinuxTemplate(
            "printf %s {v}",
            CommandLibraryTestHelpers.RequiredParameter("v", "Value"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["v"] = "x'; rm -rf /" };

        service.GenerateCommand(linux, values).Should().Be("printf %s 'x'\\''; rm -rf /'");
        service.GenerateCommand(windows, values).Should().Be("Write-Output 'x''; rm -rf /'");
    }

    // A-03: an InlineInQuotes parameter whose placeholder is not inside a single-quoted span
    // only gets its quotes escaped, so it lands bare and the blacklist still applies.
    [Theory]
    [InlineData("Windows")]
    [InlineData("Linux")]
    public void ValidateParameters_InlineInQuotesOutsideSingleQuotes_StillRefusesSemicolon(string platform)
    {
        CommandGeneratorService service = CreateService();
        TemplateParameter parameter = CommandLibraryTestHelpers.RequiredParameter("v", "Value");
        parameter.Quoting = QuotingMode.InlineInQuotes;
        CommandTemplate template = platform == "Windows"
            ? WindowsTemplate("Write-Output {v}", parameter)
            : LinuxTemplate("echo {v}", parameter);
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["v"] = "a; whoami" };

        service.ValidateParameters(template, values, out List<string> errors);

        errors.Should().Equal(MessageKeys.ValidationParameterDangerousCharacters);
        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));
    }

    // A-03: a correctly placed InlineInQuotes parameter is literal, so an LDAP filter passes.
    [Fact]
    public void GenerateCommand_InlineInQuotesInsideSingleQuotes_AcceptsAnLdapFilter()
    {
        CommandGeneratorService service = CreateService();
        TemplateParameter parameter = CommandLibraryTestHelpers.RequiredParameter("filter", "Filter");
        parameter.Quoting = QuotingMode.InlineInQuotes;
        CommandTemplate template = WindowsTemplate("Get-ADObject -LDAPFilter '{filter}'", parameter);
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["filter"] = "(&(objectClass=user)(cn=O'Brien))" };

        service.ValidateParameters(template, values, out List<string> errors);

        errors.Should().BeEmpty();
        service.GenerateCommand(template, values)
            .Should().Be("Get-ADObject -LDAPFilter '(&(objectClass=user)(cn=O''Brien))'");
    }

    // A-03: single quotes the shell reads inside a double-quoted string are not literal:
    // PowerShell expands $(...) in "Done: '{v}'". A ShellQuote placeholder written inside a
    // double-quoted string keeps the blacklist, because its own quotes would not protect it.
    [Fact]
    public void ValidateParameters_ShellQuoteInsideDoubleQuotes_StillRefusesSubexpression()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "Write-Host \"Done: {v}\"",
            CommandLibraryTestHelpers.RequiredParameter("v", "Value"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["v"] = "$(whoami)" };

        service.ValidateParameters(template, values, out List<string> errors);

        errors.Should().Equal(MessageKeys.ValidationParameterDangerousCharacters);
        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));
    }

    // A-03: a carriage return or a line feed submits the command on the terminal, which no
    // quoting prevents, so both are refused whatever the quoting mode and placement.
    [Theory]
    [InlineData("shell-quote", "\r")]
    [InlineData("shell-quote", "\n")]
    [InlineData("inline-in-single-quotes", "\r")]
    [InlineData("inline-in-single-quotes", "\n")]
    [InlineData("inline-misplaced", "\r")]
    [InlineData("inline-misplaced", "\n")]
    public void ValidateParameters_CarriageReturnOrLineFeed_IsRefusedInEveryMode(string mode, string newline)
    {
        CommandGeneratorService service = CreateService();
        TemplateParameter parameter = CommandLibraryTestHelpers.RequiredParameter("v", "Value");
        string pattern = "Write-Output {v}";
        if (mode != "shell-quote")
        {
            parameter.Quoting = QuotingMode.InlineInQuotes;
            pattern = mode == "inline-in-single-quotes" ? "Write-Output '{v}'" : pattern;
        }

        CommandTemplate template = WindowsTemplate(pattern, parameter);
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["v"] = "a" + newline + "b" };

        service.ValidateParameters(template, values, out List<string> errors);

        errors.Should().Equal(MessageKeys.ValidationParameterDangerousCharacters);
        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));
    }

    // Control character (built at runtime to avoid attribute-literal escaping ambiguity).
    [Fact]
    public void GenerateCommand_StringWithControlCharacter_Throws()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "echo {v}",
            CommandLibraryTestHelpers.RequiredParameter("v", "Value"));
        string value = "a" + (char)1 + "b";
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["v"] = value };

        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));
    }

    // Allow-list boundary: an apostrophe and a double-quote are NOT in DangerousChars, so they
    // are safely quoted rather than rejected.
    [Fact]
    public void GenerateCommand_StringWithQuotes_IsQuotedNotRejected()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "echo {v}",
            CommandLibraryTestHelpers.RequiredParameter("v", "Value"));

        string withApostrophe = service.GenerateCommand(
            template,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["v"] = "a'b" });
        string withDoubleQuote = service.GenerateCommand(
            template,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["v"] = "a\"b" });

        withApostrophe.Should().Be("echo 'a''b'");
        withDoubleQuote.Should().Be("echo 'a\"b'");
    }

    // D2 Lot B: default producer path remains ShellQuote when TemplateParameter.Quoting is null.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Fact]
    public void GenerateCommand_NullQuotingMode_UsesShellQuoteOnWindows()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "Write-Output {v}",
            CommandLibraryTestHelpers.RequiredParameter("v", "Value"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["v"] = "O'Brien" };

        string command = service.GenerateCommand(template, values);

        command.Should().Be("Write-Output 'O''Brien'");
    }

    // D2 Lot B: default producer path remains ShellQuote when TemplateParameter.Quoting is null.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Fact]
    public void GenerateCommand_NullQuotingMode_UsesShellQuoteOnLinux()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = LinuxTemplate(
            "printf %s {v}",
            CommandLibraryTestHelpers.RequiredParameter("v", "Value"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["v"] = "it's" };

        string command = service.GenerateCommand(template, values);

        command.Should().Be("printf %s 'it'\\''s'");
    }

    // D2 Lot B: InlineInQuotes is for placeholders already inside a single-quoted context.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Fact]
    public void GenerateCommand_InlineInQuotes_WindowsString_DoesNotAddOuterQuotes()
    {
        CommandGeneratorService service = CreateService();
        TemplateParameter parameter = CommandLibraryTestHelpers.RequiredParameter("searchTerm", "Search term");
        parameter.Quoting = QuotingMode.InlineInQuotes;
        CommandTemplate template = WindowsTemplate(
            "Get-ADUser -Filter \"Name -like '*{searchTerm}*'\"",
            parameter);
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["searchTerm"] = "foo" };

        string command = service.GenerateCommand(template, values);

        command.Should().Be("Get-ADUser -Filter \"Name -like '*foo*'\"");
        command.Should().Contain("-like '*foo*'");
        command.Should().NotContain("''foo''");
    }

    // D2 Lot B: InlineInQuotes keeps platform-specific inner escaping but does not wrap.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Fact]
    public void GenerateCommand_InlineInQuotes_WindowsStringWithApostrophe_EscapesInsideExistingQuotes()
    {
        CommandGeneratorService service = CreateService();
        TemplateParameter parameter = CommandLibraryTestHelpers.RequiredParameter("searchTerm", "Search term");
        parameter.Quoting = QuotingMode.InlineInQuotes;
        CommandTemplate template = WindowsTemplate(
            "Get-ADUser -Filter \"Name -like '*{searchTerm}*'\"",
            parameter);
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["searchTerm"] = "O'Brien" };

        string command = service.GenerateCommand(template, values);

        command.Should().Be("Get-ADUser -Filter \"Name -like '*O''Brien*'\"");
        command.Should().NotContain("'''O", "InlineInQuotes must not add a second shell-quote wrapper");
    }

    // PowerShell ends a single-quoted string on four typographic quotes as well as on the
    // apostrophe, so a Windows value holding one is doubled like an apostrophe, quoted or inline.
    [Theory]
    [InlineData(0x2018)]
    [InlineData(0x2019)]
    [InlineData(0x201A)]
    [InlineData(0x201B)]
    public void GenerateCommand_WindowsStringWithTypographicQuote_DoublesIt(int codePoint)
    {
        string quote = ((char)codePoint).ToString();
        CommandGeneratorService service = CreateService();
        TemplateParameter inline = CommandLibraryTestHelpers.RequiredParameter("inner", "Inner");
        inline.Quoting = QuotingMode.InlineInQuotes;
        CommandTemplate template = WindowsTemplate(
            "Get-Item {outer} -Filter '{inner}'",
            CommandLibraryTestHelpers.RequiredParameter("outer", "Outer"),
            inline);
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["outer"] = $"O{quote}Brien",
            ["inner"] = $"a{quote}b",
        };

        string command = service.GenerateCommand(template, values);

        command.Should().Be($"Get-Item 'O{quote}{quote}Brien' -Filter 'a{quote}{quote}b'");
    }

    // Bash has no typographic quote rule: a Linux value keeps its typographic quote as is.
    [Fact]
    public void GenerateCommand_LinuxStringWithTypographicQuote_LeavesItAlone()
    {
        string quote = ((char)0x2019).ToString();
        CommandGeneratorService service = CreateService();
        CommandTemplate template = LinuxTemplate(
            "echo {msg}",
            CommandLibraryTestHelpers.RequiredParameter("msg", "Message"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["msg"] = $"it{quote}s" };

        string command = service.GenerateCommand(template, values);

        command.Should().Be($"echo 'it{quote}s'");
    }

    // D2 Lot B: InlineInQuotes keeps platform-specific inner escaping but does not wrap.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Fact]
    public void GenerateCommand_InlineInQuotes_LinuxStringWithApostrophe_EscapesInsideExistingQuotes()
    {
        CommandGeneratorService service = CreateService();
        TemplateParameter parameter = CommandLibraryTestHelpers.RequiredParameter("searchTerm", "Search term");
        parameter.Quoting = QuotingMode.InlineInQuotes;
        CommandTemplate template = LinuxTemplate(
            "grep '*{searchTerm}*' access.log",
            parameter);
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["searchTerm"] = "it's" };

        string command = service.GenerateCommand(template, values);

        command.Should().Be("grep '*it'\\''s*' access.log");
        command.Should().NotContain("''it", "InlineInQuotes must not add a second shell-quote wrapper");
    }

    // D2 Lot B: driveletter is a validated bare type for patterns such as {driveLetter}:.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Fact]
    public void GenerateCommand_DriveLetterType_SubstitutesValueWithoutQuotes()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "manage-bde -on {driveLetter}: -RecoveryPassword",
            CommandLibraryTestHelpers.RequiredParameter("driveLetter", "Drive letter", "driveletter"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["driveLetter"] = "C" };

        string command = service.GenerateCommand(template, values);

        command.Should().Be("manage-bde -on C: -RecoveryPassword");
        command.Should().Contain(" C:");
        command.Should().NotContain("'C':");
    }

    // D2 Lot B: driveletter is a whitelist type, so malformed values fail before substitution.
    // Producer: CommandGeneratorService.GenerateCommand -> ValidateParameterValue.
    [Theory]
    [InlineData("CD")]
    [InlineData("1")]
    [InlineData(";")]
    public void GenerateCommand_InvalidDriveLetter_Throws(string value)
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "manage-bde -on {driveLetter}:",
            CommandLibraryTestHelpers.RequiredParameter("driveLetter", "Drive letter", "driveletter"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["driveLetter"] = value };

        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));
    }

    // D2 Lot B: QuotingMode changes only escaping; G1 dangerous-character validation still runs first.
    // Producer: CommandGeneratorService.GenerateCommand -> ValidateParameterValue.
    [Theory]
    [InlineData("foo;bar")]
    [InlineData("foo$(bar)")]
    public void GenerateCommand_InlineInQuotesWithDangerousCharacter_Throws(string value)
    {
        CommandGeneratorService service = CreateService();
        TemplateParameter parameter = CommandLibraryTestHelpers.RequiredParameter("searchTerm", "Search term");
        parameter.Quoting = QuotingMode.InlineInQuotes;
        CommandTemplate template = WindowsTemplate(
            "Get-ADUser -Filter \"Name -like '*{searchTerm}*'\"",
            parameter);
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["searchTerm"] = value };

        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));
    }

    [Fact]
    public void TemplateParameter_QuotingMode_RoundTripsThroughCompactStorageAsString()
    {
        TemplateParameter parameter = CommandLibraryTestHelpers.RequiredParameter("searchTerm", "Search term");
        parameter.Quoting = QuotingMode.InlineInQuotes;

        string json = JsonSerializer.Serialize(parameter, JsonOptionsHelper.CompactStorage);
        TemplateParameter? deserialized = JsonSerializer.Deserialize<TemplateParameter>(
            json,
            JsonOptionsHelper.CompactStorage);

        json.Should().Contain("\"Quoting\":\"InlineInQuotes\"");
        deserialized.Should().NotBeNull();
        deserialized!.Quoting.Should().Be(QuotingMode.InlineInQuotes);
    }

    [Fact]
    public void TemplateParameter_MissingQuotingMode_DeserializesToNull()
    {
        const string json = """
            {"Name":"searchTerm","Label":"Search term","Type":"string","Required":true}
            """;

        TemplateParameter? parameter = JsonSerializer.Deserialize<TemplateParameter>(
            json,
            JsonOptionsHelper.CompactStorage);

        parameter.Should().NotBeNull();
        parameter!.Quoting.Should().BeNull();
    }

    // D2 Lot B step 2a: seed parameters tagged InlineInQuotes must no longer double-wrap.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Theory]
    [MemberData(nameof(InlineInQuotesSeedParameters))]
    public void GenerateCommand_SeedInlineInQuotesParameters_DoNotDoubleWrap(
        string fileName,
        string templateName,
        CommandTemplate template,
        string parameterName)
    {
        CommandGeneratorService service = CreateService();
        Dictionary<string, string> values = CreateSeedGenerationValues(template, parameterName);

        string command = service.GenerateCommand(template, values);

        ContainsValueInsideSingleQuotedSpan(command, "A B")
            .Should()
            .BeTrue($"{fileName} / {templateName} / {parameterName} should keep the value inside one existing single-quoted span");
        command.Should().NotContain("''A B''", $"{fileName} / {templateName} / {parameterName} must not double-wrap");
    }

    // A-02: every shipped template opens with the defaults it ships, so each one has to
    // validate and generate with exactly those values. The value dictionary is what the
    // Command Library view model starts from (GetInitialParameterValue): the default, or an
    // empty string when there is none. A required parameter with no default is the one
    // error allowed, because the operator is expected to fill it; any other error (a
    // refused character, a malformed number) means the action opens broken.
    [Theory]
    [MemberData(nameof(EverySeedTemplate))]
    public void GenerateCommand_EverySeedTemplate_AcceptsItsShippedDefaults(string seedTemplate)
    {
        string[] parts = seedTemplate.Split(" / ", StringSplitOptions.None);
        CommandTemplate template = LoadSeedTemplate(parts[0], parts[1]);
        CommandGeneratorService service = CreateService();

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        List<string> expectedRequiredErrors = [];
        foreach (TemplateParameter parameter in template.Parameters)
        {
            values[parameter.Name] = parameter.DefaultValue ?? string.Empty;
            if (parameter.Required && string.IsNullOrWhiteSpace(values[parameter.Name]))
            {
                expectedRequiredErrors.Add(
                    string.Format(System.Globalization.CultureInfo.InvariantCulture, MessageKeys.ValidationParameterRequired, parameter.Label));
            }
        }

        service.ValidateParameters(template, values, out List<string> errors);

        errors.Should().BeEquivalentTo(
            expectedRequiredErrors,
            $"{seedTemplate} must validate with its shipped defaults; only an empty required field may be reported");

        // The operator fills the required fields before generating; every other value keeps
        // its shipped default, so generation still exercises each default.
        foreach (TemplateParameter parameter in template.Parameters.Where(parameter =>
            parameter.Required && string.IsNullOrWhiteSpace(values[parameter.Name])))
        {
            values[parameter.Name] = parameter.IsChoice
                ? parameter.AllowedValues![0]
                : SafeValueForParameter(parameter);
        }

        Func<string> generate = () => service.GenerateCommand(template, values);
        if (OverlongShippedTemplates.Contains(seedTemplate))
        {
            // Pinned from the other side too: once one of these is shortened, this assertion
            // fails and the entry has to leave the list, so the list cannot outlive the defect.
            generate.Should().Throw<InvalidOperationException>(
                    $"{seedTemplate} is listed as longer than the generator accepts; remove it from the list once it fits")
                .WithMessage($"*{ValidationConstants.MaxCommandLength}*");
            return;
        }

        generate.Should().NotThrow($"{seedTemplate} must generate with its shipped defaults");
    }

    // F-08: an empty optional value used to arrive as '' (hostname '', git pull ''). The
    // generator now leaves such an argument out, so an empty quoted token left in a command
    // built from the shipped defaults comes from the pattern: quotes written around a
    // placeholder whose default is empty (-HostHeader '{hostHeader}').
    [Theory]
    [MemberData(nameof(EverySeedTemplate))]
    public void GenerateCommand_EverySeedTemplate_ShippedDefaultsQuoteNothingEmpty(string seedTemplate)
    {
        string[] parts = seedTemplate.Split(" / ", StringSplitOptions.None);
        CommandTemplate template = LoadSeedTemplate(parts[0], parts[1]);
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (TemplateParameter parameter in template.Parameters)
        {
            string value = parameter.DefaultValue ?? string.Empty;
            if (parameter.Required && string.IsNullOrWhiteSpace(value))
            {
                value = parameter.IsChoice ? parameter.AllowedValues![0] : SafeValueForParameter(parameter);
            }

            values[parameter.Name] = value;
        }

        string command = CreateService().GenerateCommand(template, values);
        bool quotesNothing = EmptyQuotedToken.IsMatch(command);

        if (EmptyQuotedSeedTemplates.Contains(seedTemplate))
        {
            // Pinned from the other side too: once the seed is fixed this fails and the entry
            // has to leave the list, so the list cannot outlive the defect.
            quotesNothing.Should().BeTrue(
                $"{seedTemplate} is listed as quoting an empty default; remove it from the list once it does not: {command}");
            return;
        }

        quotesNothing.Should().BeFalse($"{seedTemplate} must not quote an empty value: {command}");
    }

    /// <summary>
    /// A pair of single or double quotes holding nothing and standing as a token of its own,
    /// alone or after <c>=</c>. Doubled quotes inside a value (<c>'it''s'</c>) and the bash
    /// close-escape-reopen sequence (<c>'\''</c>) touch other characters and do not match.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex EmptyQuotedToken = new(
        @"(^|[\s=])(''|"""")(\s|$)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Shipped templates whose pattern writes quotes around a placeholder whose default is
    /// empty, so the generator cannot leave the argument out. A seed-side defect, fixed in
    /// the seed files, not here.
    /// </summary>
    private static readonly HashSet<string> EmptyQuotedSeedTemplates = new(StringComparer.Ordinal)
    {
        "iis-create-site.json / windowsCommandTemplate",
    };

    [Fact]
    public void EmptyQuotedSeedTemplates_NameOnlyTemplatesTheCatalogueShips()
    {
        HashSet<string> shipped = EverySeedTemplate()
            .Select(row => (string)row[0])
            .ToHashSet(StringComparer.Ordinal);

        EmptyQuotedSeedTemplates.Where(entry => !shipped.Contains(entry))
            .Should().BeEmpty("a listed template must still have a seed row, or the list outlives the defect");
    }

    /// <summary>
    /// Shipped templates whose pattern alone is longer than
    /// <see cref="ValidationConstants.MaxCommandLength"/>, so they cannot generate whatever
    /// the values. Found by the theory above; they would open on a generation error.
    /// Whether the bound or the scripts should change is an open decision, not this test's
    /// to make, so the list records the defect instead of hiding it.
    /// </summary>
    /// <remarks>
    /// Empty since the nine it held left the default catalogue with the workstation debloat
    /// pack (data/packs), which keeps them as they are.
    /// </remarks>
    private static readonly HashSet<string> OverlongShippedTemplates = new(StringComparer.Ordinal);

    /// <summary>
    /// An entry of <see cref="OverlongShippedTemplates"/> whose seed row is gone is never
    /// reached by the theory above, so it would stay listed and green forever. Every entry
    /// has to name a template the catalogue still ships.
    /// </summary>
    [Fact]
    public void OverlongShippedTemplates_NameOnlyTemplatesTheCatalogueShips()
    {
        HashSet<string> shipped = EverySeedTemplate()
            .Select(row => (string)row[0])
            .ToHashSet(StringComparer.Ordinal);

        OverlongShippedTemplates.Where(entry => !shipped.Contains(entry))
            .Should().BeEmpty("a listed template must still have a seed row, or the list outlives the defect");
    }

    public static IEnumerable<object[]> EverySeedTemplate()
    {
        string seedActionsDirectory = FindSeedActionsDirectory();
        foreach (string filePath in Directory.EnumerateFiles(seedActionsDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            ActionModel? action = JsonSerializer.Deserialize<ActionModel>(File.ReadAllText(filePath), JsonOptionsHelper.CaseInsensitive);
            if (action == null)
            {
                continue;
            }

            string fileName = Path.GetFileName(filePath);
            if (action.WindowsCommandTemplate != null)
            {
                yield return new object[] { fileName + " / windowsCommandTemplate" };
            }

            if (action.LinuxCommandTemplate != null)
            {
                yield return new object[] { fileName + " / linuxCommandTemplate" };
            }
        }
    }

    // D2 Lot B step 2b: seed driveLetter parameters must be validated bare values.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Theory]
    [MemberData(nameof(DriveLetterSeedTemplates))]
    public void GenerateCommand_SeedDriveLetterParameters_SubstituteWithoutQuotes(
        string fileName,
        CommandTemplate template)
    {
        CommandGeneratorService service = CreateService();
        Dictionary<string, string> values = CreateSeedGenerationValues(template, "driveLetter", "D");

        string command = service.GenerateCommand(template, values);

        command.Should().Contain("D:", $"{fileName} should emit the drive mount point without quoting the letter");
        command.Should().NotContain("'D'", $"{fileName} must not single-quote the drive letter");

        Dictionary<string, string> invalidValues = CreateSeedGenerationValues(template, "driveLetter", "DD");
        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, invalidValues));
    }

    // D2 Lot B step 2b: archive affix placeholder is inside one single-quoted span.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Fact]
    public void GenerateCommand_SeedArchiveTarAffixPlaceholder_UsesOneQuotedArchiveToken()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = LoadSeedTemplate("archive-tar-linux.json", "linuxCommandTemplate");
        Dictionary<string, string> values = CreateSeedGenerationValues(template, "archiveName", "my arc");

        string command = service.GenerateCommand(template, values);

        command.Should().Contain("'my arc.tar.gz'");
        command.Should().NotContain("''my arc''");
        command.Should().NotContain("\"");
    }

    // D2 Lot B step 2b: icacls affix placeholder keeps the ACL suffix in the same quoted token.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Fact]
    public void GenerateCommand_SeedIcaclsAffixPlaceholder_UsesOneQuotedAclToken()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = LoadSeedTemplate("win-legacy-icacls.json", "windowsCommandTemplate");
        Dictionary<string, string> values = CreateSeedGenerationValues(template, "user", "CORP\\svc");

        string command = service.GenerateCommand(template, values);

        command.Should().Contain("'CORP\\svc:(OI)(CI)F'");
        command.Should().NotContain("''CORP\\svc''");
        command.Should().NotContain("\"");
    }

    // D2 Lot B step 2c: Defender numeric placeholders must not leave a stray PowerShell $.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Fact]
    public void GenerateCommand_SeedDefenderCloudProtection_UsesNumberPlaceholderWithoutDollar()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = LoadSeedTemplate(
            "defender-configure-cloud-protection.json",
            "windowsCommandTemplate");
        Dictionary<string, string> values = CreateSeedDefaultValues(template);

        string command = service.GenerateCommand(template, values);

        command.Should().Be("Set-MpPreference -MAPSReporting 2");

        Dictionary<string, string> invalidValues = CreateSeedGenerationValues(template, "level", "abc");
        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, invalidValues));
    }

    // D2 Lot B step 2c: Defender schedule uses a bare numeric day and a quoted time string.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Fact]
    public void GenerateCommand_SeedDefenderScheduleScan_UsesNumberDayAndQuotedTime()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = LoadSeedTemplate("defender-schedule-scan.json", "windowsCommandTemplate");
        Dictionary<string, string> values = CreateSeedDefaultValues(template);

        string command = service.GenerateCommand(template, values);

        // F-12: a time holds nothing PowerShell reads differently unquoted, so it arrives bare.
        command.Should().Be("Set-MpPreference -ScanScheduleDay 0 -ScanScheduleTime 02:00:00");
    }

    // D2 Lot B step 2c: users enter false/true while the template supplies the PowerShell $ prefix.
    // Producer: CommandGeneratorService.GenerateCommand -> EscapeParameterValue.
    [Theory]
    [InlineData("false", "Set-MpPreference -DisableRealtimeMonitoring $false")]
    [InlineData("true", "Set-MpPreference -DisableRealtimeMonitoring $true")]
    public void GenerateCommand_SeedDefenderRealtimeMonitoring_InlinesBooleanAfterDollar(
        string value,
        string expectedCommand)
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = LoadSeedTemplate("defender-enable-realtime.json", "windowsCommandTemplate");
        Dictionary<string, string> values = CreateSeedGenerationValues(template, "enableDisable", value);

        string command = service.GenerateCommand(template, values);

        command.Should().Be(expectedCommand);
    }

    // D2 Lot B step 2c: values still pass through the dangerous-character blocklist.
    // Producer: CommandGeneratorService.GenerateCommand -> ValidateParameterValue.
    [Fact]
    public void GenerateCommand_SeedDefenderRealtimeMonitoring_ValueWithDollarThrows()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = LoadSeedTemplate("defender-enable-realtime.json", "windowsCommandTemplate");
        Dictionary<string, string> values = CreateSeedGenerationValues(template, "enableDisable", "$false");

        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));
    }

    // ===== TEST-01: type validation rejects =====

    [Theory]
    [InlineData("-bad")]
    [InlineData("host_underscore")]
    public void GenerateCommand_InvalidHostname_Throws(string value)
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "-ComputerName {h}",
            CommandLibraryTestHelpers.RequiredParameter("h", "Host", "hostname"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["h"] = value };

        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));
    }

    [Fact]
    public void GenerateCommand_OverlongHostname_Throws()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "-ComputerName {h}",
            CommandLibraryTestHelpers.RequiredParameter("h", "Host", "hostname"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["h"] = new string('a', 256) };

        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));
    }

    [Theory]
    [InlineData("srv01")]
    [InlineData("sub.domain.com")]
    public void GenerateCommand_ValidHostname_Succeeds(string value)
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "-ComputerName {h}",
            CommandLibraryTestHelpers.RequiredParameter("h", "Host", "hostname"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["h"] = value };

        string command = service.GenerateCommand(template, values);

        command.Should().Be("-ComputerName " + value);
    }

    [Theory]
    [InlineData("999.1.1.1")]
    [InlineData("not-an-ip")]
    public void GenerateCommand_InvalidIpAddress_Throws(string value)
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "-Target {ip}",
            CommandLibraryTestHelpers.RequiredParameter("ip", "Target", "ipaddress"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["ip"] = value };

        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("2001:db8::1")]
    public void GenerateCommand_ValidIpAddress_Succeeds(string value)
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "-Target {ip}",
            CommandLibraryTestHelpers.RequiredParameter("ip", "Target", "ipaddress"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["ip"] = value };

        string command = service.GenerateCommand(template, values);

        command.Should().Be("-Target " + value);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    public void GenerateCommand_InvalidInteger_Throws(string value)
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "-Count {n}",
            CommandLibraryTestHelpers.RequiredParameter("n", "Count", "int"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["n"] = value };

        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));
    }

    [Fact]
    public void GenerateCommand_ValidInteger_Succeeds()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "-Count {n}",
            CommandLibraryTestHelpers.RequiredParameter("n", "Count", "int"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["n"] = "42" };

        string command = service.GenerateCommand(template, values);

        command.Should().Be("-Count 42");
    }

    [Fact]
    public void GenerateCommand_ValidPathUnderAppData_Succeeds()
    {
        CommandGeneratorService service = CreateService();
        string validPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Heimdall",
            "report.txt");
        CommandTemplate template = WindowsTemplate(
            "-Path {p}",
            CommandLibraryTestHelpers.RequiredParameter("p", "Path", "path"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["p"] = validPath };

        string command = service.GenerateCommand(template, values);

        // Path is a quoted type; assert it is single-quoted and contains the path payload.
        command.Should().StartWith("-Path '").And.EndWith("'");
        command.Should().Contain("report.txt");
    }

    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public void GenerateCommand_InvalidPath_Throws(string value)
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "-Path {p}",
            CommandLibraryTestHelpers.RequiredParameter("p", "Path", "path"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["p"] = value };

        Assert.Throws<InvalidOperationException>(() => service.GenerateCommand(template, values));
    }

    // E-01: a path is checked by its shape, not resolved on this machine. The rows that
    // relied on the local AppData folders (tilde, percent sign, traversal, relative path,
    // Desktop/Downloads/Documents) are retired on purpose: the command runs elsewhere, the
    // value is quoted like a string, and all of those are literal text inside the quotes.
    public static IEnumerable<object[]> InvalidPaths()
    {
        yield return new object[] { "   " };                                                         // blank
        yield return new object[] { "/var/log/" + (char)0 + "syslog" };                               // NUL
        yield return new object[] { "/var/log/syslog\nrm -rf /" };                                    // line feed
        yield return new object[] { "C:\\inetpub\\wwwroot\r" };                                       // carriage return
        yield return new object[] { "C:\\" + new string('a', ValidationConstants.MaxPathLength) };   // over the bound
    }

    // E-01: paths of another machine validate and come out quoted, on both platforms.
    // F-12: a path made only of characters no shell reads specially comes out bare.
    [Theory]
    [InlineData("/var/log/syslog", false)]
    [InlineData("/etc/nginx/nginx.conf", false)]
    [InlineData("C:\\inetpub\\wwwroot", true)]
    [InlineData("C:\\Program Files (x86)\\App", true)]
    [InlineData("~/backup", true)]
    [InlineData("relative/dir", false)]
    public void GenerateCommand_RemotePath_ValidatesAndComesOutQuoted(string value, bool quoted)
    {
        string argument = quoted ? "'" + value + "'" : value;
        CommandGeneratorService service = CreateService();
        CommandTemplate windows = WindowsTemplate(
            "Get-Item -Path {p}",
            CommandLibraryTestHelpers.RequiredParameter("p", "Path", "path"));
        CommandTemplate linux = LinuxTemplate(
            "tail -n 50 {p}",
            CommandLibraryTestHelpers.RequiredParameter("p", "Path", "path"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["p"] = value };

        service.ValidateParameters(windows, values, out List<string> windowsErrors);
        service.ValidateParameters(linux, values, out List<string> linuxErrors);

        windowsErrors.Should().BeEmpty();
        linuxErrors.Should().BeEmpty();
        service.GenerateCommand(windows, values).Should().Be("Get-Item -Path " + argument);
        service.GenerateCommand(linux, values).Should().Be("tail -n 50 " + argument);
    }

    // E-01: the localized path reports the same refusals as the generation path.
    [Theory]
    [MemberData(nameof(InvalidPaths))]
    public void ValidateParameters_InvalidPath_ReportsInvalidPath(string value)
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = LinuxTemplate(
            "tail {p}",
            CommandLibraryTestHelpers.RequiredParameter("p", "Path", "path"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["p"] = value };

        service.ValidateParameters(template, values, out List<string> errors);

        if (string.IsNullOrWhiteSpace(value))
        {
            // A blank required value is reported as missing, not as malformed.
            errors.Should().Equal(MessageKeys.ValidationParameterRequired);
            return;
        }

        errors.Should().Equal(MessageKeys.ValidationParameterInvalidPath);
    }

    // ===== TEST-01: ValidateParameters localized path (line 120) =====

    [Fact]
    public void ValidateParameters_RequiredMissing_ReturnsFalseWithError()
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "echo {name}",
            CommandLibraryTestHelpers.RequiredParameter("name", "Name"));
        Dictionary<string, string> values = new(StringComparer.Ordinal);

        bool isValid = service.ValidateParameters(template, values, out List<string> errors);

        isValid.Should().BeFalse();
        errors.Should().ContainSingle();
    }

    [Fact]
    public void ValidateParameters_DangerousCharacterInString_ReturnsFalseWithError()
    {
        // A-03: a bare ShellQuote placeholder no longer refuses '&' (its quotes make it
        // literal); inside a double-quoted string it still does.
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "echo \"{v}\"",
            CommandLibraryTestHelpers.RequiredParameter("v", "Value"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["v"] = "a&b" };

        bool isValid = service.ValidateParameters(template, values, out List<string> errors);

        isValid.Should().BeFalse();
        errors.Should().NotBeEmpty();
    }

    private static CommandTemplate WindowsTemplate(string pattern, params TemplateParameter[] parameters) =>
        new CommandTemplate
        {
            Id = "win-template",
            Name = "Windows template",
            Platform = Platform.Windows,
            CommandPattern = pattern,
            Parameters = [.. parameters]
        };

    private static CommandTemplate LinuxTemplate(string pattern, params TemplateParameter[] parameters) =>
        new CommandTemplate
        {
            Id = "linux-template",
            Name = "Linux template",
            Platform = Platform.Linux,
            CommandPattern = pattern,
            Parameters = [.. parameters]
        };

    private static CommandGeneratorService CreateService() =>
        new CommandGeneratorService(new FakeTwinShellLocalizationService());

    private static CommandTemplate CreateTemplate(
        string id,
        string title,
        string pattern,
        params TemplateParameter[] parameters)
    {
        ActionModel action = CommandLibraryTestHelpers.CreateLinuxAction(id, title, pattern, parameters);
        return action.LinuxCommandTemplate!;
    }

    private static TemplateParameter[] CreateParameters(int count)
    {
        TemplateParameter[] parameters = new TemplateParameter[count];
        for (int index = 0; index < parameters.Length; index++)
        {
            parameters[index] = CommandLibraryTestHelpers.OptionalParameter($"p{index}", $"Parameter {index}");
        }

        return parameters;
    }

    public static IEnumerable<object[]> InlineInQuotesSeedParameters()
    {
        string seedActionsDirectory = FindSeedActionsDirectory();
        foreach (string filePath in Directory.EnumerateFiles(seedActionsDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            string json = File.ReadAllText(filePath);
            ActionModel? action = JsonSerializer.Deserialize<ActionModel>(json, JsonOptionsHelper.CaseInsensitive);
            if (action == null)
            {
                continue;
            }

            foreach ((string TemplateName, CommandTemplate? Template) item in new[]
            {
                ("windowsCommandTemplate", action.WindowsCommandTemplate),
                ("linuxCommandTemplate", action.LinuxCommandTemplate)
            })
            {
                if (item.Template == null)
                {
                    continue;
                }

                foreach (TemplateParameter parameter in item.Template.Parameters.Where(parameter =>
                    parameter.Quoting == QuotingMode.InlineInQuotes &&
                    IsPlaceholderInsideSingleQuotedSpan(item.Template.CommandPattern, parameter.Name)))
                {
                    yield return new object[]
                    {
                        Path.GetFileName(filePath),
                        item.TemplateName,
                        item.Template,
                        parameter.Name
                    };
                }
            }
        }
    }

    public static IEnumerable<object[]> DriveLetterSeedTemplates()
    {
        string seedActionsDirectory = FindSeedActionsDirectory();
        foreach (string filePath in Directory.EnumerateFiles(seedActionsDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            string json = File.ReadAllText(filePath);
            ActionModel? action = JsonSerializer.Deserialize<ActionModel>(json, JsonOptionsHelper.CaseInsensitive);
            if (action == null)
            {
                continue;
            }

            foreach (CommandTemplate? template in new[] { action.WindowsCommandTemplate, action.LinuxCommandTemplate })
            {
                if (template == null)
                {
                    continue;
                }

                if (template.Parameters.Any(parameter =>
                    string.Equals(parameter.Name, "driveLetter", StringComparison.Ordinal) &&
                    string.Equals(parameter.Type, "driveletter", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return new object[]
                    {
                        Path.GetFileName(filePath),
                        template
                    };
                }
            }
        }
    }

    private static CommandTemplate LoadSeedTemplate(string fileName, string templateName)
    {
        string filePath = Path.Combine(FindSeedActionsDirectory(), fileName);
        string json = File.ReadAllText(filePath);
        ActionModel action = JsonSerializer.Deserialize<ActionModel>(json, JsonOptionsHelper.CaseInsensitive)
            ?? throw new InvalidOperationException($"Could not deserialize seed action {fileName}.");

        return templateName switch
        {
            "windowsCommandTemplate" => action.WindowsCommandTemplate
                ?? throw new InvalidOperationException($"{fileName} has no Windows command template."),
            "linuxCommandTemplate" => action.LinuxCommandTemplate
                ?? throw new InvalidOperationException($"{fileName} has no Linux command template."),
            _ => throw new ArgumentOutOfRangeException(nameof(templateName), templateName, "Unknown seed template name.")
        };
    }

    private static Dictionary<string, string> CreateSeedDefaultValues(CommandTemplate template)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);

        foreach (TemplateParameter parameter in template.Parameters)
        {
            values[parameter.Name] = !string.IsNullOrWhiteSpace(parameter.DefaultValue)
                ? parameter.DefaultValue
                : SafeValueForParameter(parameter);
        }

        return values;
    }

    private static Dictionary<string, string> CreateSeedGenerationValues(
        CommandTemplate template,
        string targetParameterName,
        string targetValue = "A B")
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);

        foreach (TemplateParameter parameter in template.Parameters)
        {
            if (string.Equals(parameter.Name, targetParameterName, StringComparison.Ordinal))
            {
                values[parameter.Name] = targetValue;
                continue;
            }

            values[parameter.Name] = SafeValueForParameter(parameter);
        }

        return values;
    }

    private static bool ContainsValueInsideSingleQuotedSpan(string command, string value)
    {
        bool insideSingleQuotedSpan = false;
        int spanStart = 0;

        for (int index = 0; index < command.Length; index++)
        {
            if (command[index] != '\'')
            {
                continue;
            }

            if (insideSingleQuotedSpan && index + 1 < command.Length && command[index + 1] == '\'')
            {
                index++;
                continue;
            }

            if (!insideSingleQuotedSpan)
            {
                spanStart = index + 1;
                insideSingleQuotedSpan = true;
                continue;
            }

            string span = command[spanStart..index];
            if (span.Contains(value, StringComparison.Ordinal))
            {
                return true;
            }

            insideSingleQuotedSpan = false;
        }

        return false;
    }

    private static bool IsPlaceholderInsideSingleQuotedSpan(string commandPattern, string parameterName)
    {
        string placeholder = "{" + parameterName + "}";
        int placeholderIndex = commandPattern.IndexOf(placeholder, StringComparison.Ordinal);
        if (placeholderIndex < 0)
        {
            return false;
        }

        int quoteCount = 0;
        for (int index = 0; index < placeholderIndex; index++)
        {
            if (commandPattern[index] == '\'')
            {
                quoteCount++;
            }
        }

        return quoteCount % 2 == 1;
    }

    private static string SafeValueForParameter(TemplateParameter parameter)
        => parameter.Type.ToLowerInvariant() switch
        {
            "hostname" => "srv01",
            "ipaddress" => "10.0.0.1",
            "int" or "integer" => "1",
            "number" => "1",
            "driveletter" => "C",
            "bool" or "boolean" => "true",
            "path" => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Heimdall",
                "seed-test.txt"),
            _ => "value"
        };

    internal static string FindSeedActionsDirectory()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            string candidate = Path.Combine(current.FullName, "data", "seed", "actions");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find data/seed/actions from the test output directory.");
    }
}
