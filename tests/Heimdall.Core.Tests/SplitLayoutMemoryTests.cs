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

using System.Text.Json;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.Core.Tests;

public sealed class SplitLayoutMemoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SplitLayoutMemory _memory;

    public SplitLayoutMemoryTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "heimdall-splitlayout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _memory = new SplitLayoutMemory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup should not mask assertion failures.
        }
    }

    [Theory]
    [InlineData("", "b", "b")]
    [InlineData("a", "", "a")]
    public void Record_EmptyId_IsNoOp(string primaryServerId, string secondaryServerId, string lookupServerId)
    {
        _memory.Record(primaryServerId, secondaryServerId, SplitOrientation.Horizontal);

        SplitLayoutEntry? entry = _memory.FindPartner(lookupServerId);

        Assert.Null(entry);
    }

    [Fact]
    public void Record_NewPair_IsFoundByBothIds()
    {
        _memory.Record("A", "B", SplitOrientation.Vertical, 0.7);

        SplitLayoutEntry? entryFromPrimary = _memory.FindPartner("A");
        SplitLayoutEntry? entryFromSecondary = _memory.FindPartner("B");

        Assert.NotNull(entryFromPrimary);
        Assert.NotNull(entryFromSecondary);
        Assert.Equal("A", entryFromPrimary!.PrimaryServerId);
        Assert.Equal("B", entryFromPrimary.SecondaryServerId);
        Assert.Equal(SplitOrientation.Vertical, entryFromPrimary.Orientation);
        Assert.Equal(0.7, entryFromPrimary.Ratio);
        Assert.Same(entryFromPrimary, entryFromSecondary);
    }

    [Fact]
    public void Record_ReversePair_DeduplicatesEitherDirection()
    {
        _memory.Record("A", "B", SplitOrientation.Horizontal);
        _memory.Record("B", "A", SplitOrientation.Vertical);

        IReadOnlyList<SplitLayoutEntry> entries = _memory.FindAllPartners("A");

        SplitLayoutEntry entry = Assert.Single(entries);
        Assert.Equal("B", entry.PrimaryServerId);
        Assert.Equal("A", entry.SecondaryServerId);
        Assert.Equal(SplitOrientation.Vertical, entry.Orientation);
    }

    [Fact]
    public void Record_SamePair_ReplacesWithLatestValues()
    {
        _memory.Record("A", "B", SplitOrientation.Horizontal, 0.25);
        _memory.Record("A", "B", SplitOrientation.Vertical, 0.75);

        IReadOnlyList<SplitLayoutEntry> entries = _memory.FindAllPartners("A");

        SplitLayoutEntry entry = Assert.Single(entries);
        Assert.Equal(SplitOrientation.Vertical, entry.Orientation);
        Assert.Equal(0.75, entry.Ratio);
    }

    [Fact]
    public void Record_ExistingPair_MovesToFront()
    {
        _memory.Record("A", "B", SplitOrientation.Horizontal);
        _memory.Record("A", "C", SplitOrientation.Vertical);
        _memory.Record("A", "B", SplitOrientation.Horizontal);

        IReadOnlyList<SplitLayoutEntry> entries = _memory.FindAllPartners("A");

        Assert.Equal(2, entries.Count);
        Assert.Equal("B", entries[0].SecondaryServerId);
        Assert.Equal("C", entries[1].SecondaryServerId);
    }

    [Fact]
    public void Record_ExceedsCapacity_EvictsOldestEntry()
    {
        for (int index = 0; index < 51; index++)
        {
            _memory.Record("HUB", "S" + index, SplitOrientation.Horizontal);
        }

        IReadOnlyList<SplitLayoutEntry> entries = _memory.FindAllPartners("HUB");

        Assert.Equal(50, entries.Count);
        Assert.Null(_memory.FindPartner("S0"));
        Assert.NotNull(_memory.FindPartner("S50"));
    }

    [Fact]
    public void FindPartner_EmptyId_ReturnsNull()
    {
        _memory.Record("A", "B", SplitOrientation.Horizontal);

        SplitLayoutEntry? entry = _memory.FindPartner("");

        Assert.Null(entry);
    }

    [Fact]
    public void FindPartner_NoMatch_ReturnsNull()
    {
        _memory.Record("A", "B", SplitOrientation.Horizontal);

        SplitLayoutEntry? entry = _memory.FindPartner("C");

        Assert.Null(entry);
    }

    [Fact]
    public void FindPartner_MultipleMatches_ReturnsMostRecent()
    {
        _memory.Record("A", "B", SplitOrientation.Horizontal);
        _memory.Record("A", "C", SplitOrientation.Vertical);

        SplitLayoutEntry? entry = _memory.FindPartner("A");

        Assert.NotNull(entry);
        Assert.Equal("C", entry!.SecondaryServerId);
        Assert.Equal(SplitOrientation.Vertical, entry.Orientation);
    }

    [Fact]
    public void FindRatio_RecordedPair_ComesBackAsRecorded()
    {
        _memory.Record("a", "b", SplitOrientation.Vertical, 0.3);

        Assert.Equal(0.3, _memory.FindRatio("a", "b")!.Value, 6);
    }

    [Fact]
    public void FindRatio_PairSeenTheOtherWayRound_IsMirrored()
    {
        _memory.Record("a", "b", SplitOrientation.Vertical, 0.3);

        Assert.Equal(0.7, _memory.FindRatio("b", "a")!.Value, 6);
    }

    [Fact]
    public void FindRatio_OnlyMatchesTheExactPair()
    {
        _memory.Record("a", "b", SplitOrientation.Vertical, 0.3);
        _memory.Record("a", "c", SplitOrientation.Vertical, 0.8);

        Assert.Equal(0.3, _memory.FindRatio("a", "b")!.Value, 6);
        Assert.Null(_memory.FindRatio("b", "c"));
        Assert.Null(_memory.FindRatio("a", ""));
    }

    [Fact]
    public void FindAllPartners_EmptyId_ReturnsEmpty()
    {
        _memory.Record("A", "B", SplitOrientation.Horizontal);

        IReadOnlyList<SplitLayoutEntry> entries = _memory.FindAllPartners("");

        Assert.Empty(entries);
    }

    [Fact]
    public void FindAllPartners_MultipleMatches_ReturnsMostRecentFirst()
    {
        _memory.Record("A", "B", SplitOrientation.Horizontal);
        _memory.Record("A", "C", SplitOrientation.Vertical);
        _memory.Record("D", "A", SplitOrientation.Horizontal);

        IReadOnlyList<SplitLayoutEntry> entries = _memory.FindAllPartners("A");

        Assert.Equal(3, entries.Count);
        Assert.Equal("D", entries[0].PrimaryServerId);
        Assert.Equal("A", entries[0].SecondaryServerId);
        Assert.Equal("A", entries[1].PrimaryServerId);
        Assert.Equal("C", entries[1].SecondaryServerId);
        Assert.Equal("A", entries[2].PrimaryServerId);
        Assert.Equal("B", entries[2].SecondaryServerId);
    }

    [Fact]
    public void Constructor_LoadsSavedEntriesFromPreviousInstance()
    {
        _memory.Record("A", "B", SplitOrientation.Vertical, 0.7);

        SplitLayoutMemory reloaded = new SplitLayoutMemory(_tempDir);
        SplitLayoutEntry? entry = reloaded.FindPartner("A");

        Assert.NotNull(entry);
        Assert.Equal("A", entry!.PrimaryServerId);
        Assert.Equal("B", entry.SecondaryServerId);
        Assert.Equal(SplitOrientation.Vertical, entry.Orientation);
        Assert.Equal(0.7, entry.Ratio);
    }

    [Fact]
    public void Constructor_LoadsLegacyBareArrayJson()
    {
        File.WriteAllText(
            FilePath,
            """
            [
              {
                "primaryServerId": "A",
                "secondaryServerId": "B",
                "orientation": 1,
                "ratio": 0.7,
                "lastUsed": "2026-01-01T00:00:00Z"
              }
            ]
            """);

        SplitLayoutMemory reloaded = new SplitLayoutMemory(_tempDir);
        SplitLayoutEntry? entry = reloaded.FindPartner("A");

        Assert.NotNull(entry);
        Assert.Equal("B", entry!.SecondaryServerId);
        Assert.Equal(SplitOrientation.Vertical, entry.Orientation);
        Assert.Equal(0.7, entry.Ratio);
    }

    [Fact]
    public void Constructor_LoadsVersionedWrapperJson()
    {
        File.WriteAllText(
            FilePath,
            """
            {
              "version": 1,
              "entries": [
                {
                  "primaryServerId": "A",
                  "secondaryServerId": "B",
                  "orientation": 1,
                  "ratio": 0.7,
                  "lastUsed": "2026-01-01T00:00:00Z"
                }
              ]
            }
            """);

        SplitLayoutMemory reloaded = new SplitLayoutMemory(_tempDir);
        SplitLayoutEntry? entry = reloaded.FindPartner("A");

        Assert.NotNull(entry);
        Assert.Equal("B", entry!.SecondaryServerId);
        Assert.Equal(SplitOrientation.Vertical, entry.Orientation);
        Assert.Equal(0.7, entry.Ratio);
    }

    [Fact]
    public void Constructor_CorruptedJson_DoesNotThrowAndStartsEmpty()
    {
        File.WriteAllText(FilePath, "{ not valid");

        SplitLayoutMemory reloaded = new SplitLayoutMemory(_tempDir);

        Assert.Null(reloaded.FindPartner("A"));
    }

    [Fact]
    public void Constructor_MissingFile_DoesNotThrowAndStartsEmpty()
    {
        SplitLayoutMemory fresh = new SplitLayoutMemory(_tempDir);

        SplitLayoutEntry? entry = fresh.FindPartner("A");

        Assert.Null(entry);
    }

    [Fact]
    public void Record_WritesParseableFileAndRemovesTemporaryFiles()
    {
        _memory.Record("A", "B", SplitOrientation.Vertical, 0.7);

        string[] tempFiles = Directory.GetFiles(_tempDir, "*.tmp");
        string json = File.ReadAllText(FilePath);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Empty(tempFiles);
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        JsonElement entries = root.GetProperty("entries");
        JsonElement entry = entries[0];
        Assert.Equal("A", entry.GetProperty("primaryServerId").GetString());
        Assert.Equal("B", entry.GetProperty("secondaryServerId").GetString());
        Assert.Equal(1, entry.GetProperty("orientation").GetInt32());
        Assert.Equal(0.7, entry.GetProperty("ratio").GetDouble());
    }

    private string FilePath => Path.Combine(_tempDir, "split-layouts.json");
}
