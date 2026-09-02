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
using Heimdall.Ssh.Pageant;

namespace Heimdall.Ssh.Tests;

public sealed class SshAgentRegistryTests
{
    [Theory]
    [InlineData(SshAgentPreference.AutoOpenSshFirst, OpenSshPipeAgent.AgentName, PageantAgent.AgentName)]
    [InlineData(SshAgentPreference.AutoPageantFirst, PageantAgent.AgentName, OpenSshPipeAgent.AgentName)]
    public void GetAgentsInPriorityOrder_AutoModes_OrderAgents(
        SshAgentPreference preference,
        string first,
        string second)
    {
        var registry = CreateRegistry(preference);

        var names = registry.GetAgentsInPriorityOrder().Select(agent => agent.Name).ToList();

        Assert.Equal([first, second], names);
    }

    [Theory]
    [InlineData(SshAgentPreference.OpenSshOnly, OpenSshPipeAgent.AgentName)]
    [InlineData(SshAgentPreference.PageantOnly, PageantAgent.AgentName)]
    public void GetAgentsInPriorityOrder_OnlyModes_FilterAgents(
        SshAgentPreference preference,
        string onlyAgent)
    {
        var registry = CreateRegistry(preference);

        var agent = Assert.Single(registry.GetAgentsInPriorityOrder());
        Assert.Equal(onlyAgent, agent.Name);
    }

    [Fact]
    public void GetAvailableAgents_FiltersUnavailableWithoutCaching()
    {
        var toggledAgent = new FakeAgent(OpenSshPipeAgent.AgentName, available: false, []);
        var registry = new SshAgentRegistry([toggledAgent]);

        Assert.Empty(registry.GetAvailableAgents());

        toggledAgent.Available = true;

        var agent = Assert.Single(registry.GetAvailableAgents());
        Assert.Equal(OpenSshPipeAgent.AgentName, agent.Name);
        Assert.Equal(2, toggledAgent.IsAvailableCallCount);
    }

    [Fact]
    public void FindKey_SearchesAcrossAvailableAgents()
    {
        var targetBlob = OpenSshAgentProtocolTests.BuildKeyBlob("ssh-ed25519");
        var registry = new SshAgentRegistry(
            [
                new FakeAgent(OpenSshPipeAgent.AgentName, available: true, []),
                new FakeAgent(PageantAgent.AgentName, available: true, [new FakeAgentKey(targetBlob)])
            ]);

        var key = registry.FindKey(targetBlob);

        Assert.NotNull(key);
        Assert.Equal(targetBlob, key.PublicKeyBlob);
    }

    /// <summary>
    /// Authentication offers the keys of the first reachable agent that holds
    /// any, and those alone - see <c>SshConnectionFactory.TryCreateAgentAuth</c>.
    /// The refusal sentence quotes this number as keys that were offered to the
    /// gateway, so summing every reachable agent would state a count that was
    /// never presented to it.
    /// </summary>
    [Fact]
    public void OfferableIdentityCount_CountsOnlyTheAgentWhoseKeysWouldBeOffered()
    {
        var registry = new SshAgentRegistry(
            [
                new FakeAgent(OpenSshPipeAgent.AgentName, available: true, [Key(), Key()]),
                new FakeAgent(PageantAgent.AgentName, available: true, [Key()])
            ],
            () => SshAgentPreference.AutoOpenSshFirst);

        Assert.Equal(2, registry.OfferableIdentityCount());
    }

    /// <summary>
    /// A running but empty agent is not what would be offered either: the
    /// selection skips it exactly as the authentication loop does.
    /// </summary>
    [Fact]
    public void OfferableIdentityCount_SkipsAnAgentThatIsRunningButEmpty()
    {
        var registry = new SshAgentRegistry(
            [
                new FakeAgent(OpenSshPipeAgent.AgentName, available: true, []),
                new FakeAgent(PageantAgent.AgentName, available: true, [Key(), Key(), Key()])
            ],
            () => SshAgentPreference.AutoOpenSshFirst);

        Assert.Equal(3, registry.OfferableIdentityCount());
    }

    private static ISshAgentKey Key() =>
        new FakeAgentKey(OpenSshAgentProtocolTests.BuildKeyBlob("ssh-ed25519"));

    private static SshAgentRegistry CreateRegistry(SshAgentPreference preference)
    {
        return new SshAgentRegistry(
            [
                new FakeAgent(PageantAgent.AgentName, available: true, []),
                new FakeAgent(OpenSshPipeAgent.AgentName, available: true, [])
            ],
            () => preference);
    }

    private sealed class FakeAgent(
        string name,
        bool available,
        IReadOnlyList<ISshAgentKey> identities) : ISshAgent
    {
        public bool Available { get; set; } = available;
        public int IsAvailableCallCount { get; private set; }
        public string Name { get; } = name;

        public bool IsAvailable()
        {
            IsAvailableCallCount++;
            return Available;
        }

        public IReadOnlyList<ISshAgentKey> GetIdentities() => identities;
    }

    private sealed class FakeAgentKey(byte[] publicKeyBlob) : ISshAgentKey
    {
        public string Comment => "fake";
        public string KeyType => "ssh-ed25519";
        public byte[] PublicKeyBlob => publicKeyBlob;
        public byte[] Sign(byte[] data, SshAgentSignFlags flags) => [1];
    }
}
