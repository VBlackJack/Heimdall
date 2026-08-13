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

namespace Heimdall.Core.Import;

/// <summary>
/// Curated subset of Microsoft RDP (.rdp) file keys mapped into a structured shape.
/// Only keys Heimdall consumes are surfaced directly; everything else is retained
/// in <see cref="UnknownKeys"/> for diagnostics.
/// </summary>
public sealed class RdpFileSchema
{
    public string? FullAddress { get; init; }

    public string? AlternateFullAddress { get; init; }

    public string? Username { get; init; }

    public int? AudioMode { get; init; }

    public bool? RedirectClipboard { get; init; }

    public bool? RedirectPrinters { get; init; }

    public bool? RedirectSmartCards { get; init; }

    public string? DrivesToRedirect { get; init; }

    public int? ScreenModeId { get; init; }

    public bool? UseMultiMon { get; init; }

    public int? DesktopWidth { get; init; }

    public int? DesktopHeight { get; init; }

    public int? SessionBpp { get; init; }

    public int? AuthenticationLevel { get; init; }

    /// <summary>
    /// Value of <c>enablecredsspsupport:i:</c>, the only field carrying NLA/CredSSP state.
    /// <c>authentication level</c> describes server authentication and must never be read as NLA.
    /// </summary>
    public int? EnableCredSspSupport { get; init; }

    public string? GatewayHostname { get; init; }

    public int? GatewayUsageMethod { get; init; }

    public bool HasPasswordBlob { get; init; }

    public IReadOnlyDictionary<string, string> UnknownKeys { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
