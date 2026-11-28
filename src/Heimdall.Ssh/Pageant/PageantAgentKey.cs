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

using Heimdall.Core.Ssh;

namespace Heimdall.Ssh.Pageant;

internal sealed class PageantAgentKey : ISshAgentKey
{
    private readonly Func<IPageantClient> _clientFactory;

    public PageantAgentKey(PageantKey key, Func<IPageantClient>? clientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        PublicKeyBlob = key.Blob;
        Comment = key.Comment;
        KeyType = key.KeyType;
        _clientFactory = clientFactory ?? (static () => new PageantClient());
    }

    public string Comment { get; }
    public string KeyType { get; }
    public byte[] PublicKeyBlob { get; }

    public byte[] Sign(byte[] data, SshAgentSignFlags flags)
    {
        IPageantClient client = _clientFactory();
        if (ISshAgentKey.IsSecurityKeyType(KeyType))
        {
            return client.SignData(
                PublicKeyBlob,
                data,
                (uint)flags,
                ISshAgentKey.SecurityKeySignTimeoutMilliseconds,
                allowInteractiveWait: true);
        }

        return client.SignData(PublicKeyBlob, data, (uint)flags);
    }

    public byte[] Sign(byte[] data, SshAgentSignFlags flags, int timeoutMilliseconds)
    {
        IPageantClient client = _clientFactory();
        uint effective = timeoutMilliseconds > 0 ? (uint)timeoutMilliseconds : PageantClient.PageantIpcTimeoutMilliseconds;
        return client.SignData(
            PublicKeyBlob,
            data,
            (uint)flags,
            effective,
            allowInteractiveWait: ISshAgentKey.IsSecurityKeyType(KeyType) || effective > PageantClient.PageantIpcTimeoutMilliseconds);
    }
}
