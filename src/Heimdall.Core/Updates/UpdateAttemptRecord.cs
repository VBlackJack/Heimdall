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
/// What the application intended to become, written to disk before it hands control to
/// the detached relauncher and exits.
/// </summary>
/// <remarks>
/// A file rather than a command-line argument, and that choice is load-bearing. The
/// relauncher can fail to start the application at all - its relaunch sits in a nested
/// try whose catch only writes a warning - so an argument would be lost in exactly the
/// case that most needs reporting. A file is still there when someone starts Heimdall by
/// hand an hour later.
/// </remarks>
/// <param name="SchemaVersion">
/// Guards against reading a record written by a future version with different meaning.
/// An unrecognized value is treated as no record at all.
/// </param>
/// <param name="AttemptedVersion">
/// The version the update was expected to install, as its cleaned string form. Compared
/// against the running version at the next startup; an empty string is tolerated rather
/// than assumed away, because the version type renders an unparsed value as empty.
/// </param>
/// <param name="StartedUtc">
/// When the attempt began. Used only to discard a record too old to be about this
/// launch, never to measure anything.
/// </param>
public sealed record UpdateAttemptRecord(
    int SchemaVersion,
    string AttemptedVersion,
    DateTimeOffset StartedUtc)
{
    /// <summary>The schema this build writes and is willing to read.</summary>
    public const int CurrentSchemaVersion = 1;
}
