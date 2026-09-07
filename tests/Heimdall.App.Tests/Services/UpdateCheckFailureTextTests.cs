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

using System.IO;
using System.Text.Json;
using FluentAssertions;
using Heimdall.App.Services;
using Heimdall.Core.Updates;

namespace Heimdall.App.Tests.Services;

/// <summary>
/// Every cause an update check can report has a sentence, in both languages.
/// </summary>
/// <remarks>
/// A-17 is only worth anything if the causes reach the user. A new member of
/// <see cref="UpdateCheckFailure"/> that nobody adds a key for would silently fall back to
/// "Update check failed. See the log for details." - the exact sentence the finding is
/// about - and no other test in the repository would notice.
/// </remarks>
public sealed class UpdateCheckFailureTextTests
{
    public static TheoryData<UpdateCheckFailure> AllCauses()
    {
        var data = new TheoryData<UpdateCheckFailure>();
        foreach (UpdateCheckFailure cause in Enum.GetValues<UpdateCheckFailure>())
        {
            data.Add(cause);
        }

        return data;
    }

    [Fact]
    public void TheCauseSetIsNotEmpty()
    {
        // A theory fed by an empty set skips every case and reports green.
        Enum.GetValues<UpdateCheckFailure>().Should().HaveCountGreaterThan(1);
    }

    [Theory]
    [MemberData(nameof(AllCauses))]
    public void StatusKey_EveryCause_ResolvesInBothLocales(UpdateCheckFailure cause)
    {
        string key = UpdateCheckFailureText.StatusKey(cause);

        key.Should().NotBeNullOrWhiteSpace();
        LocaleKeys("en.json").Should().Contain(key, because: $"{cause} has to be sayable in English");
        LocaleKeys("fr.json").Should().Contain(key, because: $"{cause} has to be sayable in French");
    }

    /// <summary>
    /// Each named cause gets its own sentence, not a shared one.
    /// </summary>
    /// <remarks>
    /// The failure this catches is a switch arm copied and not edited. Every key would exist,
    /// every locale would resolve, and two causes calling for opposite responses would print
    /// the same advice - which is A-17 again, one layer up.
    /// </remarks>
    [Fact]
    public void StatusKey_NamedCauses_DoNotShareASentence()
    {
        var keys = Enum.GetValues<UpdateCheckFailure>()
            .Where(cause => cause != UpdateCheckFailure.None)
            .Select(UpdateCheckFailureText.StatusKey)
            .ToList();

        keys.Should().OnlyHaveUniqueItems();
        keys.Should().NotContain(
            UpdateCheckFailureText.UnknownCauseKey,
            because: "a named cause that falls back to the generic sentence is an unhandled arm");
    }

    /// <summary>
    /// Each cause is pinned to its own sentence, by name.
    /// </summary>
    /// <remarks>
    /// Uniqueness is a property of the collection and does not say which sentence goes with
    /// which cause: swap two arms of the switch and every key stays unique, both catalogues
    /// still resolve, and a timed-out check tells the user they are offline. This table is
    /// the only thing in the repository that ties a cause to its wording.
    /// </remarks>
    [Theory]
    [InlineData(UpdateCheckFailure.NetworkUnreachable, "SettingsUpdateStatusFailedNetworkUnreachable")]
    [InlineData(UpdateCheckFailure.SecureChannelFailed, "SettingsUpdateStatusFailedSecureChannel")]
    [InlineData(UpdateCheckFailure.RateLimited, "SettingsUpdateStatusFailedRateLimited")]
    [InlineData(UpdateCheckFailure.ProxyRefused, "SettingsUpdateStatusFailedProxyRefused")]
    [InlineData(UpdateCheckFailure.SourceNotFound, "SettingsUpdateStatusFailedSourceNotFound")]
    [InlineData(UpdateCheckFailure.MalformedResponse, "SettingsUpdateStatusFailedMalformedResponse")]
    [InlineData(UpdateCheckFailure.AccessDenied, "SettingsUpdateStatusFailedAccessDenied")]
    [InlineData(UpdateCheckFailure.SourceUnavailable, "SettingsUpdateStatusFailedSourceUnavailable")]
    [InlineData(UpdateCheckFailure.TimedOut, "SettingsUpdateStatusFailedTimedOut")]
    public void StatusKey_EachCause_HasItsOwnNamedSentence(UpdateCheckFailure cause, string expected)
    {
        UpdateCheckFailureText.StatusKey(cause).Should().Be(expected);
    }

    /// <summary>
    /// Every cause the table pins is a cause that exists, and every cause is pinned.
    /// </summary>
    /// <remarks>
    /// The table above is written by hand, so it can fall behind the enum without failing:
    /// a new member would simply have no row. This counts them.
    /// </remarks>
    [Fact]
    public void TheSentenceTable_CoversEveryNamedCause()
    {
        var named = Enum.GetValues<UpdateCheckFailure>()
            .Where(cause => cause != UpdateCheckFailure.None)
            .ToList();

        named.Should().HaveCount(9, because: "a cause added without a row in the table above "
            + "would be shown the generic sentence this whole change exists to replace");
    }

    /// <summary>
    /// The sentences themselves are present and distinct, in both languages.
    /// </summary>
    /// <remarks>
    /// The key-set tests check that the NAME appears in the catalogue. An empty value, or two
    /// causes given the same wording by a copied line, passes all of them and shows the user
    /// nothing or the wrong thing. This reads the values.
    /// </remarks>
    [Theory]
    [InlineData("en.json")]
    [InlineData("fr.json")]
    public void EveryCauseSentence_IsWrittenAndDistinct(string fileName)
    {
        var values = Enum.GetValues<UpdateCheckFailure>()
            .Where(cause => cause != UpdateCheckFailure.None)
            .Select(UpdateCheckFailureText.StatusKey)
            .Select(key => LocaleValues(fileName)[key])
            .ToList();

        values.Should().OnlyContain(v => !string.IsNullOrWhiteSpace(v));
        values.Should().OnlyHaveUniqueItems(
            because: "two causes sharing one sentence is the defect A-17 records, one layer up");
    }

    [Fact]
    public void StatusKey_None_IsTheGenericSentence()
    {
        // Asked only after the caller decided the check failed, so a None here is a plumbing
        // mistake. It must not become an empty status line in front of the user.
        UpdateCheckFailureText.StatusKey(UpdateCheckFailure.None)
            .Should().Be(UpdateCheckFailureText.UnknownCauseKey);
    }

    [Fact]
    public void RetryHintKey_OnlyForARateLimitThatCameWithAWait()
    {
        UpdateCheckFailureText.RetryHintKey(UpdateCheckFailure.RateLimited, TimeSpan.FromMinutes(5))
            .Should().NotBeNull();

        // No wait volunteered, so nothing extra is said. A duration is never invented: a
        // number that turns out wrong teaches the user to ignore every number afterwards.
        UpdateCheckFailureText.RetryHintKey(UpdateCheckFailure.RateLimited, null).Should().BeNull();
        UpdateCheckFailureText.RetryHintKey(UpdateCheckFailure.RateLimited, TimeSpan.Zero).Should().BeNull();
        UpdateCheckFailureText.RetryHintKey(UpdateCheckFailure.RateLimited, TimeSpan.FromSeconds(-30))
            .Should().BeNull();

        // Only rate limiting has a wait worth quoting; the others are not time-bounded.
        UpdateCheckFailureText.RetryHintKey(UpdateCheckFailure.NetworkUnreachable, TimeSpan.FromMinutes(5))
            .Should().BeNull();
    }

    [Fact]
    public void RetryHintKey_ResolvesInBothLocales()
    {
        string? key = UpdateCheckFailureText.RetryHintKey(
            UpdateCheckFailure.RateLimited, TimeSpan.FromMinutes(5));

        key.Should().NotBeNull();
        LocaleKeys("en.json").Should().Contain(key!);
        LocaleKeys("fr.json").Should().Contain(key!);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(59, 1)]
    [InlineData(60, 1)]
    [InlineData(61, 2)]
    [InlineData(1800, 30)]
    public void WholeMinutesToWait_RoundsUpAndNeverSaysZero(int seconds, int expected)
    {
        // Up, not to nearest: "try again in 6 minutes" with 6 minutes 40 seconds left earns
        // the user a second refusal. And a reset seconds away must not read as "0 minutes".
        UpdateCheckFailureText.WholeMinutesToWait(TimeSpan.FromSeconds(seconds))
            .Should().Be(expected);
    }

    /// <summary>
    /// A rate limit under a minute gets the singular sentence.
    /// </summary>
    /// <remarks>
    /// <see cref="UpdateCheckFailureText.WholeMinutesToWait"/> has a floor of one, so the
    /// plural template guarantees "try again in about 1 minutes" for every reset under a
    /// minute. That is not a corner case: the floor exists to produce exactly that number.
    /// </remarks>
    [Fact]
    public void RetryHintKey_AWaitOfOneMinute_UsesTheSingularSentence()
    {
        string? singular = UpdateCheckFailureText.RetryHintKey(
            UpdateCheckFailure.RateLimited, TimeSpan.FromSeconds(20));
        string? plural = UpdateCheckFailureText.RetryHintKey(
            UpdateCheckFailure.RateLimited, TimeSpan.FromMinutes(20));

        singular.Should().NotBe(plural);
        LocaleValues("en.json")[singular!].Should().NotContain("{0}");
        LocaleValues("fr.json")[singular!].Should().NotContain("{0}");
        LocaleValues("en.json")[plural!].Should().Contain("{0}");
        LocaleValues("fr.json")[plural!].Should().Contain("{0}");
    }

    private static Dictionary<string, string> LocaleValues(string fileName)
    {
        string path = Path.Combine(RepoRoot(), "locales", fileName);
        File.Exists(path).Should().BeTrue(because: $"the locale file must be found at {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    private static HashSet<string> LocaleKeys(string fileName)
    {
        string path = Path.Combine(RepoRoot(), "locales", fileName);
        File.Exists(path).Should().BeTrue(because: $"the locale file must be found at {path}");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "locales")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull(because: "the walk up from the test output must reach the repository");
        return directory!.FullName;
    }
}
