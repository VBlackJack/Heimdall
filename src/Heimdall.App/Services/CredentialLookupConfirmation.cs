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
using Heimdall.Core.Configuration;
using Heimdall.Core.Localization;
using Heimdall.Core.Utilities;

namespace Heimdall.App.Services;

/// <summary>What the user is asked to confirm about one imported session.</summary>
/// <param name="LookupKey">The entry the credential provider would be asked for.</param>
/// <param name="Protocol">The connection type.</param>
/// <param name="Host">The host the connection goes to.</param>
/// <param name="Port">The port the connection goes to.</param>
/// <param name="User">The account, or empty.</param>
/// <param name="GatewayChain">The SSH gateways crossed, nearest first, each with its host.</param>
/// <param name="RdGateway">The RD Gateway host, or null.</param>
internal sealed record CredentialLookupRow(
    string LookupKey,
    string Protocol,
    string Host,
    int Port,
    string User,
    IReadOnlyList<string> GatewayChain,
    string? RdGateway);

/// <summary>
/// Builds what the "Confirm credential lookup" command shows before it lets a session's
/// stored password be looked up in the external credential provider.
/// </summary>
internal static class CredentialLookupConfirmation
{
    /// <summary>Separates the gateways of a chain in the text shown.</summary>
    private const string GatewaySeparator = " > ";

    /// <summary>
    /// Longest profile-supplied value shown, in characters. Longer values are cut with a marker,
    /// so a name cannot push the destination out of view.
    /// </summary>
    internal const int MaxValueLength = 80;

    /// <summary>The most sessions one confirmation lists; a larger selection is confirmed in rounds.</summary>
    internal const int MaxRowsShown = 10;

    /// <summary>Whether two rows show the user the same thing.</summary>
    internal static bool AreSame(CredentialLookupRow first, CredentialLookupRow second)
        => string.Equals(first.LookupKey, second.LookupKey, StringComparison.Ordinal)
            && string.Equals(first.Protocol, second.Protocol, StringComparison.OrdinalIgnoreCase)
            && string.Equals(first.Host, second.Host, StringComparison.Ordinal)
            && first.Port == second.Port
            && string.Equals(first.User, second.User, StringComparison.Ordinal)
            && string.Equals(first.RdGateway, second.RdGateway, StringComparison.Ordinal)
            && first.GatewayChain.SequenceEqual(second.GatewayChain, StringComparer.Ordinal);

    private static string Visible(string? value) => UntrustedText.MakeVisible(value, MaxValueLength);

    /// <summary>Whether a session still waits for the user to confirm its lookup.</summary>
    internal static bool NeedsConfirmation(ServerProfileDto? profile)
        => profile is { CredentialLookupConfirmed: false } && !SharedSessionIds.IsSharedProfile(profile);

    /// <summary>
    /// The rows for the given sessions, resolved after the group defaults as the connection would.
    /// </summary>
    internal static IReadOnlyList<CredentialLookupRow> BuildRows(
        IEnumerable<ServerProfileDto> profiles,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(settings);

        List<CredentialLookupRow> rows = [];
        foreach (ServerProfileDto source in profiles)
        {
            ServerProfileDto profile = source.CloneFaithfully();
            GroupDefaultsDto.ApplyInherited(profile, settings.GroupDefaults);
            ServerItemViewModel view = ServerItemViewModel.FromDto(profile);

            string? gatewayId = SshGatewayRoute.GatewayIdFor(profile, settings.GroupDefaults);
            rows.Add(new CredentialLookupRow(
                CredentialLookupPolicy.NameBasedTitle(profile),
                profile.ConnectionType,
                profile.RemoteServer,
                view.EffectivePort,
                view.Username,
                gatewayId is null ? [] : ResolveChain(gatewayId, settings.SshGateways),
                string.IsNullOrWhiteSpace(profile.RdpGateway) ? null : profile.RdpGateway));
        }

        return rows;
    }

    /// <summary>The text of the confirmation: one block per session.</summary>
    internal static string FormatRows(IReadOnlyList<CredentialLookupRow> rows, LocalizationManager localizer)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(localizer);

        List<string> blocks = [];
        foreach (CredentialLookupRow row in rows)
        {
            List<string> lines =
            [
                localizer.Format(
                    "CredentialLookupConfirmRow",
                    Visible(row.LookupKey),
                    Visible(row.Protocol),
                    Visible(row.Host),
                    row.Port,
                    string.IsNullOrEmpty(row.User) ? localizer["CredentialLookupConfirmNoUser"] : Visible(row.User))
            ];

            if (row.GatewayChain.Count > 0)
            {
                lines.Add(localizer.Format(
                    "CredentialLookupConfirmGateways",
                    string.Join(GatewaySeparator, row.GatewayChain.Select(Visible))));
            }

            if (row.RdGateway is not null)
            {
                lines.Add(localizer.Format("CredentialLookupConfirmRdGateway", Visible(row.RdGateway)));
            }

            blocks.Add(string.Join(Environment.NewLine, lines));
        }

        return string.Join(Environment.NewLine + Environment.NewLine, blocks);
    }

    private static List<string> ResolveChain(string gatewayId, IReadOnlyList<SshGatewayDto> gateways)
    {
        List<string> chain = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        string? current = gatewayId;
        while (SshGatewayRoute.IsAssigned(current) && seen.Add(current!))
        {
            SshGatewayDto? gateway = gateways.FirstOrDefault(
                candidate => string.Equals(candidate.Id, current, StringComparison.OrdinalIgnoreCase));
            if (gateway is null)
            {
                chain.Add(current!);
                break;
            }

            string name = string.IsNullOrWhiteSpace(gateway.Name) ? gateway.Id : gateway.Name;
            chain.Add($"{name} ({gateway.Host}:{gateway.Port})");
            current = gateway.ParentGatewayId;
        }

        return chain;
    }
}
