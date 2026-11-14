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

using Heimdall.App.Services.Import;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// What an mRemoteNG import writes so the sessions tree shows the file's order. Names are ASCII
/// because the tree compares names under the current culture.
/// </summary>
public sealed class ImportSourceOrderTests
{
    private static ServerProfileDto Row(string name, string? group, int sortOrder = 0) =>
        new() { Id = name, DisplayName = name, Group = group, SortOrder = sortOrder };

    private static int[] Orders(params ServerProfileDto[] rows) => [.. rows.Select(row => row.SortOrder)];

    private static List<string>? Merge(string[] stored, string?[] groupsBefore, params string?[] importedGroups) =>
        ImportSourceOrder.MergeFolderOrder(
            stored,
            ImportSourceOrder.FolderPathsOf(groupsBefore),
            ImportSourceOrder.FolderPathsInFileOrder(importedGroups.Select(group => Row("r", group))));

    [Fact]
    public void ArrangeSessions_EmptyInventory_RanksInFileOrder()
    {
        ServerProfileDto webB = Row("web-b", "Zeta");
        ServerProfileDto webA = Row("web-a", "Zeta");

        ImportSourceOrder.ArrangeSessions([], [webB, webA]);

        Assert.Equal([10, 20], Orders(webB, webA));
    }

    [Fact]
    public void ArrangeSessions_AnAlreadyAlphabeticalGroup_WritesNothing()
    {
        ServerProfileDto first = Row("alpha", "Zeta");
        ServerProfileDto second = Row("bravo", "Zeta");

        ImportSourceOrder.ArrangeSessions([], [first, second]);

        Assert.Equal([0, 0], Orders(first, second));
    }

    [Fact]
    public void ArrangeSessions_AnUnarrangedExistingFolder_KeepsTheExistingRowsFirstAlphabetically()
    {
        ServerProfileDto m = Row("m", "Ops");
        ServerProfileDto c = Row("c", "Ops");
        ServerProfileDto z = Row("z", "Ops");
        ServerProfileDto a = Row("a", "Ops");

        ImportSourceOrder.ArrangeSessions([m, c], [z, a]);

        Assert.Equal([10, 20, 30, 40], Orders(c, m, z, a));
    }

    [Fact]
    public void ArrangeSessions_AnUnarrangedExistingFolder_WritesNothingWhenTheResultIsAlphabeticalAnyway()
    {
        ServerProfileDto c = Row("c", "Ops");
        ServerProfileDto m = Row("m", "Ops");
        ServerProfileDto x = Row("x", "Ops");
        ServerProfileDto y = Row("y", "Ops");

        ImportSourceOrder.ArrangeSessions([c, m], [x, y]);

        Assert.Equal([0, 0, 0, 0], Orders(c, m, x, y));
    }

    [Fact]
    public void ArrangeSessions_AnArrangedFolder_KeepsItsVisibleOrderIncludingALateUnrankedRow()
    {
        ServerProfileDto m = Row("m", "Ops", sortOrder: 10);
        ServerProfileDto c = Row("c", "Ops", sortOrder: 20);
        ServerProfileDto b = Row("b", "Ops");
        ServerProfileDto q = Row("q", "Ops");
        ServerProfileDto a = Row("a", "Ops");

        ImportSourceOrder.ArrangeSessions([b, c, m], [q, a]);

        Assert.Equal([10, 20, 30, 40, 50], Orders(m, c, b, q, a));
    }

    [Fact]
    public void ArrangeSessions_MatchesGroupsIgnoringCaseAndSpelling_AndRewritesTheImportedGroup()
    {
        ServerProfileDto c = Row("c", "Lab\\Ops");
        ServerProfileDto m = Row("m", "Lab/Ops");
        ServerProfileDto z = Row("z", " lab//ops ");
        ServerProfileDto a = Row("a", "LAB/OPS");

        ImportSourceOrder.ArrangeSessions([m, c], [z, a]);

        Assert.Equal([10, 20, 30, 40], Orders(c, m, z, a));
        Assert.Equal("lab/ops", z.Group);
        Assert.Equal("LAB/OPS", a.Group);
        Assert.Equal("Lab\\Ops", c.Group);
    }

    [Fact]
    public void ArrangeSessions_GroupsThatReceiveNoImport_AreNotWritten()
    {
        ServerProfileDto untouchedZero = Row("u1", "Other");
        ServerProfileDto untouchedSeven = Row("u2", "Other", sortOrder: 7);
        ServerProfileDto zebra = Row("zebra", "Ops");
        ServerProfileDto apple = Row("apple", "Ops");

        ImportSourceOrder.ArrangeSessions([untouchedZero, untouchedSeven], [zebra, apple]);

        Assert.Equal([0, 7], Orders(untouchedZero, untouchedSeven));
        Assert.Equal([10, 20], Orders(zebra, apple));
    }

    [Fact]
    public void ArrangeSessions_RootRows_AreArranged_AndKeepNoGroup()
    {
        ServerProfileDto existing = Row("old", "");
        ServerProfileDto rootZ = Row("root-z", null);
        ServerProfileDto rootA = Row("root-a", "  ");

        ImportSourceOrder.ArrangeSessions([existing], [rootZ, rootA]);

        Assert.Equal([10, 20, 30], Orders(existing, rootZ, rootA));
        Assert.Null(rootZ.Group);
        Assert.Null(rootA.Group);
    }

    [Fact]
    public void ArrangeSessions_ARowThatAlsoSitsInTheInventory_IsNotCountedTwice()
    {
        ServerProfileDto zebra = Row("zebra", "Ops");
        ServerProfileDto apple = Row("apple", "Ops");

        ImportSourceOrder.ArrangeSessions([zebra, apple], [zebra, apple]);

        Assert.Equal([10, 20], Orders(zebra, apple));
    }

    [Fact]
    public void ArrangeSessions_RowsOfOneGroupMetAtTwoPlacesInTheList_JoinAtTheFirstAppearance()
    {
        ServerProfileDto x = Row("zz", "Prod");
        ServerProfileDto y = Row("y", "Other");
        ServerProfileDto z = Row("aa", "Prod");
        ServerProfileDto[] imported = [x, y, z];

        ImportSourceOrder.ArrangeSessions([], imported);

        Assert.Equal([10, 0, 20], Orders(x, y, z));
        Assert.Equal(["Prod", "Other"], ImportSourceOrder.FolderPathsInFileOrder(imported));
        Assert.Equal(["Prod", "Other"], Merge([], [], "Prod", "Other", "Prod"));
    }

    [Fact]
    public void ArrangeSessions_ALargeImport_RanksEachGroupOnItsOwn()
    {
        List<ServerProfileDto> imported = [];
        for (int folder = 0; folder < 300; folder++)
        {
            for (int session = 20; session > 0; session--)
            {
                imported.Add(Row($"s{folder:D3}-{session:D2}", $"F{300 - folder:D3}/Sub"));
            }
        }

        ServerProfileDto sortedFirst = Row("a-first", "Sorted");
        ServerProfileDto sortedLast = Row("b-last", "Sorted");
        imported.Add(sortedFirst);
        imported.Add(sortedLast);

        ImportSourceOrder.ArrangeSessions([], imported);
        List<string>? order = ImportSourceOrder.MergeFolderOrder(
            [],
            new HashSet<string>(),
            ImportSourceOrder.FolderPathsInFileOrder(imported));

        Assert.Equal(10, imported[0].SortOrder);
        Assert.Equal(200, imported[19].SortOrder);
        Assert.Equal(10, imported[5980].SortOrder);
        Assert.Equal(200, imported[5999].SortOrder);
        Assert.Equal([0, 0], Orders(sortedFirst, sortedLast));
        Assert.NotNull(order);
        Assert.Equal("F300", order[0]);
        Assert.Equal("Sorted", order[^1]);
    }

    [Fact]
    public void ArrangeSessions_TwoRowsWithTheSameName_KeepTheFileOrderAsRanks()
    {
        ServerProfileDto first = Row("dup", "Ops");
        first.Id = "first";
        ServerProfileDto second = Row("dup", "Ops");
        second.Id = "second";

        ImportSourceOrder.ArrangeSessions([], [first, second]);

        Assert.Equal([10, 20], Orders(first, second));
    }

    [Fact]
    public void MergeFolderOrder_EmptyInventory_ListsEachParentsChildrenInFileOrder()
    {
        List<string>? order = Merge([], [], "Zeta", "Zeta/Sub2", "Zeta/Sub1", "Zeta", "Alpha");

        Assert.Equal(["Zeta", "Alpha", "Zeta/Sub2", "Zeta/Sub1"], order);
    }

    [Fact]
    public void MergeFolderOrder_UnarrangedExistingFolders_KeepTheirAlphabeticalPlaceBeforeTheNewOnes()
    {
        List<string>? order = Merge([], ["Lab", "Ops"], "Zeta", "Alpha");

        Assert.Equal(["Lab", "Ops", "Zeta", "Alpha"], order);
    }

    [Fact]
    public void MergeFolderOrder_StoredOrderOfExistingFolders_IsKept()
    {
        List<string>? order = Merge(["Ops", "Lab"], ["Lab", "Ops"], "Zeta", "Alpha");

        Assert.Equal(["Ops", "Lab", "Zeta", "Alpha"], order);
    }

    [Fact]
    public void MergeFolderOrder_AnImportedFolderThatExists_KeepsItsPlace()
    {
        List<string>? order = Merge(["Lab", "Ops"], ["Lab", "Ops"], "Zeta", "Lab", "Alpha");

        Assert.Equal(["Lab", "Ops", "Zeta", "Alpha"], order);
    }

    [Fact]
    public void MergeFolderOrder_NoNewChild_ReturnsNull()
    {
        Assert.Null(Merge(["Ops", "Lab"], ["Lab", "Ops"], "Lab", "Ops"));
    }

    [Fact]
    public void MergeFolderOrder_ALoneNewChild_ReturnsNull()
    {
        Assert.Null(Merge([], [], "Solo", "Solo/Inner"));
    }

    [Fact]
    public void MergeFolderOrder_NewFoldersThatAreAlphabeticalAnyway_ReturnsNull()
    {
        Assert.Null(Merge([], ["Lab"], "Zeta"));
    }

    [Fact]
    public void MergeFolderOrder_ExistingOpsImportedAsUpperCase_WritesNothingAtTheRoot()
    {
        Assert.Null(Merge([], ["Zeta", "ops"], "OPS", "OPS/x"));
    }

    [Fact]
    public void MergeFolderOrder_EntriesOfOtherParents_AreKept_AndAStaleEntryIsDroppedOnlyWhenWriting()
    {
        string[] groupsBefore = ["Other/a", "Other/b", "Lab", "Ops"];

        List<string>? order = Merge(["Ghost", "Other/b", "Other/a"], groupsBefore, "Zeta", "Alpha");

        Assert.Equal(["Other/b", "Other/a", "Lab", "Ops", "Other", "Zeta", "Alpha"], order);
        Assert.Null(Merge(["Ghost", "Other/b", "Other/a"], groupsBefore, "Other"));
    }
}
