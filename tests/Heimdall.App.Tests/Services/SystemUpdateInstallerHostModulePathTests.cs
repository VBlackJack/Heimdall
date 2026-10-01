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
/// The module path the update relauncher's Windows PowerShell host is given.
/// </summary>
/// <remarks>
/// Heimdall started from a PowerShell 7 session inherits PowerShell 7's PSModulePath, and the
/// relauncher is started with <c>UseShellExecute=false</c>, so it inherited it verbatim. Measured
/// on 2026-10-01: Windows PowerShell 5.1 given that path loads PowerShell 7's manifests for
/// Microsoft.PowerShell.Utility, Management and Host, and cannot load its Security module at
/// all, so Get-AuthenticodeSignature and Get-FileHash do not exist. PowerShell 7 itself strips
/// those entries when it starts powershell.exe; this launch did not.
/// </remarks>
public sealed class SystemUpdateInstallerHostModulePathTests
{
    private const string WindowsPowerShellHost = @"C:\WINDOWS\System32\WindowsPowerShell\v1.0\powershell.exe";
    private const string PowerShell7Host = @"C:\Program Files\PowerShell\7\pwsh.exe";
    private const string PersonalPowerShell7 = @"C:\Users\u\Documents\PowerShell\Modules";
    private const string SharedPowerShell7 = @"C:\Program Files\PowerShell\Modules";
    private const string PowerShell7Home = @"c:\program files\powershell\7\Modules";
    private const string AllUsersWindowsPowerShell = @"C:\Program Files\WindowsPowerShell\Modules";
    private const string SystemWindowsPowerShell = @"C:\WINDOWS\system32\WindowsPowerShell\v1.0\Modules";
    private const string PersonalWindowsPowerShell = @"C:\Users\u\Documents\WindowsPowerShell\Modules";
    private const string CustomMachineModules = @"D:\Corp\Modules";
    private const string Arguments = "-NoProfile";

    private static readonly PowerShellModuleRoots Roots = new(
        [PersonalPowerShell7, SharedPowerShell7],
        [PersonalWindowsPowerShell, AllUsersWindowsPowerShell, SystemWindowsPowerShell]);

    /// <summary>
    /// The module path a PowerShell 7 session hands its children (CI run 36870815225), plus an
    /// entry no default carries, so the expected value cannot be met by the test process's own
    /// environment leaking through.
    /// </summary>
    private static readonly string InheritedFromPowerShell7 = string.Join(
        ';',
        PersonalPowerShell7,
        SharedPowerShell7,
        PowerShell7Home,
        AllUsersWindowsPowerShell,
        SystemWindowsPowerShell,
        CustomMachineModules);

    [Fact]
    public void DetachedStartInfo_InheritedFromPowerShell7_GivesWindowsPowerShellItsOwnModulePath()
    {
        ProcessStartInfo startInfo = SystemUpdateInstallerHost.CreateDetachedStartInfo(
            WindowsPowerShellHost,
            Arguments,
            InheritedFromPowerShell7,
            Roots,
            path => string.Equals(path, PowerShell7Host, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(
            string.Join(';', AllUsersWindowsPowerShell, SystemWindowsPowerShell, CustomMachineModules),
            startInfo.Environment[WindowsPowerShellModulePath.VariableName]);
    }

    [Fact]
    public void DetachedStartInfo_NothingInherited_LetsWindowsPowerShellBuildItsOwn()
    {
        ProcessStartInfo startInfo = SystemUpdateInstallerHost.CreateDetachedStartInfo(
            WindowsPowerShellHost,
            Arguments,
            inheritedModulePath: null,
            Roots,
            _ => false);

        // Absent, not empty: Windows PowerShell keeps an inherited value verbatim, an empty one
        // included, and builds its defaults only when there is none at all.
        Assert.False(startInfo.Environment.ContainsKey(WindowsPowerShellModulePath.VariableName));
    }

    [Fact]
    public void DetachedStartInfo_ForAnotherHost_LeavesTheEnvironmentAlone()
    {
        ProcessStartInfo startInfo = SystemUpdateInstallerHost.CreateDetachedStartInfo(
            PowerShell7Host,
            Arguments,
            InheritedFromPowerShell7,
            Roots,
            _ => true);

        Assert.Equal(
            Environment.GetEnvironmentVariable(WindowsPowerShellModulePath.VariableName),
            startInfo.Environment.TryGetValue(WindowsPowerShellModulePath.VariableName, out string? value)
                ? value
                : null);
    }
}
