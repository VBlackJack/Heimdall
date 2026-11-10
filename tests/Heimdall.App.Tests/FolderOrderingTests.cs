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

using Heimdall.App.ViewModels;

namespace Heimdall.App.Tests;

/// <summary>
/// The pure ranking and nudge rules of the manual folder order.
/// </summary>
public sealed class FolderOrderingTests
{
    [Fact]
    public void RanksOf_NumbersListedPathsInListOrder_IgnoringCaseBlanksAndRepeats()
    {
        IReadOnlyDictionary<string, int> ranks = FolderOrdering.RanksOf(["Zulu", "", "alpha", "ZULU"]);

        Assert.Equal(0, FolderOrdering.RankOf(ranks, "zulu"));
        Assert.Equal(1, FolderOrdering.RankOf(ranks, "Alpha"));
        Assert.Equal(FolderOrdering.Unordered, FolderOrdering.RankOf(ranks, "Mike"));
    }

    [Fact]
    public void RanksOf_NoList_RanksEveryPathAlike()
    {
        IReadOnlyDictionary<string, int> ranks = FolderOrdering.RanksOf(null);

        Assert.Empty(ranks);
        Assert.Equal(FolderOrdering.Unordered, FolderOrdering.RankOf(ranks, "Any"));
    }

    [Fact]
    public void Nudge_FirstMoveInAGroup_WritesEverySiblingInDisplayedOrder()
    {
        List<string>? order = FolderOrdering.Nudge(
            stored: [],
            siblings: ["A", "B", "C"],
            path: "C",
            delta: -1,
            existing: ["A", "B", "C"]);

        Assert.Equal(["A", "C", "B"], order);
    }

    [Theory]
    [InlineData("A", -1)]
    [InlineData("C", 1)]
    [InlineData("B", 0)]
    [InlineData("Missing", 1)]
    public void Nudge_AtAnEdgeOrWithoutAFolder_MovesNothing(string path, int delta)
    {
        Assert.Null(FolderOrdering.Nudge([], ["A", "B", "C"], path, delta, ["A", "B", "C"]));
    }

    [Fact]
    public void Nudge_KeepsOtherGroupsEntriesAndDropsStaleOnes()
    {
        List<string>? order = FolderOrdering.Nudge(
            stored: ["Other/Y", "Gone", "A", "Other/X", "Other/Y"],
            siblings: ["A", "B"],
            path: "A",
            delta: 1,
            existing: ["A", "B", "Other/X", "Other/Y"]);

        Assert.Equal(["Other/Y", "Other/X", "B", "A"], order);
    }
}
