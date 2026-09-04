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

using System.Collections.ObjectModel;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using ActionModel = TwinShell.Core.Models.Action;

namespace Heimdall.App.ViewModels.CommandPalette;

/// <summary>
/// Search partial of <see cref="CommandPaletteViewModel"/>: the
/// <see cref="OnSearchTextChanged"/> generated handler plus the fuzzy
/// scoring, tool-command parsing and ad-hoc SSH parsing helpers.
/// </summary>
public sealed partial class CommandPaletteViewModel
{
    /// <summary>
    /// Generated partial: recomputes the palette result set every time the
    /// user types in the search TextBox (the XAML binding uses
    /// <c>UpdateSourceTrigger=PropertyChanged</c>). When the query is
    /// empty, seeds the results from active sessions (split mode), recent
    /// tools and the top N servers.
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        var query = value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(query))
        {
            var initialResults = new List<ServerItemViewModel>();

            // In split mode, show active sessions first (merge candidates)
            if (_splitPaletteSession is not null)
            {
                foreach (var s in _main.Connection.ActiveSessions)
                {
                    // Skip the session being split
                    if (s == _splitPaletteSession) continue;
                    if (s.HostControl is null) continue;

                    initialResults.Add(new ServerItemViewModel
                    {
                        Id = $"session-{s.ServerId}",
                        DisplayName = MergeArrowPrefix + s.Title,
                        RemoteServer = _localizer["SplitMergeActiveSession"],
                        ConnectionType = s.ConnectionType ?? "",
                        Group = _localizer["SplitActiveSessionsHeader"]
                    });
                }
            }

            // In split mode, boost previously paired servers to the top
            var boostedIds = new HashSet<string>(StringComparer.Ordinal);
            if (_splitPaletteSession is not null)
            {
                var sessionServerId = _splitPaletteSession.OriginalServerId;
                var partners = _main.Split.LayoutMemory.FindAllPartners(sessionServerId);
                foreach (var entry in partners)
                {
                    var partnerId = string.Equals(entry.PrimaryServerId, sessionServerId, StringComparison.Ordinal)
                        ? entry.SecondaryServerId
                        : entry.PrimaryServerId;

                    var serverVm = _main.ServerList.Servers.FirstOrDefault(
                        s => string.Equals(s.Id, partnerId, StringComparison.Ordinal));
                    if (serverVm is not null && boostedIds.Add(partnerId))
                    {
                        initialResults.Add(serverVm);
                    }
                }
            }

            // Show servers (skip already-boosted ones).
            // In split mode, show ALL servers so every treeview entry is reachable.
            // In normal palette mode, surface up to 10 servers, with hosts the
            // user recently connected to bubbled to the top (RDP-DISC-05).
            var availableServers = _main.ServerList.Servers
                .Where(s => !boostedIds.Contains(s.Id));

            if (_splitPaletteSession is null)
            {
                var recentHosts = _recentConnections.GetRecents(MaxRecentHostSuggestions)
                    .Select(r => r.Host)
                    .ToList();
                var recentlyConnected = new List<ServerItemViewModel>();
                var others = new List<ServerItemViewModel>();

                foreach (var server in availableServers)
                {
                    var host = (server.RemoteServer ?? string.Empty).Trim().ToLowerInvariant();
                    if (!string.IsNullOrEmpty(host) && recentHosts.Contains(host))
                    {
                        recentlyConnected.Add(server);
                    }
                    else
                    {
                        others.Add(server);
                    }
                }

                // Reorder recentlyConnected to match the most-recent-first order from the tracker.
                recentlyConnected = recentlyConnected
                    .OrderBy(s => recentHosts.IndexOf((s.RemoteServer ?? string.Empty).Trim().ToLowerInvariant()))
                    .ToList();

                initialResults.AddRange(recentlyConnected);
                initialResults.AddRange(others.Take(Math.Max(0, MaxServerSuggestions - recentlyConnected.Count)));
            }
            else
            {
                initialResults.AddRange(availableServers);
            }

            // Then recent tools at the bottom (if any)
            foreach (var toolId in _main.RecentToolIds)
            {
                var desc = _toolRegistry.GetById(toolId);
                if (desc is not null)
                {
                    initialResults.Add(new ServerItemViewModel
                    {
                        Id = $"tool-{desc.Id.ToLowerInvariant()}",
                        DisplayName = _localizer[desc.LabelKey],
                        ConnectionType = desc.ToolType,
                        Group = _localizer["PaletteRecentToolsHeader"]
                    });
                }
            }

            Results = new ObservableCollection<ServerItemViewModel>(initialResults);
            SelectedItem = Results.FirstOrDefault();
            return;
        }

        // Explicit tool invocation with argument: "ping 8.8.8.8", "subnet 10.0.0.0/8".
        // Returns single tool result so the argument-bearing flow stays uncluttered.
        var explicitTool = TryParseExplicitToolInvocation(query);
        if (explicitTool is not null)
        {
            Results = new ObservableCollection<ServerItemViewModel>([explicitTool]);
            SelectedItem = Results.FirstOrDefault();
            return;
        }

        // Special: "tool" or "tools" lists every registered tool, grouped by category.
        if (string.Equals(query, "tool", StringComparison.OrdinalIgnoreCase)
            || string.Equals(query, "tools", StringComparison.OrdinalIgnoreCase))
        {
            var allTools = _toolRegistry.All.Select(BuildToolPaletteItem).ToList();
            Results = new ObservableCollection<ServerItemViewModel>(allTools);
            SelectedItem = Results.FirstOrDefault();
            return;
        }

        // Unified fuzzy ranking across tools (label + aliases), external tools,
        // and the server inventory. Lets queries like "calculator" or "encoder"
        // surface tool matches that the prefix-only path used to miss.
        var scored = new List<(ServerItemViewModel Item, int Score)>();

        foreach (var desc in _toolRegistry.All)
        {
            var score = ScoreToolDescriptor(desc, query);
            if (score > 0)
            {
                scored.Add((BuildToolPaletteItem(desc), score));
            }
        }

        var extTools = _main.CurrentSettings?.ExternalTools ?? [];
        foreach (var ext in extTools)
        {
            if (string.IsNullOrWhiteSpace(ext.Name)) continue;
            var score = FuzzyScoreString(ext.Name, query);
            if (score > 0)
            {
                scored.Add((BuildExternalToolItem(ext), score));
            }
        }

        foreach (var server in _main.ServerList.Servers)
        {
            var score = FuzzyScore(server, query);
            if (score > 0)
            {
                scored.Add((server, score));
            }
        }

        foreach (var snippet in Snippets)
        {
            var score = ScoreSnippet(snippet, query);
            if (score > 0)
            {
                scored.Add((BuildSnippetItem(snippet), score));
            }
        }

        var matches = scored
            .OrderByDescending(x => x.Score)
            .Take(MaxFuzzyResults)
            .Select(x => x.Item)
            .ToList();

        // Ad-hoc SSH URL parsing: "ssh user@host" or "user@host:port"
        var adHoc = TryParseAdHocSsh(query);
        if (adHoc is not null && matches.Count == 0)
        {
            matches.Insert(0, adHoc);
        }

        // Bare IP / hostname: propose SSH and RDP ad-hoc connections.
        // Order is biased by per-host history so users hitting the same host
        // again with the same protocol see it on top (RDP-DISC-04).
        if (matches.Count == 0 && LooksLikeHostOrIp(query))
        {
            var ssh = new ServerItemViewModel
            {
                Id = $"{AdHocProfileIds.Prefix}ssh-{query}",
                IsTypedDestination = true,
                DisplayName = _localizer.Format("QuickConnectSshTo", query),
                RemoteServer = query,
                Endpoint = query,
                ConnectionType = "SSH",
                Group = _localizer["PaletteQuickConnectHeader"]
            };
            var rdp = new ServerItemViewModel
            {
                Id = $"{AdHocProfileIds.Prefix}rdp-{query}",
                IsTypedDestination = true,
                DisplayName = _localizer.Format("QuickConnectRdpTo", query),
                RemoteServer = query,
                Endpoint = query,
                ConnectionType = "RDP",
                Group = _localizer["PaletteQuickConnectHeader"]
            };

            var lastProtocol = _recentConnections.GetLastProtocol(query);
            if (string.Equals(lastProtocol, "RDP", StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(rdp);
                matches.Add(ssh);
            }
            else
            {
                matches.Add(ssh);
                matches.Add(rdp);
            }
        }

        Results = new ObservableCollection<ServerItemViewModel>(matches);
        SelectedItem = Results.FirstOrDefault();
    }

    /// <summary>
    /// Scores how well a server matches a query using fuzzy matching.
    /// Returns 0 for no match. Higher = better match.
    /// </summary>
    private static int FuzzyScore(ServerItemViewModel server, string query)
    {
        int best = 0;
        best = Math.Max(best, FuzzyScoreString(server.DisplayName, query));
        best = Math.Max(best, FuzzyScoreString(server.RemoteServer ?? "", query));
        best = Math.Max(best, FuzzyScoreString(server.Group ?? "", query) / 2);
        best = Math.Max(best, FuzzyScoreString(server.Username ?? "", query) / 2);
        best = Math.Max(best, FuzzyScoreString(server.ConnectionType ?? "", query) / 2);
        best = Math.Max(best, FuzzyScoreString(server.Environment ?? "", query) / 2);
        best = Math.Max(best, FuzzyScoreString(server.Tags ?? "", query) / 2);
        best = Math.Max(best, FuzzyScoreString(server.ProjectName ?? "", query) / 2);
        return best;
    }

    private static int FuzzyScoreString(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return 0;

        // Exact prefix match = highest score
        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 100 + (query.Length * 10);

        // Contains = good score
        if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 50 + (query.Length * 5);

        // Fuzzy: all query chars appear in order (non-contiguous)
        int qi = 0;
        int consecutive = 0;
        int score = 0;

        for (int ti = 0; ti < text.Length && qi < query.Length; ti++)
        {
            if (char.ToLowerInvariant(text[ti]) == char.ToLowerInvariant(query[qi]))
            {
                qi++;
                consecutive++;
                score += consecutive * 2; // Consecutive chars score more
            }
            else
            {
                consecutive = 0;
            }
        }

        return qi == query.Length ? score : 0;
    }

    /// <summary>
    /// Detects an explicit tool invocation of the form <c>"&lt;prefix&gt; &lt;argument&gt;"</c>
    /// (e.g. <c>"ping 8.8.8.8"</c>, <c>"subnet 10.0.0.0/8"</c>). The matching descriptor
    /// must declare a <see cref="ToolDescriptor.LabelWithArgKey"/>; otherwise the input
    /// falls through to the unified fuzzy ranker. Returns <c>null</c> when no descriptor
    /// matches an explicit argument-bearing invocation.
    /// </summary>
    private ServerItemViewModel? TryParseExplicitToolInvocation(string query)
    {
        var lower = query.ToLowerInvariant();
        foreach (var descriptor in _toolRegistry.All)
        {
            if (descriptor.LabelWithArgKey is null) continue;

            foreach (var prefix in descriptor.CommandPrefixes)
            {
                // Require "<prefix> <something>" so plain "ping" still flows through ranking.
                if (!lower.StartsWith(prefix + " ", StringComparison.Ordinal)) continue;

                var rest = query[prefix.Length..].Trim();
                if (string.IsNullOrEmpty(rest)) continue;

                return new ServerItemViewModel
                {
                    Id = $"tool-{descriptor.Id.ToLowerInvariant()}|{rest}",
                    DisplayName = _localizer.Format(descriptor.LabelWithArgKey, rest),
                    ConnectionType = descriptor.ToolType,
                    Group = _localizer["PaletteToolsSectionHeader"]
                };
            }
        }
        return null;
    }

    /// <summary>
    /// Scores a tool descriptor against a free-text query. Considers the localized
    /// label, every alias in <see cref="ToolDescriptor.CommandPrefixes"/>, and the
    /// localized category (with halved weight). Exact alias matches return a synthetic
    /// top score so typing a known command (<c>"ping"</c>) keeps its historical ranking.
    /// </summary>
    private int ScoreToolDescriptor(ToolDescriptor descriptor, string query)
    {
        var best = FuzzyScoreString(_localizer[descriptor.LabelKey], query);

        foreach (var prefix in descriptor.CommandPrefixes)
        {
            if (string.Equals(prefix, query, StringComparison.OrdinalIgnoreCase))
            {
                return ToolExactAliasScore;
            }
            best = Math.Max(best, FuzzyScoreString(prefix, query));
        }

        best = Math.Max(best, FuzzyScoreString(_localizer[descriptor.CategoryLabelKey], query) / 2);
        return best;
    }

    /// <summary>Builds the palette item for a built-in tool (no argument).</summary>
    private ServerItemViewModel BuildToolPaletteItem(ToolDescriptor descriptor) => new()
    {
        Id = $"tool-{descriptor.Id.ToLowerInvariant()}",
        DisplayName = _localizer[descriptor.LabelKey],
        ConnectionType = descriptor.ToolType,
        Group = _localizer[descriptor.CategoryLabelKey]
    };

    /// <summary>Builds the palette item for a user-configured external tool.</summary>
    private ServerItemViewModel BuildExternalToolItem(ExternalToolDefinition ext) => new()
    {
        Id = $"ext-tool-{ext.Name}",
        DisplayName = ext.Name,
        ConnectionType = "EXTERNAL",
        Group = _localizer["PaletteExternalToolsHeader"]
    };

    private const int ToolExactAliasScore = 999;

    /// <summary>Maximum number of fuzzy-ranked results shown in the palette.</summary>
    private const int MaxFuzzyResults = 20;

    /// <summary>Maximum number of recently-connected hosts bubbled to the top of the empty-query seed.</summary>
    private const int MaxRecentHostSuggestions = 10;

    /// <summary>Maximum number of servers surfaced in the empty-query seed (normal, non-split mode).</summary>
    private const int MaxServerSuggestions = 10;

    /// <summary>Maximum length of the command preview shown next to a snippet result.</summary>
    private const int SnippetPreviewMaxLength = 96;

    /// <summary>Decorative prefix marking an active session as a split-merge candidate.</summary>
    private const string MergeArrowPrefix = "↔ ";

    /// <summary>Ellipsis glyph appended to truncated previews.</summary>
    private const string EllipsisGlyph = "...";

    /// <summary>
    /// Returns true when the input looks like a bare IP address or hostname
    /// (no spaces, no protocol prefix, alphanumeric with dots and hyphens).
    /// </summary>
    private static bool LooksLikeHostOrIp(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Contains(' '))
            return false;
        return System.Net.IPAddress.TryParse(input, out _)
            || System.Text.RegularExpressions.Regex.IsMatch(
                input, @"^[a-zA-Z0-9][a-zA-Z0-9.\-]*$");
    }

    /// <summary>
    /// Attempts to parse an ad-hoc SSH URL from the palette search text.
    /// Supports: <c>ssh user@host</c>, <c>ssh user@host:port</c>,
    /// <c>user@host</c>. Returns a temporary
    /// <see cref="ServerItemViewModel"/> for connection, or <c>null</c>.
    /// </summary>
    private ServerItemViewModel? TryParseAdHocSsh(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var text = input.Trim();
        if (text.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..].Trim();
        }

        // Match user@host or user@host:port
        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"^([^@]+)@([^:]+)(?::(\d+))?$");

        if (!match.Success) return null;

        string user = match.Groups[1].Value;
        string host = match.Groups[2].Value;
        int port = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 22;

        var vm = new ServerItemViewModel
        {
            Id = $"{AdHocProfileIds.Prefix}{Guid.NewGuid():N}",
            IsTypedDestination = true,
            DisplayName = $"{user}@{host}:{port}",
            RemoteServer = host,
            ConnectionType = "SSH",
            Group = _localizer["PaletteQuickConnectHeader"]
        };
        return vm;
    }

    /// <summary>
    /// Scores a TwinShell action against a free-text query. The Title carries
    /// the strongest weight, then each Tag (full weight - sysops queries like
    /// "disk" or "df" usually match a tag), with Description and Category at
    /// halved weight.
    /// </summary>
    private static int ScoreSnippet(ActionModel action, string query)
    {
        var best = FuzzyScoreString(action.Title, query);

        foreach (var tag in action.Tags)
        {
            if (string.IsNullOrEmpty(tag)) continue;
            best = Math.Max(best, FuzzyScoreString(tag, query));
        }

        best = Math.Max(best, FuzzyScoreString(action.Description, query) / 2);
        best = Math.Max(best, FuzzyScoreString(action.Category, query) / 2);
        if (!string.IsNullOrEmpty(action.Notes))
        {
            best = Math.Max(best, FuzzyScoreString(action.Notes, query) / 2);
        }
        return best;
    }

    /// <summary>
    /// Builds the palette item for a snippet. The endpoint slot carries a
    /// short command preview so the user can confirm what will be copied
    /// before pressing Enter.
    /// </summary>
    private ServerItemViewModel BuildSnippetItem(ActionModel action)
    {
        var preview = ResolveSnippetCommand(action);
        if (preview.Length > SnippetPreviewMaxLength)
        {
            preview = preview[..SnippetPreviewMaxLength] + EllipsisGlyph;
        }

        return new ServerItemViewModel
        {
            Id = $"snippet-{action.PublicId:N}",
            DisplayName = action.Title,
            Endpoint = preview,
            ConnectionType = "SNIPPET",
            Group = _localizer["PaletteSnippetsHeader"]
        };
    }
}
