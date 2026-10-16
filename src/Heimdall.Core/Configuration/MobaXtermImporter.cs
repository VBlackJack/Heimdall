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
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Heimdall.Core.Models;

namespace Heimdall.Core.Configuration;

/// <summary>
/// Parses MobaXterm session files (.mxtsessions or MobaXterm.ini [Bookmarks] sections)
/// and converts them into <see cref="ServerProfileDto"/> and <see cref="SshGatewayDto"/> instances.
/// </summary>
/// <remarks>
/// <para>MobaXterm session format (INI):</para>
/// <code>
/// [Bookmarks_1]
/// SubRep=Folder\SubFolder
/// ImgNum=42
/// name=#icon#type%host%port%user%%0%-1%login%gwHost%gwPort%gwUser%...#MobaFont%...#0#comment
/// </code>
/// <para>The number between the first two <c>#</c> is the session ICON, chosen by the user; the
/// number that opens the third section is the session TYPE. Reading the icon as the type dropped
/// every session that had a custom icon.</para>
/// <para>Passwords are encrypted with MobaXterm's proprietary algorithm and are never read. Key
/// paths are never imported: they are per-machine, and a UNC path would make this machine
/// authenticate to a remote host when the key is read.</para>
/// <para>Nothing is skipped silently: every line that does not become a profile, and every value
/// that could not be resolved, is reported in <see cref="MobaXtermImportResult.Warnings"/>.</para>
/// </remarks>
public static partial class MobaXtermImporter
{
    /// <summary>Identifier prefix used when the caller does not supply one.</summary>
    public const string DefaultIdPrefix = "mobaxterm-";

    private const string PasswordsSection = "Passwords";
    private const string CredentialsSection = "Credentials";
    private const string BookmarkSectionPrefix = "Bookmarks";
    private const string FolderKey = "SubRep";
    private const string FolderIconKey = "ImgNum";

    private const char SectionSeparator = '#';
    private const char FieldSeparator = '%';
    private const char IniCommentPrefix = ';';
    private const char IniKeyValueSeparator = '=';
    private const char MobaFolderSeparator = '\\';
    private const char GroupSeparator = '/';
    private const char TagInputSeparator = ',';
    private const string TagSeparator = ", ";

    // Sections of a session value once split on '#': [0] is empty (the value starts with '#'),
    // [1] the icon, [2] the type and the '%' fields, [3] the terminal font and colours, [4] a
    // flag, [5] the free comment. Taken by position rather than "after the last '#'" because
    // MobaXterm.ini lines carry one more section after the comment (see the realistic fixtures).
    private const int IconSectionIndex = 1;
    private const int MainSectionIndex = 2;
    private const int CommentSectionIndex = 5;

    private const string ProfileIdDomain = "session";
    private const string GatewayIdDomain = "gateway";
    private const char IdInputSeparator = '\n';
    private const int IdHashBytes = 16;

    private const char GatewayNameUserSeparator = '@';
    private const char GatewayNamePortSeparator = ':';

    private const string SshConnectionType = "SSH";
    private const string RdpConnectionType = "RDP";
    private const string EmbeddedMode = "Embedded";

    /// <summary>
    /// Where each supported session type keeps its fields, by index in the '%' list that follows
    /// the type code.
    /// </summary>
    /// <remarks>
    /// <para>Type 0 (SSH) is verified against a real team file: host, port and user at 0-2, the
    /// login command at 6, the SSH gateway host, port and user at 7-9.</para>
    /// <para>Type 4 (RDP) is kept from the existing realistic RDP fixture, which carries host, port
    /// and user at 0-2 and its SSH gateway at 12-14. It has no login command.</para>
    /// <para>Every other code is reported as unsupported rather than guessed.</para>
    /// </remarks>
    private static readonly Dictionary<int, SessionLayout> SessionLayouts = new()
    {
        [0] = new SessionLayout(
            SshConnectionType,
            DefaultPorts.Ssh,
            LoginCommandIndex: 6,
            GatewayHostIndex: 7,
            GatewayPortIndex: 8,
            GatewayUserIndex: 9),
        [4] = new SessionLayout(
            RdpConnectionType,
            DefaultPorts.Rdp,
            LoginCommandIndex: null,
            GatewayHostIndex: 12,
            GatewayPortIndex: 13,
            GatewayUserIndex: 14),
    };

    private const int HostIndex = 0;
    private const int PortIndex = 1;
    private const int UserIndex = 2;

    /// <summary>
    /// Parses a MobaXterm session file with the default identifier prefix and no variable resolver.
    /// </summary>
    /// <param name="content">Full text content of the .mxtsessions or MobaXterm.ini file.</param>
    public static MobaXtermImportResult Parse(string content) =>
        Parse(content, MobaXtermImportOptions.Default);

    /// <summary>
    /// Parses a MobaXterm session file and returns the profiles, the gateways they reference, and
    /// a warning for every line or value that could not be taken as it is.
    /// </summary>
    /// <param name="content">Full text content of the .mxtsessions or MobaXterm.ini file.</param>
    /// <param name="options">Identifier prefix and variable resolver supplied by the caller.</param>
    public static MobaXtermImportResult Parse(string content, MobaXtermImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);

        var result = new MobaXtermImportResult();
        List<IniSection> sections = ParseIniSections(content);
        result.StoredCredentialCount = CountStoredCredentialEntries(sections);

        var context = new ParseContext(options, result);
        foreach (IniSection section in sections)
        {
            if (!IsBookmarkSection(section.Name))
            {
                continue;
            }

            int folderIndex = section.Entries.FindIndex(
                entry => entry.Key.Equals(FolderKey, StringComparison.OrdinalIgnoreCase));
            string folder = folderIndex < 0 ? string.Empty : section.Entries[folderIndex].Value;
            string group = SanitizeGroupName(folder.Trim());

            foreach (KeyValuePair<string, string> entry in section.Entries)
            {
                if (IsMetaKey(entry.Key))
                {
                    continue;
                }

                try
                {
                    ParseSession(context, section.Name, entry.Key.Trim(), entry.Value, group);
                }
                catch (Exception ex)
                {
                    context.Warn(
                        section.Name,
                        entry.Key.Trim(),
                        MobaXtermImportWarningKind.ParseError,
                        ex.Message);
                }
            }
        }

        Logging.FileLogger.Info(
            $"MobaXterm parse: {result.Servers.Count} profile(s), {result.Gateways.Count} gateway(s), "
            + $"{result.Warnings.Count} warning(s).");
        return result;
    }

    private static void ParseSession(
        ParseContext context,
        string section,
        string sessionName,
        string rawValue,
        string group)
    {
        if (!TryReadSessionLine(rawValue, out SessionLine line))
        {
            context.Warn(section, sessionName, MobaXtermImportWarningKind.MalformedSessionLine, string.Empty);
            return;
        }

        if (!int.TryParse(line.TypeText, NumberStyles.None, CultureInfo.InvariantCulture, out int typeCode)
            || !SessionLayouts.TryGetValue(typeCode, out SessionLayout? layout))
        {
            context.Warn(section, sessionName, MobaXtermImportWarningKind.UnsupportedSessionType, line.TypeText);
            return;
        }

        string host = FieldAt(line.Fields, HostIndex).Trim();
        if (host.Length == 0)
        {
            context.Warn(section, sessionName, MobaXtermImportWarningKind.MissingHost, string.Empty);
            return;
        }

        string profileId = BuildId(context.Options.IdPrefix, ProfileIdDomain, group, sessionName);
        if (!context.ProfileIds.Add(profileId))
        {
            context.Warn(section, sessionName, MobaXtermImportWarningKind.DuplicateSession, group);
            return;
        }

        string? gatewayId = null;
        string gatewayHost = FieldAt(line.Fields, layout.GatewayHostIndex).Trim();
        if (gatewayHost.Length > 0)
        {
            // A session that names a gateway is never imported as a direct connection: that
            // would reach the host around the bastion the file asked for.
            string? canonicalGatewayHost = ImportedProfileValidator.CanonicalizeHost(gatewayHost);
            if (canonicalGatewayHost is null)
            {
                context.ProfileIds.Remove(profileId);
                context.Warn(section, sessionName, MobaXtermImportWarningKind.InvalidGatewayHost, gatewayHost);
                return;
            }

            int gatewayPort = ParsePort(FieldAt(line.Fields, layout.GatewayPortIndex), DefaultPorts.Ssh);
            string gatewayUser = context
                .ResolveVariables(section, sessionName, FieldAt(line.Fields, layout.GatewayUserIndex))
                .Trim();
            gatewayId = context.GetOrAddGateway(canonicalGatewayHost, gatewayPort, gatewayUser);
        }

        string user = context
            .ResolveVariables(section, sessionName, FieldAt(line.Fields, UserIndex))
            .Trim();
        int port = ParsePort(FieldAt(line.Fields, PortIndex), layout.DefaultPort);

        var dto = new ServerProfileDto
        {
            Id = profileId,
            DisplayName = sessionName,
            Origin = ProfileOrigin.ImportMobaXterm,
            RemoteServer = host,
            ConnectionType = layout.ConnectionType,
            UseDirectConnection = gatewayId is null,
            SshGatewayId = gatewayId,
            Group = string.IsNullOrWhiteSpace(group) ? null : group,
            Tags = BuildTags(line.Comment),
        };

        ApplyProtocolSettings(dto, layout, port, user);
        ApplyLoginCommand(dto, layout, line.Fields);
        context.Result.Servers.Add(dto);
    }

    private static void ApplyProtocolSettings(ServerProfileDto dto, SessionLayout layout, int port, string user)
    {
        switch (layout.ConnectionType)
        {
            case SshConnectionType:
                dto.SshPort = port;
                dto.RemotePort = port;
                dto.SshUsername = NullIfEmpty(user);
                dto.SshMode = EmbeddedMode;
                break;

            case RdpConnectionType:
                dto.RemotePort = port;
                dto.RdpUsername = NullIfEmpty(user);
                dto.RdpMode = EmbeddedMode;
                break;
        }
    }

    /// <summary>
    /// Maps the command MobaXterm runs on the remote host after login to a post-connect step.
    /// </summary>
    /// <remarks>
    /// The step keeps the model's own defaults. The profile is left with
    /// <see cref="ServerProfileDto.ExecutionConfirmed"/> false, so the post-connect confirmation
    /// asks before an imported command is typed into a session.
    /// </remarks>
    private static void ApplyLoginCommand(ServerProfileDto dto, SessionLayout layout, string[] fields)
    {
        if (layout.LoginCommandIndex is not int index)
        {
            return;
        }

        string command = FieldAt(fields, index).Trim();
        if (command.Length == 0)
        {
            return;
        }

        dto.PostConnectSteps = [new PostConnectStep { Input = command }];
    }

    private static string? BuildTags(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return null;
        }

        string[] tags = comment.Split(
            TagInputSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tags.Length == 0 ? null : string.Join(TagSeparator, tags);
    }

    /// <summary>
    /// Splits a session value into its icon, type, '%' fields and comment.
    /// </summary>
    internal static bool TryReadSessionLine(string rawValue, out SessionLine line)
    {
        line = default;
        string trimmed = rawValue.Trim();
        if (trimmed.Length == 0 || trimmed[0] != SectionSeparator)
        {
            return false;
        }

        string[] sections = trimmed.Split(SectionSeparator);
        if (sections.Length <= MainSectionIndex)
        {
            return false;
        }

        string main = sections[MainSectionIndex];
        int typeEnd = main.IndexOf(FieldSeparator);
        string typeText = (typeEnd < 0 ? main : main[..typeEnd]).Trim();
        if (typeText.Length == 0)
        {
            return false;
        }

        string[] fields = typeEnd < 0 ? [] : main[(typeEnd + 1)..].Split(FieldSeparator);
        string? comment = sections.Length > CommentSectionIndex ? sections[CommentSectionIndex] : null;
        line = new SessionLine(sections[IconSectionIndex].Trim(), typeText, fields, comment);
        return true;
    }

    private static string FieldAt(string[] fields, int index) =>
        index < fields.Length ? fields[index] : string.Empty;

    /// <summary>
    /// A deterministic identifier: the prefix, then a hash of the parts that name the entry, so
    /// the same file parsed twice yields the same identifiers.
    /// </summary>
    internal static string BuildId(string prefix, string domain, params string[] parts)
    {
        string input = domain + IdInputSeparator + string.Join(IdInputSeparator, parts);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return prefix + Convert.ToHexStringLower(hash, 0, IdHashBytes);
    }

    private static int CountStoredCredentialEntries(List<IniSection> sections)
    {
        // These sections contain proprietary-encrypted secrets; count them only for user warning.
        return sections
            .Where(section =>
                section.Name.Equals(PasswordsSection, StringComparison.OrdinalIgnoreCase)
                || section.Name.Equals(CredentialsSection, StringComparison.OrdinalIgnoreCase))
            .Sum(section => section.Entries.Count);
    }

    /// <summary>
    /// Reads every section and entry in file order. Duplicate section names and duplicate keys
    /// are all kept, so nothing is overwritten before it can be reported.
    /// </summary>
    private static List<IniSection> ParseIniSections(string content)
    {
        var sections = new List<IniSection>();
        IniSection? current = null;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith(IniCommentPrefix))
            {
                continue;
            }

            string trimmedLine = line.Trim();
            if (trimmedLine.StartsWith('[') && trimmedLine.Contains(']'))
            {
                current = new IniSection(trimmedLine[1..trimmedLine.IndexOf(']')], []);
                sections.Add(current);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            int separatorIndex = line.IndexOf(IniKeyValueSeparator);
            if (separatorIndex > 0)
            {
                current.Entries.Add(new KeyValuePair<string, string>(
                    line[..separatorIndex].Trim(),
                    line[(separatorIndex + 1)..]));
            }
        }

        return sections;
    }

    private static bool IsBookmarkSection(string sectionName) =>
        sectionName.StartsWith(BookmarkSectionPrefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsMetaKey(string key) =>
        key.Equals(FolderKey, StringComparison.OrdinalIgnoreCase)
        || key.Equals(FolderIconKey, StringComparison.OrdinalIgnoreCase);

    private static int ParsePort(string value, int defaultPort) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port > 0
            ? port
            : defaultPort;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Sanitize a MobaXterm SubRep folder path for use as a Heimdall group name.
    /// MobaXterm uses backslash-separated hierarchical folders (e.g. "ADSEC\Gateways").
    /// Heimdall uses forward-slash hierarchy (e.g. "ADSEC/Gateways").
    /// Strips parent-directory traversal sequences while preserving the hierarchy.
    /// </summary>
    internal static string SanitizeGroupName(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return string.Empty;
        }

        // Convert MobaXterm backslash hierarchy to Heimdall forward-slash hierarchy
        var sanitized = folder.Replace(MobaFolderSeparator, GroupSeparator);

        // Strip traversal sequences
        sanitized = sanitized.Replace("../", string.Empty);
        sanitized = sanitized.Replace("./", string.Empty);

        // Remove leading/trailing slashes and collapse double slashes
        while (sanitized.Contains("//"))
        {
            sanitized = sanitized.Replace("//", "/");
        }

        sanitized = sanitized.Trim(GroupSeparator).Trim();

        // Strip characters illegal in file names from each segment
        var invalidChars = Path.GetInvalidFileNameChars()
            .Where(c => c != GroupSeparator && c != MobaFolderSeparator)
            .ToArray();

        foreach (var c in invalidChars)
        {
            sanitized = sanitized.Replace(c.ToString(), string.Empty);
        }

        return sanitized;
    }

    [GeneratedRegex(@"\[([A-Za-z_][A-Za-z0-9_]*)\]")]
    private static partial Regex VariableTokenRegex();

    /// <summary>One parsed session value.</summary>
    internal readonly record struct SessionLine(string Icon, string TypeText, string[] Fields, string? Comment);

    private sealed record SessionLayout(
        string ConnectionType,
        int DefaultPort,
        int? LoginCommandIndex,
        int GatewayHostIndex,
        int GatewayPortIndex,
        int GatewayUserIndex);

    private sealed record IniSection(string Name, List<KeyValuePair<string, string>> Entries);

    private readonly record struct GatewayKey(string Host, int Port, string User);

    private readonly record struct ReportedVariable(string Section, string SessionName, string Name);

    /// <summary>State shared by the sessions of one parse.</summary>
    private sealed class ParseContext(MobaXtermImportOptions options, MobaXtermImportResult result)
    {
        private readonly Dictionary<GatewayKey, string> _gatewayIds = new();
        private readonly HashSet<ReportedVariable> _reportedVariables = new();

        public MobaXtermImportOptions Options { get; } = options;

        public MobaXtermImportResult Result { get; } = result;

        public HashSet<string> ProfileIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Warn(string section, string sessionName, MobaXtermImportWarningKind kind, string detail) =>
            Result.Warnings.Add(new MobaXtermImportWarning(section, sessionName, kind, detail));

        /// <summary>
        /// Replaces each <c>[NAME]</c> token through the caller's resolver. A token the resolver
        /// does not know stays as written and is reported once for this session.
        /// </summary>
        public string ResolveVariables(string section, string sessionName, string value)
        {
            if (value.Length == 0)
            {
                return value;
            }

            return VariableTokenRegex().Replace(value, match =>
            {
                string name = match.Groups[1].Value;
                string? resolved = Options.VariableResolver?.Invoke(name);
                if (!string.IsNullOrEmpty(resolved))
                {
                    return resolved;
                }

                // Once per session, even when the session's login and its gateway's login both
                // carry the token: the user has one value to define, not two.
                if (_reportedVariables.Add(new ReportedVariable(section, sessionName, name.ToUpperInvariant())))
                {
                    Warn(section, sessionName, MobaXtermImportWarningKind.UnresolvedVariable, name);
                }

                return match.Value;
            });
        }

        /// <summary>
        /// Returns the gateway for this host, port and user, creating it the first time; the key
        /// is case-insensitive like the gateway reconciliation that later persists it.
        /// </summary>
        public string GetOrAddGateway(string host, int port, string user)
        {
            var key = new GatewayKey(
                host.ToUpperInvariant(),
                port,
                user.ToUpperInvariant());
            if (_gatewayIds.TryGetValue(key, out string? existingId))
            {
                return existingId;
            }

            string id = BuildId(
                Options.IdPrefix,
                GatewayIdDomain,
                key.Host,
                port.ToString(CultureInfo.InvariantCulture),
                key.User);
            _gatewayIds[key] = id;
            Result.Gateways.Add(new SshGatewayDto
            {
                Id = id,
                Name = BuildGatewayName(host, port, user),
                Host = host,
                Port = port,
                User = user,
            });
            return id;
        }

        private static string BuildGatewayName(string host, int port, string user)
        {
            string name = user.Length == 0 ? host : $"{user}{GatewayNameUserSeparator}{host}";
            return port == DefaultPorts.Ssh
                ? name
                : $"{name}{GatewayNamePortSeparator}{port.ToString(CultureInfo.InvariantCulture)}";
        }
    }
}

/// <summary>
/// What the caller supplies to a MobaXterm parse.
/// </summary>
public sealed class MobaXtermImportOptions
{
    /// <summary>Options with the default prefix and no variable resolver.</summary>
    public static MobaXtermImportOptions Default { get; } = new();

    /// <summary>Prefix of every profile and gateway identifier the parse derives.</summary>
    public string IdPrefix { get; init; } = MobaXtermImporter.DefaultIdPrefix;

    /// <summary>
    /// Resolves a MobaXterm variable name (the text between <c>[</c> and <c>]</c>) to its value,
    /// or returns null when the name is unknown. Null means no variable is resolved.
    /// </summary>
    public Func<string, string?>? VariableResolver { get; init; }
}

/// <summary>Why a MobaXterm line or value could not be taken as it is.</summary>
public enum MobaXtermImportWarningKind
{
    /// <summary>The value does not have the <c>#icon#type%...</c> shape; not imported.</summary>
    MalformedSessionLine,

    /// <summary>The session type is not one this importer maps; not imported.</summary>
    UnsupportedSessionType,

    /// <summary>The session names no host; not imported.</summary>
    MissingHost,

    /// <summary>The session's gateway host is not a usable host; not imported.</summary>
    InvalidGatewayHost,

    /// <summary>Another session with the same name already exists in the same folder; not imported.</summary>
    DuplicateSession,

    /// <summary>A <c>[NAME]</c> variable has no value; imported with the token left as written.</summary>
    UnresolvedVariable,

    /// <summary>The line could not be read for another reason; not imported.</summary>
    ParseError,
}

/// <summary>
/// One line or value of a MobaXterm file that could not be taken as it is.
/// </summary>
/// <param name="Section">The INI section the session sits in.</param>
/// <param name="SessionName">The session name, as written in the file.</param>
/// <param name="Kind">Why the warning was raised.</param>
/// <param name="Detail">The offending value (type code, host, variable name), or empty.</param>
public sealed record MobaXtermImportWarning(
    string Section,
    string SessionName,
    MobaXtermImportWarningKind Kind,
    string Detail)
{
    /// <summary>Whether the session was still imported despite the warning.</summary>
    public bool SessionImported => Kind == MobaXtermImportWarningKind.UnresolvedVariable;
}

/// <summary>
/// Result of a MobaXterm session file import operation.
/// </summary>
public sealed class MobaXtermImportResult
{
    /// <summary>Successfully parsed server profiles.</summary>
    public List<ServerProfileDto> Servers { get; } = [];

    /// <summary>Gateways referenced by <see cref="Servers"/>, deduplicated within the file.</summary>
    public List<SshGatewayDto> Gateways { get; } = [];

    /// <summary>Every session skipped and every value left unresolved, with the reason.</summary>
    public List<MobaXtermImportWarning> Warnings { get; } = [];

    /// <summary>
    /// Number of entries found in MobaXterm [Passwords]/[Credentials] sections,
    /// exposed only to warn the user; values are never decrypted.
    /// </summary>
    public int StoredCredentialCount { get; internal set; }
}
