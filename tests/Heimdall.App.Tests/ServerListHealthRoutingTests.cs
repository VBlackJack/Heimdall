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
using Heimdall.Core.SessionHealth;

namespace Heimdall.App.Tests;

public sealed partial class ServerListSelectionTests
{
    [Fact]
    public async Task HealthRouting_DropsStaleGenerationForIndexedServer()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync(
            withHealthMonitor: true);
        fixture.LoadServers(
            new AppSettings(),
            CreateServer("health-1", "Health 1", string.Empty));
        var server = fixture.ServerById("health-1");
        SessionHealthMonitor monitor = Assert.IsType<SessionHealthMonitor>(
            fixture.HealthMonitor);

        bool newerApplied = monitor.PublishState(
            server.Id,
            new HealthState(HealthStatus.Up, DateTime.UtcNow, 3, null),
            generation: 8);
        bool olderApplied = monitor.PublishState(
            server.Id,
            new HealthState(HealthStatus.Down, DateTime.UtcNow, null, "late"),
            generation: 7);
        bool staleUiEventApplied = fixture.ViewModel.ApplyServerHealthChange(
            new HealthStateChange(
                server.Id,
                new HealthState(HealthStatus.Down, DateTime.UtcNow, null, "queued-late"),
                Generation: 7));

        Assert.True(newerApplied);
        Assert.False(olderApplied);
        Assert.False(staleUiEventApplied);
        Assert.Equal(HealthStatus.Up, server.HealthState.Status);
    }

    [Fact]
    public async Task HealthIndex_RebuildsWhenInventoryIsReplaced()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.LoadServers(
            new AppSettings(),
            CreateServer("remove-me", "Remove", string.Empty),
            CreateServer("keep-me", "Keep", string.Empty));

        Assert.Equal(2, fixture.ViewModel.HealthServerIndexCount);

        fixture.LoadServers(
            new AppSettings(),
            CreateServer("keep-me", "Keep", string.Empty),
            CreateServer("add-me", "Add", string.Empty));

        Assert.Equal(2, fixture.ViewModel.HealthServerIndexCount);
        Assert.False(fixture.ViewModel.ApplyServerHealthChange(
            new HealthStateChange(
                "remove-me",
                new HealthState(HealthStatus.Up, DateTime.UtcNow, 1, null),
                Generation: 10)));
        Assert.True(fixture.ViewModel.ApplyServerHealthChange(
            new HealthStateChange(
                "add-me",
                new HealthState(HealthStatus.Down, DateTime.UtcNow, null, "refused"),
                Generation: 10)));
        Assert.Equal(
            HealthStatus.Down,
            fixture.ServerById("add-me").HealthState.Status);
    }

    [Fact]
    public async Task HealthIndex_RoutesLargeInventoryById()
    {
        await using var fixture = await ServerListSelectionFixture.CreateAsync();
        ServerProfileDto[] servers = Enumerable.Range(0, 500)
            .Select(index => CreateServer(
                $"scale-{index:D3}",
                $"Scale {index:D3}",
                string.Empty))
            .ToArray();
        fixture.LoadServers(new AppSettings(), servers);

        Assert.Equal(servers.Length, fixture.ViewModel.HealthServerIndexCount);
        Assert.True(fixture.ViewModel.ApplyServerHealthChange(
            new HealthStateChange(
                "scale-499",
                new HealthState(HealthStatus.Up, DateTime.UtcNow, 2, null),
                Generation: 42)));
        Assert.Equal(
            HealthStatus.Up,
            fixture.ServerById("scale-499").HealthState.Status);
    }

    private sealed class FixtureHealthProbe : IHealthProbe
    {
        public Task<HealthState> ProbeAsync(
            string host,
            int port,
            int timeoutMs,
            CancellationToken ct)
        {
            return Task.FromResult(
                new HealthState(HealthStatus.Up, DateTime.UtcNow, 1, null));
        }
    }
}
