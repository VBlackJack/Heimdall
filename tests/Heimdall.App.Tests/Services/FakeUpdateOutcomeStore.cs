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

namespace Heimdall.App.Tests;

/// <summary>
/// In-memory <see cref="IUpdateOutcomeStore"/> that records the order of the calls made
/// to it.
/// </summary>
/// <remarks>
/// The order is the point. The attempt record has to be written before the relauncher is
/// launched, because the relauncher can be killed the instant it starts and this process
/// is about to exit. A counter cannot express that; a sequence can - and observing a
/// sequence is also what avoids the counter-versus-background-load race this codebase has
/// been caught by before.
/// </remarks>
internal sealed class FakeUpdateOutcomeStore : IUpdateOutcomeStore
{
    public const string WriteAttemptCall = "WriteAttempt";

    public const string ClearCall = "Clear";

    public const string TakeCall = "TryTakePending";

    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls => _calls;

    public string? LastAttemptedVersion { get; private set; }

    public UpdateAttemptRecord? Pending { get; set; }

    public UpdateFailureRecord? PendingFailure { get; set; }

    /// <summary>Lets a collaborator record itself into the same sequence.</summary>
    public void RecordExternalCall(string name) => _calls.Add(name);

    public void WriteAttempt(string attemptedVersion)
    {
        LastAttemptedVersion = attemptedVersion;
        _calls.Add(WriteAttemptCall);
    }

    public void Clear()
    {
        Pending = null;
        _calls.Add(ClearCall);
    }

    public PendingUpdateOutcome? TryTakePending()
    {
        _calls.Add(TakeCall);
        UpdateAttemptRecord? pending = Pending;
        UpdateFailureRecord? failure = PendingFailure;
        Pending = null;
        PendingFailure = null;
        return pending is null ? null : new PendingUpdateOutcome(pending, failure);
    }
}
