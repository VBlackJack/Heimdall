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

using System.Globalization;
using Heimdall.App.Tests.TestHelpers;
using Heimdall.App.ViewModels;
using Heimdall.Core.Configuration;
using Heimdall.Core.SessionHealth;

namespace Heimdall.App.Tests;

/// <summary>
/// The SSH key, latency and last opened lines of the properties panel, as the row computes them.
/// </summary>
public sealed class PropertiesPanelFieldsTests
{
    [Theory]
    [InlineData("SSH", @"C:\keys\ops.ppk")]
    [InlineData("SFTP", @"C:\keys\ops.ppk")]
    [InlineData("RDP", "")]
    [InlineData("WEB", "")]
    public void TheKey_IsShownOnlyWhereItIsUsed(string connectionType, string expected)
    {
        ServerItemViewModel row = ServerItemViewModel.FromDto(new ServerProfileDto
        {
            Id = "srv",
            DisplayName = "srv",
            ConnectionType = connectionType,
            RemoteServer = "host.example.test",
            SshKeyPath = @"C:\keys\ops.ppk",
        });

        Assert.Equal(expected, row.SshKeyText);
    }

    /// <summary>A profile edited from SSH to RDP keeps its key field; the line goes with the type.</summary>
    [Fact]
    public void ChangingTheType_RemovesTheKeyLine()
    {
        ServerProfileDto dto = new()
        {
            Id = "srv",
            DisplayName = "srv",
            ConnectionType = "SSH",
            RemoteServer = "host.example.test",
            SshKeyPath = @"C:\keys\ops.ppk",
        };
        ServerItemViewModel row = ServerItemViewModel.FromDto(dto);
        List<string?> changed = [];
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.UpdateFromDto(new ServerProfileDto
        {
            Id = "srv",
            DisplayName = "srv",
            ConnectionType = "RDP",
            RemoteServer = "host.example.test",
            SshKeyPath = @"C:\keys\ops.ppk",
        });

        Assert.Equal("", row.SshKeyText);
        Assert.Contains(nameof(ServerItemViewModel.SshKeyText), changed);
    }

    [Fact]
    public void TheLatency_OfASuccessfulProbe_IsShown()
    {
        ServerItemViewModel row = Row();
        List<string?> changed = [];
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.HealthState = new HealthState(HealthStatus.Up, DateTime.UtcNow, 42, null);

        Assert.Equal("42 ms", row.LatencyText);
        Assert.Contains(nameof(ServerItemViewModel.LatencyText), changed);
    }

    /// <summary>
    /// A failed probe can still carry the time it took to fail; shown, it would read as the
    /// latency of a host that is down.
    /// </summary>
    [Theory]
    [InlineData(HealthStatus.Down)]
    [InlineData(HealthStatus.Probing)]
    [InlineData(HealthStatus.Unknown)]
    public void NoLatency_IsShownWithoutASuccessfulProbe(HealthStatus status)
    {
        ServerItemViewModel row = Row();

        row.HealthState = new HealthState(status, DateTime.UtcNow, 42, "timeout");

        Assert.Equal("", row.LatencyText);
    }

    [Fact]
    public void TheLastOpenedTime_IsShownInLocalTime()
    {
        ServerItemViewModel row = Row();
        DateTime openedUtc = new(2026, 10, 12, 8, 30, 0, DateTimeKind.Utc);
        List<string?> changed = [];
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.LastOpenedUtc = openedUtc;

        Assert.Equal(openedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), row.LastOpenedText);
        Assert.Contains(nameof(ServerItemViewModel.LastOpenedText), changed);

        row.LastOpenedUtc = null;
        Assert.Equal("", row.LastOpenedText);
    }

    private static ServerItemViewModel Row() => ServerItemViewModel.FromDto(
        new ServerProfileDto
        {
            Id = "srv",
            DisplayName = "srv",
            ConnectionType = "SSH",
            RemoteServer = "host.example.test",
        },
        localizer: EnglishLocalizer.Create());
}

/// <summary>
/// The last opened time, seen through the list: read from the history once, kept on every row
/// rebuild, and moved when a session opens.
/// </summary>
public sealed partial class ServerListSelectionTests
{
    private static readonly DateTime HistoryOpenedUtc = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TheHistory_ReachesTheRowsItNames()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.ViewModel.LastOpenedSource = () => new Dictionary<string, DateTime> { ["alpha"] = HistoryOpenedUtc };

        fixture.LoadServers(
            await fixture.ConfigManager.LoadSettingsAsync(),
            CreateServer("alpha", "Alpha", "ops"),
            CreateServer("beta", "Beta", "ops"));

        Assert.Equal(HistoryOpenedUtc, fixture.ServerById("alpha").LastOpenedUtc);
        Assert.Null(fixture.ServerById("beta").LastOpenedUtc);
    }

    [Fact]
    public async Task TheHistory_IsReadOnce()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        int reads = 0;
        fixture.ViewModel.LastOpenedSource = () =>
        {
            reads++;
            return new Dictionary<string, DateTime>();
        };
        AppSettings settings = await fixture.ConfigManager.LoadSettingsAsync();

        fixture.LoadServers(settings, CreateServer("alpha", "Alpha", "ops"));
        fixture.LoadServers(settings, CreateServer("alpha", "Alpha", "ops"));

        Assert.Equal(1, reads);
    }

    /// <summary>
    /// A reload builds new rows. The time a session was opened in this run is not in the file the
    /// list read at startup, so it must survive in the list, not only on the old row.
    /// </summary>
    [Fact]
    public async Task AnOpenedSession_MovesTheTimeAndKeepsItAcrossAReload()
    {
        await using ServerListSelectionFixture fixture = await ServerListSelectionFixture.CreateAsync();
        fixture.ViewModel.LastOpenedSource = () => new Dictionary<string, DateTime> { ["alpha"] = HistoryOpenedUtc };
        AppSettings settings = await fixture.ConfigManager.LoadSettingsAsync();
        fixture.LoadServers(settings, CreateServer("alpha", "Alpha", "ops"));
        DateTime openedNow = HistoryOpenedUtc.AddDays(5);

        fixture.ViewModel.NoteOpened("alpha", openedNow);
        Assert.Equal(openedNow, fixture.ServerById("alpha").LastOpenedUtc);

        fixture.LoadServers(settings, CreateServer("alpha", "Alpha", "ops"));
        Assert.Equal(openedNow, fixture.ServerById("alpha").LastOpenedUtc);
    }
}
