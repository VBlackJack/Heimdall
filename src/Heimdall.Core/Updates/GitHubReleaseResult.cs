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

namespace Heimdall.Core.Updates;

/// <summary>
/// What a release lookup produced: a release, or the reason there is not one.
/// </summary>
/// <remarks>
/// <para>
/// This type replaces a nullable <c>GitHubRelease</c> on the client seam, and the reason it
/// is a type rather than an out parameter is the invariant in its constructor. A test double
/// that keeps answering "nothing, and no reason" would leave every caller's failure handling
/// unexercised while every existing test stayed green; here that value cannot be built.
/// </para>
/// <para>
/// <see cref="RetryAfter"/> is only ever a hint the source volunteered. It is never invented,
/// never defaulted to a guess, and a caller that gets null must say less rather than say a
/// number nobody promised.
/// </para>
/// </remarks>
public sealed record GitHubReleaseResult
{
    /// <param name="release">The release, when there is one.</param>
    /// <param name="failure">Why there is not one, when there is not.</param>
    /// <param name="retryAfter">How long the source asked the caller to wait, if it said.</param>
    /// <exception cref="ArgumentException">
    /// A release with a failure, or neither a release nor a failure. Both are states this
    /// type exists to make unrepresentable.
    /// </exception>
    public GitHubReleaseResult(
        GitHubRelease? release,
        UpdateCheckFailure failure,
        TimeSpan? retryAfter = null)
    {
        if (release is null && failure == UpdateCheckFailure.None)
        {
            throw new ArgumentException(
                "A lookup that produced no release must say why.", nameof(failure));
        }

        if (release is not null && failure != UpdateCheckFailure.None)
        {
            throw new ArgumentException(
                "A lookup that produced a release did not fail.", nameof(failure));
        }

        Release = release;
        Failure = failure;
        RetryAfter = retryAfter;
    }

    /// <summary>The release, or null when the lookup failed.</summary>
    public GitHubRelease? Release { get; }

    /// <summary>Why the lookup failed, or <see cref="UpdateCheckFailure.None"/>.</summary>
    public UpdateCheckFailure Failure { get; }

    /// <summary>How long the source asked the caller to wait, when it volunteered one.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>A lookup that produced a release.</summary>
    public static GitHubReleaseResult Succeeded(GitHubRelease release) =>
        new(release ?? throw new ArgumentNullException(nameof(release)), UpdateCheckFailure.None);

    /// <summary>A lookup that produced a reason instead.</summary>
    public static GitHubReleaseResult Failed(UpdateCheckFailure failure, TimeSpan? retryAfter = null) =>
        new(null, failure, retryAfter);
}
