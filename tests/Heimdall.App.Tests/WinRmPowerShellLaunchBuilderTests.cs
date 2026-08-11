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

using Heimdall.App.Services.WinRm;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

public sealed class WinRmPowerShellLaunchBuilderTests
{
    [Fact]
    public void Build_CurrentUser_ProducesEnterPSSessionCommand()
    {
        WinRmPowerShellLaunchBuilder builder = new WinRmPowerShellLaunchBuilder(
            executableName => executableName == "pwsh.exe" ? @"C:\Program Files\PowerShell\7\pwsh.exe" : null);
        ServerProfileDto server = CreateServer();

        WinRmPowerShellLaunchSpec spec = builder.Build(server);

        Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", spec.Executable);
        Assert.Contains("-NoLogo -NoExit -NoProfile -Command", spec.Arguments, StringComparison.Ordinal);
        Assert.Contains("Enter-PSSession", spec.Arguments, StringComparison.Ordinal);
        Assert.Contains("-ComputerName 'server01.contoso.local'", spec.Arguments, StringComparison.Ordinal);
        Assert.Contains("-Port 5986", spec.Arguments, StringComparison.Ordinal);
        Assert.Contains("-Authentication Negotiate", spec.Arguments, StringComparison.Ordinal);
        Assert.Contains("-UseSSL", spec.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("-Credential", spec.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WhenPwshMissing_FallsBackToWindowsPowerShellName()
    {
        WinRmPowerShellLaunchBuilder builder = new WinRmPowerShellLaunchBuilder(FindWindowsPowerShellNameOnly);

        WinRmPowerShellLaunchSpec spec = builder.Build(CreateServer());

        Assert.Equal("powershell.exe", spec.Executable);
    }

    [Fact]
    public void Build_WhenPwshFound_UsesPwshExecutable()
    {
        WinRmPowerShellLaunchBuilder builder = new WinRmPowerShellLaunchBuilder(FindPwshAndWindowsPowerShell);

        WinRmPowerShellLaunchSpec spec = builder.Build(CreateServer());

        Assert.Equal(@"C:\Tools\pwsh.exe", spec.Executable);
    }

    [Fact]
    public void Build_WhenOnlyWindowsPowerShellFound_UsesWindowsPowerShellExecutable()
    {
        WinRmPowerShellLaunchBuilder builder = new WinRmPowerShellLaunchBuilder(FindWindowsPowerShellPathOnly);

        WinRmPowerShellLaunchSpec spec = builder.Build(CreateServer());

        Assert.Equal(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", spec.Executable);
    }

    [Fact]
    public void Build_WhenNoPowerShellHostFound_Throws()
    {
        WinRmPowerShellLaunchBuilder builder = new WinRmPowerShellLaunchBuilder(_ => null);

        WinRmConfigurationException ex =
            Assert.Throws<WinRmConfigurationException>(() => builder.Build(CreateServer()));

        Assert.Equal("ErrorWinRmNoPowerShellHost", ex.LocalizationKey);
    }

    [Fact]
    public void Build_CredentialMode_UsesBootstrapFileArgument()
    {
        WinRmPowerShellLaunchBuilder builder = new WinRmPowerShellLaunchBuilder(FindWindowsPowerShellNameOnly);
        ServerProfileDto server = CreateServer();
        server.WinRmIdentityMode = WinRmIdentityMode.Credential;
        server.WinRmUsername = @"CONTOSO\operator";
        server.WinRmPasswordEncrypted = "encrypted";

        WinRmPowerShellLaunchSpec spec = builder.Build(server, @"C:\Temp\heimdall winrm.ps1");

        Assert.Equal("powershell.exe", spec.Executable);
        Assert.Contains("-ExecutionPolicy Bypass -File", spec.Arguments, StringComparison.Ordinal);
        Assert.Contains("\"C:\\Temp\\heimdall winrm.ps1\"", spec.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTOSO", spec.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("encrypted", spec.Arguments, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_CredentialModeWithoutBootstrapPath_Throws()
    {
        WinRmPowerShellLaunchBuilder builder = new WinRmPowerShellLaunchBuilder(FindWindowsPowerShellNameOnly);
        ServerProfileDto server = CreateServer();
        server.WinRmIdentityMode = WinRmIdentityMode.Credential;

        WinRmConfigurationException ex =
            Assert.Throws<WinRmConfigurationException>(() => builder.Build(server));

        Assert.Equal("ErrorWinRmBootstrapPathMissing", ex.LocalizationKey);
    }

    [Fact]
    public void BuildEnterPSSessionCommand_WithCredentialExpression_AppendsCredential()
    {
        ServerProfileDto server = CreateServer();
        server.WinRmUseSsl = false;
        server.WinRmPort = 5985;

        string command = WinRmPowerShellLaunchBuilder.BuildEnterPSSessionCommand(
            server,
            "$credential");

        Assert.Equal(
            "Enter-PSSession -ComputerName 'server01.contoso.local' -Port 5985 -Authentication Negotiate -Credential $credential",
            command);
    }

    [Fact]
    public void BuildEnterPSSessionCommand_WithSslAndSkipCertificateCheck_AddsSessionOption()
    {
        ServerProfileDto server = CreateServer();
        server.WinRmSkipCertificateCheck = true;

        string command = WinRmPowerShellLaunchBuilder.BuildEnterPSSessionCommand(
            server,
            credentialExpression: null);

        Assert.Contains(
            "-SessionOption (New-PSSessionOption -SkipCACheck -SkipCNCheck -SkipRevocationCheck)",
            command,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BuildEnterPSSessionCommand_WithSslAndStrictCertificateCheck_OmitsSessionOption()
    {
        ServerProfileDto server = CreateServer();
        server.WinRmSkipCertificateCheck = false;

        string command = WinRmPowerShellLaunchBuilder.BuildEnterPSSessionCommand(
            server,
            credentialExpression: null);

        Assert.DoesNotContain("-SessionOption", command, StringComparison.Ordinal);
        Assert.DoesNotContain("SkipCACheck", command, StringComparison.Ordinal);
        Assert.DoesNotContain("SkipCNCheck", command, StringComparison.Ordinal);
        Assert.DoesNotContain("SkipRevocationCheck", command, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildEnterPSSessionCommand_WithHttpAndSkipCertificateCheck_OmitsSessionOption()
    {
        ServerProfileDto server = CreateServer();
        server.WinRmUseSsl = false;
        server.WinRmPort = DefaultPorts.WinRmHttp;
        server.WinRmSkipCertificateCheck = true;

        string command = WinRmPowerShellLaunchBuilder.BuildEnterPSSessionCommand(
            server,
            credentialExpression: null);

        Assert.DoesNotContain("-UseSSL", command, StringComparison.Ordinal);
        Assert.DoesNotContain("-SessionOption", command, StringComparison.Ordinal);
        Assert.DoesNotContain("SkipCACheck", command, StringComparison.Ordinal);
        Assert.DoesNotContain("SkipCNCheck", command, StringComparison.Ordinal);
        Assert.DoesNotContain("SkipRevocationCheck", command, StringComparison.Ordinal);
    }

    [Fact]
    public void QuoteCommandLineArgument_WithPath_DoesNotDoubleBackslashes()
    {
        string quoted = WinRmPowerShellLaunchBuilder.QuoteCommandLineArgument(
            @"C:\Temp\heimdall_winrm_x.ps1");

        Assert.Equal(@"""C:\Temp\heimdall_winrm_x.ps1""", quoted);
    }

    [Fact]
    public void QuoteCommandLineArgument_WithQuote_EscapesQuote()
    {
        string quoted = WinRmPowerShellLaunchBuilder.QuoteCommandLineArgument(
            "C:\\Temp\\heimdall \"winrm\".ps1");

        Assert.Equal("\"C:\\Temp\\heimdall \\\"winrm\\\".ps1\"", quoted);
    }

    [Fact]
    public void QuoteCommandLineArgument_WithTrailingBackslash_DoublesTrailingBackslash()
    {
        string quoted = WinRmPowerShellLaunchBuilder.QuoteCommandLineArgument(@"C:\Temp\");

        Assert.Equal(@"""C:\Temp\\""", quoted);
    }

    [Fact]
    public void QuotePowerShellLiteral_DoublesSingleQuotes()
    {
        string literal = WinRmPowerShellLaunchBuilder.QuotePowerShellLiteral("srv'01");

        Assert.Equal("'srv''01'", literal);
    }

    [Fact]
    public void Build_WithMissingPort_UsesSslAwareDefault()
    {
        ServerProfileDto server = CreateServer();
        server.WinRmPort = 0;
        server.WinRmUseSsl = true;

        string command = WinRmPowerShellLaunchBuilder.BuildEnterPSSessionCommand(
            server,
            credentialExpression: null);

        Assert.Contains("-Port 5986", command, StringComparison.Ordinal);
        Assert.Contains("-Authentication Negotiate", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithInvalidHost_Throws()
    {
        WinRmPowerShellLaunchBuilder builder = new WinRmPowerShellLaunchBuilder(_ => null);
        ServerProfileDto server = CreateServer();
        server.RemoteServer = "bad host; Remove-Item";

        WinRmConfigurationException ex =
            Assert.Throws<WinRmConfigurationException>(() => builder.Build(server));

        Assert.Equal("ErrorWinRmInvalidHost", ex.LocalizationKey);
    }

    [Fact]
    public void Build_WithUndefinedIdentityMode_ThrowsProfileInvalid()
    {
        WinRmPowerShellLaunchBuilder builder = new WinRmPowerShellLaunchBuilder(
            FindWindowsPowerShellNameOnly);
        ServerProfileDto server = CreateServer();
        server.WinRmIdentityMode = (WinRmIdentityMode)2;

        WinRmConfigurationException ex =
            Assert.Throws<WinRmConfigurationException>(() => builder.Build(server));

        Assert.Equal("ErrorWinRmProfileInvalid", ex.LocalizationKey);
    }

    private static ServerProfileDto CreateServer()
        => new ServerProfileDto
        {
            ConnectionType = "WINRM",
            RemoteServer = "server01.contoso.local",
            WinRmPort = DefaultPorts.WinRmHttps,
            WinRmUseSsl = true,
            WinRmIdentityMode = WinRmIdentityMode.CurrentUser
        };

    private static string? FindPwshAndWindowsPowerShell(string executableName)
    {
        if (string.Equals(executableName, "pwsh.exe", StringComparison.OrdinalIgnoreCase))
        {
            return @"C:\Tools\pwsh.exe";
        }

        if (string.Equals(executableName, "powershell.exe", StringComparison.OrdinalIgnoreCase))
        {
            return @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        }

        return null;
    }

    private static string? FindWindowsPowerShellPathOnly(string executableName)
    {
        return string.Equals(executableName, "powershell.exe", StringComparison.OrdinalIgnoreCase)
            ? @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
            : null;
    }

    private static string? FindWindowsPowerShellNameOnly(string executableName)
    {
        return string.Equals(executableName, "powershell.exe", StringComparison.OrdinalIgnoreCase)
            ? "powershell.exe"
            : null;
    }
}
