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

namespace Heimdall.App.ViewModels.CommandLibrary;

/// <summary>
/// Where one step of a runbook run stands. Each value says what was done with the command,
/// never how it ended: a terminal returns no exit code.
/// </summary>
public enum RunbookStepStatus
{
    /// <summary>Not handled yet.</summary>
    Pending,

    /// <summary>Submitted to the terminal.</summary>
    Sent,

    /// <summary>Typed at the prompt without being submitted; the operator runs it.</summary>
    Inserted,

    /// <summary>Copied to the clipboard, for a tab with no terminal to send to.</summary>
    Copied,

    /// <summary>Passed over by the operator.</summary>
    Skipped,

    /// <summary>The terminal refused the text; the step can be sent again or skipped.</summary>
    FailedToSend
}
