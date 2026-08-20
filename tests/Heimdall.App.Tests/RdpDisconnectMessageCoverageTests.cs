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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Heimdall.Rdp.ActiveX;

namespace Heimdall.App.Tests;

/// <summary>
/// Ties the RDP disconnect decoders to the messages they are asking to be shown.
/// </summary>
/// <remarks>
/// <para>The decoders return a suffix and the diagnostic factory prepends <c>RdpDisconnect</c> to
/// it. Those are two files in two projects, and nothing but this test says the second one has an
/// entry for every answer the first one can give. A missing entry does not fail a build and does
/// not fail any other test: it reaches the user as an empty overlay, at the exact moment their
/// session just died and they most need to be told why.</para>
/// <para>The sweep is over the decoders themselves rather than a list written here, so a decoder
/// arm added later without its two locale entries fails this test rather than shipping silent.
/// </para>
/// </remarks>
public sealed class RdpDisconnectMessageCoverageTests
{
    // The composition this test exists to protect, kept identical to the factory's.
    private const string KeyPrefix = "RdpDisconnect";

    // The two family blocks, sampled rather than swept: each is 65536 values wide, and what is
    // being verified is that the family answer has a message, not that every member reaches it.
    // The bounds themselves are asserted in RdpActiveXHostTests.
    private static readonly int[] ExtendedProbes =
    [
        0x0300_0015, 0x0300_0003, 0x0300_0005, 0x0300_000C, 0x0300_0032, 0x0300_0033,
        0x0300_0000, 0x0300_4242, 0x0300_FFFF,
        0x0200_0000, 0x0200_4242, 0x0200_FFFF,
        4, 9, 265, 266, 267, 768,
    ];

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void EveryPrimaryReasonTheDecoderNames_HasAMessage(string language)
    {
        IReadOnlyDictionary<string, JsonElement> locale = ReadLocale(language);
        List<string> missing = [];

        // The primary channel is a two-byte encoding, so the whole space is small enough to sweep
        // exhaustively rather than sampled: no arm can hide from this.
        for (int reason = 0; reason <= ushort.MaxValue; reason++)
        {
            string? suffix = RdpActiveXHost.GetDisconnectReasonKey(reason);
            if (suffix is null)
            {
                continue;
            }

            string key = KeyPrefix + suffix;
            if (!locale.ContainsKey(key))
            {
                missing.Add($"reason {reason} decodes to '{suffix}' but {language}.json has no '{key}'");
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing.Distinct()));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void EveryExtendedReasonTheDecoderNames_HasAMessage(string language)
    {
        IReadOnlyDictionary<string, JsonElement> locale = ReadLocale(language);
        List<string> missing = [];

        foreach (int extendedReason in ExtendedProbes)
        {
            string? suffix = RdpActiveXHost.GetExtendedDisconnectReasonKey(extendedReason);
            Assert.True(
                suffix is not null,
                $"extended reason 0x{extendedReason:X8} is probed here because it is expected to "
                    + "decode, and it no longer does");

            string key = KeyPrefix + suffix;
            if (!locale.ContainsKey(key))
            {
                missing.Add(
                    $"extended reason 0x{extendedReason:X8} decodes to '{suffix}' but "
                        + $"{language}.json has no '{key}'");
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing.Distinct()));
    }

    // Guarding the guard: the sweep above proves nothing if the fallback key it is written against
    // is itself absent, and it would not notice, because an undecoded reason is skipped rather
    // than reported.
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void TheFallbackMessageExists(string language)
    {
        Assert.True(ReadLocale(language).ContainsKey(KeyPrefix + "UnknownCode"));
    }

    // And guarding it once more: a locale file that failed to load would read as an empty
    // dictionary, which turns both sweeps into assertions about nothing.
    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void TheLocaleFileIsNotEmpty(string language)
    {
        Assert.True(ReadLocale(language).Count > 1000);
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadLocale(string language)
    {
        string path = Path.Combine(FindRepoRoot(), "locales", $"{language}.json");
        Assert.True(File.Exists(path), $"Locale file not found: {path}");

        Dictionary<string, JsonElement>? parsed =
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path));

        Assert.NotNull(parsed);
        return parsed;
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from test binary directory: {AppContext.BaseDirectory}");
    }
}
