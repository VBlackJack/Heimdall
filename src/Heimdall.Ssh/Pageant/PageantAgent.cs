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
using Heimdall.Ssh.Agents;
using Heimdall.Ssh.OpenSsh;

namespace Heimdall.Ssh.Pageant;

/// <summary>
/// PuTTY Pageant implementation of <see cref="ISshAgent"/>.
/// </summary>
public sealed class PageantAgent : IMutableSshAgent
{
    public const string AgentName = "Pageant";

    public string Name => AgentName;

    public bool SupportsLifetimeConstraint => false;

    public bool IsAvailable() => PageantClient.IsAvailable();

    public IReadOnlyList<ISshAgentKey> GetIdentities()
    {
        PageantClient client = new PageantClient();
        return client.GetIdentities()
            .Select(key => new PageantAgentKey(key))
            .Cast<ISshAgentKey>()
            .ToList();
    }

    public bool AddIdentity(byte[] addIdentityWireMessage)
    {
        ArgumentNullException.ThrowIfNull(addIdentityWireMessage);
        var client = new PageantClient();
        var response = client.SendMessage(addIdentityWireMessage);
        return OpenSshAgentProtocol.ParseSuccessResponse(response);
    }

    public bool RemoveIdentity(byte[] publicKeyBlob)
    {
        ArgumentNullException.ThrowIfNull(publicKeyBlob);
        var client = new PageantClient();
        var request = OpenSshAgentProtocol.BuildRemoveIdentityMessage(publicKeyBlob);
        var response = client.SendMessage(request);
        return OpenSshAgentProtocol.ParseSuccessResponse(response);
    }

    public bool RemoveAllIdentities()
    {
        var client = new PageantClient();
        var request = OpenSshAgentProtocol.BuildRemoveAllIdentitiesMessage();
        var response = client.SendMessage(request);
        return OpenSshAgentProtocol.ParseSuccessResponse(response);
    }
}
