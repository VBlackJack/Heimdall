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
/// A folder written as a local path can lead elsewhere: its final form decides. A drive root or a network
/// location is refused; a link to an ordinary local folder works.
/// </summary>
public sealed class RecipeFolderFinalFormTests
{
    private static RecipeProgramVerdict Resolve(FakeRecipeMachine machine, string program, params string[] folders)
        => new RecipeProgramResolver(machine).Resolve(program, folders);

    [Theory]
    [InlineData(@"C:\", RecipeFolderRefusal.LeadsToDriveRoot)]
    [InlineData(@"C:", RecipeFolderRefusal.LeadsToDriveRoot)]
    [InlineData(@"d:\", RecipeFolderRefusal.LeadsToDriveRoot)]
    [InlineData(@"\\srv\share\x", RecipeFolderRefusal.LeadsToNetwork)]
    [InlineData(@"\\srv\share", RecipeFolderRefusal.LeadsToNetwork)]
    [InlineData("//srv/share/x", RecipeFolderRefusal.LeadsToNetwork)]
    [InlineData(@"E:\real\tools", RecipeFolderRefusal.None)]
    [InlineData(@"C:\Program Files\Tools", RecipeFolderRefusal.None)]
    [InlineData(null, RecipeFolderRefusal.None)]
    [InlineData("", RecipeFolderRefusal.None)]
    public void TheFinalFormOfAFolder_IsJudgedByWhereItLeads(string? finalForm, RecipeFolderRefusal expected)
        => Assert.Equal(expected, RecipeFolderRule.CheckFinalForm(finalForm));

    [Fact]
    public void AFolderOnAMappedNetworkDrive_RefusesItsPrograms()
    {
        FakeRecipeMachine machine = new FakeRecipeMachine().WithFile(@"Z:\tools\tool.exe");
        machine.Finals[@"Z:\tools"] = @"\\srv\share\tools";
        machine.Finals[@"Z:\tools\tool.exe"] = @"\\srv\share\tools\tool.exe";

        RecipeProgramVerdict verdict = Resolve(machine, @"Z:\tools\tool.exe", @"Z:\tools");

        // The written form alone would let it in; its final form is a share, which no root covers.
        Assert.Equal(RecipeProgramRefusal.LinkLeavesAllowedRoots, verdict.Refusal);
    }

    [Fact]
    public void AFolderThatLeadsToADriveRoot_RefusesItsPrograms()
    {
        FakeRecipeMachine machine = new FakeRecipeMachine().WithFile(@"D:\link\tool.exe");
        machine.Finals[@"D:\link"] = @"C:\";
        machine.Finals[@"D:\link\tool.exe"] = @"C:\tool.exe";

        Assert.Equal(RecipeProgramRefusal.LinkLeavesAllowedRoots, Resolve(machine, @"D:\link\tool.exe", @"D:\link").Refusal);
    }

    [Fact]
    public void AFolderThatLeadsToAnOrdinaryLocalFolder_StillWorks()
    {
        FakeRecipeMachine machine = new FakeRecipeMachine().WithFile(@"D:\link\tool.exe");
        machine.Finals[@"D:\link"] = @"E:\real\tools";
        machine.Finals[@"D:\link\tool.exe"] = @"E:\real\tools\tool.exe";

        RecipeProgramVerdict verdict = Resolve(machine, @"D:\link\tool.exe", @"D:\link");

        Assert.True(verdict.IsResolved);
        Assert.Equal(@"E:\real\tools\tool.exe", verdict.ResolvedPath);
    }

    [Fact]
    public void AFolderWhoseFinalFormCannotBeResolved_KeepsTheBehaviourItHadWrittenAlone()
    {
        FakeRecipeMachine machine = new FakeRecipeMachine().WithFile(@"D:\tools\tool.exe");
        machine.Finals[@"D:\tools"] = null;

        Assert.True(Resolve(machine, @"D:\tools\tool.exe", @"D:\tools").IsResolved);
    }

    public static TheoryData<string, bool> FinalForms => new()
    {
        { @"E:\real\tools", true },
        { @"E:\real\tools\", true },
        { @"E:\" + new string('a', 250), false },
        { @"E:\real\a*b", false },
        { @"E:\real\a|b", false },
        { @"C:\", false },
        { @"\\srv\share\x", false },
        { @"\\srv\share", false },
    };

    [Theory]
    [MemberData(nameof(FinalForms))]
    public void TheResolverAndTheRuleJudgeAFinalFormTheSameWay(string finalForm, bool accepted)
    {
        string link = @"D:\link";
        FakeRecipeMachine machine = new FakeRecipeMachine().WithFile(link + @"\tool.exe");
        machine.Finals[link] = finalForm;
        machine.Finals[link + @"\tool.exe"] = finalForm.TrimEnd('\\') + @"\tool.exe";

        RecipeProgramVerdict verdict = Resolve(machine, link + @"\tool.exe", link);

        Assert.Equal(accepted, RecipeFolderRule.CheckFinalForm(finalForm) == RecipeFolderRefusal.None);
        Assert.Equal(accepted, verdict.IsResolved);
    }

    [Fact]
    public void ATooLongFinalForm_IsTheGenericRefusal_AndADriveRootAndANetworkPathKeepTheirOwn()
    {
        Assert.Equal(RecipeFolderRefusal.LeadsToUnallowedLocation, RecipeFolderRule.CheckFinalForm(@"E:\" + new string('a', 250)));
        Assert.Equal(RecipeFolderRefusal.LeadsToUnallowedLocation, RecipeFolderRule.CheckFinalForm(@"E:\real\a*b"));
        Assert.Equal(RecipeFolderRefusal.LeadsToDriveRoot, RecipeFolderRule.CheckFinalForm(@"C:\"));
        Assert.Equal(RecipeFolderRefusal.LeadsToNetwork, RecipeFolderRule.CheckFinalForm(@"\\srv\share\x"));
    }
}
