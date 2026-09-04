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

using Heimdall.App.ViewModels.Tools;
using Heimdall.Core.Configuration;

namespace Heimdall.App.Tests;

/// <summary>
/// The selection of a "Route via" list survives what a settings save does to the inventory:
/// every entry replaced by a clone, entries reordered, the picked one edited or gone.
/// </summary>
public sealed class GatewayRouteModelTests
{
    [Fact]
    public void Select_ByComboIndex_NamesTheGatewayAndZeroMeansDirect()
    {
        GatewayRouteModel model = new();
        model.Seed([Gateway("gw-1", "Paris"), Gateway("gw-2", "Berlin")]);

        Assert.Equal("gw-2", model.Select(2)?.Id);
        Assert.Equal("gw-2", model.SelectedId);
        Assert.Equal(2, model.SelectedIndex);

        Assert.Null(model.Select(0));
        Assert.Null(model.SelectedId);
        Assert.Equal(0, model.SelectedIndex);
    }

    [Fact]
    public void Apply_KeepsTheSelectionByIdAcrossACloneAndAReorder()
    {
        GatewayRouteModel model = new();
        model.Seed([Gateway("gw-1", "Paris"), Gateway("gw-2", "Berlin")]);
        model.Select(2);

        // The same two gateways, freshly cloned and in the other order, one of them renamed.
        GatewayRouteRefresh refresh = model.Apply([Gateway("gw-2", "Berlin DC"), Gateway("gw-1", "Paris")]);

        Assert.True(refresh.SelectionChanged);
        Assert.Equal("gw-2", refresh.Selected?.Id);
        Assert.Equal("Berlin DC", refresh.Selected?.Name);
        Assert.Null(refresh.LostGateway);
        Assert.Equal(1, model.SelectedIndex);
    }

    [Fact]
    public void Apply_ReportsNothingWhenTheSelectedGatewayIsUntouched()
    {
        GatewayRouteModel model = new();
        model.Seed([Gateway("gw-1", "Paris"), Gateway("gw-2", "Berlin")]);
        model.Select(1);

        // A save that edited the OTHER gateway: the selected one arrives as an identical clone.
        GatewayRouteRefresh refresh = model.Apply([Gateway("gw-1", "Paris"), Gateway("gw-2", "Berlin, renamed")]);

        Assert.False(refresh.SelectionChanged);
        Assert.Equal(GatewayRouteRefresh.Unchanged, refresh);
        Assert.Equal("gw-1", model.SelectedId);
    }

    [Fact]
    public void Apply_ReportsAHostChangeAsANewGatewayToDial()
    {
        GatewayRouteModel model = new();
        model.Seed([Gateway("gw-1", "Paris", host: "10.0.0.1")]);
        model.Select(1);

        GatewayRouteRefresh refresh = model.Apply([Gateway("gw-1", "Paris", host: "10.0.0.2")]);

        Assert.True(refresh.SelectionChanged);
        Assert.Equal("10.0.0.2", refresh.Selected?.Host);
    }

    [Fact]
    public void Apply_FallsBackToDirectAndNamesTheGatewayThatWentAway()
    {
        GatewayRouteModel model = new();
        model.Seed([Gateway("gw-1", "Paris"), Gateway("gw-2", "Berlin")]);
        model.Select(2);

        GatewayRouteRefresh refresh = model.Apply([Gateway("gw-1", "Paris")]);

        Assert.True(refresh.SelectionChanged);
        Assert.Null(refresh.Selected);
        Assert.Equal("Berlin", refresh.LostGateway?.Name);
        Assert.Null(model.SelectedId);
        Assert.Equal(0, model.SelectedIndex);
        Assert.Single(model.Gateways);
    }

    [Fact]
    public void Apply_LeavesADirectSelectionAloneWhateverTheInventoryDid()
    {
        GatewayRouteModel model = new();
        model.Seed([Gateway("gw-1", "Paris")]);

        GatewayRouteRefresh refresh = model.Apply([]);

        Assert.False(refresh.SelectionChanged);
        Assert.Null(model.SelectedId);
        Assert.Empty(model.Gateways);
    }

    [Fact]
    public void SameGateway_ReadsEveryPersistedProperty_NotAHandKeptList()
    {
        // A credential change is not a name, host or port change, and it must still count: the
        // tool dials with it. The comparison goes through the DTO's own serialization so that a
        // property added later counts without anyone remembering to list it.
        SshGatewayDto before = Gateway("gw-1", "Paris");
        SshGatewayDto after = Gateway("gw-1", "Paris");
        after.SshPasswordEncrypted = "different";

        Assert.False(GatewayRouteModel.SameGateway(before, after));
        Assert.True(GatewayRouteModel.SameGateway(before, Gateway("gw-1", "Paris")));
    }

    private static SshGatewayDto Gateway(string id, string name, string host = "127.0.0.1")
        => new()
        {
            Id = id,
            Name = name,
            Host = host,
            Port = 22,
            User = "bastion"
        };
}
