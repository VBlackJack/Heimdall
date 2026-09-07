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
/// Reference to a newer release that cannot be installed automatically.
/// </summary>
public sealed record ReleaseRef(HeimdallVersion Version, string TagName, string HtmlUrl);

/// <summary>
/// Result of an update check: installable updates use Update; non-installable newer releases use Release.
/// </summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update, ReleaseRef? Release = null)
{
    /// <summary>
    /// Why the check failed, when <see cref="Status"/> is
    /// <see cref="UpdateCheckStatus.CheckFailed"/>.
    /// </summary>
    /// <remarks>
    /// Carried beside the status rather than folded into it: the status says what a caller
    /// should do, and adding a status per cause would have forced every caller to re-decide a
    /// question it already answered. It does NOT follow that every cause deserves the same
    /// handling - the startup banner reads this to decide whether to back off, because a
    /// spent quota is the one cause that retrying makes worse.
    /// </remarks>
    public UpdateCheckFailure Failure { get; init; }

    /// <summary>How long the source asked the caller to wait, when it volunteered one.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// A check that failed, with the reason it failed.
    /// </summary>
    /// <remarks>
    /// <see cref="GitHubReleaseResult"/> makes "no release and no reason" unrepresentable at
    /// the client seam, which is the cheap half. This is the seam the finding is actually
    /// about, and a plain <c>init</c> property cannot be validated against a positional
    /// parameter, so the guarantee is a factory plus a guard test rather than a constructor
    /// throw. Every production construction of a failed check goes through here.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="failure"/> is <see cref="UpdateCheckFailure.None"/>, which would be a
    /// failed check that declines to say why - exactly the state A-17 records.
    /// </exception>
    public static UpdateCheckResult Failed(UpdateCheckFailure failure, TimeSpan? retryAfter = null)
    {
        if (failure == UpdateCheckFailure.None)
        {
            throw new ArgumentException(
                "A check that failed must say why.", nameof(failure));
        }

        return new UpdateCheckResult(UpdateCheckStatus.CheckFailed, null)
        {
            Failure = failure,
            RetryAfter = retryAfter,
        };
    }
}
