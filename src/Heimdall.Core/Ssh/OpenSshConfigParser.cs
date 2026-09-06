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

using System.Text;

namespace Heimdall.Core.Ssh;

/// <summary>
/// Parses a limited ssh_config subset into importable candidates and diagnostics.
/// </summary>
public static class OpenSshConfigParser
{
    /// <summary>
    /// Parses OpenSSH config content into structured import candidates.
    /// </summary>
    /// <param name="contents">Raw ssh_config text content.</param>
    /// <returns>The parsed candidates and non-fatal diagnostics.</returns>
    public static OpenSshParseResult Parse(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var diagnostics = new List<OpenSshImportDiagnostic>();
        var blocks = new List<HostBlockState>();
        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HostBlockState? currentBlock = null;
        var isInsideMatchBlock = false;

        var lines = contents.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var trimmedLine = StripComments(lines[index]).Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine))
            {
                continue;
            }

            if (!TrySplitDirective(trimmedLine, out var directive, out var value, out var valueWasQuoted))
            {
                continue;
            }

            if (isInsideMatchBlock)
            {
                if (directive.Equals("host", StringComparison.OrdinalIgnoreCase))
                {
                    isInsideMatchBlock = false;
                }
                else if (directive.Equals("match", StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(new OpenSshImportDiagnostic(
                        OpenSshDiagnosticLevel.Warning,
                        lineNumber,
                        OpenSshDiagnosticCode.MatchBlockIgnored));
                    continue;
                }
                else
                {
                    continue;
                }
            }

            if (directive.Equals("host", StringComparison.OrdinalIgnoreCase))
            {
                AddCurrentBlock(currentBlock, blocks);
                currentBlock = CreateHostBlock(value, lineNumber, diagnostics, seenAliases);
                continue;
            }

            if (directive.Equals("match", StringComparison.OrdinalIgnoreCase))
            {
                AddCurrentBlock(currentBlock, blocks);
                currentBlock = null;
                diagnostics.Add(new OpenSshImportDiagnostic(
                    OpenSshDiagnosticLevel.Warning,
                    lineNumber,
                    OpenSshDiagnosticCode.MatchBlockIgnored));
                isInsideMatchBlock = true;
                continue;
            }

            if (directive.Equals("include", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new OpenSshImportDiagnostic(
                    OpenSshDiagnosticLevel.Warning,
                    lineNumber,
                    OpenSshDiagnosticCode.IncludeDirectiveIgnored,
                    value));
                continue;
            }

            if (currentBlock is null)
            {
                diagnostics.Add(new OpenSshImportDiagnostic(
                    OpenSshDiagnosticLevel.Info,
                    lineNumber,
                    OpenSshDiagnosticCode.UnknownDirectiveIgnored,
                    directive));
                continue;
            }

            ApplyDirective(currentBlock, directive, value, valueWasQuoted, userProfile, lineNumber, diagnostics);
        }

        AddCurrentBlock(currentBlock, blocks);

        return new OpenSshParseResult(BuildCandidates(blocks, diagnostics), diagnostics);
    }

    private static void AddCurrentBlock(HostBlockState? block, ICollection<HostBlockState> blocks)
    {
        if (block is not null)
        {
            blocks.Add(block);
        }
    }

    private static HostBlockState? CreateHostBlock(
        string value,
        int lineNumber,
        ICollection<OpenSshImportDiagnostic> diagnostics,
        ISet<string> seenAliases)
    {
        var aliases = new List<string>();
        foreach (var alias in SplitTokens(value))
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            if (IsWildcardAlias(alias))
            {
                diagnostics.Add(new OpenSshImportDiagnostic(
                    OpenSshDiagnosticLevel.Warning,
                    lineNumber,
                    OpenSshDiagnosticCode.WildcardAliasIgnored,
                    alias));
                continue;
            }

            if (!seenAliases.Add(alias))
            {
                diagnostics.Add(new OpenSshImportDiagnostic(
                    OpenSshDiagnosticLevel.Warning,
                    lineNumber,
                    OpenSshDiagnosticCode.DuplicateAliasInFile,
                    alias));
                continue;
            }

            aliases.Add(alias);
        }

        return aliases.Count == 0
            ? null
            : new HostBlockState(lineNumber, aliases);
    }

    /// <summary>
    /// Applies one directive to the current Host block. OpenSSH keeps the first value
    /// obtained for a directive, so a repeated directive inside one block is ignored.
    /// </summary>
    private static void ApplyDirective(
        HostBlockState block,
        string directive,
        string value,
        bool valueWasQuoted,
        string userProfile,
        int lineNumber,
        ICollection<OpenSshImportDiagnostic> diagnostics)
    {
        if (directive.Equals("hostname", StringComparison.OrdinalIgnoreCase))
        {
            if (block.HostName is null)
            {
                block.HostName = value;
                block.HostNameLineNumber = lineNumber;
            }

            return;
        }

        if (directive.Equals("port", StringComparison.OrdinalIgnoreCase))
        {
            if (block.ObtainedPort is not null)
            {
                return;
            }

            if (int.TryParse(value, out var port) && port is >= 1 and <= 65535)
            {
                block.ObtainedPort = port;
                return;
            }

            diagnostics.Add(new OpenSshImportDiagnostic(
                OpenSshDiagnosticLevel.Warning,
                lineNumber,
                OpenSshDiagnosticCode.InvalidPort,
                value));
            block.ObtainedPort = HostBlockState.DefaultPort;
            return;
        }

        if (directive.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            block.User ??= value;
            return;
        }

        if (directive.Equals("identityfile", StringComparison.OrdinalIgnoreCase))
        {
            if (block.IdentityFile is null)
            {
                block.IdentityFile = ExpandIdentityFile(value, userProfile, lineNumber, diagnostics);
            }

            return;
        }

        if (directive.Equals("proxyjump", StringComparison.OrdinalIgnoreCase))
        {
            if (block.ProxyJumpValue is null)
            {
                block.ProxyJumpValue = value;
                block.ProxyJumpLineNumber = lineNumber;
                block.ProxyJumpWasQuoted = valueWasQuoted;
            }

            return;
        }

        if (directive.Equals("proxycommand", StringComparison.OrdinalIgnoreCase))
        {
            if (block.ProxyCommandValue is null)
            {
                block.ProxyCommandValue = value;
                block.ProxyCommandLineNumber = lineNumber;
            }

            return;
        }

        diagnostics.Add(new OpenSshImportDiagnostic(
            OpenSshDiagnosticLevel.Info,
            lineNumber,
            OpenSshDiagnosticCode.UnknownDirectiveIgnored,
            directive));
    }

    private static string ExpandIdentityFile(
        string value,
        string userProfile,
        int lineNumber,
        ICollection<OpenSshImportDiagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(userProfile) || !value.StartsWith('~'))
        {
            return value;
        }

        if (value.Length > 1 && value[1] is not '/' and not '\\')
        {
            return value;
        }

        var trimmed = value.Length == 1
            ? string.Empty
            : value[1..].TrimStart('/', '\\');
        var expanded = string.IsNullOrEmpty(trimmed)
            ? userProfile
            : Path.Combine(userProfile, trimmed.Replace('/', Path.DirectorySeparatorChar));

        diagnostics.Add(new OpenSshImportDiagnostic(
            OpenSshDiagnosticLevel.Info,
            lineNumber,
            OpenSshDiagnosticCode.IdentityFileTildeExpanded,
            value));

        return expanded;
    }

    private static IReadOnlyList<OpenSshImportCandidate> BuildCandidates(
        IReadOnlyList<HostBlockState> blocks,
        ICollection<OpenSshImportDiagnostic> diagnostics)
    {
        var candidates = new List<OpenSshImportCandidate>();
        var blockByAlias = blocks
            .SelectMany(block => block.Aliases.Select(alias => (Alias: alias, Block: block)))
            .GroupBy(item => item.Alias, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Block, StringComparer.OrdinalIgnoreCase);

        foreach (var block in blocks)
        {
            foreach (var alias in block.Aliases)
            {
                if (!TryResolveHostName(alias, block, diagnostics, out string hostName))
                {
                    continue;
                }

                candidates.Add(new OpenSshImportCandidate
                {
                    Alias = alias,
                    HostName = hostName,
                    Port = block.Port,
                    User = block.User,
                    IdentityFile = block.IdentityFile,
                    SourceLineNumber = block.SourceLineNumber,
                    ProxyJumpChain = BuildProxyJumpChain(alias, hostName, block, blockByAlias, diagnostics)
                });
            }
        }

        return candidates;
    }

    /// <summary>
    /// Resolves the host name of a candidate. OpenSSH expands <c>%h</c> in
    /// <c>HostName</c> to the host alias and <c>%%</c> to a literal percent sign; any
    /// other token depends on runtime state Heimdall does not have, so the candidate
    /// is reported and skipped rather than imported with a host that cannot resolve.
    /// </summary>
    private static bool TryResolveHostName(
        string alias,
        HostBlockState block,
        ICollection<OpenSshImportDiagnostic> diagnostics,
        out string hostName)
    {
        if (string.IsNullOrWhiteSpace(block.HostName))
        {
            diagnostics.Add(new OpenSshImportDiagnostic(
                OpenSshDiagnosticLevel.Info,
                block.SourceLineNumber,
                OpenSshDiagnosticCode.HostNameFallbackToAlias,
                alias));

            hostName = alias;
            return true;
        }

        if (TryExpandHostNameTokens(block.HostName, alias, out string expanded))
        {
            hostName = expanded;
            return true;
        }

        diagnostics.Add(new OpenSshImportDiagnostic(
            OpenSshDiagnosticLevel.Warning,
            block.HostNameLineNumber ?? block.SourceLineNumber,
            OpenSshDiagnosticCode.HostNameTokenSubstitution,
            block.HostName));

        hostName = string.Empty;
        return false;
    }

    private const char TokenIntroducer = '%';
    private const char HostAliasToken = 'h';

    /// <summary>
    /// Expands the OpenSSH <c>HostName</c> tokens Heimdall can honour statically.
    /// Returns false when the value carries a token that needs runtime state.
    /// </summary>
    private static bool TryExpandHostNameTokens(string value, string alias, out string expanded)
    {
        StringBuilder builder = new StringBuilder(value.Length + alias.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != TokenIntroducer)
            {
                builder.Append(current);
                continue;
            }

            if (index + 1 >= value.Length)
            {
                expanded = string.Empty;
                return false;
            }

            char token = value[index + 1];
            index++;
            if (token == TokenIntroducer)
            {
                builder.Append(TokenIntroducer);
            }
            else if (token == HostAliasToken)
            {
                builder.Append(alias);
            }
            else
            {
                expanded = string.Empty;
                return false;
            }
        }

        expanded = builder.ToString();
        return true;
    }

    private static IReadOnlyList<OpenSshProxyJumpHop> BuildProxyJumpChain(
        string alias,
        string hostName,
        HostBlockState block,
        IReadOnlyDictionary<string, HostBlockState> blockByAlias,
        ICollection<OpenSshImportDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(block.ProxyJumpValue))
        {
            if (!string.IsNullOrWhiteSpace(block.ProxyCommandValue))
            {
                diagnostics.Add(new OpenSshImportDiagnostic(
                    OpenSshDiagnosticLevel.Warning,
                    block.ProxyCommandLineNumber ?? block.SourceLineNumber,
                    OpenSshDiagnosticCode.ProxyCommandUnsupported,
                    block.ProxyCommandValue));
            }

            return [];
        }

        if (!string.IsNullOrWhiteSpace(block.ProxyCommandValue))
        {
            diagnostics.Add(new OpenSshImportDiagnostic(
                OpenSshDiagnosticLevel.Warning,
                block.ProxyJumpLineNumber ?? block.SourceLineNumber,
                OpenSshDiagnosticCode.ProxyJumpMixedWithProxyCommand,
                block.ProxyJumpValue));
            return [];
        }

        if (block.ProxyJumpValue.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (block.ProxyJumpValue.Contains('%', StringComparison.Ordinal))
        {
            diagnostics.Add(new OpenSshImportDiagnostic(
                OpenSshDiagnosticLevel.Warning,
                block.ProxyJumpLineNumber ?? block.SourceLineNumber,
                OpenSshDiagnosticCode.ProxyJumpTokenSubstitution,
                block.ProxyJumpValue));
            return [];
        }

        if (!TryParseProxyJump(block.ProxyJumpValue, block.ProxyJumpWasQuoted, out var rawHops))
        {
            diagnostics.Add(new OpenSshImportDiagnostic(
                OpenSshDiagnosticLevel.Warning,
                block.ProxyJumpLineNumber ?? block.SourceLineNumber,
                OpenSshDiagnosticCode.ProxyJumpUnrecognizedSyntax,
                block.ProxyJumpValue));
            return [];
        }

        if (HasProxyJumpCycle(alias, hostName, rawHops, blockByAlias))
        {
            diagnostics.Add(new OpenSshImportDiagnostic(
                OpenSshDiagnosticLevel.Warning,
                block.ProxyJumpLineNumber ?? block.SourceLineNumber,
                OpenSshDiagnosticCode.ProxyJumpCycle,
                alias));
            return [];
        }

        return rawHops
            .Select(raw => ResolveHop(raw, blockByAlias, block.ProxyJumpLineNumber ?? block.SourceLineNumber))
            .ToList();
    }

    private static OpenSshProxyJumpHop ResolveHop(
        RawProxyJumpHop raw,
        IReadOnlyDictionary<string, HostBlockState> blockByAlias,
        int lineNumber)
    {
        if (!blockByAlias.TryGetValue(raw.Host, out var hopBlock))
        {
            return new OpenSshProxyJumpHop
            {
                Host = raw.Host,
                HostName = raw.Host,
                Port = raw.Port ?? 22,
                User = raw.User,
                SourceLineNumber = lineNumber
            };
        }

        string hopHostName = raw.Host;
        if (!string.IsNullOrWhiteSpace(hopBlock.HostName))
        {
            hopHostName = TryExpandHostNameTokens(hopBlock.HostName, raw.Host, out string expandedHopHostName)
                ? expandedHopHostName
                : hopBlock.HostName;
        }

        return new OpenSshProxyJumpHop
        {
            Host = raw.Host,
            HostName = hopHostName,
            Port = raw.Port ?? hopBlock.Port,
            User = string.IsNullOrWhiteSpace(raw.User) ? hopBlock.User : raw.User,
            IdentityFile = hopBlock.IdentityFile,
            SourceLineNumber = lineNumber
        };
    }

    private static bool HasProxyJumpCycle(
        string alias,
        string hostName,
        IReadOnlyList<RawProxyJumpHop> rawHops,
        IReadOnlyDictionary<string, HostBlockState> blockByAlias)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            alias,
            hostName
        };

        foreach (var hop in rawHops)
        {
            if (!seen.Add(hop.Host))
            {
                return true;
            }

            if (blockByAlias.TryGetValue(hop.Host, out var hopBlock))
            {
                if (!string.IsNullOrWhiteSpace(hopBlock.HostName) && !seen.Add(hopBlock.HostName))
                {
                    return true;
                }

                if (HasTransitiveProxyJumpCycle(alias, hopBlock, blockByAlias, []))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasTransitiveProxyJumpCycle(
        string rootAlias,
        HostBlockState block,
        IReadOnlyDictionary<string, HostBlockState> blockByAlias,
        HashSet<HostBlockState> visited)
    {
        if (!visited.Add(block) || string.IsNullOrWhiteSpace(block.ProxyJumpValue))
        {
            return false;
        }

        if (block.ProxyJumpValue.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            !TryParseProxyJump(block.ProxyJumpValue, block.ProxyJumpWasQuoted, out var rawHops))
        {
            return false;
        }

        foreach (var hop in rawHops)
        {
            if (hop.Host.Equals(rootAlias, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (blockByAlias.TryGetValue(hop.Host, out var hopBlock) &&
                HasTransitiveProxyJumpCycle(rootAlias, hopBlock, blockByAlias, visited))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseProxyJump(
        string value,
        bool valueWasQuoted,
        out IReadOnlyList<RawProxyJumpHop> hops)
    {
        hops = [];

        if (valueWasQuoted ||
            string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsWhiteSpace) ||
            value.IndexOfAny(['"', '\'', '\\']) >= 0)
        {
            return false;
        }

        var parsed = new List<RawProxyJumpHop>();
        foreach (var part in value.Split(','))
        {
            if (!TryParseProxyJumpHop(part, out var hop))
            {
                return false;
            }

            parsed.Add(hop);
        }

        hops = parsed;
        return parsed.Count > 0;
    }

    private static bool TryParseProxyJumpHop(string value, out RawProxyJumpHop hop)
    {
        hop = default;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsWhiteSpace) ||
            value.Contains('%', StringComparison.Ordinal) ||
            value.IndexOfAny(['"', '\'', '\\']) >= 0)
        {
            return false;
        }

        string? user = null;
        var hostPort = value;
        var atIndex = value.IndexOf('@', StringComparison.Ordinal);
        if (atIndex >= 0)
        {
            if (atIndex == 0 || atIndex == value.Length - 1 || value.IndexOf('@', atIndex + 1) >= 0)
            {
                return false;
            }

            user = value[..atIndex];
            hostPort = value[(atIndex + 1)..];
        }

        string host;
        int? port = null;
        var colonIndex = hostPort.LastIndexOf(':');
        if (colonIndex >= 0)
        {
            if (colonIndex == 0 ||
                colonIndex == hostPort.Length - 1 ||
                hostPort.IndexOf(':') != colonIndex ||
                !int.TryParse(hostPort[(colonIndex + 1)..], out var parsedPort) ||
                parsedPort is < 1 or > 65535)
            {
                return false;
            }

            host = hostPort[..colonIndex];
            port = parsedPort;
        }
        else
        {
            host = hostPort;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        hop = new RawProxyJumpHop(host, user, port);
        return true;
    }

    private static string StripComments(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(line.Length);
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var current = line[i];
            if (current == '"')
            {
                inQuotes = !inQuotes;
                builder.Append(current);
                continue;
            }

            if (current == '#' && !inQuotes)
            {
                break;
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static bool TrySplitDirective(
        string line,
        out string directive,
        out string value,
        out bool valueWasQuoted)
    {
        directive = string.Empty;
        value = string.Empty;
        valueWasQuoted = false;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var firstWhitespaceIndex = line.IndexOfAny([' ', '\t']);
        if (firstWhitespaceIndex < 0)
        {
            directive = line.Trim();
            return !string.IsNullOrWhiteSpace(directive);
        }

        directive = line[..firstWhitespaceIndex].Trim();
        var rawValue = line[(firstWhitespaceIndex + 1)..].Trim();
        valueWasQuoted = IsOuterQuoted(rawValue);
        value = valueWasQuoted ? rawValue[1..^1] : rawValue;
        return !string.IsNullOrWhiteSpace(directive);
    }

    private static bool IsOuterQuoted(string value)
    {
        return value.Length >= 2 && value[0] == '"' && value[^1] == '"';
    }

    private static IReadOnlyList<string> SplitTokens(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var tokens = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        foreach (var current in value)
        {
            if (current == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(current) && !inQuotes)
            {
                if (builder.Length > 0)
                {
                    tokens.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            builder.Append(current);
        }

        if (builder.Length > 0)
        {
            tokens.Add(builder.ToString());
        }

        return tokens;
    }

    private static bool IsWildcardAlias(string alias)
    {
        return alias.StartsWith('!') || alias.Contains('*') || alias.Contains('?');
    }

    private readonly record struct RawProxyJumpHop(string Host, string? User, int? Port);

    private sealed class HostBlockState(int sourceLineNumber, IReadOnlyList<string> aliases)
    {
        public int SourceLineNumber { get; } = sourceLineNumber;

        public IReadOnlyList<string> Aliases { get; } = aliases;

        public string? HostName { get; set; }

        public int? HostNameLineNumber { get; set; }

        /// <summary>Port used when the block carries no valid Port directive.</summary>
        public const int DefaultPort = 22;

        /// <summary>The first Port value obtained in the block, or null when none was seen.</summary>
        public int? ObtainedPort { get; set; }

        public int Port => ObtainedPort ?? DefaultPort;

        public string? User { get; set; }

        public string? IdentityFile { get; set; }

        public string? ProxyJumpValue { get; set; }

        public int? ProxyJumpLineNumber { get; set; }

        public bool ProxyJumpWasQuoted { get; set; }

        public string? ProxyCommandValue { get; set; }

        public int? ProxyCommandLineNumber { get; set; }
    }
}
