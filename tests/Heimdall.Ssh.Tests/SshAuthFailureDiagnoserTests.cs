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

using Heimdall.Ssh;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// The diagnosis states how many agent identities were available and nothing
/// else. It never names a cause, because the same refusal is produced by a wrong
/// stored password and by an unloaded agent key.
/// </summary>
public sealed class SshAuthFailureDiagnoserTests
{
    private static SshConnectionParams AgentOnlyHop() => new SshConnectionParams
    {
        Host = "gw.example.test",
        Username = "ssh-user"
    };

    private static SshConnectionParams KeyFileHop() => new SshConnectionParams
    {
        Host = "gw.example.test",
        Username = "ssh-user",
        KeyPath = @"C:\keys\gateway.pem"
    };

    private static SshConnectionParams PasswordHop() => new SshConnectionParams
    {
        Host = "gw.example.test",
        Username = "ssh-user",
        Password = "stored"
    };

    [Fact]
    public void NoAgentIdentity_SaysNoAgentKeyWasOffered()
    {
        SshAuthFailureDiagnosis diagnosis = SshAuthFailureDiagnoser.Diagnose(
            [KeyFileHop()],
            agentIdentityCount: 0);

        Assert.False(diagnosis.AgentIdentityAvailable);
        Assert.Equal(SshAuthFailureLocaleKeys.NoAgentKeyLoaded, diagnosis.ContextMessageKey);
    }

    // The hop that carries a stored password is the case the whole repair turns
    // on: the wording it selects must be the same observation as any other, not
    // a claim that the agent is why the password was refused.
    [Fact]
    public void APasswordHopAndAKeyFileHop_SelectTheSameObservation()
    {
        SshAuthFailureDiagnosis password = SshAuthFailureDiagnoser.Diagnose(
            [PasswordHop()],
            agentIdentityCount: 0);
        SshAuthFailureDiagnosis keyFile = SshAuthFailureDiagnoser.Diagnose(
            [KeyFileHop()],
            agentIdentityCount: 0);

        Assert.Equal(keyFile.ContextMessageKey, password.ContextMessageKey);
    }

    [Fact]
    public void OneAgentIdentity_SaysThatOneKeyWasOfferedAndRefused()
    {
        SshAuthFailureDiagnosis diagnosis = SshAuthFailureDiagnoser.Diagnose(
            [KeyFileHop()],
            agentIdentityCount: 1);

        Assert.True(diagnosis.AgentIdentityAvailable);
        Assert.Equal(1, diagnosis.AgentIdentityCount);
        Assert.Equal(SshAuthFailureLocaleKeys.OneAgentKeyRefused, diagnosis.ContextMessageKey);
    }

    // The owner's nearest neighbour: Pageant running, holding the wrong keys.
    // Before the repair this returned no wording at all and the user saw only
    // "Permission denied (password)." for a failure that was again about the
    // agent.
    [Fact]
    public void SeveralAgentIdentities_SayHowManyWereOfferedAndRefused()
    {
        SshAuthFailureDiagnosis diagnosis = SshAuthFailureDiagnoser.Diagnose(
            [KeyFileHop()],
            agentIdentityCount: 3);

        Assert.Equal(3, diagnosis.AgentIdentityCount);
        Assert.Equal(SshAuthFailureLocaleKeys.ManyAgentKeysRefused, diagnosis.ContextMessageKey);
    }

    [Fact]
    public void AnAgentThatIsRunningAndAnAgentThatIsAbsent_AreNeverToldApartByTheSameSentence()
    {
        SshAuthFailureDiagnosis absent = SshAuthFailureDiagnoser.Diagnose(
            [KeyFileHop()],
            agentIdentityCount: 0);
        SshAuthFailureDiagnosis loaded = SshAuthFailureDiagnoser.Diagnose(
            [KeyFileHop()],
            agentIdentityCount: 1);

        Assert.NotEqual(absent.ContextMessageKey, loaded.ContextMessageKey);
    }

    [Fact]
    public void AnAgentOnlyHop_IsStillReportedAsAgentDependent()
    {
        SshAuthFailureDiagnosis diagnosis = SshAuthFailureDiagnoser.Diagnose(
            [KeyFileHop(), AgentOnlyHop()],
            agentIdentityCount: 0);

        Assert.True(diagnosis.AgentIsSoleAuthSource);
    }

    [Fact]
    public void AHopWithAKeyFileOrAPassword_IsNotAgentDependent()
    {
        Assert.False(SshAuthFailureDiagnoser.AgentIsSoleAuthSource(KeyFileHop()));
        Assert.False(SshAuthFailureDiagnoser.AgentIsSoleAuthSource(PasswordHop()));
        Assert.True(SshAuthFailureDiagnoser.AgentIsSoleAuthSource(AgentOnlyHop()));
    }

    // The agent-is-sole-source observation must select no wording of its own:
    // the key it used to select could never render, because the chain pre-flight
    // refuses that exact state before any dial happens.
    [Fact]
    public void TheAgentSoleSourceObservation_SelectsNoWordingOfItsOwn()
    {
        SshAuthFailureDiagnosis soleSource = SshAuthFailureDiagnoser.Diagnose(
            [AgentOnlyHop()],
            agentIdentityCount: 0);
        SshAuthFailureDiagnosis notSoleSource = SshAuthFailureDiagnoser.Diagnose(
            [KeyFileHop()],
            agentIdentityCount: 0);

        Assert.True(soleSource.AgentIsSoleAuthSource);
        Assert.False(notSoleSource.AgentIsSoleAuthSource);
        Assert.Equal(notSoleSource.ContextMessageKey, soleSource.ContextMessageKey);
    }

    [Fact]
    public void ANegativeIdentityCount_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SshAuthFailureDiagnoser.Diagnose([KeyFileHop()], agentIdentityCount: -1));
    }

    [Theory]
    [InlineData(SshFailureCode.AuthRejected)]
    [InlineData(SshFailureCode.KeyRejected)]
    [InlineData(SshFailureCode.PasswordRejected)]
    [InlineData(SshFailureCode.NoSupportedAuth)]
    public void AuthRefusalCodes_AreRecognised(SshFailureCode code)
    {
        Assert.True(SshAuthFailureDiagnoser.IsAuthRejection(code));
    }

    [Theory]
    [InlineData(SshFailureCode.NetworkRefused)]
    [InlineData(SshFailureCode.HostKeyMismatch)]
    [InlineData(SshFailureCode.PortInUse)]
    [InlineData(SshFailureCode.Cancelled)]
    [InlineData(null)]
    public void NonAuthCodes_AreNotRecognisedAsRefusals(SshFailureCode? code)
    {
        Assert.False(SshAuthFailureDiagnoser.IsAuthRejection(code));
    }
}
