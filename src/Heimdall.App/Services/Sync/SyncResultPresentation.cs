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

namespace Heimdall.App.Services.Sync;

/// <summary>
/// The dialog flavor to surface for a Git-sync result.
/// </summary>
public enum SyncDialogKind
{
    /// <summary>Informational (non-error) outcome.</summary>
    Info,

    /// <summary>Successful but partial outcome (warnings present).</summary>
    Warning,

    /// <summary>Failed outcome.</summary>
    Error
}

/// <summary>
/// Pure presentation of a Git-sync result: the dialog kind plus the already
/// resolved title and body text to display.
/// </summary>
public sealed record SyncResultPresentation(SyncDialogKind Kind, string Title, string Body);
