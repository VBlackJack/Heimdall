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

using System.Runtime.Versioning;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

[SupportedOSPlatform("windows")]
public class CommandCredentialProviderTests
{
    // Generous timeout to absorb GitHub Actions Windows runner latency on
    // cmd.exe boot. The production default stays intentionally tight; tests
    // opt into a wider window via this factory.
    private const int TestTimeoutMs = 60000;

    private static CommandCredentialProvider CreateProvider(
        string? commandTemplate,
        string? databasePath = null,
        string? unlockSecret = null,
        string? usernameCommandTemplate = null,
        bool firstLineOnly = false,
        string? keyFilePath = null)
    {
        return new CommandCredentialProvider(
            commandTemplate, databasePath, timeoutMs: TestTimeoutMs, unlockSecret: unlockSecret,
            usernameCommandTemplate: usernameCommandTemplate, firstLineOnly: firstLineOnly,
            keyFilePath: keyFilePath);
    }

    // ---------------------------------------------------------------
    // Constructor & IsAvailable
    // ---------------------------------------------------------------

    [Fact]
    public void Constructor_NullCommand_DoesNotThrow()
    {
        var provider = CreateProvider(null);
        Assert.False(provider.IsAvailable);
    }

    [Fact]
    public void Constructor_EmptyCommand_IsNotAvailable()
    {
        var provider = CreateProvider("");
        Assert.False(provider.IsAvailable);
    }

    [Fact]
    public void Constructor_WhitespaceCommand_IsNotAvailable()
    {
        var provider = CreateProvider("   ");
        Assert.False(provider.IsAvailable);
    }

    [Fact]
    public void Constructor_ValidCommand_IsAvailable()
    {
        var provider = CreateProvider("cmd.exe /c echo hello");
        Assert.True(provider.IsAvailable);
    }

    [Fact]
    public void Name_ReturnsCommand()
    {
        var provider = CreateProvider("anything");
        Assert.Equal("Command", provider.Name);
    }

    // ---------------------------------------------------------------
    // GetCredentialAsync — unavailable provider
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_WhenNotAvailable_ReturnsNull()
    {
        var provider = CreateProvider(null);
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");
        Assert.Null(result);
    }

    // ---------------------------------------------------------------
    // Placeholder substitution
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_SubstitutesHostPlaceholder()
    {
        var provider = CreateProvider("cmd.exe /c echo {Host}");
        var result = await provider.GetCredentialAsync("myserver.local", 22, "admin", "MyServer");

        Assert.NotNull(result);
        Assert.Equal("myserver.local", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_SubstitutesPortPlaceholder()
    {
        var provider = CreateProvider("cmd.exe /c echo {Port}");
        var result = await provider.GetCredentialAsync("host", 2222, "user", "title");

        Assert.NotNull(result);
        Assert.Equal("2222", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_SubstitutesUserPlaceholder()
    {
        var provider = CreateProvider("cmd.exe /c echo {User}");
        var result = await provider.GetCredentialAsync("host", 22, "testuser", "title");

        Assert.NotNull(result);
        Assert.Equal("testuser", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_SubstitutesTitlePlaceholder()
    {
        var provider = CreateProvider("cmd.exe /c echo {Title}");
        var result = await provider.GetCredentialAsync("host", 22, "user", "Production-DB");

        Assert.NotNull(result);
        Assert.Equal("Production-DB", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_SubstitutesDatabasePlaceholder()
    {
        var provider = CreateProvider(
            "cmd.exe /c echo {Database}", @"C:\vault\passwords.kdbx");
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Contains("passwords.kdbx", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_PlaceholderSubstitution_CaseInsensitive()
    {
        var provider = CreateProvider("cmd.exe /c echo {host}");
        var result = await provider.GetCredentialAsync("casetest", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Equal("casetest", result.Password);
    }

    // ---------------------------------------------------------------
    // Command execution — happy path
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_ReturnsCommandOutput()
    {
        var provider = CreateProvider("cmd.exe /c echo test-password");
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Equal("test-password", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_PreservesUsername()
    {
        var provider = CreateProvider("cmd.exe /c echo secret");
        var result = await provider.GetCredentialAsync("host", 22, "admin", "title");

        Assert.NotNull(result);
        Assert.Equal("admin", result.Username);
    }

    [Fact]
    public async Task GetCredentialAsync_NullUsername_ReturnsEmpty()
    {
        var provider = CreateProvider("cmd.exe /c echo secret");
        var result = await provider.GetCredentialAsync("host", 22, null, "title");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Username);
    }

    // ---------------------------------------------------------------
    // Output trimming
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_StripsOnlyTheFinalLineTerminator()
    {
        // cmd.exe /c echo appends CRLF; that terminator, and only it, is removed.
        var provider = CreateProvider("cmd.exe /c echo padded-output");
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Equal("padded-output", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_KeepsSpacesAtTheEdgesOfThePassword()
    {
        // keepassxc-cli prints a password that begins or ends with a space as it is; trimming
        // the output handed the server another password. `sort` echoes stdin back unchanged.
        var provider = CreateProvider("cmd.exe /c sort", unlockSecret: "  pw with spaces  ");
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Equal("  pw with spaces  ", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_BlankOutput_ReturnsNull()
    {
        var provider = CreateProvider("cmd.exe /c sort", unlockSecret: "   ");
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCredentialAsync_DecodesTheToolOutputAsUtf8()
    {
        // `type` copies a file's bytes to stdout untouched, so the bytes are UTF-8 whatever the
        // console code page is, as keepassxc-cli's are.
        string path = Path.Combine(Path.GetTempPath(), $"heimdall-utf8-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "motdepassé\r\n", new System.Text.UTF8Encoding(false));
        try
        {
            var provider = CreateProvider("cmd.exe /c type \"{Database}\"", databasePath: path);
            var result = await provider.GetCredentialAsync("host", 22, "user", "title");

            Assert.NotNull(result);
            Assert.Equal("motdepassé", result.Password);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildStartInfo_EveryRedirectedPipeIsUtf8WithoutAByteOrderMark()
    {
        // The test host has a console and Heimdall has none, so a round trip through a child
        // process here cannot show the default Heimdall gets (the ANSI code page). The decision
        // itself is asserted instead.
        var startInfo = CommandCredentialProvider.BuildStartInfo("tool.exe", "args", redirectStandardInput: true);

        foreach (var encoding in new[]
                 {
                     startInfo.StandardOutputEncoding, startInfo.StandardErrorEncoding, startInfo.StandardInputEncoding
                 })
        {
            Assert.NotNull(encoding);
            Assert.Equal(65001, encoding.CodePage);
            Assert.Empty(encoding.GetPreamble());
        }
    }

    // ---------------------------------------------------------------
    // Empty output
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_EmptyOutput_ReturnsNull()
    {
        // "type nul" outputs nothing on Windows
        var provider = CreateProvider("cmd.exe /c type nul");
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.Null(result);
    }

    // ---------------------------------------------------------------
    // Non-zero exit code
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_NonZeroExitCode_ReturnsNull()
    {
        var provider = CreateProvider("cmd.exe /c exit 1");
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.Null(result);
    }

    // ---------------------------------------------------------------
    // Timeout
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_Timeout_ThrowsOperationCanceled()
    {
        // Use a short cancellation token to simulate timeout faster than the 10s default
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // ping -n 30 ensures the process runs long enough to be canceled
        var provider = CreateProvider("cmd.exe /c ping -n 30 127.0.0.1");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetCredentialAsync("host", 22, "user", "title", cts.Token));
    }

    [Fact]
    public async Task GetCredentialAsync_OwnTimeout_ThrowsTimeoutNotCancellation()
    {
        // The configured timeout is a vault failure, not the caller cancelling: callers report
        // or skip it, and treat only a real cancellation as one.
        var provider = new CommandCredentialProvider("cmd.exe /c ping -n 30 127.0.0.1", null, timeoutMs: 500);

        var exception = await Assert.ThrowsAsync<CredentialProviderTimeoutException>(
            () => provider.GetCredentialAsync("host", 22, "user", "title"));

        Assert.Equal(TimeSpan.FromMilliseconds(500), exception.Timeout);
    }

    [Fact]
    public async Task GetCredentialAsync_UsernameCommandTimesOut_FallsBackToHint()
    {
        var provider = new CommandCredentialProvider(
            "cmd.exe /c echo secret",
            null,
            timeoutMs: 3000,
            usernameCommandTemplate: "cmd.exe /c ping -n 30 127.0.0.1");

        var result = await provider.GetCredentialAsync("host", 22, null, "title");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Username);
        Assert.Equal("secret", result.Password);
    }

    // ---------------------------------------------------------------
    // CancellationToken respected
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_AlreadyCancelled_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var provider = CreateProvider("cmd.exe /c echo should-not-run");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetCredentialAsync("host", 22, "user", "title", cts.Token));
    }

    // ---------------------------------------------------------------
    // Invalid executable
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_InvalidExecutable_ThrowsLaunchFailure()
    {
        // Reported apart from "no password": the advice for a missing tool is not the advice
        // for a wrong entry name.
        var provider = CreateProvider(
            "nonexistent-binary-xyz --get-password");

        var exception = await Assert.ThrowsAsync<CredentialProviderLaunchException>(
            () => provider.GetCredentialAsync("host", 22, "user", "title"));

        Assert.Equal("nonexistent-binary-xyz", exception.Executable);
    }

    [Fact]
    public async Task GetCredentialAsync_UsernameCommandMissing_FallsBackToHint()
    {
        var provider = CreateProvider(
            "cmd.exe /c echo secret",
            usernameCommandTemplate: "nonexistent-binary-xyz --get-user");

        var result = await provider.GetCredentialAsync("host", 22, "hint", "title");
        Assert.NotNull(result);
        Assert.Equal("hint", result.Username);

        var unhinted = await provider.GetCredentialAsync("host", 22, null, "title");
        Assert.NotNull(unhinted);
        Assert.Equal(string.Empty, unhinted.Username);
        Assert.Equal("secret", unhinted.Password);
    }

    // ---------------------------------------------------------------
    // Sanitization — shell metacharacters stripped
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_SanitizesShellMetachars()
    {
        // The semicolon in the host should be stripped by SanitizeArgValue
        var provider = CreateProvider("cmd.exe /c echo {Host}");
        var result = await provider.GetCredentialAsync("safe;injected", 22, "user", "title");

        Assert.NotNull(result);
        Assert.DoesNotContain(";", result.Password);
        Assert.Equal("safeinjected", result.Password);
    }

    // ---------------------------------------------------------------
    // Multiple placeholders in one command
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_MultiPlaceholders_AllSubstituted()
    {
        var provider = CreateProvider(
            "cmd.exe /c echo {User}@{Host}:{Port}");
        var result = await provider.GetCredentialAsync("server1", 3306, "dbadmin", "title");

        Assert.NotNull(result);
        Assert.Equal("dbadmin@server1:3306", result.Password);
    }

    // ---------------------------------------------------------------
    // Context-aware sanitization via ExpandTemplate (internal)
    // ---------------------------------------------------------------

    [Fact]
    public void ExpandTemplate_ShellTarget_StripsParensAndPercent()
    {
        var provider = CreateProvider("cmd.exe /c echo {Title}");
        var expanded = provider.ExpandTemplate(
            "cmd.exe /c echo {Title}", "host", 22, "user", "Web (prod)");
        Assert.Equal("cmd.exe /c echo Web prod", expanded);
    }

    [Fact]
    public void ExpandTemplate_ShellTarget_StripsSingleQuotes()
    {
        var provider = CreateProvider("pwsh.exe -c echo {Title}");
        var expanded = provider.ExpandTemplate(
            "pwsh.exe -c echo {Title}", "host", 22, "user", "John's Server");
        Assert.Equal("pwsh.exe -c echo Johns Server", expanded);
    }

    [Fact]
    public void ExpandTemplate_RegularExe_PreservesParens()
    {
        var provider = CreateProvider("keepassxc-cli.exe show {Title}");
        var expanded = provider.ExpandTemplate(
            "keepassxc-cli.exe show {Title}", "host", 22, "user", "Web (prod)");
        Assert.Equal("keepassxc-cli.exe show Web (prod)", expanded);
    }

    [Fact]
    public void ExpandTemplate_RegularExe_PreservesSingleQuotes()
    {
        var provider = CreateProvider("bw.exe get password {Title}");
        var expanded = provider.ExpandTemplate(
            "bw.exe get password {Title}", "host", 22, "user", "John's Server");
        Assert.Equal("bw.exe get password John's Server", expanded);
    }

    [Fact]
    public void ExpandTemplate_RegularExe_PreservesPercent()
    {
        var provider = CreateProvider(
            "keepassxc-cli.exe show {Database} {Title}", "%AppData%\\db.kdbx");
        var expanded = provider.ExpandTemplate(
            "keepassxc-cli.exe show {Database} {Title}",
            "host", 22, "user", "entry1");
        Assert.Equal("keepassxc-cli.exe show %AppData%\\db.kdbx entry1", expanded);
    }

    [Fact]
    public void ExpandTemplate_UnknownRegularExe_StillStripsSemicolon()
    {
        // An executable that is not a known password manager keeps the relaxed stripping:
        // what it does with an argument is not known.
        var provider = CreateProvider("mytool.exe get {Host}");
        var expanded = provider.ExpandTemplate(
            "mytool.exe get {Host}", "host;injected", 22, "user", "title");
        Assert.Equal("mytool.exe get hostinjected", expanded);
    }

    [Theory]
    [InlineData("keepassxc-cli")]
    [InlineData("keepassxc-cli.exe")]
    [InlineData(@"C:\Program Files\KeePassXC\keepassxc-cli.exe")]
    [InlineData("KPScript.exe")]
    [InlineData("bw")]
    [InlineData("op.exe")]
    [InlineData("pass")]
    [InlineData("gopass")]
    public void ExpandTemplate_KnownVaultTool_KeepsEveryCharacterOfTheEntryName(string executable)
    {
        // An entry called "Srv R&D" was looked up as "Srv RD" and never found (measured with
        // keepassxc-cli 2.7). These tools take their arguments from the process, no shell.
        string template = executable.Contains(' ') ? $"\"{executable}\" show \"{{Title}}\"" : $"{executable} show \"{{Title}}\"";
        var provider = CreateProvider(template);

        var expanded = provider.ExpandTemplate(template, "host", 22, "user", "Srv R&D; $x|y<z>`w");

        Assert.EndsWith("show \"Srv R&D; $x|y<z>`w\"", expanded);
    }

    [Fact]
    public void ExpandTemplate_KnownVaultTool_StillStripsDoubleQuoteAndNewlines()
    {
        var provider = CreateProvider("keepassxc-cli show \"{Title}\"");
        var expanded = provider.ExpandTemplate(
            "keepassxc-cli show \"{Title}\"", "host", 22, "user", "a\"b\r\nc");
        Assert.Equal("keepassxc-cli show \"abc\"", expanded);
    }

    [Fact]
    public void ExpandTemplate_TrailingBackslashBeforeAClosingQuote_IsDoubled()
    {
        // "share\" would read as an escaped quote and merge the arguments that follow.
        var provider = CreateProvider("keepassxc-cli show \"{Title}\" -a Password");
        var expanded = provider.ExpandTemplate(
            "keepassxc-cli show \"{Title}\" -a Password", "host", 22, "user", @"share\");
        Assert.Equal(@"keepassxc-cli show ""share\\"" -a Password", expanded);
    }

    [Fact]
    public void ExpandTemplate_TrailingBackslashNotBeforeAQuote_IsLeftAlone()
    {
        var provider = CreateProvider("keepassxc-cli show {Title} -a Password");
        var expanded = provider.ExpandTemplate(
            "keepassxc-cli show {Title} -a Password", "host", 22, "user", @"share\");
        Assert.Equal(@"keepassxc-cli show share\ -a Password", expanded);
    }

    [Fact]
    public async Task GetCredentialAsync_TrailingBackslashEntry_ReachesTheToolAsOneArgument()
    {
        // End to end through the CRT argument parser of a real child: findstr receives the
        // value as one argument only if the closing quote survived the backslash.
        string path = Path.Combine(Path.GetTempPath(), $"heimdall-bs-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "line with share\\ inside\r\n");
        try
        {
            var finder = CreateProvider("findstr.exe /l /c:\"{Title}\" \"{Database}\"", databasePath: path);
            var result = await finder.GetCredentialAsync("host", 22, "user", @"share\");

            Assert.NotNull(result);
            Assert.Equal(@"line with share\ inside", result.Password);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExpandTemplate_RegularExe_StillStripsDoubleQuotes()
    {
        var provider = CreateProvider("bw.exe get {Title}");
        var expanded = provider.ExpandTemplate(
            "bw.exe get {Title}", "host", 22, "user", "entry\"injected");
        Assert.Equal("bw.exe get entryinjected", expanded);
    }

    // ---------------------------------------------------------------
    // Unlock secret injection via stdin
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_WithUnlockSecret_FeedsStdin()
    {
        // `sort` reads its standard input and writes the (single) line back to stdout. The
        // provider must redirect stdin and write the unlock secret, so the echoed output
        // equals the injected secret — proving stdin injection works end to end.
        var provider = CreateProvider("cmd.exe /c sort", unlockSecret: "vault-master-pw");
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Equal("vault-master-pw", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_NullUnlockSecret_DoesNotRedirectStdin()
    {
        // No regression: with a null unlock secret, stdin is not redirected and a plain
        // command that never reads stdin still completes and returns its output. (A forced
        // stdin redirect with no writer would leave the child waiting on an open pipe.)
        var provider = CreateProvider("cmd.exe /c echo no-stdin-needed", unlockSecret: null);
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Equal("no-stdin-needed", result.Password);
    }

    // ---------------------------------------------------------------
    // Username command (separate vault lookup)
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_UsernameCommand_RunsWhenHintEmpty()
    {
        var provider = CreateProvider(
            "cmd.exe /c echo secret",
            usernameCommandTemplate: "cmd.exe /c echo resolveduser");
        var result = await provider.GetCredentialAsync("host", 22, null, "title");

        Assert.NotNull(result);
        Assert.Equal("resolveduser", result.Username);
        Assert.Equal("secret", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_UsernameCommand_NotRunWhenHintProvided()
    {
        var provider = CreateProvider(
            "cmd.exe /c echo secret",
            usernameCommandTemplate: "cmd.exe /c echo resolveduser");
        var result = await provider.GetCredentialAsync("host", 22, "explicit", "title");

        Assert.NotNull(result);
        Assert.Equal("explicit", result.Username);
        Assert.Equal("secret", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_NoUsernameCommand_EchoesHint()
    {
        // No regression: without a username command, the hint is echoed unchanged.
        var provider = CreateProvider("cmd.exe /c echo secret", usernameCommandTemplate: null);
        var result = await provider.GetCredentialAsync("host", 22, "hintuser", "title");

        Assert.NotNull(result);
        Assert.Equal("hintuser", result.Username);
        Assert.Equal("secret", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_UsernameCommandFailure_FallsBackToHint()
    {
        // A failed username command must not fail the whole call: the password is still
        // returned and the username falls back to the (empty) hint.
        var provider = CreateProvider(
            "cmd.exe /c echo secret",
            usernameCommandTemplate: "cmd.exe /c exit 1");
        var result = await provider.GetCredentialAsync("host", 22, null, "title");

        Assert.NotNull(result);
        Assert.Equal(string.Empty, result.Username);
        Assert.Equal("secret", result.Password);
    }

    // ---------------------------------------------------------------
    // First-line-only output mode (KeePass2 KPScript, pass)
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetCredentialAsync_FirstLineOnly_TakesFirstLine()
    {
        // Emulates KPScript: the value on line 1, then a trailing "OK: ..." status line.
        var provider = CreateProvider(
            "cmd.exe /c echo thepass& echo OK: done", firstLineOnly: true);
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Equal("thepass", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_FirstLineOnly_SkipsLeadingBlankLines()
    {
        // `echo.` prints an empty line first; first-line-only must skip it.
        var provider = CreateProvider(
            "cmd.exe /c echo.& echo realvalue", firstLineOnly: true);
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Equal("realvalue", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_FirstLineOnly_KeepsSpacesAtTheEdgesOfTheLine()
    {
        // `sort` puts the line starting with a space first, then the status line.
        var provider = CreateProvider(
            "cmd.exe /c sort", unlockSecret: " pw \nOK: done", firstLineOnly: true);
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Equal(" pw ", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_FirstLineOnlyFalse_ReturnsWholeOutput()
    {
        // Default behaviour: the whole trimmed output is returned (status line included).
        var provider = CreateProvider(
            "cmd.exe /c echo thepass& echo OK: done", firstLineOnly: false);
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Contains("thepass", result.Password);
        Assert.Contains("OK: done", result.Password);
    }

    [Fact]
    public async Task GetCredentialAsync_FirstLineOnly_AppliesToUsernameCommand()
    {
        var provider = CreateProvider(
            "cmd.exe /c echo secret",
            usernameCommandTemplate: "cmd.exe /c echo resolveduser& echo OK: done",
            firstLineOnly: true);
        var result = await provider.GetCredentialAsync("host", 22, null, "title");

        Assert.NotNull(result);
        Assert.Equal("resolveduser", result.Username);
        Assert.Equal("secret", result.Password);
    }

    [Fact]
    public void ExpandTemplate_UnknownTemplate_DefaultsToStrict()
    {
        var provider = CreateProvider("");
        var expanded = provider.ExpandTemplate(
            "", "host(test)", 22, "user", "title");
        // Empty template -> IsShellTarget returns true -> strict
        Assert.DoesNotContain("(", expanded);
    }

    // ---------------------------------------------------------------
    // Key file placeholder + path-aware sanitization
    // ---------------------------------------------------------------

    [Fact]
    public void ExpandTemplate_KeyFile_IsExpandedForRegularExe()
    {
        var provider = CreateProvider(
            "keepassxc-cli.exe show -k {KeyFile} {Title}",
            keyFilePath: @"C:\vault\company.keyx");
        var expanded = provider.ExpandTemplate(
            "keepassxc-cli.exe show -k {KeyFile} {Title}", "host", 22, "user", "entry1");
        Assert.Equal(@"keepassxc-cli.exe show -k C:\vault\company.keyx entry1", expanded);
    }

    [Fact]
    public void ExpandTemplate_KeyFile_PreservesPathSpecialChars()
    {
        // A path-aware sanitizer must keep space, &, ( and ) intact for a non-shell target.
        var provider = CreateProvider(
            "keepassxc-cli.exe show -k {KeyFile}",
            keyFilePath: @"C:\Team R&D\sub (x)\company.keyx");
        var expanded = provider.ExpandTemplate(
            "keepassxc-cli.exe show -k {KeyFile}", "host", 22, "user", "title");
        Assert.Equal(@"keepassxc-cli.exe show -k C:\Team R&D\sub (x)\company.keyx", expanded);
    }

    [Fact]
    public void ExpandTemplate_Database_PreservesPathSpecialChars()
    {
        // Regression lock for the latent {Database} corruption: the same special characters
        // are now preserved by the path-aware sanitizer.
        var provider = CreateProvider(
            "keepassxc-cli.exe show {Database}",
            databasePath: @"C:\Team R&D\sub (x)\company.kdbx");
        var expanded = provider.ExpandTemplate(
            "keepassxc-cli.exe show {Database}", "host", 22, "user", "title");
        Assert.Equal(@"keepassxc-cli.exe show C:\Team R&D\sub (x)\company.kdbx", expanded);
    }

    [Fact]
    public void ExpandTemplate_KeyFile_StripsDoubleQuote()
    {
        // The double quote is the one argument-boundary metacharacter and must be removed.
        var provider = CreateProvider(
            "keepassxc-cli.exe show -k {KeyFile}",
            keyFilePath: "C:\\vault\\comp\"any.keyx");
        var expanded = provider.ExpandTemplate(
            "keepassxc-cli.exe show -k {KeyFile}", "host", 22, "user", "title");
        Assert.Equal(@"keepassxc-cli.exe show -k C:\vault\company.keyx", expanded);
    }

    [Fact]
    public void ExpandTemplate_EmptyKeyFile_ExpandsToEmptyQuotedArg()
    {
        // An empty key file with a -k "{KeyFile}" template must not throw; it expands to -k "".
        var provider = CreateProvider(
            "keepassxc-cli.exe show -k \"{KeyFile}\"",
            keyFilePath: null);
        var expanded = provider.ExpandTemplate(
            "keepassxc-cli.exe show -k \"{KeyFile}\"", "host", 22, "user", "title");
        Assert.Equal("keepassxc-cli.exe show -k \"\"", expanded);
    }

    [Fact]
    public void ExpandTemplate_ShellTarget_StillStripsKeyFilePathChars()
    {
        // For a shell target the path stays strictly stripped (& and parentheses removed).
        var provider = CreateProvider(
            "cmd.exe /c echo {KeyFile}",
            keyFilePath: @"C:\Team R&D\sub (x)\company.keyx");
        var expanded = provider.ExpandTemplate(
            "cmd.exe /c echo {KeyFile}", "host", 22, "user", "title");
        Assert.Equal(@"cmd.exe /c echo C:\Team RD\sub x\company.keyx", expanded);
    }
}
