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

namespace Heimdall.Rdp;

/// <summary>
/// What a completed <c>TERMSRV/&lt;host&gt;</c> staging attempt did with the Windows store.
/// </summary>
public enum DomainCredentialWriteOutcome
{
    /// <summary>The entry now carries this launch's account and ownership marker.</summary>
    Written,

    /// <summary>
    /// An entry was left in place: one saved outside Heimdall, or one a launch of this process
    /// still in flight wrote for the same account. The client signs in with what is stored.
    /// </summary>
    ExistingEntryKept,

    /// <summary>
    /// A launch of this process still in flight holds the entry for another account, or for
    /// an account that could not be read. Launching now would sign in with that account, so
    /// the launch must be refused until the earlier one has read its entry.
    /// </summary>
    LaunchInFlightForAnotherAccount,
}
