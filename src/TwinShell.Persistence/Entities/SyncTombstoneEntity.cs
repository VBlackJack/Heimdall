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

namespace TwinShell.Persistence.Entities;

/// <summary>
/// A shared entry the operator deleted, kept so the next sync export removes it from the sync
/// folder and tells the other installs.
/// </summary>
public sealed class SyncTombstoneEntity
{
    /// <summary>The public id of the deleted action, template, runbook or category.</summary>
    public Guid PublicId { get; set; }

    /// <summary>When it was deleted, in UTC.</summary>
    public DateTime DeletedAt { get; set; }
}
