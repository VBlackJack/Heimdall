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

using Renci.SshNet.Common;

namespace Heimdall.Ssh.Tests;

/// <summary>
/// The tunnel manager kept a classification table of its own, shorter than
/// <see cref="FailureClassifier"/>'s, so the commonest gateway failures reached the user as an
/// unknown error. Each case below was <see cref="SshFailureCode.Unknown"/> before.
/// </summary>
public sealed class TunnelFailureClassificationTests
{
    public static TheoryData<Exception, SshFailureCode> FailuresTheTunnelUsedToCallUnknown => new()
    {
        { new SshOperationTimeoutException("Socket read operation has timed out after 30000 milliseconds."), SshFailureCode.NetworkTimedOut },
        { new ProxyException("SOCKS4: Connection rejected."), SshFailureCode.ForwardingFailed },
        { new SshPassPhraseNullOrEmptyException("Private key is encrypted but passphrase is empty."), SshFailureCode.PassphraseRequired },
        { new IOException("Unable to read data.", new SocketException((int)SocketError.TimedOut)), SshFailureCode.NetworkTimedOut },
        { new SocketException((int)SocketError.ConnectionReset), SshFailureCode.NetworkReset },
        { new SocketException((int)SocketError.NetworkUnreachable), SshFailureCode.NetworkUnreachable },
    };

    [Theory]
    [MemberData(nameof(FailuresTheTunnelUsedToCallUnknown))]
    public void ClassifyAndBuildFailureResult_NamesTheFailure(Exception failure, SshFailureCode expected)
    {
        foreach (bool isChained in new[] { false, true })
        {
            TunnelResult result = TunnelManager.ClassifyAndBuildFailureResult(failure, () => { }, isChained);

            Assert.False(result.Success);
            Assert.Equal(expected, result.FailureCode);
        }
    }

    // The caller's agent context and Plink fallback only fire on these codes, so a wrong
    // passphrase on the one gateway dialled has to arrive as PassphraseRejected to reach them.
    [Fact]
    public void ClassifyAndBuildFailureResult_WrongPassphraseOnTheGatewayDialled_IsPassphraseRejected()
    {
        SshConnectionParams gateway = new()
        {
            Host = "bastion.example.test",
            Username = "ops",
            KeyPath = @"C:\keys\bastion.ppk",
            KeyPassphrase = "wrong"
        };

        TunnelResult result = TunnelManager.ClassifyAndBuildFailureResult(
            new SshException("MAC verification failed for PuTTY key file"),
            () => { },
            isChained: false,
            gateway);

        Assert.Equal(SshFailureCode.PassphraseRejected, result.FailureCode);
    }

    // A refused sign-in keeps the code the caller keys its agent context and fallback on, even
    // where the shared classifier would have named it more precisely.
    [Fact]
    public void ClassifyAndBuildFailureResult_RefusedSignIn_StaysAuthRejected()
    {
        TunnelResult result = TunnelManager.ClassifyAndBuildFailureResult(
            new SshAuthenticationException("Permission denied (publickey)."),
            () => { },
            isChained: false);

        Assert.Equal(SshFailureCode.AuthRejected, result.FailureCode);
    }

    // A gateway name that does not resolve is the commonest typo. The two socket tables used to
    // disagree on it, and the shared one called it unknown.
    [Theory]
    [InlineData(SocketError.HostNotFound)]
    [InlineData(SocketError.TryAgain)]
    [InlineData(SocketError.NoData)]
    public void Classify_NameResolutionFailure_IsNetworkUnreachable(SocketError error)
    {
        SocketException failure = new((int)error);

        Assert.Equal(SshFailureCode.NetworkUnreachable, FailureClassifier.Classify(failure).Code);
        Assert.Equal(
            SshFailureCode.NetworkUnreachable,
            TunnelManager.ClassifyAndBuildFailureResult(failure, () => { }, isChained: true).FailureCode);
    }
}
