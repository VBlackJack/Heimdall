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

namespace Heimdall.Core.Discovery;

/// <summary>
/// Generates Draw.io (diagrams.net) XML files from network scan snapshots
/// for visual network topology diagrams.
/// </summary>
public static class DrawIoExporter
{
    /// <summary>
    /// Generates a Draw.io XML string from a <see cref="NetworkScanSnapshot"/>.
    /// </summary>
    public static string Generate(NetworkScanSnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<mxfile host=\"Heimdall\">");
        sb.AppendLine($"  <diagram name=\"{EscapeXml(snapshot.Profile.Subnet)} - {snapshot.Timestamp:yyyy-MM-dd HH:mm}\">");
        sb.AppendLine("    <mxGraphModel dx=\"1200\" dy=\"800\" grid=\"1\" gridSize=\"10\">");
        sb.AppendLine("      <root>");
        sb.AppendLine("        <mxCell id=\"0\"/>");
        sb.AppendLine("        <mxCell id=\"1\" parent=\"0\"/>");

        // Separate hosts with open ports (classified) from ping-only hosts (no services)
        var groups = snapshot.Hosts
            .GroupBy(h =>
            {
                if (!h.Services.Any(s => s.IsOpen))
                    return "Ping Only (No Open Ports)";
                return h.PrimaryRole?.Role ?? "Unclassified";
            })
            .OrderBy(g => g.Key == "Ping Only (No Open Ports)" ? 1 : 0)
            .ThenBy(g => g.Key)
            .ToList();

        var cellId = 2;
        var groupX = 40;

        foreach (var group in groups)
        {
            var groupId = cellId++;
            var groupWidth = 200;
            var groupHeight = Math.Max(120, group.Count() * 90 + 40);

            sb.AppendLine($"        <mxCell id=\"{groupId}\" value=\"{EscapeXml(group.Key)}\" " +
                $"style=\"swimlane;startSize=30;fillColor={GetRoleColor(group.Key)};fontColor=#ffffff;rounded=1;\" " +
                $"vertex=\"1\" parent=\"1\">");
            sb.AppendLine($"          <mxGeometry x=\"{groupX}\" y=\"40\" width=\"{groupWidth}\" height=\"{groupHeight}\" as=\"geometry\"/>");
            sb.AppendLine("        </mxCell>");

            var groupY = 40;
            foreach (var host in group)
            {
                var nodeId = cellId++;
                var label = BuildNodeLabel(host);
                var style = GetNodeStyle(group.Key);

                sb.AppendLine($"        <mxCell id=\"{nodeId}\" value=\"{EscapeXml(label)}\" " +
                    $"style=\"{style}\" vertex=\"1\" parent=\"{groupId}\">");
                sb.AppendLine($"          <mxGeometry x=\"20\" y=\"{groupY}\" width=\"160\" height=\"70\" as=\"geometry\"/>");
                sb.AppendLine("        </mxCell>");

                groupY += 80;
            }

            groupX += 240;
        }

        sb.AppendLine("      </root>");
        sb.AppendLine("    </mxGraphModel>");
        sb.AppendLine("  </diagram>");
        sb.AppendLine("</mxfile>");

        return sb.ToString();
    }

    private static string BuildNodeLabel(HostScanResult host)
    {
        var sb = new StringBuilder();
        sb.Append(host.IpAddress);
        if (!string.IsNullOrEmpty(host.Hostname))
            sb.Append($"\n{host.Hostname}");

        // Show manufacturer from MAC OUI if available
        if (!string.IsNullOrEmpty(host.Manufacturer))
            sb.Append($"\n[{host.Manufacturer}]");

        var ports = string.Join(", ", host.Services.Where(s => s.IsOpen).Select(s => s.Port));
        if (!string.IsNullOrEmpty(ports))
            sb.Append($"\nPorts: {ports}");
        else if (!string.IsNullOrEmpty(host.MacAddress))
            sb.Append($"\nMAC: {host.MacAddress}");

        if (host.OsFingerprint is not null)
            sb.Append($"\nOS: {host.OsFingerprint.OsGuess}");

        if (!string.IsNullOrEmpty(host.NetBiosName))
            sb.Append($"\nNetBIOS: {host.NetBiosName}");

        if (host.SnmpInfo?.SysName is not null)
            sb.Append($"\nSNMP: {host.SnmpInfo.SysName}");

        var tlsCert = host.Services.FirstOrDefault(s => s.Certificate is not null)?.Certificate;
        if (tlsCert is not null)
        {
            sb.Append(tlsCert.IsExpired ? "\nCert EXPIRED" : $"\nCert: {tlsCert.TlsVersion}");
        }

        return sb.ToString();
    }

    private static string GetRoleColor(string role) => role switch
    {
        "Active Directory" => "#1E40AF",
        var r when r.StartsWith("Web Server", StringComparison.Ordinal) => "#16A34A",
        var r when r.StartsWith("Database", StringComparison.Ordinal) => "#D97706",
        "Mail Server" => "#7C3AED",
        "Windows RDP" => "#2563EB",
        "SSH Server" => "#059669",
        var r when r.Contains("Camera", StringComparison.Ordinal) => "#DC2626",
        var r when r.Contains("NAS", StringComparison.Ordinal) => "#EA580C",
        var r when r.Contains("Router", StringComparison.Ordinal) || r.Contains("Switch", StringComparison.Ordinal) => "#0891B2",
        var r when r.Contains("Firewall", StringComparison.Ordinal) => "#B91C1C",
        var r when r.Contains("Printer", StringComparison.Ordinal) => "#6B7280",
        var r when r.Contains("Hypervisor", StringComparison.Ordinal) || r.Contains("VMware", StringComparison.Ordinal) || r.Contains("Proxmox", StringComparison.Ordinal) => "#7C3AED",
        "Network Equipment (SNMP)" => "#6B7280",
        "Ping Only (No Open Ports)" => "#9CA3AF",
        _ => "#44475A"
    };

    private static string GetNodeStyle(string role) => role switch
    {
        "Active Directory" => "rounded=1;whiteSpace=wrap;fillColor=#1E40AF;fontColor=#ffffff;strokeColor=#1E3A8A;fontSize=10;align=left;spacingLeft=8;",
        var r when r.StartsWith("Web Server", StringComparison.Ordinal) => "rounded=1;whiteSpace=wrap;fillColor=#16A34A;fontColor=#ffffff;strokeColor=#15803D;fontSize=10;align=left;spacingLeft=8;",
        var r when r.StartsWith("Database", StringComparison.Ordinal) => "shape=cylinder3;whiteSpace=wrap;fillColor=#D97706;fontColor=#ffffff;strokeColor=#B45309;fontSize=10;size=8;",
        "Mail Server" => "rounded=1;whiteSpace=wrap;fillColor=#7C3AED;fontColor=#ffffff;strokeColor=#6D28D9;fontSize=10;align=left;spacingLeft=8;",
        var r when r.Contains("Camera", StringComparison.Ordinal) => "rounded=1;whiteSpace=wrap;fillColor=#DC2626;fontColor=#ffffff;strokeColor=#B91C1C;fontSize=10;align=left;spacingLeft=8;",
        var r when r.Contains("NAS", StringComparison.Ordinal) => "rounded=1;whiteSpace=wrap;fillColor=#EA580C;fontColor=#ffffff;strokeColor=#C2410C;fontSize=10;align=left;spacingLeft=8;",
        var r when r.Contains("Printer", StringComparison.Ordinal) => "rounded=1;whiteSpace=wrap;fillColor=#6B7280;fontColor=#ffffff;strokeColor=#4B5563;fontSize=10;align=left;spacingLeft=8;",
        "Ping Only (No Open Ports)" => "rounded=1;whiteSpace=wrap;fillColor=#E5E7EB;fontColor=#6B7280;strokeColor=#9CA3AF;fontSize=10;align=left;spacingLeft=8;dashed=1;",
        _ => "rounded=1;whiteSpace=wrap;fillColor=#44475A;fontColor=#ffffff;strokeColor=#6272A4;fontSize=10;align=left;spacingLeft=8;"
    };

    private static string EscapeXml(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("\n", "&#xa;");
}
