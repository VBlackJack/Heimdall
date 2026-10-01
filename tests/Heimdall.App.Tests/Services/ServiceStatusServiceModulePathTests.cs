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

using System.Diagnostics;
using Heimdall.App.Services;

namespace Heimdall.App.Tests.Services;

/// <summary>
/// The module path the service listing's Windows PowerShell host is given, the same decision as
/// the update relauncher's (see <see cref="SystemUpdateInstallerHostModulePathTests"/>).
/// </summary>
public sealed class ServiceStatusServiceModulePathTests
{
    private const string PowerShell7Host = @"C:\Program Files\PowerShell\7\pwsh.exe";
    private const string PersonalPowerShell7 = @"C:\Users\u\Documents\PowerShell\Modules";
    private const string SharedPowerShell7 = @"C:\Program Files\PowerShell\Modules";
    private const string PowerShell7Home = @"c:\program files\powershell\7\Modules";
    private const string AllUsersWindowsPowerShell = @"C:\Program Files\WindowsPowerShell\Modules";
    private const string SystemWindowsPowerShell = @"C:\WINDOWS\system32\WindowsPowerShell\v1.0\Modules";
    private const string PersonalWindowsPowerShell = @"C:\Users\u\Documents\WindowsPowerShell\Modules";
    private const string CustomMachineModules = @"D:\Corp\Modules";

    private static readonly PowerShellModuleRoots Roots = new(
        [PersonalPowerShell7, SharedPowerShell7],
        [PersonalWindowsPowerShell, AllUsersWindowsPowerShell, SystemWindowsPowerShell]);

    [Fact]
    public void ServiceList_InheritedFromPowerShell7_GivesWindowsPowerShellItsOwnModulePath()
    {
        string inherited = string.Join(
            ';',
            PersonalPowerShell7,
            SharedPowerShell7,
            PowerShell7Home,
            AllUsersWindowsPowerShell,
            SystemWindowsPowerShell,
            CustomMachineModules);

        ProcessStartInfo startInfo = ServiceStatusService.CreateServiceListStartInfo(
            inherited,
            Roots,
            path => string.Equals(path, PowerShell7Host, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(
            string.Join(';', AllUsersWindowsPowerShell, SystemWindowsPowerShell, CustomMachineModules),
            startInfo.Environment[WindowsPowerShellModulePath.VariableName]);
    }

    [Fact]
    public void ServiceAction_InheritedFromPowerShell7_SetsWindowsPowerShellModulePathBeforeTheCmdlet()
    {
        string inherited = string.Join(
            ';',
            PersonalPowerShell7,
            SharedPowerShell7,
            PowerShell7Home,
            AllUsersWindowsPowerShell,
            SystemWindowsPowerShell,
            CustomMachineModules);

        string script = ServiceStatusService.BuildServiceActionScript(
            "Start-Service",
            "bits",
            inherited,
            Roots,
            path => string.Equals(path, PowerShell7Host, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(
            @"$env:PSModulePath = 'C:\Program Files\WindowsPowerShell\Modules;"
                + @"C:\WINDOWS\system32\WindowsPowerShell\v1.0\Modules;D:\Corp\Modules'; Start-Service 'bits'",
            script);
    }

    [Fact]
    public void ServiceAction_ModulePathWithApostrophe_StaysInsideTheLiteral()
    {
        string inherited = string.Join(';', SharedPowerShell7, @"D:\O'Brien's\Modules");

        string script = ServiceStatusService.BuildServiceActionScript(
            "Stop-Service",
            "O'Brien",
            inherited,
            Roots,
            _ => false);

        Assert.Equal(@"$env:PSModulePath = 'D:\O''Brien''s\Modules'; Stop-Service 'O''Brien'", script);
    }

    /// <summary>
    /// PowerShell closes a single-quoted string on any of four typographic quotes as well as the
    /// ASCII one; escaping only the apostrophe let such a path end the literal early.
    /// </summary>
    [Theory]
    [InlineData(0x2018)]
    [InlineData(0x2019)]
    [InlineData(0x201A)]
    [InlineData(0x201B)]
    public void ServiceAction_TypographicQuote_IsDoubledInBothLiterals(int codePoint)
    {
        string quote = ((char)codePoint).ToString();
        string inherited = string.Join(';', SharedPowerShell7, $@"D:\O{quote}Brien\Modules");

        string script = ServiceStatusService.BuildServiceActionScript(
            "Start-Service",
            $"a{quote}b",
            inherited,
            Roots,
            _ => false);

        Assert.Equal(
            $@"$env:PSModulePath = 'D:\O{quote}{quote}Brien\Modules'; Start-Service 'a{quote}{quote}b'",
            script);
    }

    [Fact]
    public void ServiceAction_NothingInherited_SetsWindowsPowerShellDefaults()
    {
        string script = ServiceStatusService.BuildServiceActionScript(
            "Restart-Service",
            "  bits  ",
            null,
            Roots,
            _ => false);

        Assert.Equal(
            @"$env:PSModulePath = 'C:\Users\u\Documents\WindowsPowerShell\Modules;"
                + @"C:\Program Files\WindowsPowerShell\Modules;"
                + @"C:\WINDOWS\system32\WindowsPowerShell\v1.0\Modules'; Restart-Service 'bits'",
            script);
    }
}
