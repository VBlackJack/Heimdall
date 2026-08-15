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

using System.Text.Json;
using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

public sealed class SftpMetadataPreflightTests
{
    [Fact]
    public void Build_ProbesCapabilitiesBeforeTheOtherClasses()
    {
        string command = SftpMetadataPreflight.Build("/srv/app/agent");

        int caps = command.IndexOf("getcap", StringComparison.Ordinal);
        int xattr = command.IndexOf("getfattr", StringComparison.Ordinal);
        int acl = command.IndexOf("getfacl", StringComparison.Ordinal);

        // Severity order, not cost order. Capabilities need CAP_SETFCAP to write back, so that is
        // the class no change of remote tooling can make recoverable; a file carrying several
        // classes must be reported by the one that will not go away.
        Assert.True(caps >= 0 && xattr > caps, "capabilities must be probed before security xattrs");
        Assert.True(acl > xattr, "security xattrs must be probed before ACLs");
    }

    [Fact]
    public void Build_TreatsAMissingToolAsUnknownRatherThanAbsent()
    {
        string command = SftpMetadataPreflight.Build("/srv/app/agent");

        // The whole point of the probe: "getcap is not installed" is not "this file has no
        // capabilities". Each availability test must exit on the tooling status, never fall
        // through to the success path.
        Assert.Equal(3, CountOccurrences(command, "command -v"));
        Assert.Equal(3, CountOccurrences(command, $"else exit {SftpMetadataPreflight.ToolingStatus}; fi"));
    }

    [Fact]
    public void Build_EscapesThePathEverywhereItAppears()
    {
        const string hostile = "/srv/oh'; rm -rf /;'.bin";
        string command = SftpMetadataPreflight.Build(hostile);

        // A path is attacker-influenced data. The payload text does appear, because it is carried
        // inside single quotes where it is inert; what must never appear is the sequence that
        // would close the quoting and splice a new command. Asserting the absence of the raw
        // text would have been the wrong oracle: it is present and harmless.
        Assert.DoesNotContain("oh'; rm", command, StringComparison.Ordinal);

        // Not vacuous: the path really is embedded, it is simply embedded quoted.
        Assert.Contains(".bin", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsBlankPaths()
    {
        Assert.ThrowsAny<ArgumentException>(() => SftpMetadataPreflight.Build(null!));
        Assert.ThrowsAny<ArgumentException>(() => SftpMetadataPreflight.Build(string.Empty));
        Assert.ThrowsAny<ArgumentException>(() => SftpMetadataPreflight.Build("   "));
    }

    [Theory]
    [InlineData(0, SftpMetadataPreflightVerdict.Proceed)]
    [InlineData(SftpMetadataPreflight.NoExistingTargetStatus, SftpMetadataPreflightVerdict.NoExistingTarget)]
    [InlineData(SftpMetadataPreflight.CapabilitiesStatus, SftpMetadataPreflightVerdict.CapabilitiesPresent)]
    [InlineData(SftpMetadataPreflight.SecurityXattrStatus, SftpMetadataPreflightVerdict.SecurityXattrsPresent)]
    [InlineData(SftpMetadataPreflight.AclStatus, SftpMetadataPreflightVerdict.AclPresent)]
    [InlineData(SftpMetadataPreflight.ToolingStatus, SftpMetadataPreflightVerdict.ToolingUnavailable)]
    [InlineData(SftpMetadataPreflight.UnreadableStatus, SftpMetadataPreflightVerdict.MetadataUnreadable)]
    public void Classify_MapsEachDocumentedStatus(int status, SftpMetadataPreflightVerdict expected)
    {
        Assert.Equal(expected, SftpMetadataPreflight.Classify(status));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(127)]
    [InlineData(137)]
    [InlineData(-1)]
    public void Classify_TreatsAnyUndocumentedStatusAsUnreadable(int status)
    {
        // A shell killed by a signal, a busybox that answered something unforeseen, or a status
        // added later without updating the map must never land on Proceed.
        SftpMetadataPreflightVerdict verdict = SftpMetadataPreflight.Classify(status);

        Assert.Equal(SftpMetadataPreflightVerdict.MetadataUnreadable, verdict);
        Assert.False(SftpMetadataPreflight.AllowsReplacement(verdict));
    }

    [Fact]
    public void AllowsReplacement_AdmitsOnlyTheTwoSafeVerdicts()
    {
        // Enumerated over the whole enum rather than a hand-written list, so a verdict added
        // later is covered by this test the day it appears instead of being silently admitted.
        foreach (SftpMetadataPreflightVerdict verdict in Enum.GetValues<SftpMetadataPreflightVerdict>())
        {
            bool expected = verdict is SftpMetadataPreflightVerdict.Proceed
                or SftpMetadataPreflightVerdict.NoExistingTarget;

            Assert.Equal(expected, SftpMetadataPreflight.AllowsReplacement(verdict));
        }
    }

    [Fact]
    public void GetRefusalLocaleKey_CoversEveryRefusingVerdict_AndOnlyThose()
    {
        foreach (SftpMetadataPreflightVerdict verdict in Enum.GetValues<SftpMetadataPreflightVerdict>())
        {
            if (SftpMetadataPreflight.AllowsReplacement(verdict))
            {
                // A verdict that proceeds has nothing to explain; asking for a message is a bug.
                Assert.ThrowsAny<ArgumentException>(
                    () => SftpMetadataPreflight.GetRefusalLocaleKey(verdict));
                continue;
            }

            string key = SftpMetadataPreflight.GetRefusalLocaleKey(verdict);
            Assert.False(string.IsNullOrWhiteSpace(key));
        }
    }

    [Fact]
    public void EveryRefusalKeyExistsInBothLocalesWithTheSamePlaceholder()
    {
        JsonDocument en = ReadLocale("en.json");
        JsonDocument fr = ReadLocale("fr.json");

        foreach (SftpMetadataPreflightVerdict verdict in Enum.GetValues<SftpMetadataPreflightVerdict>())
        {
            if (SftpMetadataPreflight.AllowsReplacement(verdict))
            {
                continue;
            }

            string key = SftpMetadataPreflight.GetRefusalLocaleKey(verdict);

            string english = ReadValue(en, key, "en.json");
            string french = ReadValue(fr, key, "fr.json");

            // One argument is formatted at the call site, the remote path.
            Assert.Equal(1, CountOccurrences(english, "{0}"));
            Assert.Equal(1, CountOccurrences(french, "{0}"));

            // A refusal that does not say what to do next is a dead end for the operator.
            Assert.Contains("privileg", english, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("privil", french, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ReadValue(JsonDocument document, string key, string file)
    {
        Assert.True(
            document.RootElement.TryGetProperty(key, out JsonElement value),
            $"Locale file '{file}' does not define '{key}'.");

        return Assert.IsType<string>(value.GetString());
    }

    private static JsonDocument ReadLocale(string fileName)
        => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FindRepoRoot(), "locales", fileName)));

    private static int CountOccurrences(string value, string fragment)
    {
        int count = 0;
        for (int index = value.IndexOf(fragment, StringComparison.Ordinal);
             index >= 0;
             index = value.IndexOf(fragment, index + fragment.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Heimdall.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
