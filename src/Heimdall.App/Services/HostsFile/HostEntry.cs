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

using System.ComponentModel;
using System.Net;
using Heimdall.App.Localization;

namespace Heimdall.App.Services.HostsFile;

/// <summary>
/// What stops a hosts file row from being written.
/// </summary>
public enum HostEntryProblem
{
    None = 0,
    MissingIpAddress = 1,
    InvalidIpAddress = 2,
    MissingHostname = 3,
    InvalidHostname = 4,
}

/// <summary>
/// Represents a single entry in the hosts file with enable/disable state.
/// </summary>
public sealed class HostEntry : INotifyPropertyChanged
{
    /// <summary>
    /// Starts a comment in a hosts file line; a hostname that contains it would turn the
    /// rest of the line into a comment once written.
    /// </summary>
    internal const char CommentMarker = '#';

    private bool _enabled;
    private string _ipAddress = string.Empty;
    private string _hostname = string.Empty;
    private string _comment = string.Empty;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            OnPropertyChanged(nameof(Enabled));
            OnPropertyChanged(nameof(EnabledDisplay));
        }
    }

    /// <summary>
    /// <see cref="Enabled" /> as a word, for the row's accessible name.
    /// </summary>
    /// <remarks>
    /// Binding the boolean directly would put an English literal into every locale:
    /// <see cref="bool" /> does not implement <see cref="IFormattable" />, so
    /// <see cref="string.Format(IFormatProvider, string, object?[])" /> falls through to
    /// <c>Boolean.ToString()</c>, which ignores the format provider and always returns the
    /// invariant "True" or "False". The visible column is a checkbox, so this word exists
    /// only for a screen reader and has to carry its own translation.
    /// </remarks>
    public string EnabledDisplay => LocalizationSource.Instance[
        _enabled ? "A11yStatusDotScheduledEnabled" : "A11yStatusDotScheduledDisabled"];

    public string IpAddress
    {
        get => _ipAddress;
        set
        {
            _ipAddress = value ?? string.Empty;
            OnPropertyChanged(nameof(IpAddress));
            OnProblemMayHaveChanged();
        }
    }

    /// <summary>
    /// The host names of the line, separated by a space when the line carries aliases.
    /// </summary>
    public string Hostname
    {
        get => _hostname;
        set
        {
            _hostname = value ?? string.Empty;
            OnPropertyChanged(nameof(Hostname));
            OnProblemMayHaveChanged();
        }
    }

    public string Comment
    {
        get => _comment;
        set { _comment = value ?? string.Empty; OnPropertyChanged(nameof(Comment)); }
    }

    /// <summary>
    /// What stops this row from being written, worked out from the values it holds now.
    /// </summary>
    public HostEntryProblem Problem => Validate(_ipAddress, _hostname);

    /// <summary>
    /// <see cref="Problem" /> as a sentence, shown in the row next to the values so the
    /// fault is read as text and not only as a color.
    /// </summary>
    public string ProblemDisplay => ProblemLocaleKey(Problem) is { } key
        ? LocalizationSource.Instance[key]
        : string.Empty;

    /// <summary>
    /// Decides whether an address and a host name can be written as a hosts file line.
    /// </summary>
    public static HostEntryProblem Validate(string? ipAddress, string? hostname)
    {
        var ip = ipAddress?.Trim() ?? string.Empty;
        if (ip.Length == 0)
        {
            return HostEntryProblem.MissingIpAddress;
        }

        if (!IsValidAddress(ip))
        {
            return HostEntryProblem.InvalidIpAddress;
        }

        var host = hostname?.Trim() ?? string.Empty;
        if (host.Length == 0)
        {
            return HostEntryProblem.MissingHostname;
        }

        return host.Contains(CommentMarker)
            ? HostEntryProblem.InvalidHostname
            : HostEntryProblem.None;
    }

    /// <summary>
    /// True for an IPv4 address written as four decimal octets, or for an IPv6 address
    /// (compressed forms such as <c>fe80::1</c> included). The loader and the editor apply
    /// this same test.
    /// </summary>
    /// <remarks>
    /// <see cref="IPAddress.TryParse(string?, out IPAddress?)" /> alone also accepts the
    /// shorthand forms <c>127.1</c>, <c>0x7f.1</c> and <c>1</c>. Requiring the parsed IPv4
    /// address to print back as the text given refuses those, and leading zeros with them.
    /// </remarks>
    public static bool IsValidAddress(string? text)
    {
        if (string.IsNullOrEmpty(text) || !IPAddress.TryParse(text, out var address))
        {
            return false;
        }

        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? text.Contains(':')
            : string.Equals(address.ToString(), text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The locale key that names a problem, or null for <see cref="HostEntryProblem.None" />.
    /// </summary>
    public static string? ProblemLocaleKey(HostEntryProblem problem) => problem switch
    {
        HostEntryProblem.MissingIpAddress => "ToolHostsEditorProblemMissingIp",
        HostEntryProblem.InvalidIpAddress => "ToolHostsEditorInvalidIp",
        HostEntryProblem.MissingHostname => "ToolHostsEditorProblemMissingHostname",
        HostEntryProblem.InvalidHostname => "ToolHostsEditorProblemInvalidHostname",
        _ => null,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnProblemMayHaveChanged()
    {
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(ProblemDisplay));
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
