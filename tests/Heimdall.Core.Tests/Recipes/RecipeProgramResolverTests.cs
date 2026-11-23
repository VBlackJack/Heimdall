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

using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>Which program may run: shape, search places, roots, extension, links, seat belt.</summary>
public sealed class RecipeProgramResolverTests
{
    private const string System32 = @"C:\Windows\System32";

    private static readonly IReadOnlyList<string> NoFolders = [];

    /// <summary>The names the plan asks for; kept here so that removing one from the product list fails a test.</summary>
    private static readonly string[] SeatBeltNames =
    [
        "cmd", "powershell", "pwsh", "bash", "sh", "zsh", "wsl", "cscript", "wscript", "mshta",
        "python", "pythonw", "py", "node", "perl", "ruby", "php", "java", "javaw",
        "msiexec", "rundll32", "regsvr32", "regsvcs", "regasm", "installutil", "msbuild",
        "certutil", "bitsadmin", "wmic", "forfiles", "schtasks", "sc", "reg", "net", "net1",
        "curl", "ssh", "scp", "sftp", "ftp", "tftp", "git", "tar", "robocopy", "xcopy",
        "expand", "extrac32", "makecab", "hh", "control", "explorer", "conhost", "cmstp",
        "odbcconf", "pcalua", "scriptrunner", "at", "taskkill",
    ];

    public static TheoryData<string> EverySeatBeltName()
    {
        TheoryData<string> data = [];
        foreach (string name in SeatBeltNames)
            data.Add(name);

        return data;
    }

    private static FakeRecipeMachine Machine() => new();

    private static RecipeProgramVerdict Resolve(FakeRecipeMachine machine, string program, IReadOnlyList<string>? folders = null)
        => new RecipeProgramResolver(machine).Resolve(program, folders ?? NoFolders);

    [Fact]
    public void ABareName_IsFoundInAnAllowedPathEntry()
    {
        FakeRecipeMachine machine = Machine().WithFile(System32 + @"\hostname.exe");
        machine.Path.Add(System32);

        RecipeProgramVerdict verdict = Resolve(machine, "hostname.exe");

        Assert.True(verdict.IsResolved);
        Assert.Equal(System32 + @"\hostname.exe", verdict.ResolvedPath);
        Assert.Equal(RecipeProgramRefusal.None, verdict.Refusal);
    }

    [Fact]
    public void ABareName_SkipsAPathEntryOutsideTheRoots_EvenWhenItComesFirst()
    {
        FakeRecipeMachine machine = Machine()
            .WithFile(@"C:\Evil\hostname.exe")
            .WithFile(System32 + @"\hostname.exe");
        machine.Path.AddRange([@"C:\Evil", System32]);

        RecipeProgramVerdict verdict = Resolve(machine, "hostname.exe");

        Assert.Equal(System32 + @"\hostname.exe", verdict.ResolvedPath);
        Assert.DoesNotContain(machine.Probed, probe => probe.StartsWith(@"C:\Evil", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ABareName_PresentOnlyOutsideTheRoots_IsNotFound()
    {
        FakeRecipeMachine machine = Machine().WithFile(@"C:\Evil\hostname.exe");
        machine.Path.Add(@"C:\Evil");

        Assert.Equal(RecipeProgramRefusal.NotFound, Resolve(machine, "hostname.exe").Refusal);
    }

    [Fact]
    public void ABareName_FollowsPathOrderInsideTheRoots()
    {
        FakeRecipeMachine machine = Machine()
            .WithFile(@"C:\Windows\hostname.exe")
            .WithFile(System32 + @"\hostname.exe");
        machine.Path.AddRange([System32, @"C:\Windows"]);

        Assert.Equal(System32 + @"\hostname.exe", Resolve(machine, "hostname.exe").ResolvedPath);
    }

    [Fact]
    public void ABareName_WithoutAnExtension_GetsExe()
    {
        FakeRecipeMachine machine = Machine().WithFile(System32 + @"\hostname.exe");
        machine.Path.Add(System32);

        Assert.Equal(System32 + @"\hostname.exe", Resolve(machine, "hostname").ResolvedPath);
    }

    [Fact]
    public void ABareName_IsNeverSearchedInTheCurrentOrApplicationDirectory()
    {
        FakeRecipeMachine machine = Machine().WithFile(Path.Combine(Directory.GetCurrentDirectory(), "tool.exe"));
        machine.Files.Add(Path.Combine(AppContext.BaseDirectory, "tool.exe"));
        machine.Path.AddRange(["", ".", "tools", @"..\bin", "%SystemRoot%", "C:", "\\Windows", @"\\server\share"]);

        RecipeProgramVerdict verdict = Resolve(machine, "tool.exe");

        Assert.Equal(RecipeProgramRefusal.NotFound, verdict.Refusal);
        Assert.Empty(machine.Probed);
    }

    [Fact]
    public void AQuotedPathEntry_IsUnquotedBeforeItIsSearched()
    {
        FakeRecipeMachine machine = Machine().WithFile(System32 + @"\hostname.exe");
        machine.Path.Add("\"" + System32 + "\"");

        Assert.True(Resolve(machine, "hostname.exe").IsResolved);
    }

    [Theory]
    [InlineData("tool.cmd", RecipeProgramRefusal.NotAnExecutable)]
    [InlineData("tool.bat", RecipeProgramRefusal.NotAnExecutable)]
    [InlineData("tool.com", RecipeProgramRefusal.NotAnExecutable)]
    [InlineData("tool.scr", RecipeProgramRefusal.NotAnExecutable)]
    [InlineData("tool.exe.", RecipeProgramRefusal.NotAnExecutable)]
    [InlineData("tool.exe ", RecipeProgramRefusal.InvalidShape)]
    [InlineData("tool.exe:stream", RecipeProgramRefusal.InvalidShape)]
    [InlineData("tool.exe.exe.txt", RecipeProgramRefusal.NotAnExecutable)]
    [InlineData(".exe", RecipeProgramRefusal.NotAnExecutable)]
    public void ABareNameThatIsNotExactlyAnExe_IsRefused(string program, RecipeProgramRefusal expected)
    {
        FakeRecipeMachine machine = Machine();
        machine.Path.Add(System32);
        foreach (string file in new[] { "tool.cmd", "tool.bat", "tool.com", "tool.scr", "tool.exe.", "tool.exe ", "tool.exe", "tool" })
            machine.Files.Add(System32 + "\\" + file);

        Assert.Equal(expected, Resolve(machine, program).Refusal);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\tool.cmd")]
    [InlineData(@"C:\Windows\System32\tool.bat")]
    [InlineData(@"C:\Windows\System32\tool.com")]
    [InlineData(@"C:\Windows\System32\tool.scr")]
    [InlineData(@"C:\Windows\System32\tool.exe.")]
    [InlineData(@"C:\Windows\System32\tool.exe ")]
    [InlineData(@"C:\Windows\System32\tool.exe:stream")]
    [InlineData(@"C:\Windows\System32\tool.exe::$DATA")]
    [InlineData(@"C:\Windows\System32\tool")]
    public void AnAbsolutePathThatIsNotExactlyAnExe_IsRefused(string program)
    {
        FakeRecipeMachine machine = Machine().WithFile(program);

        RecipeProgramVerdict verdict = Resolve(machine, program);

        Assert.Equal(RecipeProgramRefusal.NotAnExecutable, verdict.Refusal);
        Assert.Null(verdict.ResolvedPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"sub\tool.exe")]
    [InlineData("sub/tool.exe")]
    [InlineData(@".\tool.exe")]
    [InlineData(@"..\tool.exe")]
    [InlineData(@"\\server\share\tool.exe")]
    [InlineData(@"\\?\C:\Windows\System32\hostname.exe")]
    [InlineData(@"\\.\pipe\tool")]
    [InlineData(@"\Windows\System32\hostname.exe")]
    [InlineData(@"C:hostname.exe")]
    [InlineData(@"C:\Windows\\System32\hostname.exe")]
    [InlineData(@"C:\Windows.\System32\hostname.exe")]
    [InlineData(@"C:\Windows \System32\hostname.exe")]
    [InlineData(@"C:\Wind*ows\hostname.exe")]
    [InlineData("tool*.exe")]
    [InlineData("tool?.exe")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData(" tool.exe")]
    public void AProgramThatIsNeitherABareNameNorADrivePath_IsRefused(string program)
    {
        FakeRecipeMachine machine = Machine().WithFile(System32 + @"\hostname.exe");
        machine.Path.Add(System32);

        RecipeProgramVerdict verdict = Resolve(machine, program);

        Assert.False(verdict.IsResolved);
        Assert.NotEqual(RecipeProgramRefusal.None, verdict.Refusal);
    }

    [Theory]
    [InlineData(@"%SystemRoot%\System32\hostname.exe")]
    [InlineData("%COMSPEC%")]
    [InlineData("host%name.exe")]
    [InlineData("host\u202Ename.exe")]
    [InlineData("host\u0001name.exe")]
    [InlineData("host\tname.exe")]
    [InlineData("host\u200Bname.exe")]
    public void APercentSignOrAControlOrFormatCharacter_IsRefusedAsAForbiddenCharacter(string program)
    {
        FakeRecipeMachine machine = Machine();
        machine.Path.Add(System32);

        Assert.Equal(RecipeProgramRefusal.ForbiddenCharacter, Resolve(machine, program).Refusal);
    }

    [Fact]
    public void ATooLongProgram_IsRefused()
    {
        string program = @"C:\Windows\" + new string('a', RecipeLimits.MaxProgramLength) + ".exe";

        Assert.Equal(RecipeProgramRefusal.InvalidShape, Resolve(Machine(), program).Refusal);
    }

    [Fact]
    public void AFolderWhoseNameStartsLikeARoot_IsNotUnderThatRoot()
    {
        FakeRecipeMachine machine = Machine().WithFile(@"C:\Program Files Evil\x.exe");

        RecipeProgramVerdict verdict = Resolve(machine, @"C:\Program Files Evil\x.exe");

        Assert.Equal(RecipeProgramRefusal.OutsideAllowedRoots, verdict.Refusal);
        Assert.Empty(machine.Probed);
    }

    [Theory]
    [InlineData(@"C:\Program Files", @"C:\Program Files Evil", false)]
    [InlineData(@"C:\Program Files", @"C:\Program Files\x", true)]
    [InlineData(@"C:\Program Files", @"c:\PROGRAM FILES\x\y.exe", true)]
    [InlineData(@"C:\Program Files", @"C:\Program Files", true)]
    [InlineData(@"C:\Program Files\", @"C:\Program Files\x", true)]
    [InlineData(@"D:\", @"D:\x", true)]
    [InlineData(@"D:\", @"E:\x", false)]
    [InlineData(@"", @"C:\x", false)]
    public void ThePrefixTest_LooksAtWholeSegmentsIgnoringCase(string root, string path, bool expected)
    {
        Assert.Equal(expected, RecipeProgramResolver.IsUnderOrEqual(path, root));
    }

    [Fact]
    public void ADotDotThatLeavesTheRoots_IsRefusedAfterCanonicalisation()
    {
        FakeRecipeMachine machine = Machine().WithFile(@"C:\Evil\x.exe");

        RecipeProgramVerdict verdict = Resolve(machine, @"C:\Windows\..\Evil\x.exe");

        Assert.Equal(RecipeProgramRefusal.OutsideAllowedRoots, verdict.Refusal);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\x.exe")]
    [InlineData(@"C:\Windows\x.exe")]
    [InlineData(@"C:\Program Files\Tool\x.exe")]
    [InlineData(@"C:\Program Files (x86)\Tool\x.exe")]
    [InlineData(@"c:\program files\tool\x.EXE")]
    [InlineData(@"C:/Program Files/Tool/x.exe")]
    public void AnAbsolutePathUnderASystemRoot_Resolves(string program)
    {
        FakeRecipeMachine machine = Machine().WithFile(Path.GetFullPath(program));

        RecipeProgramVerdict verdict = Resolve(machine, program);

        Assert.True(verdict.IsResolved);
        Assert.Equal(Path.GetFullPath(program), verdict.ResolvedPath);
    }

    [Fact]
    public void AnAbsolutePath_ThatDoesNotExist_IsNotFound()
    {
        Assert.Equal(RecipeProgramRefusal.NotFound, Resolve(Machine(), System32 + @"\missing.exe").Refusal);
    }

    [Fact]
    public void AFolderTheUserAdded_IsAnAllowedRoot()
    {
        FakeRecipeMachine machine = Machine().WithFile(@"D:\tools\sub\x.exe");

        Assert.Equal(RecipeProgramRefusal.OutsideAllowedRoots, Resolve(machine, @"D:\tools\sub\x.exe").Refusal);
        Assert.True(Resolve(machine, @"D:\tools\sub\x.exe", [@"D:\tools"]).IsResolved);
        Assert.True(Resolve(machine, @"D:\tools\sub\x.exe", [@"D:\tools\"]).IsResolved);
        Assert.Equal(RecipeProgramRefusal.OutsideAllowedRoots, Resolve(machine, @"D:\tools\sub\x.exe", [@"D:\toolsmith"]).Refusal);
    }

    [Fact]
    public void AUserFolder_AlsoOpensItsPathEntries()
    {
        FakeRecipeMachine machine = Machine().WithFile(@"D:\tools\x.exe");
        machine.Path.Add(@"D:\tools");

        Assert.Equal(RecipeProgramRefusal.NotFound, Resolve(machine, "x.exe").Refusal);
        Assert.Equal(@"D:\tools\x.exe", Resolve(machine, "x.exe", [@"D:\tools"]).ResolvedPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tools")]
    [InlineData(@"\\server\share")]
    [InlineData("%TEMP%")]
    [InlineData("D:tools")]
    public void AnUnusableUserFolder_IsIgnoredAndOpensNothing(string folder)
    {
        FakeRecipeMachine machine = Machine().WithFile(@"D:\tools\x.exe");

        Assert.Equal(RecipeProgramRefusal.OutsideAllowedRoots, Resolve(machine, @"D:\tools\x.exe", [folder]).Refusal);
    }

    [Theory]
    [MemberData(nameof(EverySeatBeltName))]
    public void EverySeatBeltName_IsRefusedByFileName(string name)
    {
        string path = System32 + "\\" + name + ".exe";
        FakeRecipeMachine machine = Machine().WithFile(path);
        machine.Path.Add(System32);

        Assert.Equal(RecipeProgramRefusal.ShellOrInterpreter, Resolve(machine, path).Refusal);
        Assert.Equal(RecipeProgramRefusal.ShellOrInterpreter, Resolve(machine, name).Refusal);
        Assert.Equal(RecipeProgramRefusal.ShellOrInterpreter, Resolve(machine, name.ToUpperInvariant() + ".EXE").Refusal);
    }

    [Fact]
    public void TheSeatBeltList_HoldsAtLeastEveryNameTheFileNamesTest()
    {
        foreach (string name in SeatBeltNames)
            Assert.True(RecipeSeatBelt.RefusedNames.Contains(name), name);
    }

    [Fact]
    public void AnOrdinaryTool_IsNotRefusedBySeatBelt()
    {
        FakeRecipeMachine machine = Machine().WithFile(System32 + @"\hostname.exe");

        Assert.True(Resolve(machine, System32 + @"\hostname.exe").IsResolved);
    }

    [Theory]
    [InlineData("CMD.EXE")]
    [InlineData("Cmd.Exe")]
    [InlineData("Cmd.Exe.MUI")]
    [InlineData("powershell.exe")]
    [InlineData("MSHTA.EXE")]
    public void AnInnocentNameWhoseVersionResourceNamesAShell_IsRefused(string originalName)
    {
        string path = @"C:\Program Files\Tool\helper.exe";
        FakeRecipeMachine machine = Machine().WithFile(path);
        machine.OriginalNames[path] = originalName;

        Assert.Equal(RecipeProgramRefusal.ShellOrInterpreterByOriginalName, Resolve(machine, path).Refusal);
    }

    [Fact]
    public void AVersionResourceThatNamesAnOrdinaryFile_OrNothing_IsNotRefused()
    {
        string path = @"C:\Program Files\Tool\helper.exe";
        FakeRecipeMachine machine = Machine().WithFile(path);
        Assert.True(Resolve(machine, path).IsResolved);

        machine.OriginalNames[path] = "helper.exe";
        Assert.True(Resolve(machine, path).IsResolved);
    }

    [Fact]
    public void ALinkWhoseTargetLeavesTheRoots_IsRefused()
    {
        string path = @"C:\Program Files\Tool\x.exe";
        FakeRecipeMachine machine = Machine().WithFile(path);
        machine.Finals[path] = @"D:\Elsewhere\x.exe";

        RecipeProgramVerdict verdict = Resolve(machine, path);

        Assert.Equal(RecipeProgramRefusal.LinkLeavesAllowedRoots, verdict.Refusal);
        Assert.Null(verdict.ResolvedPath);
    }

    [Fact]
    public void ALinkWhoseTargetStaysInsideTheRoots_ResolvesToTheTarget()
    {
        string path = @"C:\Program Files\Tool\x.exe";
        FakeRecipeMachine machine = Machine().WithFile(path);
        machine.Finals[path] = @"C:\Program Files\Tool\v2\real.exe";

        Assert.Equal(@"C:\Program Files\Tool\v2\real.exe", Resolve(machine, path).ResolvedPath);
    }

    [Fact]
    public void ALinkToAShellInsideTheRoots_IsRefusedBySeatBelt()
    {
        string path = @"C:\Program Files\Tool\harmless.exe";
        FakeRecipeMachine machine = Machine().WithFile(path);
        machine.Finals[path] = System32 + @"\cmd.exe";

        Assert.Equal(RecipeProgramRefusal.ShellOrInterpreter, Resolve(machine, path).Refusal);
    }

    [Fact]
    public void ALinkToSomethingThatIsNotAnExe_IsRefused()
    {
        string path = @"C:\Program Files\Tool\x.exe";
        FakeRecipeMachine machine = Machine().WithFile(path);
        machine.Finals[path] = @"C:\Program Files\Tool\notes.txt";

        Assert.Equal(RecipeProgramRefusal.NotAnExecutable, Resolve(machine, path).Refusal);
    }

    [Fact]
    public void AFinalPathThatCannotBeResolved_IsRefused()
    {
        string path = @"C:\Program Files\Tool\x.exe";
        FakeRecipeMachine machine = Machine().WithFile(path);
        machine.Finals[path] = null;

        Assert.Equal(RecipeProgramRefusal.CannotResolve, Resolve(machine, path).Refusal);
    }

    [Fact]
    public void TheFirstFileFound_DecidesEvenWhenALaterPathEntryHoldsAGoodOne()
    {
        string bad = @"C:\Windows\x.exe";
        FakeRecipeMachine machine = Machine().WithFile(bad).WithFile(System32 + @"\x.exe");
        machine.Finals[bad] = @"D:\Elsewhere\x.exe";
        machine.Path.AddRange([@"C:\Windows", System32]);

        Assert.Equal(RecipeProgramRefusal.LinkLeavesAllowedRoots, Resolve(machine, "x.exe").Refusal);
    }

    [Fact]
    public void TheRefusalFragment_IsSafeToDisplay()
    {
        RecipeProgramVerdict verdict = Resolve(Machine(), "a\u202Eb\u0001.exe");

        Assert.Equal(@"a\u202Eb\u0001.exe", verdict.Fragment);
    }

    [Fact]
    public void TheSamePathComparison_SaysSameDifferentOrNoLongerResolves()
    {
        string stored = System32 + @"\hostname.exe";
        FakeRecipeMachine machine = Machine().WithFile(stored);
        machine.Path.Add(System32);
        RecipeProgramResolver resolver = new(machine);

        Assert.Equal(RecipeProgramComparison.Same, resolver.CompareWithStored("hostname.exe", NoFolders, stored));
        Assert.Equal(RecipeProgramComparison.Same, resolver.CompareWithStored("hostname.exe", NoFolders, stored.ToUpperInvariant()));

        // A same-named file now shadows it earlier in PATH.
        machine.Path.Insert(0, @"C:\Windows");
        machine.Files.Add(@"C:\Windows\hostname.exe");
        Assert.Equal(RecipeProgramComparison.Different, resolver.CompareWithStored("hostname.exe", NoFolders, stored));

        // The file is gone.
        machine.Files.Clear();
        Assert.Equal(RecipeProgramComparison.NoLongerResolves, resolver.CompareWithStored("hostname.exe", NoFolders, stored));
    }

    [Fact]
    public void TheSamePathComparison_ReportsNoLongerResolvesWhenTheProgramIsNowRefused()
    {
        string stored = @"C:\Program Files\Tool\x.exe";
        FakeRecipeMachine machine = Machine().WithFile(stored);
        RecipeProgramResolver resolver = new(machine);
        Assert.Equal(RecipeProgramComparison.Same, resolver.CompareWithStored(stored, NoFolders, stored));

        machine.Finals[stored] = @"D:\Elsewhere\x.exe";

        Assert.Equal(RecipeProgramComparison.NoLongerResolves, resolver.CompareWithStored(stored, NoFolders, stored));
    }

    [Fact]
    public void TheExtendedPathPrefix_IsRemovedFromADrivePath_AndUncIsMadePlain()
    {
        Assert.Equal(@"C:\x\y.exe", RecipeProgramNative.StripExtendedPrefix(@"\\?\C:\x\y.exe"));
        Assert.Equal(@"\\srv\share\y.exe", RecipeProgramNative.StripExtendedPrefix(@"\\?\UNC\srv\share\y.exe"));
        Assert.Equal(@"C:\x\y.exe", RecipeProgramNative.StripExtendedPrefix(@"C:\x\y.exe"));
    }

    // ---- The real file system, with temporary folders standing in for the roots ----

    [Fact]
    public void ARealFileInATemporaryRoot_ResolvesThroughRealFileChecks()
    {
        using ScratchFolder scratch = new();
        string file = scratch.File(@"pf\app\tool.exe");
        TempRecipeMachine machine = new() { WindowsDirectory = scratch.Dir("win"), ProgramFilesDirectory = Path.Combine(scratch.Root, "pf") };
        RecipeProgramResolver resolver = new(machine);

        RecipeProgramVerdict verdict = resolver.Resolve(file, NoFolders);

        Assert.True(verdict.IsResolved);
        Assert.Equal(file, verdict.ResolvedPath, ignoreCase: true);
        Assert.Equal(RecipeProgramRefusal.NotFound, resolver.Resolve(Path.Combine(scratch.Root, @"pf\app\missing.exe"), NoFolders).Refusal);
        Assert.Equal(RecipeProgramRefusal.NotFound, resolver.Resolve(scratch.Dir(@"pf\dir.exe"), NoFolders).Refusal);
    }

    [Fact]
    public void AJunctionWhoseTargetLeavesTheRoots_IsRefused_ForARealJunction()
    {
        using ScratchFolder scratch = new();
        string outside = scratch.Dir("outside");
        scratch.File(@"outside\evil.exe");
        string pf = scratch.Dir("pf");
        string junction = scratch.Junction(@"pf\jct", outside);
        TempRecipeMachine machine = new() { WindowsDirectory = scratch.Dir("win"), ProgramFilesDirectory = pf };
        machine.Path.Add(junction);
        RecipeProgramResolver resolver = new(machine);

        Assert.Equal(RecipeProgramRefusal.LinkLeavesAllowedRoots, resolver.Resolve(Path.Combine(junction, "evil.exe"), NoFolders).Refusal);
        Assert.Equal(RecipeProgramRefusal.LinkLeavesAllowedRoots, resolver.Resolve("evil.exe", NoFolders).Refusal);
    }

    [Fact]
    public void AJunctionThatStaysInsideTheRoots_ResolvesToTheFinalTarget_ForARealJunction()
    {
        using ScratchFolder scratch = new();
        string real = scratch.Dir(@"pf\real");
        scratch.File(@"pf\real\tool.exe");
        string junction = scratch.Junction(@"pf\alias", real);
        TempRecipeMachine machine = new() { WindowsDirectory = scratch.Dir("win"), ProgramFilesDirectory = Path.Combine(scratch.Root, "pf") };
        RecipeProgramResolver resolver = new(machine);

        RecipeProgramVerdict verdict = resolver.Resolve(Path.Combine(junction, "tool.exe"), NoFolders);

        Assert.True(verdict.IsResolved);
        Assert.Equal(Path.Combine(real, "tool.exe"), verdict.ResolvedPath, ignoreCase: true);
    }

    [Fact]
    public void ARootReachedThroughAJunction_AcceptsFilesUnderItsFinalForm_ForARealJunction()
    {
        using ScratchFolder scratch = new();
        string real = scratch.Dir("real");
        scratch.File(@"real\tool.exe");
        string viaJunction = scratch.Junction("alias", real);
        TempRecipeMachine machine = new() { WindowsDirectory = scratch.Dir("win") };
        RecipeProgramResolver resolver = new(machine);

        RecipeProgramVerdict verdict = resolver.Resolve(Path.Combine(viaJunction, "tool.exe"), [viaJunction]);

        Assert.True(verdict.IsResolved);
        Assert.Equal(Path.Combine(real, "tool.exe"), verdict.ResolvedPath, ignoreCase: true);
    }

    [Fact]
    public void TheSamePathComparison_NoticesARealJunctionRetargetedOutsideTheRoots()
    {
        using ScratchFolder scratch = new();
        string real = scratch.Dir(@"pf\real");
        scratch.File(@"pf\real\tool.exe");
        string junction = scratch.Junction(@"pf\alias", real);
        TempRecipeMachine machine = new() { WindowsDirectory = scratch.Dir("win"), ProgramFilesDirectory = Path.Combine(scratch.Root, "pf") };
        RecipeProgramResolver resolver = new(machine);
        string program = Path.Combine(junction, "tool.exe");
        string stored = resolver.Resolve(program, NoFolders).ResolvedPath!;

        Assert.Equal(RecipeProgramComparison.Same, resolver.CompareWithStored(program, NoFolders, stored));

        Directory.Delete(junction);
        string outside = scratch.Dir("outside");
        scratch.File(@"outside\tool.exe");
        Directory.CreateDirectory(junction);
        JunctionNative.SetMountPoint(junction, outside);

        Assert.Equal(RecipeProgramComparison.NoLongerResolves, resolver.CompareWithStored(program, NoFolders, stored));
    }

    [Fact]
    public void ARealFileWithArbitraryBytes_HasNoVersionResource()
    {
        using ScratchFolder scratch = new();
        string file = scratch.File("anything.exe");

        Assert.Null(RecipeMachine.Instance.ReadOriginalFileName(file));
    }

    [Fact]
    public void TheRealMachine_ReadsTheOriginalFileNameOfTheSystemCommandInterpreter_WithoutRunningIt()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        string? original = RecipeMachine.Instance.ReadOriginalFileName(cmd);

        // The version resource of the system interpreter names its language-neutral file.
        Assert.Equal("Cmd.Exe.MUI", original, ignoreCase: true);
    }

    [Fact]
    public void ACopyOfTheSystemInterpreterUnderAnInnocentName_IsRefusedByItsVersionResource_ForARealFile()
    {
        using ScratchFolder scratch = new();
        string copy = Path.Combine(scratch.Dir("pf"), "innocent.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), copy);
        TempRecipeMachine machine = new() { WindowsDirectory = scratch.Dir("win"), ProgramFilesDirectory = Path.Combine(scratch.Root, "pf") };

        Assert.Equal(RecipeProgramRefusal.ShellOrInterpreterByOriginalName, new RecipeProgramResolver(machine).Resolve(copy, NoFolders).Refusal);
    }

    [Fact]
    public void TheRealMachine_ResolvesASystemToolByAbsolutePathAndRefusesTheInterpreter()
    {
        string hostname = Path.Combine(Environment.SystemDirectory, "hostname.exe");
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        RecipeProgramResolver resolver = new();

        RecipeProgramVerdict good = resolver.Resolve(hostname, NoFolders);

        Assert.True(good.IsResolved);
        Assert.Equal(hostname, good.ResolvedPath, ignoreCase: true);
        Assert.Equal(RecipeProgramRefusal.ShellOrInterpreter, resolver.Resolve(cmd, NoFolders).Refusal);
        Assert.Equal(RecipeProgramRefusal.ShellOrInterpreter, resolver.Resolve("powershell", NoFolders).Refusal);
    }
}
