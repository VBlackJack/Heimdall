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

/// <summary>Holds a fixed set of shared profiles and gateways and hands out copies, like the provider.</summary>
internal sealed class StubSharedSessionProvider(
    IReadOnlyList<ServerProfileDto> profiles,
    IReadOnlyList<SshGatewayDto> gateways) : ISharedSessionProvider
{
    public event Action? Changed;

    public int LoadCount { get; private set; }

    public IReadOnlyList<SharedSessionSourceState> Sources => [];

    public IReadOnlyList<ServerProfileDto> Profiles => profiles.Select(profile => profile.CloneFaithfully()).ToList();

    public IReadOnlyList<SshGatewayDto> Gateways => gateways.Select(gateway => gateway.CloneFaithfully()).ToList();

    public ServerProfileDto? FindProfile(string profileId) =>
        profiles.FirstOrDefault(profile => string.Equals(profile.Id, profileId, StringComparison.Ordinal))
            ?.CloneFaithfully();

    public Task LoadAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        LoadCount++;
        Changed?.Invoke();
        return Task.CompletedTask;
    }
}
