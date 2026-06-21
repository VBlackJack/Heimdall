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
        bool firstLineOnly = false)
    {
        return new CommandCredentialProvider(
            commandTemplate, databasePath, timeoutMs: TestTimeoutMs, unlockSecret: unlockSecret,
            usernameCommandTemplate: usernameCommandTemplate, firstLineOnly: firstLineOnly);
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
    public async Task GetCredentialAsync_TrimsWhitespaceFromOutput()
    {
        // cmd.exe /c echo adds a trailing newline; verify it's trimmed
        var provider = CreateProvider("cmd.exe /c echo   padded-output  ");
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.NotNull(result);
        Assert.Equal("padded-output", result.Password);
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
    public async Task GetCredentialAsync_InvalidExecutable_ReturnsNull()
    {
        var provider = CreateProvider(
            "nonexistent-binary-xyz --get-password");
        var result = await provider.GetCredentialAsync("host", 22, "user", "title");

        Assert.Null(result);
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
    public void ExpandTemplate_RegularExe_StillStripsSemicolon()
    {
        var provider = CreateProvider("op.exe get {Host}");
        var expanded = provider.ExpandTemplate(
            "op.exe get {Host}", "host;injected", 22, "user", "title");
        Assert.Equal("op.exe get hostinjected", expanded);
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
        // Empty template → IsShellTarget returns true → strict
        Assert.DoesNotContain("(", expanded);
    }
}
