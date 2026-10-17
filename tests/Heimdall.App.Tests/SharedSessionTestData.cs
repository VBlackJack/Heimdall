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

using Heimdall.App.Services.SharedSessions;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// A shared source holding one session behind a shared gateway and one direct session, with fake
/// hosts only, as the provider would hand them out after sanitizing.
/// </summary>
internal sealed class SharedSessionTestData
{
    public const string SourceId = "testsource1";
    public const string SharedFolder = "PROD";
    public const string LoginCommand = "sudo su - www";

    private SharedSessionTestData(
        ServerProfileDto profile,
        ServerProfileDto directProfile,
        SshGatewayDto gateway,
        StubSharedSessionProvider provider)
    {
        Profile = profile;
        DirectProfile = directProfile;
        Gateway = gateway;
        Provider = provider;
    }

    /// <summary>The session behind <see cref="Gateway"/>.</summary>
    public ServerProfileDto Profile { get; }

    /// <summary>A session with no gateway, in the same folder, with no login of its own.</summary>
    public ServerProfileDto DirectProfile { get; }

    public SshGatewayDto Gateway { get; }

    public StubSharedSessionProvider Provider { get; }

    public static SharedSessionTestData Create()
    {
        string prefix = SharedSessionIds.PrefixFor(SourceId);
        var gateway = new SshGatewayDto
        {
            Id = prefix + "gateway1",
            Name = "jdoe@gw1.example.test",
            Host = "gw1.example.test",
            Port = DefaultPorts.Ssh,
            User = "jdoe",
        };

        var profile = new ServerProfileDto
        {
            Id = prefix + "profile1",
            DisplayName = "srv-db01",
            Group = SharedFolder,
            ConnectionType = "SSH",
            RemoteServer = "srv-db01.example.test",
            SshPort = DefaultPorts.Ssh,
            RemotePort = DefaultPorts.Ssh,
            SshUsername = "jdoe",
            SshGatewayId = gateway.Id,
            UseDirectConnection = false,
            Origin = ProfileOrigin.ImportMobaXterm,
            PostConnectSteps = [new PostConnectStep { Input = LoginCommand }],
        };

        var directProfile = new ServerProfileDto
        {
            Id = prefix + "profile2",
            DisplayName = "srv-tools01",
            Group = SharedFolder,
            ConnectionType = "SSH",
            RemoteServer = "srv-tools01.example.test",
            SshPort = DefaultPorts.Ssh,
            RemotePort = DefaultPorts.Ssh,
            UseDirectConnection = true,
            Origin = ProfileOrigin.ImportMobaXterm,
        };

        return new SharedSessionTestData(
            profile,
            directProfile,
            gateway,
            new StubSharedSessionProvider([profile, directProfile], [gateway]));
    }
}

/// <summary>
/// Holds a set of shared profiles, gateways and source states and hands out copies, like the
/// provider. Without states it reports no source, as before the tree listed shared sessions.
/// </summary>
internal sealed class StubSharedSessionProvider(
    IReadOnlyList<ServerProfileDto> profiles,
    IReadOnlyList<SshGatewayDto> gateways,
    IReadOnlyList<SharedSessionSourceState>? sources = null) : ISharedSessionProvider
{
    private IReadOnlyList<ServerProfileDto> _profiles = profiles;
    private IReadOnlyList<SshGatewayDto> _gateways = gateways;
    private IReadOnlyList<SharedSessionSourceState> _sources = sources ?? [];

    public event Action? Changed;

    public event Action<SharedSessionSourceReload>? SourceReloaded;

    public int LoadCount { get; private set; }

    /// <summary>The settings the last load was asked to read.</summary>
    public AppSettings? LastLoadedSettings { get; private set; }

    public IReadOnlyList<SharedSessionSourceState> Sources => _sources;

    public IReadOnlyList<ServerProfileDto> Profiles => _profiles.Select(profile => profile.CloneFaithfully()).ToList();

    public IReadOnlyList<SshGatewayDto> Gateways => _gateways.Select(gateway => gateway.CloneFaithfully()).ToList();

    public ServerProfileDto? FindProfile(string profileId) =>
        _profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal))
            ?.CloneFaithfully();

    /// <summary>Replaces what the next load hands out, as a changed file would.</summary>
    public void SetContents(
        IReadOnlyList<ServerProfileDto> profiles,
        IReadOnlyList<SshGatewayDto> gateways,
        IReadOnlyList<SharedSessionSourceState> sources)
    {
        _profiles = profiles;
        _gateways = gateways;
        _sources = sources;
    }

    public Task LoadAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        LoadCount++;
        LastLoadedSettings = settings;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>Raises the reload of one source, as the provider does after a file changed.</summary>
    public void RaiseSourceReloaded(SharedSessionSourceState state, int previousIssueCount = 0)
    {
        Changed?.Invoke();
        SourceReloaded?.Invoke(new SharedSessionSourceReload(state, previousIssueCount));
    }

    /// <summary>A loaded state for <paramref name="sourceId"/>.</summary>
    public static SharedSessionSourceState Loaded(string sourceId, string name, int profileCount) =>
        new(sourceId, name, @"C:\shares\" + sourceId + ".mxtsessions", SharedSessionSourceStatus.Loaded, null, profileCount, 0, [], 0);

    /// <summary>A failed state for <paramref name="sourceId"/>, carrying <paramref name="reason"/>.</summary>
    public static SharedSessionSourceState Failed(string sourceId, string name, string reason) =>
        new(sourceId, name, @"C:\shares\" + sourceId + ".mxtsessions", SharedSessionSourceStatus.Error, reason, 0, 0, [], 0);

    /// <summary>A disabled state for <paramref name="sourceId"/>.</summary>
    public static SharedSessionSourceState Disabled(string sourceId, string name) =>
        new(sourceId, name, "", SharedSessionSourceStatus.Disabled, null, 0, 0, [], 0);
}
