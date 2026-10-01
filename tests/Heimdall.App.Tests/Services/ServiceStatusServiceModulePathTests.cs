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
}
