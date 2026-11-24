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

/// <summary>
/// The owner of a file or of a folder above it can rewrite its permissions, so an owner that is not the system,
/// the administrators or the installer service refuses a program under Windows or Program Files.
/// </summary>
public sealed class RecipeProgramOwnerTests
{
    private const string UsersSid = "S-1-5-32-545";

    private const string SomeUserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private const string OwnerRightsSid = "S-1-3-4";

    private const int ReadAndExecute = 0x1200A9;

    private const string System32 = @"C:\Windows\System32";

    private const string Tool = @"C:\Windows\System32\tool.exe";

    private static FakeRecipeMachine Machine() => new FakeRecipeMachine().WithFile(Tool);

    private static RecipeProgramVerdict Resolve(FakeRecipeMachine machine, string program, IReadOnlyList<string>? folders = null)
        => new RecipeProgramResolver(machine).Resolve(program, folders ?? []);

    [Theory]
    [InlineData(RecipeAccessPolicy.SystemSid)]
    [InlineData(RecipeAccessPolicy.AdministratorsSid)]
    [InlineData(RecipeAccessPolicy.TrustedInstallerSid)]
    public void ATrustedOwnerOfEveryItemOnTheWay_PassesTheWalk(string owner)
    {
        FakeRecipeMachine machine = Machine().WithOwner(Tool, owner).WithOwner(System32, owner).WithOwner(@"C:\Windows", owner);

        Assert.True(Resolve(machine, Tool).IsResolved);
    }

    [Theory]
    [InlineData(RecipeAccessPolicy.SystemSid, true)]
    [InlineData(RecipeAccessPolicy.AdministratorsSid, true)]
    [InlineData(RecipeAccessPolicy.TrustedInstallerSid, true)]
    [InlineData(UsersSid, false)]
    [InlineData(SomeUserSid, false)]
    [InlineData(OwnerRightsSid, false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheSystemTheAdministratorsAndTheInstallerAreTrustedOwners(string? sid, bool expected)
        => Assert.Equal(expected, RecipeAccessPolicy.IsTrustedOwner(sid));

    [Fact]
    public void AUserWhoOwnsTheFile_RefusesTheProgram()
    {
        FakeRecipeMachine machine = Machine().WithOwner(Tool, SomeUserSid);

        Assert.Equal(RecipeProgramRefusal.WritableByUser, Resolve(machine, Tool).Refusal);
    }

    [Theory]
    [InlineData(System32)]
    [InlineData(@"C:\Windows")]
    public void AUserWhoOwnsAFolderAboveTheFile_RefusesTheProgram(string folder)
    {
        FakeRecipeMachine machine = Machine().WithOwner(folder, SomeUserSid);

        Assert.Equal(RecipeProgramRefusal.WritableByUser, Resolve(machine, Tool).Refusal);
    }

    [Fact]
    public void AnOwnerThatCannotBeRead_IsAsUnknownAsPermissionsThatCannotBeRead()
    {
        FakeRecipeMachine machine = Machine().WithOwner(System32, null);

        Assert.Equal(RecipeProgramRefusal.CannotReadPermissions, Resolve(machine, Tool).Refusal);
    }

    [Fact]
    public void AnOwnerRightsEntry_DoesNotRescueAnUntrustedOwner()
    {
        // OWNER RIGHTS can limit what an owner may do; it is deliberately not honoured.
        FakeRecipeMachine machine = Machine().WithOwner(Tool, SomeUserSid).WithRule(Tool, OwnerRightsSid, ReadAndExecute);

        Assert.Equal(RecipeProgramRefusal.WritableByUser, Resolve(machine, Tool).Refusal);
    }

    [Fact]
    public void AProgramUnderAUserFolderOutsideTheBuiltInRoots_IsStillNotWalked_WhoeverOwnsIt()
    {
        FakeRecipeMachine machine = new FakeRecipeMachine().WithFile(@"D:\tools\tool.exe").WithOwner(@"D:\tools\tool.exe", SomeUserSid);

        RecipeProgramVerdict verdict = Resolve(machine, @"D:\tools\tool.exe", [@"D:\tools"]);

        Assert.True(verdict.IsResolved);
        Assert.Empty(machine.PermissionReads);
    }

    [Fact]
    public void TheWalkReadsEachItemOnce_ForItsOwnerAndItsRulesTogether()
    {
        FakeRecipeMachine machine = Machine();

        Assert.True(Resolve(machine, Tool).IsResolved);

        Assert.Equal(machine.PermissionReads.Count, machine.PermissionReads.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ARealSystemProgram_StillResolvesThroughTheRealMachine_AndItsOwnerIsTrusted()
    {
        string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");

        RecipeItemAccess? access = RecipeMachine.Instance.ReadAccess(ping);

        Assert.NotNull(access);
        Assert.True(RecipeAccessPolicy.IsTrustedOwner(access.OwnerSid), "owner: " + access.OwnerSid);
        Assert.True(new RecipeProgramResolver().Resolve(ping, []).IsResolved);
    }
}
