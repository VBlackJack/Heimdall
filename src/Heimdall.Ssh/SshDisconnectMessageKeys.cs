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

namespace Heimdall.Ssh;

/// <summary>
/// Locale keys of the disconnect details this layer composes itself. The message
/// text lives in the application's locale catalogues; the SSH layer only names it,
/// the way <see cref="SshConnectionProbe"/> names its probe messages.
/// </summary>
public static class SshDisconnectMessageKeys
{
    /// <summary>The remote shell closed its channel while the transport stayed up.</summary>
    public const string MessageKeyRemoteShellExited = "SshDisconnectRemoteShellExited";
}
