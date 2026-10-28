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

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// The parameter and variable names that the session's host and user name prefill. The Command
/// Library, the command palette and the runbook variables all answer "is this name a host or a
/// user" from this one list, and the runbook variables help spells the same names out, so a
/// name added here is a name the help must list (a test compares the two).
/// </summary>
/// <remarks>
/// The names are matched on their own, lower-cased: no parameter type says "user", and a
/// <c>hostname</c> or <c>ipaddress</c> typed parameter is a host whatever it is called.
/// </remarks>
public static class SessionPrefillNames
{
    /// <summary>Names that the session's target host fills.</summary>
    public static IReadOnlyList<string> HostNames { get; } =
    [
        "host", "hostname", "target", "targethost", "computername",
        "server", "ip", "ipaddress", "remotehost", "remotecomputer",
    ];

    /// <summary>Names that the session's user name fills.</summary>
    public static IReadOnlyList<string> UserNames { get; } =
    [
        "user", "username", "login", "account", "samaccountname",
    ];

    /// <summary>The parameter type that makes a parameter a host whatever its name.</summary>
    private const string HostnameType = "hostname";

    /// <summary>The other parameter type that makes a parameter a host whatever its name.</summary>
    private const string IpAddressType = "ipaddress";

    /// <summary>True for a name, or a parameter type, that the target host fills.</summary>
    public static bool IsHost(string name, string? type)
    {
        if (string.Equals(type, HostnameType, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, IpAddressType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Contains(HostNames, name);
    }

    /// <summary>True for a name that the session's user name fills.</summary>
    public static bool IsUser(string name) => Contains(UserNames, name);

    private static bool Contains(IReadOnlyList<string> names, string name)
    {
        var lowered = name.ToLowerInvariant();
        foreach (var candidate in names)
        {
            if (string.Equals(candidate, lowered, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
