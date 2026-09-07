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

using Heimdall.Core.Updates;

namespace Heimdall.App.Services;

/// <summary>
/// The locale key that says what stopped an update check.
/// </summary>
/// <remarks>
/// One place, kept beside <see cref="UpdateInstallOutcomeText"/>, which does the same job for
/// the install. Only the settings page shows any of these today: the startup banner reports a
/// failed check by showing nothing, deliberately, so as not to interrupt a launch. The mapping
/// lives here rather than in the view model so that the day a second surface wants to say what
/// happened, it says the same thing.
/// </remarks>
public static class UpdateCheckFailureText
{
    /// <summary>
    /// Used when the cause is unknown, and as the wording every other key improves on.
    /// </summary>
    public const string UnknownCauseKey = "SettingsUpdateStatusFailed";

    /// <summary>
    /// The locale key for <paramref name="failure"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="UpdateCheckFailure.None"/> maps to the generic key rather than to null. A
    /// caller only asks this question having already decided the check failed, so a
    /// <c>None</c> here is a plumbing mistake, and answering it with the old sentence keeps
    /// that mistake from becoming an empty status line in front of the user.
    /// </remarks>
    public static string StatusKey(UpdateCheckFailure failure) => failure switch
    {
        UpdateCheckFailure.NetworkUnreachable => "SettingsUpdateStatusFailedNetworkUnreachable",
        UpdateCheckFailure.SecureChannelFailed => "SettingsUpdateStatusFailedSecureChannel",
        UpdateCheckFailure.RateLimited => "SettingsUpdateStatusFailedRateLimited",
        UpdateCheckFailure.ProxyRefused => "SettingsUpdateStatusFailedProxyRefused",
        UpdateCheckFailure.SourceNotFound => "SettingsUpdateStatusFailedSourceNotFound",
        UpdateCheckFailure.MalformedResponse => "SettingsUpdateStatusFailedMalformedResponse",
        UpdateCheckFailure.AccessDenied => "SettingsUpdateStatusFailedAccessDenied",
        UpdateCheckFailure.SourceUnavailable => "SettingsUpdateStatusFailedSourceUnavailable",
        UpdateCheckFailure.TimedOut => "SettingsUpdateStatusFailedTimedOut",
        _ => UnknownCauseKey,
    };

    /// <summary>
    /// The locale key for a rate limit whose reset time the source volunteered, or null when
    /// there is nothing more to say than <see cref="StatusKey"/> already says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A separate key rather than an appended clause, because the two sentences are not the
    /// same sentence in every language. Null when the source said nothing: a waiting time is
    /// never invented, since a wrong one is worse than none - it teaches the user to ignore
    /// the number.
    /// </para>
    /// <para>
    /// One minute gets its own sentence. <see cref="WholeMinutesToWait"/> has a floor of one,
    /// so a plural template guarantees "try again in about 1 minutes" for every reset under a
    /// minute - not a corner case, but the case the floor exists to produce. Both languages
    /// need the singular written out rather than derived, since neither builds it the same
    /// way from the number.
    /// </para>
    /// </remarks>
    public static string? RetryHintKey(UpdateCheckFailure failure, TimeSpan? retryAfter)
    {
        if (failure != UpdateCheckFailure.RateLimited
            || retryAfter is not { } wait
            || wait <= TimeSpan.Zero)
        {
            return null;
        }

        return WholeMinutesToWait(wait) == 1
            ? "SettingsUpdateStatusFailedRateLimitedUntilAMinute"
            : "SettingsUpdateStatusFailedRateLimitedUntil";
    }

    /// <summary>
    /// Rounds a waiting time up to whole minutes, with a floor of one.
    /// </summary>
    /// <remarks>
    /// Up rather than to nearest: telling somebody to wait 6 minutes when 6 minutes 40
    /// seconds remain earns a second refusal. The floor keeps "try again in 0 minutes" off
    /// the screen for a reset that is seconds away.
    /// </remarks>
    public static int WholeMinutesToWait(TimeSpan retryAfter) =>
        Math.Max(1, (int)Math.Ceiling(retryAfter.TotalMinutes));
}
