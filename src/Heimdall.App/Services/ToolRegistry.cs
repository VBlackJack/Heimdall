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

using System.Collections.Frozen;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;
using Heimdall.Core.Recipes;

namespace Heimdall.App.Services;

/// <summary>
/// Single source of truth for all built-in tools. Replaces the three
/// duplicate lists (menu definitions, palette commands, view factory switch)
/// with one ordered registry. Adding a new tool = one entry here + one View class.
/// </summary>
public sealed class ToolRegistry
{
    /// <summary>
    /// Stable tool id of the Command Library (TwinShell) tool. Exposed as a
    /// constant so callers (e.g. the Command Palette bridge) reference it
    /// without re-typing the magic literal.
    /// </summary>
    public const string CommandLibraryToolId = "CMDLIB";

    private readonly record struct ToolEntry(ToolDescriptor Descriptor, Func<IToolView> Factory);

    private readonly IReadOnlyList<ToolEntry> _entries;
    private readonly FrozenDictionary<string, ToolEntry> _byId;

    // Dynamic external tool overlay (populated after startup scan)
    private readonly List<ToolEntry> _externalEntries = [];
    private readonly Dictionary<string, ToolEntry> _externalById = new(StringComparer.OrdinalIgnoreCase);

    // Recipe overlay (tools described by imported recipe files). Kept apart from the external
    // overlay so that replacing one never drops the other; both are guarded by _externalLock,
    // because the composed list and the id lookups must change together.
    private readonly List<ToolEntry> _recipeEntries = [];
    private readonly Dictionary<string, ToolEntry> _recipeById = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _externalLock = new();

    /// <summary>All tool descriptors in display order (for menus and palette).</summary>
    public IReadOnlyList<ToolDescriptor> All { get; private set; }

    /// <summary>Raised when external tools are registered after a provider scan.</summary>
    public event Action? ExternalToolsChanged;

    /// <summary>
    /// Raised when the set of recipe tools changes. May be raised on any thread; a subscriber that
    /// touches the UI marshals itself.
    /// </summary>
    public event Action? RecipeToolsChanged;

    public ToolRegistry()
    {
        var entries = new List<ToolEntry>
        {
            // ── Network ───────────────────────────────────────────────
            Entry("PING",     ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolPing",     "PaletteToolPingWith",     ["ping"],                true,  () => new Views.Tools.PingToolView(),           "Geo.Tool.NetworkScanner", ToolPack.NetworkDiagnostics),
            Entry("DNS",      ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolDns",      "PaletteToolDnsWith",      ["dns","nslookup","dig"],true,  () => new Views.Tools.DnsLookupView(),          "Geo.Tool.DnsLookup", ToolPack.NetworkDiagnostics),
            Entry("CERT",     ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolCert",     "PaletteToolCertWith",     ["cert","ssl"],          true,  () => new Views.Tools.CertInspectorView(),     "Geo.Tool.CertInspector", ToolPack.NetworkDiagnostics),
            Entry("PORTSCAN", ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolPortScan", "PaletteToolPortScanWith", ["portscan","scan"],     true,  () => new Views.Tools.PortScannerView(),        "Geo.Tool.PortScanner", ToolPack.Audit),
            Entry("SUBNET",   ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolSubnet",   "PaletteToolSubnetWith",   ["subnet"],              false, () => new Views.Tools.SubnetCalculatorView(),   "Geo.Tool.NetworkScanner", ToolPack.Essentials),
            Entry("IPCONV",   ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolIpConv",   "PaletteToolIpConvWith",   ["ip","ipconv"],         false, () => new Views.Tools.IpConverterView(),        "Geo.Tool.IpConverter", ToolPack.Essentials),
            Entry("HTTP",     ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolHttp",     "PaletteToolHttpWith",     ["http","status"],       false, () => new Views.Tools.HttpStatusCodesView(),   "Geo.Tool.HttpStatus", ToolPack.Essentials),
            Entry("WHOIS",    ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolWhois",    "PaletteToolWhoisWith",    ["whois"],               true,  () => new Views.Tools.WhoisLookupView(),       "Geo.Tool.Whois", ToolPack.NetworkDiagnostics),
            Entry("HTTPHEADERS", ToolCategory.Network, "ToolCategoryNetwork", "PaletteToolHttpHeaders", "PaletteToolHttpHeadersWith", ["headers","httpheaders","secheaders"], true, () => new Views.Tools.HttpHeaderAnalyzerView(), "Geo.Tool.HttpHeaders", ToolPack.NetworkDiagnostics),
            Entry("BANNER",   ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolBanner",   "PaletteToolBannerWith",   ["banner","grab"],               true,  () => new Views.Tools.BannerGrabberView(),      "Geo.Tool.BannerGrabber", ToolPack.Audit),
            Entry("TCPTRACE", ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolTrace",    "PaletteToolTraceWith",    ["trace","traceroute","tracert"], true,  () => new Views.Tools.TcpTracerouteView(),      "Geo.Tool.Traceroute", ToolPack.NetworkDiagnostics),
            Entry("SNMPWALK", ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolSnmp",     "PaletteToolSnmpWith",     ["snmp","snmpwalk"],             true,  () => new Views.Tools.SnmpWalkerView(),         "Geo.Tool.SnmpWalker", ToolPack.Audit),
            Entry("ARPMON",   ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolArp",      "PaletteToolArpWith",      ["arp","arpmon"],                false, () => new Views.Tools.ArpMonitorView(),         "Geo.Tool.ArpMonitor", ToolPack.NetworkDiagnostics),
            Entry("FWTEST",   ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolFwTest",   "PaletteToolFwTestWith",   ["fw","firewall","fwtest"],      false, () => new Views.Tools.FirewallTesterView(),     "Geo.Tool.FirewallTester", ToolPack.Audit),
            Entry("NETMAP",   ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolNetMap",   "PaletteToolNetMapWith",   ["netmap","cartography","discover"], false, () => new Views.Tools.NetworkCartographyView(), "Geo.Tool.NetMap", ToolPack.Audit),
            Entry("NETCALC",  ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolNetCalc",  "PaletteToolNetCalcWith",  ["netcalc","vlan","supernet"], false, () => new Views.Tools.NetworkCalculatorView(), "Geo.Tool.NetworkCalculator", ToolPack.Essentials),
            Entry("TCPPING",  ToolCategory.Network,  "ToolCategoryNetwork",  "PaletteToolTcpPing",  "PaletteToolTcpPingWith",  ["tcpping","tcping","portping"],              true,  () => new Views.Tools.TcpPingView(),           "Geo.Tool.NetworkScanner", ToolPack.NetworkDiagnostics),

            // ── Security ──────────────────────────────────────────────
            Entry("HASH",     ToolCategory.Security, "ToolCategorySecurity", "PaletteToolHash",     "PaletteToolHashWith",     ["hash"],                false, () => new Views.Tools.HashGeneratorView(),      "Geo.Tool.HashGenerator", ToolPack.Essentials),
            Entry("HMAC",     ToolCategory.Security, "ToolCategorySecurity", "PaletteToolHmac",     "PaletteToolHmacWith",     ["hmac"],                false, () => new Views.Tools.HmacGeneratorView(),      "Geo.Tool.HashGenerator", ToolPack.Essentials),
            Entry("PASSWORD", ToolCategory.Security, "ToolCategorySecurity", "PaletteToolPassword", "PaletteToolPasswordWith", ["password","pwgen"],    false, () => new Views.Tools.PasswordGeneratorView(),  "Geo.Tool.PasswordGenerator", ToolPack.Essentials),
            Entry("SSHKEY",   ToolCategory.Security, "ToolCategorySecurity", "PaletteToolSshKey",   "PaletteToolSshKeyWith",   ["sshkey","keygen"],     false, () => new Views.Tools.SshKeyGeneratorView(),    "Geo.Tool.SshKeyGenerator", ToolPack.Essentials),
            Entry("CERTGEN",  ToolCategory.Security, "ToolCategorySecurity", "PaletteToolCertGen",  "PaletteToolCertGenWith",  ["certgen","certificate","openssl"], false, () => new Views.Tools.CertificateGeneratorView(), "Geo.Tool.CertificateGenerator", ToolPack.Essentials),
            Entry("JWT",      ToolCategory.Security, "ToolCategorySecurity", "PaletteToolJwt",      "PaletteToolJwtWith",      ["jwt"],                 false, () => new Views.Tools.JwtParserView(),         "Geo.Tool.Jwt", ToolPack.Essentials),
            Entry("TOTP",     ToolCategory.Security, "ToolCategorySecurity", "PaletteToolTotp",     "PaletteToolTotpWith",     ["totp","otp","2fa"],    false, () => new Views.Tools.TotpGeneratorView(),     "Geo.Tool.Totp", ToolPack.Essentials),
            Entry("PWDAUDIT", ToolCategory.Security, "ToolCategorySecurity", "PaletteToolPwdAudit", "PaletteToolPwdAuditWith", ["pwdaudit","password-audit","passcheck"], false, () => new Views.Tools.PasswordAuditView(), "Geo.Tool.PasswordAudit", ToolPack.Essentials),
            Entry("SSHAUDIT",ToolCategory.Security, "ToolCategorySecurity", "PaletteToolSshAudit", "PaletteToolSshAuditWith", ["sshaudit","keyaudit"],          false, () => new Views.Tools.SshKeyAuditView(),        "Geo.Tool.SshKeyAudit", ToolPack.Essentials),
            Entry("SSHSERVERAUDIT",ToolCategory.Security, "ToolCategorySecurity", "PaletteToolSshServerAudit", "PaletteToolSshServerAuditWith", ["sshserveraudit","ssh-audit","sshd-audit"], true, () => new Views.Tools.SshServerAuditView(), "Geo.Tool.SshKeyAudit", ToolPack.Audit),
            Entry("TLSAUDIT",ToolCategory.Security, "ToolCategorySecurity", "PaletteToolTlsAudit", "PaletteToolTlsAuditWith", ["tlsaudit","sslaudit"],          true,  () => new Views.Tools.TlsAuditView(),           "Geo.Tool.TlsAudit", ToolPack.Audit),
            Entry("DNSSEC",  ToolCategory.Security, "ToolCategorySecurity", "PaletteToolDnsSec",   "PaletteToolDnsSecWith",   ["dnssec","spf","dmarc","dkim"],  true,  () => new Views.Tools.DnsSecurityView(),         "Geo.Tool.DnsSecurity", ToolPack.NetworkDiagnostics),
            Entry("SMBENUM", ToolCategory.Security, "ToolCategorySecurity", "PaletteToolSmb",      "PaletteToolSmbWith",      ["smb","ntlm","netbios"],         true,  () => new Views.Tools.SmbEnumeratorView(),       "Geo.Tool.SmbEnumerator", ToolPack.Audit),
            Entry("DEFAULTCREDS", ToolCategory.Security, "ToolCategorySecurity", "PaletteToolDefCred", "PaletteToolDefCredWith", ["defaultcreds","creds"],       true,  () => new Views.Tools.DefaultCredentialView(),   "Geo.Tool.DefaultCreds", ToolPack.Audit),
            Entry("CVELOOKUP",ToolCategory.Security, "ToolCategorySecurity", "PaletteToolCve",     "PaletteToolCveWith",      ["cve","vuln","vulnerability"],   false, () => new Views.Tools.CveLookupView(),           "Geo.Tool.CveLookup", ToolPack.Essentials),
            Entry("SECNUMCLOUD",ToolCategory.Security, "ToolCategorySecurity", "PaletteToolAudit",  "PaletteToolAuditWith",    ["secnumcloud","audit","compliance","anssi"], false, () => new Views.Tools.SecNumCloudAuditView(), "Geo.Tool.SecNumCloud", ToolPack.Audit),

            // ── Encoding & Format ─────────────────────────────────────
            Entry("BASE64",   ToolCategory.Encoding, "ToolCategoryEncoding", "PaletteToolBase64",   "PaletteToolBase64With",   ["base64"],              false, () => new Views.Tools.Base64ToolView(),         "Geo.Tool.Base64Encoding", ToolPack.Essentials),
            Entry("URLENC",   ToolCategory.Encoding, "ToolCategoryEncoding", "PaletteToolUrlEnc",   "PaletteToolUrlEncWith",   ["url","urlencode"],     false, () => new Views.Tools.UrlEncoderView(),        "Geo.Tool.UrlEncoder", ToolPack.Essentials),
            Entry("JSON",     ToolCategory.Encoding, "ToolCategoryEncoding", "PaletteToolJson",     "PaletteToolJsonWith",     ["json"],                false, () => new Views.Tools.JsonFormatterView(),      "Geo.Tool.JsonFormatter", ToolPack.Essentials),
            Entry("REGEX",    ToolCategory.Encoding, "ToolCategoryEncoding", "PaletteToolRegex",    "PaletteToolRegexWith",    ["regex"],               false, () => new Views.Tools.RegexTesterView(),        "Geo.Tool.RegexTester", ToolPack.Essentials),
            Entry("DIFF",     ToolCategory.Encoding, "ToolCategoryEncoding", "PaletteToolDiff",     "PaletteToolDiffWith",     ["diff"],                false, () => new Views.Tools.TextDiffView(),          "Geo.Tool.Diff", ToolPack.Essentials),
            Entry("TEXTCASE", ToolCategory.Encoding, "ToolCategoryEncoding", "PaletteToolTextCase", "PaletteToolTextCaseWith", ["case","textcase"],     false, () => new Views.Tools.TextCaseConverterView(), "Geo.Tool.TextCase", ToolPack.Essentials),

            // ── System ────────────────────────────────────────────────
            Entry("CHMOD",    ToolCategory.System,   "ToolCategorySystem",   "PaletteToolChmod",    "PaletteToolChmodWith",    ["chmod"],               false, () => new Views.Tools.ChmodCalculatorView(),   "Geo.Tool.Chmod", ToolPack.Essentials),
            Entry("DATETIME", ToolCategory.System,   "ToolCategorySystem",   "PaletteToolDateTime", "PaletteToolDateTimeWith", ["datetime","epoch"],    false, () => new Views.Tools.DateTimeConverterView(), "Geo.Tool.DateTime", ToolPack.Essentials),
            Entry("UUID",     ToolCategory.System,   "ToolCategorySystem",   "PaletteToolUuid",     "PaletteToolUuidWith",     ["uuid","guid"],         false, () => new Views.Tools.UuidGeneratorView(),     "Geo.Tool.Uuid", ToolPack.Essentials),
            Entry("ULID",     ToolCategory.System,   "ToolCategorySystem",   "PaletteToolUlid",     "PaletteToolUlidWith",     ["ulid"],               false, () => new Views.Tools.UlidGeneratorView(),     "Geo.Tool.Ulid", ToolPack.Essentials),
            Entry("CRONTAB",  ToolCategory.System,   "ToolCategorySystem",   "PaletteToolCron",     "PaletteToolCronWith",     ["cron","crontab"],      false, () => new Views.Tools.CrontabBuilderView(),    "Geo.Tool.Crontab", ToolPack.Essentials),
            Entry("LOGVIEW",  ToolCategory.System,   "ToolCategorySystem",   "PaletteToolLogView",  "PaletteToolLogViewWith",  ["log","tail","logview"], false, () => new Views.Tools.LogViewerView(),          "Geo.Tool.LogViewer", ToolPack.Administration),
            Entry("HOSTS",    ToolCategory.System,   "ToolCategorySystem",   "PaletteToolHosts",    "PaletteToolHostsWith",    ["hosts"],               false, () => new Views.Tools.HostsFileEditorView(),    "Geo.Tool.HostsFileEditor", ToolPack.Administration),
            Entry("SSHCONFIG",ToolCategory.System,   "ToolCategorySystem",   "PaletteToolSshConfig","PaletteToolSshConfigWith",["sshconfig","ssh-config"], false, () => new Views.Tools.SshConfigGeneratorView(), "Geo.Tool.SshConfigGenerator", ToolPack.Essentials),
            Entry("CRONJOB",  ToolCategory.System,   "ToolCategorySystem",   "PaletteToolCronJob",  "PaletteToolCronJobWith",  ["cronjob","crontab-manager","tasks"], false, () => new Views.Tools.CronJobManagerView(), "Geo.Tool.CronJobManager", ToolPack.Administration),
            Entry("SERVICES", ToolCategory.System,   "ToolCategorySystem",   "PaletteToolServices", "PaletteToolServicesWith", ["services","svc","systemctl"],        false, () => new Views.Tools.ServiceStatusView(),  "Geo.Tool.ServiceStatusDashboard", ToolPack.Administration),
            Entry("NOTES",    ToolCategory.System,   "ToolCategorySystem",   "PaletteToolNotes",    "PaletteToolNotesWith",    ["notes","note","markdown","md","confluence"], false, () => new Views.Tools.NotesToolView(), "Geo.Tool.Notes", ToolPack.Core),
            Entry("DIAGRAM",  ToolCategory.System,   "ToolCategorySystem",   "PaletteToolDiagram",  "PaletteToolDiagramWith",  ["diagram","drawio","schema"],         false, () => new Views.Tools.DiagramEditorView(),  "Geo.Tool.Diagram", ToolPack.Administration),
            Entry(CommandLibraryToolId, ToolCategory.System, "ToolCategorySystem", "PaletteToolCmdLib", "PaletteToolCmdLibWith", ["cmd","command","library","snippet","twinshell","cheat","cheatsheet","powershell","bash"], false, () => new Views.Tools.CommandLibraryView(), "Geo.Tool.CommandLibrary", ToolPack.Core),
            Entry("PRIVLAUNCH", ToolCategory.System, "ToolCategorySystem", "PaletteToolPrivLaunch", null, ["privlaunch","sudo","nsudo","elevate","system","trustedinstaller"], false, () => new Views.Tools.PrivilegeLauncherView(), "Geo.Tool.PrivilegeLauncher", ToolPack.Audit),

            // ── External ─────────────────────────────────────────────
            Entry("WOL",       ToolCategory.External, "ToolCategoryExternal", "PaletteToolWol",       "PaletteToolWolWith",       ["wol","wake","wakeonlan"],              false, () => new Views.Tools.WakeOnLanView(),      "Geo.Tool.WakeOnLan", ToolPack.NetworkDiagnostics),
            Entry("OPENPORTS", ToolCategory.External, "ToolCategoryExternal", "PaletteToolOpenPorts", "PaletteToolOpenPortsWith", ["openports","ports","netstat","connections"], false, () => new Views.Tools.OpenPortsView(), "Geo.Tool.PortScanner", ToolPack.NetworkDiagnostics),
            Entry("NETIF",     ToolCategory.External, "ToolCategoryExternal", "PaletteToolNetIf",     "PaletteToolNetIfWith",     ["netif","interfaces","nic","adapter"],       false, () => new Views.Tools.NetworkInterfacesView(), "Geo.Tool.NetworkScanner", ToolPack.NetworkDiagnostics),
            Entry("ROUTES",    ToolCategory.External, "ToolCategoryExternal", "PaletteToolRoutes",    "PaletteToolRoutesWith",    ["routes","route","routing"],                 false, () => new Views.Tools.RouteTableView(),        "Geo.Tool.Traceroute", ToolPack.NetworkDiagnostics),
            Entry("DNSBATCH",  ToolCategory.External, "ToolCategoryExternal", "PaletteToolDnsBatch",  "PaletteToolDnsBatchWith",  ["dnsbatch","resolve","resolver"],            false, () => new Views.Tools.DnsBatchResolverView(),  "Geo.Tool.DnsLookup", ToolPack.NetworkDiagnostics),
            Entry("WIFI",      ToolCategory.External, "ToolCategoryExternal", "PaletteToolWifi",      "PaletteToolWifiWith",      ["wifi","wlan","ssid","wireless"],            false, () => new Views.Tools.WifiNetworksView(),      "Geo.Tool.NetworkScanner", ToolPack.NetworkDiagnostics),
        };

        _entries = entries;
        All = entries.Select(e => e.Descriptor).ToList();
        _byId = entries.ToFrozenDictionary(
            e => e.Descriptor.Id,
            e => e,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers external tools detected by <see cref="ExternalToolProviderService"/>.
    /// Merges them into the <see cref="All"/> list under the External category.
    /// Thread-safe - can be called from a background scan thread.
    /// </summary>
    public void RegisterExternalTools(
        IReadOnlyList<Core.Configuration.ExternalToolInfo> tools)
    {
        lock (_externalLock)
        {
            _externalEntries.Clear();
            _externalById.Clear();

            foreach (var tool in tools)
            {
                var id = $"EXT:{tool.ProviderName.ToUpperInvariant()}:{tool.Id}";
                var descriptor = new ToolDescriptor(
                    Id: id,
                    Category: ToolCategory.External,
                    CategoryLabelKey: "ToolCategoryExternal",
                    LabelKey: tool.Name, // Direct name (not i18n key) - external tools have static names
                    LabelWithArgKey: null,
                    CommandPrefixes: [tool.Id.ToLowerInvariant(), tool.Name.ToLowerInvariant().Replace(" ", "")],
                    IsNetworkTool: false,
                    IconResourceKey: tool.IconResourceKey ?? "Geo.Tool.External",
                    DescriptionKey: tool.DescriptionKey,
                    ExternalProviderName: tool.ProviderName
                );

                var capturedTool = tool;
                var entry = new ToolEntry(descriptor, () =>
                {
                    var view = new Views.Tools.ExternalToolWrapperView();
                    view.SetToolInfo(capturedTool);
                    return view;
                });

                _externalEntries.Add(entry);
                _externalById[id] = entry;
            }

            // Rebuild the All list: built-in entries + external entries + recipe entries
            RebuildAll();
        }

        ExternalToolsChanged?.Invoke();
    }

    /// <summary>
    /// Replaces the recipe tools with one tool per recipe, each opened through the supplied factory.
    /// </summary>
    /// <param name="recipes">The recipes to list, in display order. A repeated id keeps its last entry.</param>
    /// <param name="viewFactory">Creates the view of a recipe; called on the UI thread when the tool is opened.</param>
    /// <remarks>
    /// Thread-safe, like <see cref="RegisterExternalTools"/>. The external overlay and the built-in
    /// list are untouched, and <see cref="RegisterExternalTools"/> leaves the recipes alone in turn.
    /// A recipe tool belongs to <see cref="ToolPack.Recipes"/> and carries the recipe's author and
    /// description as text, never as a locale key; its only palette alias is its own id.
    /// </remarks>
    public void RegisterRecipeTools(IReadOnlyList<Recipe> recipes, Func<Recipe, IToolView> viewFactory)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentNullException.ThrowIfNull(viewFactory);

        lock (_externalLock)
        {
            _recipeEntries.Clear();
            _recipeById.Clear();

            foreach (Recipe recipe in recipes)
            {
                ToolDescriptor descriptor = CreateRecipeDescriptor(recipe);
                Recipe captured = recipe;
                ToolEntry entry = new(descriptor, () => viewFactory(captured));

                if (_recipeById.TryGetValue(descriptor.Id, out ToolEntry previous))
                {
                    _recipeEntries.Remove(previous);
                }

                _recipeEntries.Add(entry);
                _recipeById[descriptor.Id] = entry;
            }

            RebuildAll();
            s_registeredRecipeGeometry = _recipeEntries.ToFrozenDictionary(
                entry => entry.Descriptor.Id,
                entry => entry.Descriptor.IconResourceKey ?? ExternalGeometryKey,
                StringComparer.OrdinalIgnoreCase);
        }

        RecipeToolsChanged?.Invoke();
    }

    /// <summary>The descriptor of the tool a recipe becomes.</summary>
    internal static ToolDescriptor CreateRecipeDescriptor(Recipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        return new ToolDescriptor(
            Id: ToolDescriptor.RecipeIdPrefix + recipe.Id,
            Category: ToolCategory.External,
            CategoryLabelKey: "ToolCategoryExternal",
            LabelKey: recipe.Name,
            LabelWithArgKey: null,
            CommandPrefixes: [recipe.Id],
            IsNetworkTool: false,
            IconResourceKey: RecipeGeometryKeyOf(recipe.Icon),
            Pack: ToolPack.Recipes,
            RecipeAuthor: recipe.Author,
            RecipeDescription: recipe.Description);
    }

    // Callers hold _externalLock: the composed list is rebuilt from the three sources together.
    private void RebuildAll()
        => All = _entries.Concat(_externalEntries).Concat(_recipeEntries).Select(e => e.Descriptor).ToList();

    /// <summary>
    /// Registers one tool under the external overlay with a factory the caller supplies.
    /// </summary>
    /// <remarks>
    /// A test seam, and nothing else uses it: every built-in tool is a XAML view that resolves
    /// application resources, so a test that must open a tool through the session manager
    /// without building an application registers a probe view here instead. The built-in list is
    /// untouched; the entry is merged the way a scanned external tool is. The descriptor is
    /// stored as given, pack included, so a test can place a probe in a pack.
    /// </remarks>
    internal void RegisterToolForTests(ToolDescriptor descriptor, Func<IToolView> factory)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_externalLock)
        {
            var entry = new ToolEntry(descriptor, factory);
            _externalEntries.Add(entry);
            _externalById[descriptor.Id] = entry;
            RebuildAll();
        }
    }

    /// <summary>
    /// Looks up a tool by its short ID (e.g. "PING") or external ID (e.g. "EXT:SYSINTERNALS:PSEXEC").
    /// Also accepts the prefixed form "TOOL:PING" - the prefix is stripped automatically.
    /// </summary>
    public ToolDescriptor? GetById(string toolId)
    {
        var key = StripPrefix(toolId);
        if (_byId.TryGetValue(key, out var entry)) return entry.Descriptor;
        lock (_externalLock)
        {
            if (_externalById.TryGetValue(key, out var ext)) return ext.Descriptor;
            return _recipeById.TryGetValue(key, out var recipe) ? recipe.Descriptor : null;
        }
    }

    /// <summary>
    /// Creates a new instance of the tool's view control.
    /// Must be called on the UI thread.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if the tool ID is unknown.</exception>
    public IToolView CreateView(string toolId)
    {
        var key = StripPrefix(toolId);
        if (_byId.TryGetValue(key, out var entry))
            return entry.Factory();
        lock (_externalLock)
        {
            if (_externalById.TryGetValue(key, out var ext))
                return ext.Factory();
            if (_recipeById.TryGetValue(key, out var recipe))
                return recipe.Factory();
        }
        throw new ArgumentException($"Unknown tool ID: {toolId}", nameof(toolId));
    }

    /// <summary>
    /// Returns true if the given tool type (e.g. "TOOL:PING" or "PING") is a network tool
    /// that should prompt for a target host when opened standalone.
    /// </summary>
    public bool IsNetworkTool(string toolId)
    {
        var key = StripPrefix(toolId);
        return _byId.TryGetValue(key, out var entry) && entry.Descriptor.IsNetworkTool;
    }

    // ── Static lookups for XAML converters (no DI access) ──────────────

    private static readonly FrozenDictionary<string, string> s_geometryKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["PING"] = "Geo.Tool.NetworkScanner",
        ["DNS"] = "Geo.Tool.DnsLookup",
        ["CERT"] = "Geo.Tool.CertInspector",
        ["PORTSCAN"] = "Geo.Tool.PortScanner",
        ["SUBNET"] = "Geo.Tool.NetworkScanner",
        ["IPCONV"] = "Geo.Tool.IpConverter",
        ["HTTP"] = "Geo.Tool.HttpStatus",
        ["WHOIS"] = "Geo.Tool.Whois",
        ["HTTPHEADERS"] = "Geo.Tool.HttpHeaders",
        ["BANNER"] = "Geo.Tool.BannerGrabber",
        ["TCPTRACE"] = "Geo.Tool.Traceroute",
        ["SNMPWALK"] = "Geo.Tool.SnmpWalker",
        ["ARPMON"] = "Geo.Tool.ArpMonitor",
        ["FWTEST"] = "Geo.Tool.FirewallTester",
        ["NETMAP"] = "Geo.Tool.NetMap",
        ["NETCALC"] = "Geo.Tool.NetworkCalculator",
        ["HASH"] = "Geo.Tool.HashGenerator",
        ["HMAC"] = "Geo.Tool.HashGenerator",
        ["PASSWORD"] = "Geo.Tool.PasswordGenerator",
        ["SSHKEY"] = "Geo.Tool.SshKeyGenerator",
        ["CERTGEN"] = "Geo.Tool.CertificateGenerator",
        ["JWT"] = "Geo.Tool.Jwt",
        ["TOTP"] = "Geo.Tool.Totp",
        ["PWDAUDIT"] = "Geo.Tool.PasswordAudit",
        ["SSHAUDIT"] = "Geo.Tool.SshKeyAudit",
        ["SSHSERVERAUDIT"] = "Geo.Tool.SshKeyAudit",
        ["TLSAUDIT"] = "Geo.Tool.TlsAudit",
        ["DNSSEC"] = "Geo.Tool.DnsSecurity",
        ["SMBENUM"] = "Geo.Tool.SmbEnumerator",
        ["DEFAULTCREDS"] = "Geo.Tool.DefaultCreds",
        ["CVELOOKUP"] = "Geo.Tool.CveLookup",
        ["SECNUMCLOUD"] = "Geo.Tool.SecNumCloud",
        ["BASE64"] = "Geo.Tool.Base64Encoding",
        ["URLENC"] = "Geo.Tool.UrlEncoder",
        ["JSON"] = "Geo.Tool.JsonFormatter",
        ["REGEX"] = "Geo.Tool.RegexTester",
        ["DIFF"] = "Geo.Tool.Diff",
        ["TEXTCASE"] = "Geo.Tool.TextCase",
        ["CHMOD"] = "Geo.Tool.Chmod",
        ["DATETIME"] = "Geo.Tool.DateTime",
        ["UUID"] = "Geo.Tool.Uuid",
        ["ULID"] = "Geo.Tool.Ulid",
        ["CRONTAB"] = "Geo.Tool.Crontab",
        ["LOGVIEW"] = "Geo.Tool.LogViewer",
        ["HOSTS"] = "Geo.Tool.HostsFileEditor",
        ["SSHCONFIG"] = "Geo.Tool.SshConfigGenerator",
        ["CRONJOB"] = "Geo.Tool.CronJobManager",
        ["SERVICES"] = "Geo.Tool.ServiceStatusDashboard",
        ["NOTES"] = "Geo.Tool.Notes",
        ["DIAGRAM"] = "Geo.Tool.Diagram",
        ["CMDLIB"] = "Geo.Tool.CommandLibrary",
        ["PRIVLAUNCH"] = "Geo.Tool.PrivilegeLauncher",
        ["WOL"] = "Geo.Tool.WakeOnLan",
        ["OPENPORTS"] = "Geo.Tool.PortScanner",
        ["NETIF"] = "Geo.Tool.NetworkScanner",
        ["ROUTES"] = "Geo.Tool.Traceroute",
        ["DNSBATCH"] = "Geo.Tool.DnsLookup",
        ["WIFI"] = "Geo.Tool.NetworkScanner",
        ["TCPPING"] = "Geo.Tool.NetworkScanner",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> s_categoryBrushKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["PING"] = "ToolNetworkBrush",
        ["DNS"] = "ToolNetworkBrush",
        ["CERT"] = "ToolNetworkBrush",
        ["PORTSCAN"] = "ToolNetworkBrush",
        ["SUBNET"] = "ToolNetworkBrush",
        ["IPCONV"] = "ToolNetworkBrush",
        ["HTTP"] = "ToolNetworkBrush",
        ["WHOIS"] = "ToolNetworkBrush",
        ["HTTPHEADERS"] = "ToolNetworkBrush",
        ["BANNER"] = "ToolNetworkBrush",
        ["TCPTRACE"] = "ToolNetworkBrush",
        ["SNMPWALK"] = "ToolNetworkBrush",
        ["ARPMON"] = "ToolNetworkBrush",
        ["FWTEST"] = "ToolNetworkBrush",
        ["NETMAP"] = "ToolNetworkBrush",
        ["NETCALC"] = "ToolNetworkBrush",
        ["HASH"] = "ToolSecurityBrush",
        ["HMAC"] = "ToolSecurityBrush",
        ["PASSWORD"] = "ToolSecurityBrush",
        ["SSHKEY"] = "ToolSecurityBrush",
        ["CERTGEN"] = "ToolSecurityBrush",
        ["JWT"] = "ToolSecurityBrush",
        ["TOTP"] = "ToolSecurityBrush",
        ["PWDAUDIT"] = "ToolSecurityBrush",
        ["SSHAUDIT"] = "ToolSecurityBrush",
        ["SSHSERVERAUDIT"] = "ToolSecurityBrush",
        ["TLSAUDIT"] = "ToolSecurityBrush",
        ["DNSSEC"] = "ToolSecurityBrush",
        ["SMBENUM"] = "ToolSecurityBrush",
        ["DEFAULTCREDS"] = "ToolSecurityBrush",
        ["CVELOOKUP"] = "ToolSecurityBrush",
        ["SECNUMCLOUD"] = "ToolSecurityBrush",
        ["BASE64"] = "ToolEncodingBrush",
        ["URLENC"] = "ToolEncodingBrush",
        ["JSON"] = "ToolEncodingBrush",
        ["REGEX"] = "ToolEncodingBrush",
        ["DIFF"] = "ToolEncodingBrush",
        ["TEXTCASE"] = "ToolEncodingBrush",
        ["CHMOD"] = "ToolSystemBrush",
        ["DATETIME"] = "ToolSystemBrush",
        ["UUID"] = "ToolSystemBrush",
        ["ULID"] = "ToolSystemBrush",
        ["CRONTAB"] = "ToolSystemBrush",
        ["LOGVIEW"] = "ToolSystemBrush",
        ["HOSTS"] = "ToolSystemBrush",
        ["SSHCONFIG"] = "ToolSystemBrush",
        ["CRONJOB"] = "ToolSystemBrush",
        ["SERVICES"] = "ToolSystemBrush",
        ["NOTES"] = "ToolSystemBrush",
        ["DIAGRAM"] = "ToolSystemBrush",
        ["CMDLIB"] = "ToolSystemBrush",
        ["PRIVLAUNCH"] = "ToolSystemBrush",
        ["WOL"] = "ToolExternalBrush",
        ["OPENPORTS"] = "ToolExternalBrush",
        ["NETIF"] = "ToolExternalBrush",
        ["ROUTES"] = "ToolExternalBrush",
        ["DNSBATCH"] = "ToolExternalBrush",
        ["WIFI"] = "ToolExternalBrush",
        ["TCPPING"] = "ToolNetworkBrush",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // The built-in glyph of each icon key a recipe may name (see RecipeIcons). A recipe never
    // carries art of its own; a key outside the set falls back to the generic external glyph.
    private static readonly FrozenDictionary<string, string> s_recipeGeometryKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [RecipeIcons.Default] = "Geo.Tool.External",
        ["terminal"] = "Geo.Tool.CommandLibrary",
        ["network"] = "Geo.Tool.NetworkScanner",
        ["search"] = "Geo.Tool.DnsLookup",
        ["document"] = "Geo.Tool.LogViewer",
        ["shield"] = "Geo.Tool.PasswordAudit",
        ["clock"] = "Geo.Tool.DateTime",
        ["server"] = "Geo.Tool.ServiceStatusDashboard",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private const string ExternalGeometryKey = "Geo.Tool.External";

    /// <summary>The geometry resource key for the icon key of a recipe; null or unknown gives the default glyph.</summary>
    internal static string RecipeGeometryKeyOf(string? iconKey)
        => iconKey is not null && s_recipeGeometryKeys.TryGetValue(iconKey, out string? geoKey)
            ? geoKey
            : ExternalGeometryKey;

    /// <summary>
    /// Returns the Geometry resource key for a tool (e.g. "TOOL:PING" → "Geo.Tool.NetworkScanner").
    /// Used by XAML converters that lack DI access.
    /// </summary>
    internal static string? GetGeometryKey(string toolId)
    {
        var key = StripPrefix(toolId);
        if (s_geometryKeys.TryGetValue(key, out var geoKey)) return geoKey;
        // External tools (EXT:*) use the generic external icon
        if (key.StartsWith("EXT:", StringComparison.OrdinalIgnoreCase)) return ExternalGeometryKey;
        // A recipe tool (RCP:*): the glyph of the icon key its recipe names, as last registered.
        // This lookup has no registry to ask, so the application's one registry publishes them.
        if (key.StartsWith(ToolDescriptor.RecipeIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return s_registeredRecipeGeometry.TryGetValue(key, out string? recipeGeoKey) ? recipeGeoKey : ExternalGeometryKey;
        }

        return null;
    }

    // Published by RegisterRecipeTools for the static lookups above (XAML converters have no DI
    // access). A single application registry exists, so the last registration is the truth.
    private static volatile FrozenDictionary<string, string> s_registeredRecipeGeometry =
        FrozenDictionary<string, string>.Empty;

    /// <summary>
    /// Returns the category brush resource key for a tool (e.g. "TOOL:PING" → "ToolNetworkBrush").
    /// Used by XAML converters that lack DI access.
    /// </summary>
    internal static string GetCategoryBrushKey(string toolId)
    {
        var key = StripPrefix(toolId);
        if (s_categoryBrushKeys.TryGetValue(key, out var brushKey)) return brushKey;
        // External tools (EXT:*) and recipe tools (RCP:*) use the external brush
        return key.StartsWith("EXT:", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(ToolDescriptor.RecipeIdPrefix, StringComparison.OrdinalIgnoreCase)
            ? "ToolExternalBrush"
            : "ToolBadgeBrush";
    }

    private static string StripPrefix(string toolId)
        => ConnectionTypeCatalog.StripToolPrefix(toolId);

    private static ToolEntry Entry(
        string id,
        ToolCategory category,
        string categoryLabelKey,
        string labelKey,
        string? labelWithArgKey,
        string[] prefixes,
        bool isNetworkTool,
        Func<IToolView> factory,
        string? iconKey,
        ToolPack pack)
        => new(new ToolDescriptor(id, category, categoryLabelKey, labelKey, labelWithArgKey, prefixes, isNetworkTool, iconKey, Pack: pack), factory);
}
