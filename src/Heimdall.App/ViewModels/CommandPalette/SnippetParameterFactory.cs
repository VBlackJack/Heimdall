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
using Heimdall.App.ViewModels.CommandLibrary;
using TwinShell.Core.Models;

namespace Heimdall.App.ViewModels.CommandPalette;

/// <summary>
/// Pure helper that builds the editable parameter entries for a command
/// template, mirroring the host-prefill rules used by the Command Library tool.
/// No UI, no localization: it only maps <see cref="TemplateParameter"/> values
/// into <see cref="CommandLibraryParameterEntry"/> instances.
/// </summary>
public static class SnippetParameterFactory
{
    /// <summary>
    /// Builds one <see cref="CommandLibraryParameterEntry"/> per template
    /// parameter. Host-capable parameters are prefilled with
    /// <paramref name="targetHost"/> when it is non-empty and acceptable for the
    /// parameter (same rules as the Command Library tool); otherwise the
    /// parameter's default value (or empty) is used.
    /// </summary>
    /// <param name="template">The template whose parameters to project.</param>
    /// <param name="targetHost">Best-effort host of the active session, or null.</param>
    /// <returns>The editable parameter entries (empty when the template has none).</returns>
    public static IReadOnlyList<CommandLibraryParameterEntry> Build(
        CommandTemplate template, string? targetHost)
    {
        ArgumentNullException.ThrowIfNull(template);

        var entries = new List<CommandLibraryParameterEntry>();
        if (template.Parameters is null) return entries;

        var host = string.IsNullOrWhiteSpace(targetHost) ? null : targetHost.Trim();

        foreach (var parameter in template.Parameters)
        {
            entries.Add(new CommandLibraryParameterEntry
            {
                Name = parameter.Name,
                Label = parameter.Label,
                Required = parameter.Required,
                Description = parameter.Description,
                Type = parameter.Type ?? "string",
                Value = GetInitialParameterValue(parameter, host)
            });
        }

        return entries;
    }

    private static string GetInitialParameterValue(TemplateParameter parameter, string? targetHost)
    {
        if (!string.IsNullOrWhiteSpace(targetHost) && CanPrefillTargetHost(parameter, targetHost!))
        {
            return targetHost!;
        }
        return parameter.DefaultValue ?? string.Empty;
    }

    private static bool CanPrefillTargetHost(TemplateParameter parameter, string targetHost)
    {
        if (string.Equals(parameter.Type, "ipaddress", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.TryParse(targetHost, out _);
        }
        return IsHostParameter(parameter.Name, parameter.Type);
    }

    private static bool IsHostParameter(string name, string? type)
    {
        if (string.Equals(type, "hostname", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "ipaddress", StringComparison.OrdinalIgnoreCase))
            return true;

        var n = name.ToLowerInvariant();
        return n is "host" or "hostname" or "target" or "targethost"
            or "computername" or "server" or "ip" or "ipaddress"
            or "remotehost" or "remotecomputer";
    }
}
