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
/// A folder that equals, lies under or contains Windows or Program Files is told to the user: whole path
/// segments, ignoring case and trailing separators.
/// </summary>
public sealed class RecipeFolderTouchesRootTests
{
    private static readonly string[] Roots = [@"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)"];

    [Theory]
    [InlineData(@"C:\Windows", true)]
    [InlineData(@"C:\Windows\System32", true)]
    [InlineData(@"C:\Windows\System32\Wbem", true)]
    [InlineData(@"C:\Program Files", true)]
    [InlineData(@"C:\Program Files\Vendor\Tool", true)]
    [InlineData(@"C:\Program Files (x86)\Vendor", true)]
    [InlineData(@"c:\windows", true)]
    [InlineData(@"C:\PROGRAM FILES\vendor", true)]
    [InlineData(@"C:\Windows\", true)]
    [InlineData(@"C:/Windows/System32/", true)]
    [InlineData(@"C:\Program Files\", true)]
    public void AFolderThatEqualsOrLiesUnderARoot_IsReported(string folder, bool expected)
        => Assert.Equal(expected, RecipeFolderRule.TouchesRoot(folder, Roots));

    [Theory]
    [InlineData(@"C:\Program Files Extra")]
    [InlineData(@"C:\Program Files (x86)Extra")]
    [InlineData(@"C:\WindowsApps")]
    [InlineData(@"C:\Windows.old")]
    [InlineData(@"C:\Tools")]
    [InlineData(@"D:\Windows")]
    [InlineData(@"D:\Program Files")]
    public void ASiblingWhoseNameOnlyStartsLikeARootOrAnotherDrive_IsNotReported(string folder)
        => Assert.False(RecipeFolderRule.TouchesRoot(folder, Roots));

    [Theory]
    [InlineData(@"C:\Users")]
    public void AnUnrelatedFolder_IsNotReported(string folder)
        => Assert.False(RecipeFolderRule.TouchesRoot(folder, Roots));

    [Fact]
    public void AFolderThatContainsARoot_IsReported()
    {
        Assert.True(RecipeFolderRule.TouchesRoot(@"C:\Program Files", [@"C:\Program Files\Vendor"]));
        Assert.True(RecipeFolderRule.TouchesRoot(@"C:\Windows\System32", [@"C:\Windows\System32\Wbem"]));
        Assert.True(RecipeFolderRule.TouchesRoot(@"C:\Windows\", [@"C:\Windows\System32"]));
    }

    [Fact]
    public void ARootThatIsNullEmptyOrBlank_IsIgnored()
    {
        Assert.False(RecipeFolderRule.TouchesRoot(@"C:\Tools", [null, string.Empty, "  "]));
        Assert.True(RecipeFolderRule.TouchesRoot(@"C:\Windows", [null, string.Empty, @"C:\Windows"]));
        Assert.False(RecipeFolderRule.TouchesRoot(@"C:\Windows", []));
    }

    [Fact]
    public void ARootWithATrailingSeparator_IsCompared_WithoutIt()
    {
        Assert.True(RecipeFolderRule.TouchesRoot(@"C:\Windows\System32", [@"C:\Windows\"]));
        Assert.False(RecipeFolderRule.TouchesRoot(@"C:\WindowsApps", [@"C:\Windows\"]));
    }
}
