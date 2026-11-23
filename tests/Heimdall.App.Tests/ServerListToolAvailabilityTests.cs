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
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// A saved <c>TOOL:*</c> entry of a tool that may not be used stays in the tree, says so and why,
/// and follows the availability service.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    private const string GatedToolId = "GATEDTOOL";

    private static ServerProfileDto CreateToolEntry(string id, string displayName, string toolId)
    {
        ServerProfileDto entry = CreateServer(id, displayName, "ops");
        entry.ConnectionType = ConnectionTypeCatalog.ToolPrefix + toolId;
        return entry;
    }

    [Theory]
    [InlineData(ToolAvailabilityState.DisabledByUser, "belongs to a tool pack that is turned off")]
    [InlineData(ToolAvailabilityState.ForbiddenByPolicy, "not allowed by your organisation")]
    public async Task ASavedToolEntry_OfAnUnavailableTool_ReportsTheStateAndTheReason(
        ToolAvailabilityState state,
        string expectedReasonFragment)
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.ViewModel.ToolStateResolver = _ => state;
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateToolEntry("tool", "Gated entry", GatedToolId));

        ServerItemViewModel item = fixture.ServerById("tool");

        Assert.True(item.IsToolUnavailable);
        Assert.NotNull(item.ToolUnavailableReason);
        Assert.Contains(expectedReasonFragment, item.ToolUnavailableReason, StringComparison.Ordinal);
        Assert.Contains("Gated entry", item.ToolUnavailableReason, StringComparison.Ordinal);

        // What a screen reader reads, and what the row tooltip shows, carry the reason too.
        Assert.Contains(item.ToolUnavailableReason, item.AccessibleName, StringComparison.Ordinal);
        Assert.Contains(item.ToolUnavailableReason, item.RowTooltipText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASavedToolEntry_OfAnAvailableTool_IsNotMarked()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.ViewModel.ToolStateResolver = _ => ToolAvailabilityState.Available;
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateToolEntry("tool", "Open entry", GatedToolId));

        ServerItemViewModel item = fixture.ServerById("tool");

        Assert.False(item.IsToolUnavailable);
        Assert.Null(item.ToolUnavailableReason);
        Assert.DoesNotContain("tool pack", item.RowTooltipText ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASavedToolEntry_OfAnUnknownTool_IsNotMarked()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.ViewModel.ToolStateResolver = _ => ToolAvailabilityState.Unknown;
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateToolEntry("tool", "Old entry", "REMOVEDTOOL"));

        Assert.False(fixture.ServerById("tool").IsToolUnavailable);
    }

    [Fact]
    public async Task ANonToolEntry_IsNeverMarked_WhateverTheResolverSays()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.ViewModel.ToolStateResolver = _ => ToolAvailabilityState.DisabledByUser;
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("ssh", "Ordinary", "ops"));

        ServerItemViewModel item = fixture.ServerById("ssh");

        Assert.False(item.IsToolUnavailable);
        Assert.Null(item.ToolUnavailableReason);
    }

    [Fact]
    public async Task WithoutAResolver_EveryEntryIsUsable()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateToolEntry("tool", "Entry", GatedToolId));

        Assert.False(fixture.ServerById("tool").IsToolUnavailable);
    }

    [Fact]
    public async Task WhenAvailabilityChanges_TheRowAndTheDetailPaneFollow()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        ToolAvailabilityState state = ToolAvailabilityState.Available;
        fixture.ViewModel.ToolStateResolver = _ => state;
        fixture.ViewModel.ToolDescriptorResolver = id => id == GatedToolId
            ? new ToolDescriptor(GatedToolId, ToolCategory.Network, "ToolCategoryNetwork", "Gated tool", null, ["gated"], true)
            : null;
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateToolEntry("tool", "Gated entry", GatedToolId));
        ServerItemViewModel item = fixture.ServerById("tool");
        fixture.ViewModel.SelectSingle(item);
        Assert.False(fixture.ViewModel.ToolDetailIsUnavailable);
        List<string?> itemChanged = [];
        List<string?> listChanged = [];
        item.PropertyChanged += (_, e) => itemChanged.Add(e.PropertyName);
        fixture.ViewModel.PropertyChanged += (_, e) => listChanged.Add(e.PropertyName);

        state = ToolAvailabilityState.DisabledByUser;
        fixture.ViewModel.RefreshToolAvailability();

        Assert.True(item.IsToolUnavailable);
        Assert.Contains(nameof(ServerItemViewModel.IsToolUnavailable), itemChanged);
        Assert.Contains(nameof(ServerItemViewModel.AccessibleName), itemChanged);
        Assert.Contains(nameof(ServerItemViewModel.RowTooltipText), itemChanged);
        Assert.True(fixture.ViewModel.ToolDetailIsUnavailable);
        Assert.Equal(item.ToolUnavailableReason, fixture.ViewModel.ToolDetailUnavailableReason);
        Assert.Contains(nameof(ServerListViewModel.ToolDetailUnavailableReason), listChanged);

        state = ToolAvailabilityState.Available;
        fixture.ViewModel.RefreshToolAvailability();

        Assert.False(item.IsToolUnavailable);
        Assert.False(fixture.ViewModel.ToolDetailIsUnavailable);
        Assert.Null(fixture.ViewModel.ToolDetailUnavailableReason);
    }

    [Fact]
    public async Task TheDetailPane_ShowsNoReasonForANonToolSelection()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.ViewModel.ToolStateResolver = _ => ToolAvailabilityState.DisabledByUser;
        fixture.LoadServers(
            fixture.ExpandGroups("ops"),
            CreateServer("ssh", "Ordinary", "ops"));

        fixture.ViewModel.SelectSingle(fixture.ServerById("ssh"));

        Assert.False(fixture.ViewModel.ToolDetailIsUnavailable);
        Assert.Null(fixture.ViewModel.ToolDetailUnavailableReason);
    }
}
