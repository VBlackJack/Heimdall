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

using System.Net;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Services.SharedSessions;

/// <summary>
/// Builds the import report of a shared source from what its file parsed to and what was loaded.
/// </summary>
/// <remarks>
/// <para>Shared files are edited by hand by several people, and the mistakes that matter look
/// like ordinary sessions: a session named after one machine that reaches another, one that takes a
/// different bastion than the rest of its folder, the same host twice. The report lists them so the
/// file can be fixed; it never decides anything about a session.</para>
/// <para>The rules, each applied to the sessions that were loaded:</para>
/// <list type="bullet">
/// <item>Skipped sessions and unresolved variables are the parser's own warnings, plus the sessions
/// the shared filter refused.</item>
/// <item>Name and host: the session name, compared case-insensitively, must equal either the host or
/// the host's first label (what precedes the first dot). A host that is an IP address has no name to
/// compare and is not checked.</item>
/// <item>Gateway outlier: in a folder of at least <see cref="Options.GatewayMinFolderSessions"/>
/// sessions where one route (a gateway, or a direct connection) is taken by at least
/// <see cref="Options.GatewayMajorityPercent"/> percent of them, every session taking another route.
/// A folder without such a majority has no outlier. Only sessions whose protocol can be routed
/// through a gateway count: a Telnet or VNC session is direct whatever the folder does, so it
/// neither votes nor stands out.</item>
/// <item>Duplicate host: two or more sessions of the same folder with the same host and the same
/// protocol, compared case-insensitively, whatever their port. An SSH and an SFTP session to one
/// host are two ways to use it, not a repetition.</item>
/// </list>
/// </remarks>
public static class SharedSessionImportReportBuilder
{
    private const char FolderSeparator = '/';
    private const char MobaFolderSeparator = '\\';
    private const string FolderKey = "SubRep";
    private const char HostLabelSeparator = '.';

    /// <summary>The thresholds of the rules that can vary.</summary>
    /// <param name="GatewayMajorityPercent">The share of a folder one route must reach to be its usual one.</param>
    /// <param name="GatewayMinFolderSessions">The fewest sessions a folder needs to be checked for outliers.</param>
    public sealed record Options(int GatewayMajorityPercent, int GatewayMinFolderSessions)
    {
        /// <summary>The thresholds of <paramref name="settings"/>, each clamped to its declared range.</summary>
        public static Options FromSettings(AppSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            return new Options(
                SettingRanges.Of(nameof(AppSettings.SharedReportGatewayMajorityPercent)).Clamp(settings.SharedReportGatewayMajorityPercent),
                SettingRanges.Of(nameof(AppSettings.SharedReportGatewayMinFolderSessions)).Clamp(settings.SharedReportGatewayMinFolderSessions));
        }
    }

    /// <summary>Builds the report of one read of a shared source.</summary>
    /// <param name="sourceId">The source.</param>
    /// <param name="sourceName">The source's name.</param>
    /// <param name="content">The decoded text of the file, to place the parser's warnings in their folder.</param>
    /// <param name="warnings">The parser's warnings.</param>
    /// <param name="loaded">The sessions loaded, sanitized.</param>
    /// <param name="gateways">The gateways loaded, sanitized.</param>
    /// <param name="refused">The parsed sessions the shared filter refused, with why.</param>
    /// <param name="options">The thresholds.</param>
    public static SharedSessionImportReport Build(
        string sourceId,
        string sourceName,
        string? content,
        IReadOnlyList<MobaXtermImportWarning> warnings,
        IReadOnlyList<ServerProfileDto> loaded,
        IReadOnlyList<SshGatewayDto> gateways,
        IReadOnlyList<SharedRefusedSession> refused,
        Options options)
    {
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(gateways);
        ArgumentNullException.ThrowIfNull(refused);
        ArgumentNullException.ThrowIfNull(options);

        Dictionary<string, string> sectionFolders = ReadSectionFolders(content);
        List<SharedSessionReportEntry> entries = [];

        foreach (MobaXtermImportWarning warning in warnings)
        {
            string folder = sectionFolders.GetValueOrDefault(warning.Section, string.Empty);
            entries.Add(warning.Kind == MobaXtermImportWarningKind.UnresolvedVariable
                ? new SharedSessionReportEntry(SharedSessionReportCheck.UnresolvedVariable, folder, warning.SessionName, warning.Detail)
                : new SharedSessionReportEntry(SharedSessionReportCheck.SkippedSession, folder, warning.SessionName, warning.Detail, SkipReason: warning.Kind));
        }

        foreach (SharedRefusedSession session in refused)
        {
            entries.Add(new SharedSessionReportEntry(
                SharedSessionReportCheck.SkippedSession,
                FolderOf(session.Profile),
                session.Profile.DisplayName ?? string.Empty,
                session.Detail,
                Refusal: session.Reason));
        }

        entries.AddRange(NameHostMismatches(loaded));
        entries.AddRange(GatewayOutliers(loaded, gateways, options));
        entries.AddRange(DuplicateHosts(loaded));

        return new SharedSessionImportReport(
            sourceId,
            sourceName,
            [.. entries
                .OrderBy(entry => entry.Check)
                .ThenBy(entry => entry.FolderPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.SessionName, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// Whether <paramref name="sessionName"/> names <paramref name="host"/>: it is the host itself or
    /// the host's first label, in any case. Null when the host cannot be compared (empty, or an
    /// IP address).
    /// </summary>
    public static bool? NameMatchesHost(string? sessionName, string? host)
    {
        string name = sessionName?.Trim() ?? string.Empty;
        string target = host?.Trim() ?? string.Empty;
        if (target.Length == 0 || IPAddress.TryParse(target, out _))
        {
            return null;
        }

        int separator = target.IndexOf(HostLabelSeparator);
        string firstLabel = separator > 0 ? target[..separator] : target;
        return string.Equals(name, target, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, firstLabel, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<SharedSessionReportEntry> NameHostMismatches(IReadOnlyList<ServerProfileDto> loaded) =>
        loaded
            .Where(profile => NameMatchesHost(profile.DisplayName, profile.RemoteServer) == false)
            .Select(profile => new SharedSessionReportEntry(
                SharedSessionReportCheck.NameHostMismatch,
                FolderOf(profile),
                profile.DisplayName ?? string.Empty,
                profile.RemoteServer ?? string.Empty));

    private static IEnumerable<SharedSessionReportEntry> GatewayOutliers(
        IReadOnlyList<ServerProfileDto> loaded,
        IReadOnlyList<SshGatewayDto> gateways,
        Options options)
    {
        Dictionary<string, SshGatewayDto> gatewaysById = gateways
            .GroupBy(gateway => gateway.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        IEnumerable<ServerProfileDto> routable = loaded.Where(profile => ProtocolCapabilities.SupportsSshGateway(profile.ConnectionType));
        foreach (IGrouping<string, ServerProfileDto> folder in routable.GroupBy(FolderOf, StringComparer.OrdinalIgnoreCase))
        {
            List<ServerProfileDto> sessions = [.. folder];
            if (sessions.Count < options.GatewayMinFolderSessions)
            {
                continue;
            }

            IGrouping<string, ServerProfileDto> usual = sessions
                .GroupBy(RouteOf, StringComparer.Ordinal)
                .OrderByDescending(route => route.Count())
                .First();

            // A tie or a plurality is not a rule the folder follows: nothing stands out from it.
            if (usual.Count() * 100 < options.GatewayMajorityPercent * sessions.Count)
            {
                continue;
            }

            string usualName = RouteName(usual.Key, gatewaysById);
            foreach (ServerProfileDto outlier in sessions.Where(session => !string.Equals(RouteOf(session), usual.Key, StringComparison.Ordinal)))
            {
                yield return new SharedSessionReportEntry(
                    SharedSessionReportCheck.GatewayOutlier,
                    folder.Key,
                    outlier.DisplayName ?? string.Empty,
                    RouteName(RouteOf(outlier), gatewaysById),
                    usualName);
            }
        }
    }

    private static IEnumerable<SharedSessionReportEntry> DuplicateHosts(IReadOnlyList<ServerProfileDto> loaded)
    {
        IEnumerable<IGrouping<(string Folder, string Host, string Protocol), ServerProfileDto>> sameHost = loaded
            .Where(profile => !string.IsNullOrWhiteSpace(profile.RemoteServer))
            .GroupBy(
                profile => (
                    Folder: FolderOf(profile).ToUpperInvariant(),
                    Host: profile.RemoteServer!.Trim().ToUpperInvariant(),
                    Protocol: (profile.ConnectionType ?? string.Empty).Trim().ToUpperInvariant()));
        foreach (IGrouping<(string Folder, string Host, string Protocol), ServerProfileDto> group in sameHost)
        {
            List<ServerProfileDto> sessions = [.. group];
            if (sessions.Count < 2)
            {
                continue;
            }

            foreach (ServerProfileDto session in sessions)
            {
                yield return new SharedSessionReportEntry(
                    SharedSessionReportCheck.DuplicateHost,
                    FolderOf(session),
                    session.DisplayName ?? string.Empty,
                    session.RemoteServer!.Trim(),
                    string.Join(", ", sessions.Where(other => !ReferenceEquals(other, session)).Select(other => other.DisplayName)));
            }
        }
    }

    /// <summary>The route of a session: its gateway's identifier, or empty for a direct connection.</summary>
    private static string RouteOf(ServerProfileDto profile) =>
        !profile.UseDirectConnection && SshGatewayRoute.IsAssigned(profile.SshGatewayId)
            ? profile.SshGatewayId!
            : string.Empty;

    /// <summary>The name a route is shown with; empty stands for a direct connection.</summary>
    private static string RouteName(string route, IReadOnlyDictionary<string, SshGatewayDto> gatewaysById) =>
        route.Length == 0
            ? string.Empty
            : gatewaysById.TryGetValue(route, out SshGatewayDto? gateway) && !string.IsNullOrWhiteSpace(gateway.Name)
                ? gateway.Name
                : route;

    private static string FolderOf(ServerProfileDto profile) => profile.Group?.Trim() ?? string.Empty;

    /// <summary>
    /// The folder of every section of the file, read from its <c>SubRep</c> line: the parser's
    /// warnings name the section, the report names the folder.
    /// </summary>
    private static Dictionary<string, string> ReadSectionFolders(string? content)
    {
        Dictionary<string, string> folders = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(content))
        {
            return folders;
        }

        string? section = null;
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length > 2 && line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                continue;
            }

            int equals = line.IndexOf('=');
            if (section is not null
                && equals > 0
                && string.Equals(line[..equals].Trim(), FolderKey, StringComparison.OrdinalIgnoreCase))
            {
                folders[section] = string.Join(
                    FolderSeparator,
                    line[(equals + 1)..]
                        .Split(MobaFolderSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        return folders;
    }
}
