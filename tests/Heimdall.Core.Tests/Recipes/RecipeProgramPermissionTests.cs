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
using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>
/// A program under Windows or Program Files is only trusted when nobody but the administrators can
/// change it, or the folders above it: some folders there are writable by any user.
/// </summary>
public sealed class RecipeProgramPermissionTests
{
    private const string UsersSid = "S-1-5-32-545";

    private const string AuthenticatedUsersSid = "S-1-5-11";

    private const string EveryoneSid = "S-1-1-0";

    private const string AllApplicationPackagesSid = "S-1-15-2-1";

    private const int ReadAndExecute = 0x1200A9;

    private const int WriteAttributes = 0x100;

    private const int Modify = 0x1301BF;

    private const int FullControl = 0x1F01FF;

    private const string Temp = @"C:\Windows\Temp";

    private const string TempProgram = @"C:\Windows\Temp\tool.exe";

    private const string System32 = @"C:\Windows\System32";

    private static FakeRecipeMachine Machine(string program)
        => new FakeRecipeMachine().WithFile(program);

    private static RecipeProgramVerdict Resolve(FakeRecipeMachine machine, string program, IReadOnlyList<string>? folders = null)
        => new RecipeProgramResolver(machine).Resolve(program, folders ?? []);

    // ---- the policy

    [Theory]
    [InlineData(UsersSid, ReadAndExecute, false)]
    [InlineData(UsersSid, WriteAttributes, false)]
    [InlineData(UsersSid, RecipeAccessPolicy.WriteData, true)]
    [InlineData(UsersSid, RecipeAccessPolicy.AppendData, true)]
    [InlineData(UsersSid, RecipeAccessPolicy.Delete, true)]
    [InlineData(UsersSid, RecipeAccessPolicy.DeleteSubdirectoriesAndFiles, true)]
    [InlineData(UsersSid, RecipeAccessPolicy.ChangePermissions, true)]
    [InlineData(UsersSid, RecipeAccessPolicy.TakeOwnership, true)]
    [InlineData(UsersSid, RecipeAccessPolicy.GenericWrite, true)]
    [InlineData(UsersSid, RecipeAccessPolicy.GenericAll, true)]
    [InlineData(AuthenticatedUsersSid, Modify, true)]
    [InlineData(EveryoneSid, FullControl, true)]
    [InlineData(AllApplicationPackagesSid, ReadAndExecute, false)]
    [InlineData(AllApplicationPackagesSid, RecipeAccessPolicy.WriteData, true)]
    [InlineData(RecipeAccessPolicy.SystemSid, FullControl, false)]
    [InlineData(RecipeAccessPolicy.AdministratorsSid, FullControl, false)]
    [InlineData(RecipeAccessPolicy.TrustedInstallerSid, FullControl, false)]
    public void APermissionEntry_CountsOnlyWhenItLetsAStandardUserModifyTheItem(string sid, int mask, bool expected)
    {
        RecipeAccessRule[] rules = [new(sid, mask, InheritOnly: false)];

        Assert.Equal(expected, RecipeAccessPolicy.GrantsModificationToStandardUsers(rules));
    }

    [Fact]
    public void TheCreatorOwnerPlaceholder_GivesNothingOnTheItemWhenItOnlyAppliesToWhatIsCreatedInside()
    {
        Assert.False(RecipeAccessPolicy.GrantsModificationToStandardUsers([new(RecipeAccessPolicy.CreatorOwnerSid, FullControl, InheritOnly: true)]));
        Assert.True(RecipeAccessPolicy.GrantsModificationToStandardUsers([new(RecipeAccessPolicy.CreatorOwnerSid, FullControl, InheritOnly: false)]));
    }

    [Fact]
    public void OneUntrustedEntryAmongTrustedOnesIsEnough()
    {
        RecipeAccessRule[] rules =
        [
            new(RecipeAccessPolicy.SystemSid, FullControl, false),
            new(RecipeAccessPolicy.AdministratorsSid, FullControl, false),
            new(UsersSid, ReadAndExecute, false),
            new(AuthenticatedUsersSid, RecipeAccessPolicy.WriteData, true),
        ];

        Assert.True(RecipeAccessPolicy.GrantsModificationToStandardUsers(rules));
    }

    // ---- the resolver, through the machine description

    [Fact]
    public void AProgramWhoseFolderIsWritableByUsers_IsRefused()
    {
        FakeRecipeMachine machine = Machine(TempProgram).WithRule(Temp, UsersSid, RecipeAccessPolicy.WriteData);

        RecipeProgramVerdict verdict = Resolve(machine, TempProgram);

        Assert.Equal(RecipeProgramRefusal.WritableByUser, verdict.Refusal);
        Assert.Null(verdict.ResolvedPath);
    }

    [Fact]
    public void AProgramThatIsItselfWritableByUsers_IsRefused()
    {
        FakeRecipeMachine machine = Machine(System32 + @"\tool.exe")
            .WithRule(System32 + @"\tool.exe", AuthenticatedUsersSid, Modify);

        Assert.Equal(RecipeProgramRefusal.WritableByUser, Resolve(machine, System32 + @"\tool.exe").Refusal);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows")]
    public void AFolderBetweenTheFileAndTheRoot_IsCheckedAsMuchAsTheRootItself(string writable)
    {
        FakeRecipeMachine machine = Machine(System32 + @"\tool.exe").WithRule(writable, UsersSid, RecipeAccessPolicy.AppendData);

        Assert.Equal(RecipeProgramRefusal.WritableByUser, Resolve(machine, System32 + @"\tool.exe").Refusal);
    }

    [Fact]
    public void ARootThatIsNotWritable_AndAFileAndFoldersThatAreNot_Resolve()
    {
        FakeRecipeMachine machine = Machine(System32 + @"\tool.exe")
            .WithRule(System32 + @"\tool.exe", RecipeAccessPolicy.TrustedInstallerSid, FullControl)
            .WithRule(System32, UsersSid, ReadAndExecute)
            .WithRule(@"C:\Windows", UsersSid, ReadAndExecute);

        RecipeProgramVerdict verdict = Resolve(machine, System32 + @"\tool.exe");

        Assert.True(verdict.IsResolved);
        Assert.Equal([System32 + @"\tool.exe", System32, @"C:\Windows"], machine.PermissionReads);
    }

    [Fact]
    public void TheWalkStopsAtTheRoot_ItNeverReadsTheDriveRootOrAnythingAboveIt()
    {
        FakeRecipeMachine machine = Machine(@"C:\Program Files\Vendor\bin\tool.exe");

        Assert.True(Resolve(machine, @"C:\Program Files\Vendor\bin\tool.exe").IsResolved);
        Assert.Equal(
            [@"C:\Program Files\Vendor\bin\tool.exe", @"C:\Program Files\Vendor\bin", @"C:\Program Files\Vendor", @"C:\Program Files"],
            machine.PermissionReads);
    }

    [Fact]
    public void ALooseProgramFilesSubfolderThatUsersCanWrite_IsRefused()
    {
        FakeRecipeMachine machine = Machine(@"C:\Program Files\Loose\tool.exe")
            .WithRule(@"C:\Program Files\Loose", AuthenticatedUsersSid, Modify);

        Assert.Equal(RecipeProgramRefusal.WritableByUser, Resolve(machine, @"C:\Program Files\Loose\tool.exe").Refusal);
    }

    [Fact]
    public void PermissionsThatCannotBeRead_RefuseTheProgram()
    {
        FakeRecipeMachine machine = Machine(System32 + @"\tool.exe");
        machine.UnreadablePermissions.Add(System32);

        Assert.Equal(RecipeProgramRefusal.CannotReadPermissions, Resolve(machine, System32 + @"\tool.exe").Refusal);

        FakeRecipeMachine fileUnreadable = Machine(System32 + @"\tool.exe");
        fileUnreadable.UnreadablePermissions.Add(System32 + @"\tool.exe");

        Assert.Equal(RecipeProgramRefusal.CannotReadPermissions, Resolve(fileUnreadable, System32 + @"\tool.exe").Refusal);
    }

    [Fact]
    public void AProgramUnderAFolderTheUserAdded_IsNotChecked_BecauseTheUserChoseToTrustIt()
    {
        FakeRecipeMachine machine = Machine(@"D:\tools\tool.exe").WithRule(@"D:\tools", EveryoneSid, FullControl);

        RecipeProgramVerdict verdict = Resolve(machine, @"D:\tools\tool.exe", [@"D:\tools"]);

        Assert.True(verdict.IsResolved);
        Assert.Empty(machine.PermissionReads);
    }

    [Fact]
    public void AUserFolderInsideTheWindowsFolder_DoesNotSwitchTheCheckOff_ForWhatLiesInsideIt()
    {
        FakeRecipeMachine machine = Machine(TempProgram).WithRule(Temp, UsersSid, FullControl);
        machine.WithFile(System32 + @"\other.exe").WithRule(System32, UsersSid, FullControl);

        Assert.Equal(RecipeProgramRefusal.WritableByUser, Resolve(machine, TempProgram, [Temp]).Refusal);
        Assert.Equal(RecipeProgramRefusal.WritableByUser, Resolve(machine, System32 + @"\other.exe", [Temp]).Refusal);
    }

    [Fact]
    public void AUserFolderThatIsAWritableSubfolderOfWindows_StillRefusesAProgramInIt()
    {
        FakeRecipeMachine machine = Machine(TempProgram).WithRule(Temp, UsersSid, Modify);

        Assert.Equal(RecipeProgramRefusal.WritableByUser, Resolve(machine, TempProgram, [Temp]).Refusal);
    }

    [Fact]
    public void AUserFolderThatIsTheWindowsFolderItself_StillRefusesAProgramInAWritableSubfolder()
    {
        FakeRecipeMachine machine = Machine(TempProgram).WithRule(Temp, UsersSid, Modify);

        Assert.Equal(RecipeProgramRefusal.WritableByUser, Resolve(machine, TempProgram, [@"C:\Windows"]).Refusal);
    }

    [Fact]
    public void AUserFolderUnderWindows_WithNothingWritableOnTheWay_Resolves()
    {
        FakeRecipeMachine machine = Machine(System32 + @"\tool.exe");

        Assert.Equal(RecipeProgramRefusal.None, Resolve(machine, System32 + @"\tool.exe", [System32]).Refusal);
        Assert.NotEmpty(machine.PermissionReads);
    }

    [Fact]
    public void ASeatBeltRefusal_ComesBeforeAnyPermissionRead()
    {
        FakeRecipeMachine machine = Machine(System32 + @"\cmd.exe");

        Assert.Equal(RecipeProgramRefusal.ShellOrInterpreter, Resolve(machine, System32 + @"\cmd.exe").Refusal);
        Assert.Empty(machine.PermissionReads);
    }

    // ---- the real machine

    [Fact]
    public void ARealSystemProgram_IsAccepted_AndTheTimeOfOneResolutionIsReasonable()
    {
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
        RecipeProgramResolver resolver = new();
        _ = resolver.Resolve(ping, []);

        Stopwatch clock = Stopwatch.StartNew();
        RecipeProgramVerdict verdict = resolver.Resolve(ping, []);
        clock.Stop();

        Assert.True(verdict.IsResolved, verdict.Refusal.ToString());
        Assert.Equal(ping, verdict.ResolvedPath, ignoreCase: true);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), clock.Elapsed.ToString());
    }

    [Fact]
    public void TheRealWindowsTempFolder_CouldNotBeTrusted_EitherItsPermissionsAreWritableByUsersOrTheyCannotBeRead()
    {
        // Measured on a standard Windows 11 account: the permission list of this folder cannot even
        // be read, which refuses a program there (CannotReadPermissions); where it can be read, the
        // Users group may create files in it, which refuses it too (WritableByUser). The folder is
        // only read, never written, and nothing is asserted on a machine that has no such folder.
        string temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        if (!Directory.Exists(temp))
            return;

        IReadOnlyList<RecipeAccessRule>? rules = RecipeMachine.Instance.ReadAllowRules(temp);

        Assert.True(rules is null || RecipeAccessPolicy.GrantsModificationToStandardUsers(rules));
    }

    [Fact]
    public void ARealSystemFile_HasReadablePermissionsThatOnlyTrustedPrincipalsCanModify()
    {
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");

        IReadOnlyList<RecipeAccessRule>? rules = RecipeMachine.Instance.ReadAllowRules(ping);

        Assert.NotNull(rules);
        Assert.False(RecipeAccessPolicy.GrantsModificationToStandardUsers(rules));
    }

    [Fact]
    public void ThePermissionsOfAFileThatDoesNotExist_CannotBeRead()
        => Assert.Null(RecipeMachine.Instance.ReadAllowRules(Path.Combine(Environment.SystemDirectory, "heimdall-no-such-file-zz9.exe")));
}
