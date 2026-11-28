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

using Heimdall.Core.Localization;
using Heimdall.Ssh.Agents;

namespace Heimdall.App.Services;

/// <summary>
/// Shared localizer for <see cref="SshAgentOperationResult"/> failure codes across SSH tools.
/// </summary>
internal static class SshAgentErrorLocalizer
{
    public static string Format(SshAgentOperationResult result, LocalizationManager? localizer)
    {
        ArgumentNullException.ThrowIfNull(result);

        string L(string key) => localizer?[key] ?? key;

        return result.ErrorCode switch
        {
            SshAgentOperationErrorCode.NoWritableAgent => L("ToolSshAgentErrNoWritableAgent"),
            SshAgentOperationErrorCode.LifetimeNotSupported => L("ToolSshAgentErrLifetimeNotSupported"),
            SshAgentOperationErrorCode.KeyRefused => L("ToolSshAgentErrKeyRefused"),
            SshAgentOperationErrorCode.RemoveRefused => L("ToolSshAgentErrRemoveRefused"),
            SshAgentOperationErrorCode.KeyNotFound => L("ToolSshAgentErrKeyNotFound"),
            _ => result.ErrorMessage ?? string.Empty
        };
    }
}
