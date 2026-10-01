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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public sealed class WindowsPowerShellModulePathTests
{
    private const string PersonalPowerShell7 = @"C:\Users\u\Documents\PowerShell\Modules";
    private const string SharedPowerShell7 = @"C:\Program Files\PowerShell\Modules";
    private const string PowerShell7Home = @"c:\program files\powershell\7\Modules";
    private const string PowerShell7HomeExecutable = @"C:\Program Files\PowerShell\7\pwsh.exe";
    private const string AllUsersWindowsPowerShell = @"C:\Program Files\WindowsPowerShell\Modules";
    private const string SystemWindowsPowerShell = @"C:\WINDOWS\system32\WindowsPowerShell\v1.0\Modules";
    private const string PersonalWindowsPowerShell = @"C:\Users\u\Documents\WindowsPowerShell\Modules";
    private const string CustomMachineModules = @"D:\Corp\Modules";
    private const string CustomUserModules = @"E:\Tools\PsModules";

    private static readonly PowerShellModuleRoots Roots = new(
        [PersonalPowerShell7, SharedPowerShell7],
        [PersonalWindowsPowerShell, AllUsersWindowsPowerShell, SystemWindowsPowerShell]);

    [Fact]
    public void FromInherited_PowerShell7Path_RemovesItsEntriesAndKeepsEveryOtherInOrder()
    {
        // The module path a PowerShell 7 session hands its children (CI run 36765683841).
        string inherited = string.Join(
            ';',
            PersonalPowerShell7,
            SharedPowerShell7,
            PowerShell7Home,
            AllUsersWindowsPowerShell,
            SystemWindowsPowerShell,
            CustomMachineModules,
            CustomUserModules);

        string? result = WindowsPowerShellModulePath.FromInherited(
            inherited,
            Roots,
            FileExistsOnly(PowerShell7HomeExecutable));

        Assert.Equal(
            string.Join(';', AllUsersWindowsPowerShell, SystemWindowsPowerShell, CustomMachineModules, CustomUserModules),
            result);
    }

    [Fact]
    public void FromInherited_PowerShell7HomeOutsideProgramFiles_IsRecognisedByItsPwshExecutable()
    {
        string inherited = string.Join(';', @"D:\Portable\pwsh-7.5\Modules", CustomMachineModules);

        string? result = WindowsPowerShellModulePath.FromInherited(
            inherited,
            Roots,
            FileExistsOnly(@"D:\Portable\pwsh-7.5\pwsh.exe"));

        Assert.Equal(CustomMachineModules, result);
    }

    [Fact]
    public void FromInherited_ModulesDirectoryWithoutPwshBesideIt_IsKept()
    {
        string? result = WindowsPowerShellModulePath.FromInherited(
            CustomMachineModules,
            Roots,
            FileExistsOnly(PowerShell7HomeExecutable));

        Assert.Equal(CustomMachineModules, result);
    }

    [Fact]
    public void FromInherited_MatchIgnoresCaseSurroundingSpaceAndTrailingSeparator()
    {
        string inherited = string.Join(
            ';',
            @" C:\PROGRAM FILES\POWERSHELL\MODULES\ ",
            @"c:\users\U\documents\powershell\modules/",
            CustomMachineModules);

        string? result = WindowsPowerShellModulePath.FromInherited(
            inherited,
            Roots,
            FileExistsOnly());

        Assert.Equal(CustomMachineModules, result);
    }

    [Fact]
    public void FromInherited_EmptySegments_AreDropped()
    {
        string? result = WindowsPowerShellModulePath.FromInherited(
            ";" + CustomMachineModules + ";;" + CustomUserModules + ";",
            Roots,
            FileExistsOnly());

        Assert.Equal(CustomMachineModules + ";" + CustomUserModules, result);
    }

    [Fact]
    public void FromInherited_OnlyPowerShell7Entries_FallsBackToWindowsPowerShellDefaults()
    {
        // Windows PowerShell keeps an inherited module path verbatim, an empty one included,
        // and would then start with no module directory at all.
        string inherited = string.Join(';', PersonalPowerShell7, SharedPowerShell7, PowerShell7Home);

        string? result = WindowsPowerShellModulePath.FromInherited(
            inherited,
            Roots,
            FileExistsOnly(PowerShell7HomeExecutable));

        Assert.Equal(
            string.Join(';', PersonalWindowsPowerShell, AllUsersWindowsPowerShell, SystemWindowsPowerShell),
            result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromInherited_NoInheritedPath_ReturnsNullSoWindowsPowerShellBuildsItsOwn(string? inherited)
    {
        string? result = WindowsPowerShellModulePath.FromInherited(inherited, Roots, FileExistsOnly());

        Assert.Null(result);
    }

    [Theory]
    [InlineData("powershell.exe", true)]
    [InlineData("POWERSHELL.EXE", true)]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", true)]
    [InlineData("pwsh.exe", false)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", false)]
    [InlineData(@"C:\Tools\powershell.exe.bak", false)]
    public void IsWindowsPowerShell_MatchesOnlyTheWindowsPowerShellExecutable(string executable, bool expected)
    {
        Assert.Equal(expected, WindowsPowerShellModulePath.IsWindowsPowerShell(executable));
    }

    private static Func<string, bool> FileExistsOnly(params string[] existingFiles)
    {
        HashSet<string> files = new(existingFiles, StringComparer.OrdinalIgnoreCase);
        return files.Contains;
    }
}
