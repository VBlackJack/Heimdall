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

        command.Should().Be("Add-ADGroupMember -Identity 'Domain Admins' -Members 'jdupont'");
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

    // Dangerous shell characters in a string value block generation (command injection guard).
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
    [InlineData("a\nb")]
    public void GenerateCommand_StringWithDangerousCharacter_Throws(string value)
    {
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "echo {v}",
            CommandLibraryTestHelpers.RequiredParameter("v", "Value"));
        Dictionary<string, string> values = new(StringComparer.Ordinal) { ["v"] = value };

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

    public static IEnumerable<object[]> InvalidPaths()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return new object[] { "~/secret.txt" };                                              // tilde expansion
        yield return new object[] { "%APPDATA%\\secret.txt" };                                      // env-var expansion
        yield return new object[] { Path.Combine(appData, "..", "..", "..", "..", "evil.txt") };    // traversal out of allowed base
        yield return new object[] { Path.Combine("relative", "path", "file.txt") };                 // non-rooted (relative)
        yield return new object[] { Path.Combine(userProfile, "Desktop", "evil.txt") };             // blocked subfolder
        yield return new object[] { Path.Combine(userProfile, "Downloads", "evil.txt") };           // blocked subfolder
        yield return new object[] { Path.Combine(userProfile, "Documents", "evil.txt") };           // blocked subfolder
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
        CommandGeneratorService service = CreateService();
        CommandTemplate template = WindowsTemplate(
            "echo {v}",
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

                foreach (TemplateParameter parameter in item.Template.Parameters.Where(parameter => parameter.Quoting == QuotingMode.InlineInQuotes))
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

    private static string FindSeedActionsDirectory()
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
