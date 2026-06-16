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
using System.Reflection;
using Heimdall.Core.Updates;

namespace Heimdall.App.Services;

/// <summary>
/// Default <see cref="IAppVersionProvider"/>. Centralizes the version logic that
/// was previously inlined in the About panel: reading AssemblyInformationalVersion
/// and deriving the build date from the canonical "YYYY.MMDDxx" form.
/// </summary>
public sealed class AppVersionProvider : IAppVersionProvider
{
    private const string UnknownVersion = "unknown";

    // Slice positions inside the 6-digit "MMDDxx" second component.
    private const int MonthStart = 0;
    private const int DayStart = 2;
    private const int DatePartLength = 2;
    private const int MinDateComponentLength = 6;

    public string InformationalVersion { get; }

    public HeimdallVersion? Current { get; }

    public DateOnly? BuildDate { get; }

    /// <summary>Reads the informational version from the entry assembly.</summary>
    public AppVersionProvider()
        : this(ReadInformationalVersion())
    {
    }

    /// <summary>Test seam: build from an explicit informational-version string.</summary>
    public AppVersionProvider(string informationalVersion)
    {
        ArgumentNullException.ThrowIfNull(informationalVersion);
        InformationalVersion = informationalVersion;
        Current = HeimdallVersion.TryParse(informationalVersion, out var version) ? version : null;
        BuildDate = DeriveBuildDate(Current);
    }

    private static string ReadInformationalVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? UnknownVersion;
    }

    private static DateOnly? DeriveBuildDate(HeimdallVersion? version)
    {
        if (version is null)
        {
            return null;
        }

        // The cleaned canonical form is "YYYY.MMDDxx" with the second component zero-padded.
        var segments = version.Value.ToString().Split('.');
        if (segments.Length < 2 || segments[1].Length < MinDateComponentLength)
        {
            return null;
        }

        var datePart = segments[1];
        if (!int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(datePart.Substring(MonthStart, DatePartLength), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(datePart.Substring(DayStart, DatePartLength), NumberStyles.None, CultureInfo.InvariantCulture, out var day))
        {
            return null;
        }

        try
        {
            return new DateOnly(year, month, day);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
