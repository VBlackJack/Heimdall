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

using Heimdall.App.Localization;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Identifies the transport selected for an SSH connection attempt.
/// </summary>
internal enum SshResolvedPath
{
    /// <summary>The in-process SSH.NET transport.</summary>
    Direct,

    /// <summary>An external PuTTY process.</summary>
    ExternalPutty,

    /// <summary>An interactive Plink process connected through pipes.</summary>
    PlinkPipe
}

/// <summary>
/// Identifies requested SSH capabilities that the resolved path cannot provide.
/// </summary>
[Flags]
internal enum SshUnavailableCapability
{
    /// <summary>X11 forwarding is unavailable.</summary>
    X11Forwarding = 1,

    /// <summary>SSH compression is unavailable.</summary>
    Compression = 2
}

/// <summary>
/// Describes the capabilities unavailable on a resolved SSH path.
/// </summary>
/// <param name="UnavailableCapabilities">The requested capabilities that cannot be honored.</param>
internal sealed record SshCapabilityNotice(SshUnavailableCapability UnavailableCapabilities)
{
    /// <summary>
    /// Gets an English capability list suitable for diagnostic logging.
    /// </summary>
    public string LogDescription => UnavailableCapabilities switch
    {
        SshUnavailableCapability.X11Forwarding => "X11 forwarding",
        SshUnavailableCapability.Compression => "compression",
        SshUnavailableCapability.X11Forwarding | SshUnavailableCapability.Compression =>
            "X11 forwarding, compression",
        _ => throw new InvalidOperationException("The capability notice must identify at least one unavailable capability.")
    };

    /// <summary>
    /// Gets the localized status key matching the unavailable capability set.
    /// </summary>
    public string StatusLocalizationKey => UnavailableCapabilities switch
    {
        SshUnavailableCapability.X11Forwarding => SshLocalizationKeys.StatusSshDirectX11Unavailable,
        SshUnavailableCapability.Compression => SshLocalizationKeys.StatusSshDirectCompressionUnavailable,
        SshUnavailableCapability.X11Forwarding | SshUnavailableCapability.Compression =>
            SshLocalizationKeys.StatusSshDirectX11AndCompressionUnavailable,
        _ => throw new InvalidOperationException("The capability notice must identify at least one unavailable capability.")
    };
}

/// <summary>
/// Resolves forwarding capability notices without opening a session or performing I/O.
/// </summary>
internal static class SshCapabilityScope
{
    /// <summary>
    /// Returns whether the resolved path can honor X11 forwarding and compression requests.
    /// </summary>
    public static bool SupportsForwarding(SshResolvedPath path)
    {
        return path switch
        {
            SshResolvedPath.Direct => false,
            SshResolvedPath.ExternalPutty => true,
            SshResolvedPath.PlinkPipe => true,
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, "Unknown SSH resolved path.")
        };
    }

    /// <summary>
    /// Returns one notice describing every requested capability unavailable on the resolved path,
    /// or <see langword="null"/> when the path can honor the request.
    /// </summary>
    public static SshCapabilityNotice? Evaluate(
        SshResolvedPath path,
        bool x11Forwarding,
        bool compression)
    {
        if (SupportsForwarding(path))
        {
            return null;
        }

        SshUnavailableCapability unavailableCapabilities = 0;
        if (x11Forwarding)
        {
            unavailableCapabilities |= SshUnavailableCapability.X11Forwarding;
        }

        if (compression)
        {
            unavailableCapabilities |= SshUnavailableCapability.Compression;
        }

        return unavailableCapabilities == 0
            ? null
            : new SshCapabilityNotice(unavailableCapabilities);
    }
}
