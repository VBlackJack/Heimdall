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

using Heimdall.App.Services.Handlers;
using Heimdall.Core.SessionDiagnostics;
using Heimdall.Ssh;

namespace Heimdall.App.Tests;

public sealed class SshSessionDiagnosticFactoryTests
{
    [Fact]
    public void FromClassifiedFailure_ForAuthRejected_UsesAuthStageAndPreservesCodeAndDetail()
    {
        var diagnostic = SshSessionDiagnosticFactory.FromClassifiedFailure(
            new SshFailureInfo(
                SshFailureCode.AuthRejected,
                "Permission denied (password).",
                true));

        Assert.Equal(SessionFailureStage.SshAuth, diagnostic.Stage);
        Assert.Equal("ErrorSshAuthRejected", diagnostic.MessageKey);
        Assert.Equal((int)SshFailureCode.AuthRejected, diagnostic.Code);
        Assert.Contains("Permission denied", diagnostic.Detail);
    }

    [Fact]
    public void FromClassifiedFailure_ForHostKeyMismatch_UsesHostKeyStage()
    {
        var diagnostic = SshSessionDiagnosticFactory.FromClassifiedFailure(
            new SshFailureInfo(
                SshFailureCode.HostKeyMismatch,
                "REMOTE HOST IDENTIFICATION HAS CHANGED!",
                true));

        Assert.Equal(SessionFailureStage.SshHostKey, diagnostic.Stage);
        Assert.Equal("ErrorSshHostKeyMismatch", diagnostic.MessageKey);
        Assert.Equal((int)SshFailureCode.HostKeyMismatch, diagnostic.Code);
    }

    [Fact]
    public void CreateGatewayFailure_UsesGatewayStage()
    {
        var diagnostic = SshSessionDiagnosticFactory.CreateGatewayFailure("Gateway tunnel failed.");

        Assert.Equal(SessionFailureStage.SshGateway, diagnostic.Stage);
        Assert.Equal("ErrorConnectionFailed", diagnostic.MessageKey);
        Assert.Null(diagnostic.Code);
        Assert.Equal("Gateway tunnel failed.", diagnostic.Detail);
    }

    [Fact]
    public void CreateHostKeyMismatchFailure_UsesHostKeyStageAndCode()
    {
        var diagnostic = SshSessionDiagnosticFactory.CreateHostKeyMismatchFailure(
            "SHA256:stored",
            "SHA256:presented",
            "server.example.com",
            22);

        Assert.Equal(SessionFailureStage.SshHostKey, diagnostic.Stage);
        Assert.Equal("ErrorHostKeyMismatch", diagnostic.MessageKey);
        Assert.Equal((int)SshFailureCode.HostKeyMismatch, diagnostic.Code);
        Assert.Contains("server.example.com:22", diagnostic.Detail);
        Assert.Contains("SHA256:stored", diagnostic.Detail);
        Assert.Contains("SHA256:presented", diagnostic.Detail);
    }

    [Theory]
    [InlineData(SshFailureCode.NetworkRefused)]
    [InlineData(SshFailureCode.NetworkTimedOut)]
    [InlineData(SshFailureCode.NetworkReset)]
    [InlineData(SshFailureCode.NetworkUnreachable)]
    public void FromClassifiedFailure_ForNetworkFailures_UsesGatewayStage(SshFailureCode code)
    {
        var diagnostic = SshSessionDiagnosticFactory.FromClassifiedFailure(
            new SshFailureInfo(
                code,
                "Network failure.",
                true));

        Assert.Equal(SessionFailureStage.SshGateway, diagnostic.Stage);
        Assert.Equal($"ErrorSsh{code}", diagnostic.MessageKey);
        Assert.Equal((int)code, diagnostic.Code);
        Assert.Equal("Network failure.", diagnostic.Detail);
    }

    [Theory]
    [InlineData(SshFailureCode.TunnelPortOwnedByDifferentProcess)]
    [InlineData(SshFailureCode.TunnelPortNotListening)]
    [InlineData(SshFailureCode.TunnelPortOwnershipIndeterminate)]
    public void FromClassifiedFailure_ForUnattestedPort_UsesSharedGatewayDiagnostic(SshFailureCode code)
    {
        var diagnostic = SshSessionDiagnosticFactory.FromClassifiedFailure(
            new SshFailureInfo(code, "Ownership failed.", true));

        Assert.Equal(SessionFailureStage.SshGateway, diagnostic.Stage);
        Assert.Equal("ErrorSshTunnelPortOwnershipUnattested", diagnostic.MessageKey);
        Assert.Equal((int)code, diagnostic.Code);
    }
}
