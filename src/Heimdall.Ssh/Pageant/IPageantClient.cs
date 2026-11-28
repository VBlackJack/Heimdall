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

namespace Heimdall.Ssh.Pageant;

/// <summary>
/// Minimal Pageant client surface used by consumers that request signatures.
/// </summary>
public interface IPageantClient
{
    /// <summary>
    /// Requests the Pageant agent to sign data with a specific key.
    /// </summary>
    /// <param name="keyBlob">Public key blob identifying which key to use for signing.</param>
    /// <param name="data">Data to sign.</param>
    /// <param name="flags">Agent signature flags.</param>
    /// <returns>The full SSH signature blob from the agent.</returns>
    byte[] SignData(byte[] keyBlob, byte[] data, uint flags = 0);

    /// <summary>
    /// Signs data with an explicit IPC timeout. When <paramref name="allowInteractiveWait"/> is true,
    /// <c>SMTO_ABORTIFHUNG</c> is omitted so hardware security key touch prompts are not cut off at 5 s.
    /// </summary>
    byte[] SignData(byte[] keyBlob, byte[] data, uint flags, uint timeoutMilliseconds, bool allowInteractiveWait)
        => SignData(keyBlob, data, flags);
}
