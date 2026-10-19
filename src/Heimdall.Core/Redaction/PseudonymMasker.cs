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
using System.Text.RegularExpressions;

namespace Heimdall.Core.Redaction;

/// <summary>Replaces IPv4 addresses and host names by stable pseudonyms (<c>ip-1</c>, <c>host-1</c>).</summary>
internal static class PseudonymMasker
{
    private const int OctetCount = 4;

    private const int MaxOctet = 255;

    private const int FullOctet = 255;

    private const char DotSeparator = '.';

    private const uint AllBitsMask = uint.MaxValue;

    private static readonly string[] ExcludedAddresses = ["0.0.0.0", "127.0.0.1", "255.255.255.255"];

    // A name ending in one of these is a file name, not a host.
    private static readonly string[] FileExtensions =
    [
        "dll", "exe", "json", "yaml", "yml", "txt", "log", "cs", "md", "sh", "ps1", "py", "conf", "ini", "xml", "html",
    ];

    internal static string ApplyIpv4(string text, Regex pattern, string ruleId, RedactionRun run) =>
        RedactionApply.Replace(text, pattern, match =>
        {
            Group group = match.Groups[RedactionApply.ValueGroupName];
            if (!IsMaskableAddress(group.ValueSpan))
            {
                return null;
            }

            run.Count(ruleId);
            return Splice(match, group, run.IpPseudonym(group.Value));
        });

    internal static string ApplyHostNames(string text, Regex promptHost, Regex fullyQualified, string ruleId, RedactionRun run)
    {
        string current = RedactionApply.Replace(text, promptHost, match => MaskHost(match, ruleId, run, checkExtension: false));
        return RedactionApply.Replace(current, fullyQualified, match => MaskHost(match, ruleId, run, checkExtension: true));
    }

    private static string? MaskHost(Match match, string ruleId, RedactionRun run, bool checkExtension)
    {
        Group group = match.Groups[RedactionApply.ValueGroupName];
        ReadOnlySpan<char> host = group.ValueSpan;
        if (IsPseudonym(host) || (checkExtension && HasFileExtension(host)))
        {
            return null;
        }

        run.Count(ruleId);
        return Splice(match, group, run.HostPseudonym(group.Value));
    }

    private static string Splice(Match match, Group group, string replacement)
    {
        int relative = group.Index - match.Index;
        ReadOnlySpan<char> whole = match.ValueSpan;
        return string.Concat(whole[..relative], replacement, whole[(relative + group.Length)..]);
    }

    private static bool IsPseudonym(ReadOnlySpan<char> value) =>
        IsNumberedPseudonym(value, RedactionRun.HostPseudonymPrefix) || IsNumberedPseudonym(value, RedactionRun.IpPseudonymPrefix);

    private static bool IsNumberedPseudonym(ReadOnlySpan<char> value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length == prefix.Length)
        {
            return false;
        }

        foreach (char c in value[prefix.Length..])
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasFileExtension(ReadOnlySpan<char> host)
    {
        ReadOnlySpan<char> last = host[(host.LastIndexOf(DotSeparator) + 1)..];
        foreach (string extension in FileExtensions)
        {
            if (last.Equals(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMaskableAddress(ReadOnlySpan<char> address)
    {
        foreach (string excluded in ExcludedAddresses)
        {
            if (address.Equals(excluded, StringComparison.Ordinal))
            {
                return false;
            }
        }

        uint value = 0;
        int octets = 0;
        int firstOctet = 0;
        foreach (Range range in address.Split(DotSeparator))
        {
            if (!int.TryParse(address[range], NumberStyles.None, CultureInfo.InvariantCulture, out int octet) || octet > MaxOctet)
            {
                return false;
            }

            if (octets == 0)
            {
                firstOctet = octet;
            }

            value = (value << 8) | (uint)octet;
            octets++;
        }

        return octets == OctetCount && !IsNetmask(firstOctet, value);
    }

    // 255.x.x.x with contiguous leading ones (255.255.255.0, 255.255.240.0, ...).
    private static bool IsNetmask(int firstOctet, uint value)
    {
        if (firstOctet != FullOctet)
        {
            return false;
        }

        uint inverted = ~value & AllBitsMask;
        return (inverted & (inverted + 1)) == 0;
    }
}
