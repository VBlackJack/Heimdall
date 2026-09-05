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
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The move plan: a rename whose new path sits under another parent.
/// </summary>
public sealed class FolderPathMoveTests
{
    [Fact]
    public void Move_UnderAnotherFolder_KeepsTheLeafAndRewritesDescendants()
    {
        bool created = FolderPath.TryCreateMove(
            "Prod/Linux",
            "Archive",
            ["Prod/Linux", "Prod/Linux/Web", "Archive", "Other"],
            out FolderRenamePlan? plan,
            out FolderMoveValidationError error);

        Assert.True(created);
        Assert.Equal(FolderMoveValidationError.None, error);
        Assert.Equal("Archive/Linux", plan!.NewPath);
        Assert.Equal("Archive/Linux/Web", plan.Rewrite("Prod/Linux/Web"));
        Assert.Equal("Prod/Linux2", plan.Rewrite("Prod/Linux2"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Move_ToTheTopLevel_DropsTheParent(string? target)
    {
        bool created = FolderPath.TryCreateMove(
            "Prod/Linux",
            target,
            ["Prod/Linux"],
            out FolderRenamePlan? plan,
            out _);

        Assert.True(created);
        Assert.Equal("Linux", plan!.NewPath);
    }

    [Fact]
    public void Move_ToTheCurrentParent_YieldsAnEqualPlan()
    {
        bool created = FolderPath.TryCreateMove(
            "Prod/Linux",
            "Prod",
            ["Prod/Linux", "Prod/Windows"],
            out FolderRenamePlan? plan,
            out _);

        Assert.True(created);
        Assert.Equal(plan!.OldPath, plan.NewPath);
    }

    [Theory]
    [InlineData("Prod/Linux")]
    [InlineData("Prod/Linux/Web")]
    [InlineData("prod/linux/web")]
    public void Move_IntoItselfOrADescendant_IsRefused(string target)
    {
        bool created = FolderPath.TryCreateMove(
            "Prod/Linux",
            target,
            ["Prod/Linux", "Prod/Linux/Web"],
            out FolderRenamePlan? plan,
            out FolderMoveValidationError error);

        Assert.False(created);
        Assert.Null(plan);
        Assert.Equal(FolderMoveValidationError.IntoItself, error);
    }

    [Fact]
    public void Move_OntoAnExistingSibling_IsRefused()
    {
        bool created = FolderPath.TryCreateMove(
            "Prod/Linux",
            "Archive",
            ["Prod/Linux", "Archive/linux"],
            out _,
            out FolderMoveValidationError error);

        Assert.False(created);
        Assert.Equal(FolderMoveValidationError.SiblingCollision, error);
    }

    [Theory]
    [InlineData("Prod/Linux", "Prod")]
    [InlineData("Prod", "")]
    public void ParentOf_ReturnsThePathAbove(string path, string expected)
    {
        Assert.Equal(expected, FolderPath.ParentOf(path));
    }

    [Theory]
    [InlineData("Prod/Linux/Web", "Web")]
    [InlineData("Prod", "Prod")]
    public void LeafOf_ReturnsTheLastSegment(string path, string expected)
    {
        Assert.Equal(expected, FolderPath.LeafOf(path));
    }

    [Theory]
    [InlineData("Prod/Linux", "Archive", true)]
    [InlineData("Prod/Linux", null, true)]
    [InlineData("Prod/Linux", "Prod", false)]
    [InlineData("Prod/Linux", "prod", false)]
    [InlineData("Prod/Linux", "Prod/Linux", false)]
    [InlineData("Prod/Linux", "Prod/Linux/Web", false)]
    [InlineData("Prod", null, false)]
    [InlineData("Prod", "Archive", true)]
    public void IsFolderMoveTarget_RefusesSelfDescendantsAndTheCurrentParent(
        string folderPath,
        string? target,
        bool expected)
    {
        Assert.Equal(expected, TreeInteractionState.IsFolderMoveTarget(folderPath, target));
    }
}
