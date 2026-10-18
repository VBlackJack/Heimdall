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

using Heimdall.Core.Models;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Reduces a profile or gateway read from a shared session file to the fields a shared source
/// may decide, by building a new object from an allowlist.
/// </summary>
/// <remarks>
/// <para>A shared file is written by other people and read here without anyone reviewing it, so
/// the question is not which fields are dangerous but which are needed. Everything a shared
/// session needs to reach its host is copied; everything else - the local shell, the web and
/// Citrix launchers, external clients, key paths, X11 and agent forwarding, every stored secret,
/// the execution trust flag - is left at the default of a new object. A field added to the DTO
/// later is therefore excluded until someone adds it here, and the reflection guard in the tests
/// fails until that decision is made either way.</para>
/// <para>Building a new object rather than clearing fields on the old one is the point: a list of
/// fields to clear protects only against the fields someone thought of.</para>
/// </remarks>
public static class SharedProfileSanitizer
{
    private const string SshConnectionType = "SSH";
    private const string RdpConnectionType = "RDP";

    /// <summary>The <see cref="ServerProfileDto"/> properties a shared profile keeps.</summary>
    public static IReadOnlySet<string> KeptProfileProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(ServerProfileDto.Id),
        nameof(ServerProfileDto.DisplayName),
        nameof(ServerProfileDto.Group),
        nameof(ServerProfileDto.ConnectionType),
        nameof(ServerProfileDto.RemoteServer),
        nameof(ServerProfileDto.RemotePort),
        nameof(ServerProfileDto.SshPort),
        nameof(ServerProfileDto.SshUsername),
        nameof(ServerProfileDto.RdpUsername),
        nameof(ServerProfileDto.SshGatewayId),
        nameof(ServerProfileDto.UseDirectConnection),
        nameof(ServerProfileDto.PostConnectSteps),
        nameof(ServerProfileDto.Tags),
        nameof(ServerProfileDto.Origin),
    };

    /// <summary>The <see cref="SshGatewayDto"/> properties a shared gateway keeps.</summary>
    public static IReadOnlySet<string> KeptGatewayProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(SshGatewayDto.Id),
        nameof(SshGatewayDto.Name),
        nameof(SshGatewayDto.Host),
        nameof(SshGatewayDto.Port),
        nameof(SshGatewayDto.User),
        nameof(SshGatewayDto.ParentGatewayId),
    };

    /// <summary>
    /// The shared form of <paramref name="source"/>, or null when it cannot be a shared profile:
    /// an identifier outside the shared namespace, a protocol other than SSH or RDP, a host the
    /// inventory would refuse (<see cref="SchemaValidator.IsValidHost"/>: a value starting with a
    /// dash or holding a space could pass for an option of Plink or of an external tool), or a
    /// gateway reference outside the shared namespace (dropping that reference would turn a session
    /// meant to cross a bastion into a direct one).
    /// </summary>
    public static ServerProfileDto? Sanitize(ServerProfileDto source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (RefusalOf(source) is not null)
        {
            return null;
        }

        string connectionType = NormalizeConnectionType(source.ConnectionType)!;

        var profile = new ServerProfileDto
        {
            Id = source.Id,
            DisplayName = source.DisplayName,
            Group = source.Group,
            ConnectionType = connectionType,
            RemoteServer = source.RemoteServer,
            RemotePort = source.RemotePort,
            SshUsername = source.SshUsername,
            RdpUsername = source.RdpUsername,
            SshGatewayId = SshGatewayRoute.IsAssigned(source.SshGatewayId) ? source.SshGatewayId : null,
            UseDirectConnection = source.UseDirectConnection,
            PostConnectSteps = SanitizeSteps(source.PostConnectSteps),
            Tags = source.Tags,
            Origin = source.Origin,
        };

        // Through the setter only when the source declared a port, so an RDP profile does not gain
        // an SSH port it never had.
        if (source.HasSshPortField)
        {
            profile.SshPort = source.SshPort;
        }

        return profile;
    }

    /// <summary>
    /// Why <see cref="Sanitize(ServerProfileDto)"/> refuses <paramref name="source"/>, or null when
    /// it accepts it. The first reason in the order of <see cref="SharedProfileRefusal"/> wins.
    /// </summary>
    public static SharedProfileRefusal? RefusalOf(ServerProfileDto source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!SharedSessionIds.IsShared(source.Id))
        {
            return SharedProfileRefusal.ForeignId;
        }

        if (NormalizeConnectionType(source.ConnectionType) is null)
        {
            return SharedProfileRefusal.UnsupportedProtocol;
        }

        if (!SchemaValidator.IsValidHost(source.RemoteServer))
        {
            return SharedProfileRefusal.InvalidHost;
        }

        if (SshGatewayRoute.IsAssigned(source.SshGatewayId) && !SharedSessionIds.IsShared(source.SshGatewayId))
        {
            return SharedProfileRefusal.ForeignGateway;
        }

        return null;
    }

    /// <summary>
    /// The shared form of <paramref name="source"/>, or null when its identifier is outside the
    /// shared namespace or its host is one the inventory would refuse. A parent outside the
    /// namespace is dropped.
    /// </summary>
    public static SshGatewayDto? Sanitize(SshGatewayDto source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!SharedSessionIds.IsShared(source.Id) || !SchemaValidator.IsValidHost(source.Host))
        {
            return null;
        }

        return new SshGatewayDto
        {
            Id = source.Id,
            Name = source.Name,
            Host = source.Host,
            Port = source.Port,
            User = source.User,
            ParentGatewayId = SharedSessionIds.IsShared(source.ParentGatewayId) ? source.ParentGatewayId : null,
        };
    }

    private static string? NormalizeConnectionType(string? connectionType) =>
        string.Equals(connectionType, SshConnectionType, StringComparison.OrdinalIgnoreCase) ? SshConnectionType
        : string.Equals(connectionType, RdpConnectionType, StringComparison.OrdinalIgnoreCase) ? RdpConnectionType
        : null;

    /// <summary>
    /// Keeps the text each step types into the remote session and how it is paced. A reference to
    /// the local command library is dropped: it names an entry on this machine, which a shared
    /// file has no business choosing.
    /// </summary>
    private static List<PostConnectStep> SanitizeSteps(IEnumerable<PostConnectStep?>? steps)
    {
        List<PostConnectStep> kept = [];
        foreach (PostConnectStep? step in steps ?? [])
        {
            if (step is null || string.IsNullOrWhiteSpace(step.Input))
            {
                continue;
            }

            kept.Add(new PostConnectStep
            {
                Id = step.Id,
                Input = step.Input,
                DelayMs = Math.Max(0, step.DelayMs),
                Enabled = step.Enabled,
                OnFailure = Enum.IsDefined(step.OnFailure) ? step.OnFailure : PostConnectFailurePolicy.Continue,
            });
        }

        return kept;
    }
}
