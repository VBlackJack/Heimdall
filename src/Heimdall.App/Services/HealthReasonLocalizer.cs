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

using Heimdall.Core.Localization;
using Heimdall.Core.SessionHealth;

namespace Heimdall.App.Services;

/// <summary>
/// Translates the short machine-friendly reason tags emitted by
/// <see cref="SessionHealthMonitor"/> into the user-facing tooltip shown on
/// each sidebar dot. Pure static surface - testable without a live WPF
/// dispatcher and shared by every view that needs to render a
/// <see cref="HealthState"/>.
/// </summary>
public static class HealthReasonLocalizer
{
    /// <summary>
    /// Returns the i18n key for a status enum value. Used to render the
    /// leading label segment of a tooltip ("Reachable", "Unreachable", ...).
    /// </summary>
    public static string StatusKey(HealthStatus status) => status switch
    {
        HealthStatus.Up => "HealthStatusUp",
        HealthStatus.Down => "HealthStatusDown",
        HealthStatus.Probing => "HealthStatusProbing",
        _ => "HealthStatusUnknown"
    };

    /// <summary>
    /// Returns the i18n key for a known reason tag, or <c>null</c> when the
    /// tag is unknown - caller then renders the raw tag in parentheses so the
    /// information is not lost during development of new reason codes.
    /// </summary>
    public static string? ReasonKey(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return null;
        return reason switch
        {
            "timeout" => "HealthReasonTimeout",
            "refused" => "HealthReasonRefused",
            "unreachable" => "HealthReasonUnreachable",
            "dns" => "HealthReasonDns",
            "behind-gateway" => "HealthReasonBehindGateway",
            "no-port" => "HealthReasonNoPort",
            "no-host" => "HealthReasonNoHost",
            _ => null
        };
    }

    /// <summary>
    /// Builds the dot tooltip from a <see cref="HealthState"/>. Always returns a
    /// non-empty string. Format examples:
    ///   <c>"Reachable (42 ms) · 14:32:55"</c>
    ///   <c>"Unreachable · Connection timed out · 14:32:55"</c>
    ///   <c>"Unknown · never"</c>
    /// </summary>
    public static string FormatTooltip(HealthState state, LocalizationManager? localizer)
    {
        string Localize(string key) => localizer?[key] ?? key;

        var status = Localize(StatusKey(state.Status));
        var parts = new List<string> { status };

        if (state.LatencyMs.HasValue)
        {
            parts[0] = $"{status} ({state.LatencyMs.Value} ms)";
        }

        if (!string.IsNullOrEmpty(state.Reason))
        {
            var reasonKey = ReasonKey(state.Reason);
            parts.Add(reasonKey is not null ? Localize(reasonKey) : $"({state.Reason})");
        }

        parts.Add(state.LastCheckUtc > DateTime.MinValue
            ? state.LastCheckUtc.ToLocalTime().ToString("HH:mm:ss")
            : Localize("HealthLastCheckedNever"));

        return string.Join(" · ", parts);
    }
}
