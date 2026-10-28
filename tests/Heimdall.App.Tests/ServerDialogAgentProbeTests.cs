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

using Heimdall.App.ViewModels.Dialogs;
using Heimdall.Core.Ssh;
using Heimdall.Ssh.Agents;

namespace Heimdall.App.Tests;

/// <summary>
/// The agent chip of the server dialog asks the machine's SSH agents what they hold. An agent
/// answers in its own time (a named pipe, a Pageant window), so the probe runs off the UI thread
/// and a late answer is applied only when it is still the one the dialog is waiting for.
/// </summary>
public sealed class ServerDialogAgentProbeTests
{
    /// <summary>
    /// A bound on how long the test waits for something that must already have happened. It is a
    /// failure bound, not a pace: the awaited events are released by the test itself.
    /// </summary>
    private static readonly TimeSpan FailureBound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task TheProbe_DoesNotHoldTheCallerWhileAnAgentIsSilent()
    {
        GatedAgent agent = new();
        ServerDialogViewModel vm = SshDialog();
        vm.AgentRegistryFactory = _ => new SshAgentRegistry([agent]);

        try
        {
            // The caller is the UI thread in production. Both the property setter that starts a
            // probe and the refresh command must return while the agent has not answered.
            Task<Task> started = Task.Factory.StartNew(
                () =>
                {
                    vm.ConnectionType = "Telnet";
                    vm.ConnectionType = "SSH";
                    return vm.RefreshAgentChipCommand.ExecuteAsync(null);
                },
                CancellationToken.None,
                TaskCreationOptions.None,
                TaskScheduler.Default);

            // A timeout here is the failure: the caller was held by the silent agent.
            Task refresh = await started.WaitAsync(FailureBound);
            Assert.True(agent.Entered.Wait(FailureBound), "The probe never reached the agent.");
            Assert.False(refresh.IsCompleted);
            Assert.Equal("", vm.AgentChipText);

            agent.Gate.Set();
            await refresh.WaitAsync(FailureBound);

            Assert.Equal(SshAgentChipState.Warn, vm.AgentChipState);
        }
        finally
        {
            agent.Gate.Set();
        }
    }

    [Fact]
    public async Task ALateAnswer_AfterTheProtocolLeftSsh_IsDropped()
    {
        GatedAgent agent = new();
        ServerDialogViewModel vm = SshDialog();
        vm.AgentRegistryFactory = _ => new SshAgentRegistry([agent]);

        try
        {
            Task refresh = vm.RefreshAgentChipCommand.ExecuteAsync(null);
            Assert.True(agent.Entered.Wait(FailureBound), "The probe never reached the agent.");

            vm.ConnectionType = "RDP";
            agent.Gate.Set();
            await refresh.WaitAsync(FailureBound);

            // The chip belongs to SSH. Showing "an agent is available" on an RDP session would be
            // a statement about a connection that does not use one.
            Assert.Equal(SshAgentChipState.Off, vm.AgentChipState);
            Assert.Equal("", vm.AgentChipText);
        }
        finally
        {
            agent.Gate.Set();
        }
    }

    [Fact]
    public async Task ALateAnswer_FromAnOlderProbe_DoesNotOverwriteTheNewerOne()
    {
        GatedAgent slow = new();
        SshAgentRegistry current = new([slow]);
        ServerDialogViewModel vm = SshDialog();
        vm.AgentRegistryFactory = _ => current;

        try
        {
            Task older = vm.RefreshAgentChipCommand.ExecuteAsync(null);
            Assert.True(slow.Entered.Wait(FailureBound), "The probe never reached the agent.");

            // The machine's agent went away while the first probe was still waiting on it.
            current = new SshAgentRegistry([]);
            await vm.RefreshAgentChipCommand.ExecuteAsync(null).WaitAsync(FailureBound);
            Assert.Equal(SshAgentChipState.Off, vm.AgentChipState);

            slow.Gate.Set();
            await older.WaitAsync(FailureBound);

            // The older probe would say "available, no key" (Warn). It lost the race.
            Assert.Equal(SshAgentChipState.Off, vm.AgentChipState);
        }
        finally
        {
            slow.Gate.Set();
        }
    }

    private static ServerDialogViewModel SshDialog()
    {
        ServerDialogViewModel vm = new();
        vm.AgentRegistryFactory = static _ => new SshAgentRegistry([]);
        vm.ConnectionType = "SSH";
        return vm;
    }

    /// <summary>An agent that is reachable only once the test says so.</summary>
    private sealed class GatedAgent : ISshAgent
    {
        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Gate { get; } = new(false);

        public string Name => "Gated agent";

        public bool IsAvailable()
        {
            Entered.Set();
            Gate.Wait();
            return true;
        }

        public IReadOnlyList<ISshAgentKey> GetIdentities() => [];
    }
}
