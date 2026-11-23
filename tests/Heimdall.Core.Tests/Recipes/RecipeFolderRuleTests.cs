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

/// <summary>Which folders may be added to the folders recipes run programs from: local, absolute, plain.</summary>
public sealed class RecipeFolderRuleTests
{
    [Theory]
    [InlineData(@"D:\Tools")]
    [InlineData(@"D:\Tools\")]
    [InlineData("D:/Tools")]
    [InlineData(@"d:\Sysinternals Suite")]
    [InlineData(@"C:\Program Files (x86)\Vendor")]
    public void AnAbsolutePathOnALocalDriveIsAccepted(string folder)
        => Assert.Equal(RecipeFolderRefusal.None, RecipeFolderRule.Check(folder));

    [Theory]
    [InlineData(@"\\server\share", RecipeFolderRefusal.Unc)]
    [InlineData(@"\\server\share\tools", RecipeFolderRefusal.Unc)]
    [InlineData("//server/share", RecipeFolderRefusal.Unc)]
    [InlineData(@"\\?\C:\Tools", RecipeFolderRefusal.Device)]
    [InlineData(@"\\.\C:\Tools", RecipeFolderRefusal.Device)]
    [InlineData(@"\??\C:\Tools", RecipeFolderRefusal.Device)]
    [InlineData(@"tools", RecipeFolderRefusal.NotAbsolute)]
    [InlineData(@"..\tools", RecipeFolderRefusal.NotAbsolute)]
    [InlineData(@"\tools", RecipeFolderRefusal.NotAbsolute)]
    [InlineData(@"D:tools", RecipeFolderRefusal.NotAbsolute)]
    [InlineData("D:", RecipeFolderRefusal.NotAbsolute)]
    [InlineData(@"D:\Tools\..\Other", RecipeFolderRefusal.NotAbsolute)]
    [InlineData(@"D:\.\Tools", RecipeFolderRefusal.NotAbsolute)]
    [InlineData(@"D:\%TEMP%", RecipeFolderRefusal.ForbiddenCharacter)]
    [InlineData(@"D:\Tools:stream", RecipeFolderRefusal.ForbiddenCharacter)]
    [InlineData(@"D:\Too*ls", RecipeFolderRefusal.ForbiddenCharacter)]
    [InlineData(@"D:\Tools\\More", RecipeFolderRefusal.ForbiddenCharacter)]
    [InlineData("D:\\Tools.", RecipeFolderRefusal.ForbiddenCharacter)]
    [InlineData("D:\\Tools ", RecipeFolderRefusal.None)]
    [InlineData("D:\\Too\u0007ls", RecipeFolderRefusal.ForbiddenCharacter)]
    [InlineData("", RecipeFolderRefusal.Empty)]
    [InlineData("   ", RecipeFolderRefusal.Empty)]
    public void EveryOtherShapeIsRefusedWithItsReason(string folder, RecipeFolderRefusal expected)
        => Assert.Equal(expected, RecipeFolderRule.Check(folder));

    [Fact]
    public void ATooLongPathIsRefused()
        => Assert.Equal(RecipeFolderRefusal.TooLong, RecipeFolderRule.Check(@"D:\" + new string('a', RecipeFolderRule.MaxLength)));

    [Fact]
    public void ANullFolderIsEmpty()
        => Assert.Equal(RecipeFolderRefusal.Empty, RecipeFolderRule.Check(null));

    [Fact]
    public void AFolderAlreadyListedIsRefusedWhateverItsSpelling()
    {
        string[] existing = [@"D:\Tools"];

        Assert.Equal(RecipeFolderRefusal.AlreadyListed, RecipeFolderRule.Check(@"d:\tools\", existing));
        Assert.Equal(RecipeFolderRefusal.AlreadyListed, RecipeFolderRule.Check("D:/Tools", existing));
        Assert.Equal(RecipeFolderRefusal.None, RecipeFolderRule.Check(@"D:\Other", existing));
    }

    [Fact]
    public void TheListIsCappedAtTheMostFolders()
    {
        string[] full = [.. Enumerable.Range(0, RecipeFolderRule.MaxFolders).Select(index => $@"D:\Tools{index}")];

        Assert.Equal(RecipeFolderRefusal.ListFull, RecipeFolderRule.Check(@"E:\One more", full));
        Assert.Equal(RecipeFolderRefusal.None, RecipeFolderRule.Check(@"E:\One more", full[..^1]));
    }

    [Fact]
    public void ACheckWithTheListStillRefusesABadShapeFirst()
        => Assert.Equal(RecipeFolderRefusal.Unc, RecipeFolderRule.Check(@"\\server\share", []));

    [Theory]
    [InlineData(@"D:\Tools\", @"D:\Tools")]
    [InlineData("D:/Tools/Sub/", @"D:\Tools\Sub")]
    [InlineData("  D:\\Tools  ", @"D:\Tools")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData("D:/", @"D:\")]
    public void ANormalizedFolderHasBackslashesAndNoTrailingSeparatorExceptForARoot(string folder, string expected)
        => Assert.Equal(expected, RecipeFolderRule.Normalize(folder));

    [Fact]
    public void EveryRefusalIsDistinctFromNone()
    {
        foreach (RecipeFolderRefusal refusal in Enum.GetValues<RecipeFolderRefusal>().Where(item => item != RecipeFolderRefusal.None))
        {
            Assert.NotEqual(RecipeFolderRefusal.None, refusal);
        }
    }
}
