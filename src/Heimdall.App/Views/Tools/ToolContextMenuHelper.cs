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

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Heimdall.Core.Localization;
using Heimdall.Core.Models;
using Heimdall.Core.Security;

namespace Heimdall.App.Views.Tools;

/// <summary>
/// Shared helper for building context menus in tool views.
/// Provides cross-tool navigation (open IP in Ping, PortScanner, etc.)
/// and common actions (copy, add to servers).
/// </summary>
public static class ToolContextMenuHelper
{
    /// <summary>
    /// Builds host-centric actions: Ping, Port Scanner, Cert Inspector, Whois, DNS, Open in Browser, Add to Servers.
    /// </summary>
    public static List<object> BuildHostActions(
        string ip,
        string? hostname,
        IReadOnlyList<int>? openPorts,
        LocalizationManager? localizer,
        Action<string, string, ToolContext?> openToolCallback)
    {
        var items = new List<object>();

        // Copy IP
        var copyIp = new MenuItem { Header = L(localizer, "ToolCtxCopyIp") };
        copyIp.Click += (_, _) => { try { Clipboard.SetText(ip); } catch (System.Runtime.InteropServices.ExternalException) { /* clipboard locked */ } };
        items.Add(copyIp);

        // Copy hostname if available
        if (!string.IsNullOrWhiteSpace(hostname) && hostname != "-")
        {
            var copyHost = new MenuItem { Header = L(localizer, "ToolCtxCopyHostname") };
            copyHost.Click += (_, _) => { try { Clipboard.SetText(hostname); } catch (System.Runtime.InteropServices.ExternalException) { /* clipboard locked */ } };
            items.Add(copyHost);
        }

        AddSeparator(items);

        // Open in Ping
        var ping = new MenuItem { Header = L(localizer, "ToolCtxOpenPing") };
        ping.Click += (_, _) => openToolCallback("PING", L(localizer, "PaletteToolPing"),
            new ToolContext(TargetHost: ip));
        items.Add(ping);

        // Open in Port Scanner
        var portScan = new MenuItem { Header = L(localizer, "ToolCtxOpenPortScan") };
        portScan.Click += (_, _) => openToolCallback("PORTSCAN", L(localizer, "PaletteToolPortScan"),
            new ToolContext(TargetHost: ip));
        items.Add(portScan);

        // Open in DNS Lookup
        var dns = new MenuItem { Header = L(localizer, "ToolCtxOpenDns") };
        dns.Click += (_, _) => openToolCallback("DNS", L(localizer, "PaletteToolDns"),
            new ToolContext(TargetHost: ip));
        items.Add(dns);

        // Open in Whois
        var whois = new MenuItem { Header = L(localizer, "ToolCtxOpenWhois") };
        whois.Click += (_, _) => openToolCallback("WHOIS", L(localizer, "PaletteToolWhois"),
            new ToolContext(TargetHost: ip));
        items.Add(whois);

        // Open in Cert Inspector (if TLS port detected)
        if (openPorts?.Any(p => p is 443 or 8443 or 636 or 993 or 995) == true)
        {
            var tlsPort = openPorts.First(p => p is 443 or 8443 or 636 or 993 or 995);
            var cert = new MenuItem { Header = L(localizer, "ToolCtxOpenCertInspector") };
            cert.Click += (_, _) => openToolCallback("CERT", L(localizer, "PaletteToolCert"),
                new ToolContext(TargetHost: ip, TargetPort: tlsPort));
            items.Add(cert);
        }

        // Open in Browser (if HTTP port detected)
        if (openPorts?.Any(p => p is 80 or 443 or 8080 or 8443 or 8006 or 5000 or 3000 or 9090) == true)
        {
            AddSeparator(items);
            var httpPort = openPorts.First(p => p is 443 or 8443 or 80 or 8080 or 8006 or 5000 or 3000 or 9090);
            var scheme = httpPort is DefaultPorts.HttpsStd or DefaultPorts.HttpsAlt ? "https" : "http";
            var url = $"{scheme}://{ip}:{httpPort}";
            var browser = new MenuItem { Header = string.Format(L(localizer, "ToolCtxOpenBrowser"), url) };
            browser.Click += (_, _) =>
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                })?.Dispose();
            };
            items.Add(browser);
        }

        // Add to Heimdall Servers
        AddSeparator(items);
        var addServer = new MenuItem { Header = L(localizer, "ToolCtxAddToServers") };
        addServer.Click += (_, _) =>
        {
            // Determine best protocol from open ports
            string connType = "SSH";
            int port = DefaultPorts.Ssh;
            if (openPorts?.Contains(DefaultPorts.Rdp) == true) { connType = "RDP"; port = DefaultPorts.Rdp; }
            else if (openPorts?.Contains(DefaultPorts.Ssh) == true) { connType = "SSH"; port = DefaultPorts.Ssh; }
            else if (openPorts?.Contains(DefaultPorts.Vnc) == true) { connType = "VNC"; port = DefaultPorts.Vnc; }
            else if (openPorts?.Contains(DefaultPorts.Telnet) == true) { connType = "Telnet"; port = DefaultPorts.Telnet; }

            openToolCallback("__ADD_SERVER__", connType,
                new ToolContext(TargetHost: ip, TargetPort: port,
                    DisplayName: hostname ?? ip, ConnectionType: connType));
        };
        items.Add(addServer);

        return items;
    }

    /// <summary>
    /// Builds a "Copy Row" menu item for a DataGrid row.
    /// </summary>
    public static MenuItem BuildCopyRowAction(string rowText, LocalizationManager? localizer)
    {
        var item = new MenuItem { Header = L(localizer, "ToolCtxCopyRow") };
        item.Click += (_, _) => { try { Clipboard.SetText(InputValidator.SanitizeCsvCell(rowText)); } catch (System.Runtime.InteropServices.ExternalException) { /* clipboard locked */ } };
        return item;
    }

    /// <summary>
    /// Builds a "Copy All Rows" menu item that copies all DataGrid rows as tab-separated text.
    /// </summary>
    public static MenuItem BuildCopyAllAction(System.Windows.Controls.DataGrid grid, LocalizationManager? localizer)
    {
        var item = new MenuItem { Header = L(localizer, "ToolCtxCopyAll") };
        item.Click += (_, _) =>
        {
            var sb = new System.Text.StringBuilder();
            // Header row
            var headers = grid.Columns.Select(c => InputValidator.SanitizeCsvCell(c.Header?.ToString() ?? ""));
            sb.AppendLine(string.Join('\t', headers));
            // Data rows
            foreach (var row in grid.Items)
            {
                var cells = grid.Columns.Select(c => InputValidator.SanitizeCsvCell(c.GetCellContent(row) switch
                {
                    TextBlock tb => tb.Text,
                    _ => ""
                }));
                sb.AppendLine(string.Join('\t', cells));
            }
            try { Clipboard.SetText(sb.ToString()); } catch (System.Runtime.InteropServices.ExternalException) { /* clipboard locked */ }
        };
        return item;
    }

    /// <summary>
    /// Builds an "Export CSV" menu item that saves DataGrid contents as a CSV file.
    /// </summary>
    public static MenuItem BuildExportCsvAction(System.Windows.Controls.DataGrid grid, LocalizationManager? localizer)
    {
        var item = new MenuItem { Header = L(localizer, "ToolCtxExportCsv") };
        item.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = L(localizer, "FileDialogCsvFilter"),
                DefaultExt = ".csv",
            };
            if (dialog.ShowDialog() != true) return;

            var sb = new System.Text.StringBuilder();
            var headers = grid.Columns.Select(c => EscapeCsv(c.Header?.ToString() ?? ""));
            sb.AppendLine(string.Join(',', headers));
            foreach (var row in grid.Items)
            {
                var cells = grid.Columns.Select(c => EscapeCsv(
                    InputValidator.SanitizeCsvCell(c.GetCellContent(row) switch
                    {
                        TextBlock tb => tb.Text,
                        _ => ""
                    })));
                sb.AppendLine(string.Join(',', cells));
            }
            System.IO.File.WriteAllText(dialog.FileName, sb.ToString(), System.Text.Encoding.UTF8);
        };
        return item;
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    /// <summary>
    /// Extracts the <see cref="Action{String, String, ToolContext}"/> open-tool callback
    /// from a <see cref="ToolContext.OpenToolAction"/> delegate.
    /// Returns null if the delegate is not set or has an incompatible signature.
    /// </summary>
    public static Action<string, string, ToolContext?>? GetOpenToolAction(ToolContext? context)
    {
        if (context?.OpenToolAction is Func<string, string, ToolContext?, Task> asyncCallback)
        {
            return (toolId, title, ctx) => _ = asyncCallback(toolId, title, ctx);
        }

        return null;
    }

    /// <summary>
    /// Selects the DataGrid row under the mouse cursor on right-click,
    /// ensuring the context menu operates on the correct row.
    /// Attach this to the DataGrid's <c>PreviewMouseRightButtonDown</c> event.
    /// </summary>
    public static void SelectRowOnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid) return;

        var hit = VisualTreeHelper.HitTest(grid, e.GetPosition(grid));
        if (hit?.VisualHit is null) return;

        var row = FindVisualParent<DataGridRow>(hit.VisualHit);
        if (row is not null)
        {
            row.IsSelected = true;
            grid.SelectedItem = row.Item;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var current = child;
        while (current is not null)
        {
            if (current is T found) return found;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static void AddSeparator(List<object> items)
    {
        items.Add(new Separator());
    }

    private static string L(LocalizationManager? localizer, string key) => localizer?[key] ?? key;
}
