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

namespace Heimdall.App.Services;

/// <summary>
/// Abstraction for a host control that can receive a command string and inject
/// it into its underlying terminal session. Implemented by embedded terminal
/// views (e.g. <c>EmbeddedSshView</c>); non-terminal panes (RDP, SFTP) do not
/// implement it, which lets callers detect whether a session can accept a
/// command before falling back to an alternative such as the clipboard.
/// </summary>
public interface ITerminalCommandSink
{
    /// <summary>
    /// Writes the given command to the terminal session. The command is
    /// forwarded as-is; the caller is responsible for validating it.
    /// </summary>
    /// <param name="command">The command string to inject.</param>
    void WriteCommand(string command);
}
