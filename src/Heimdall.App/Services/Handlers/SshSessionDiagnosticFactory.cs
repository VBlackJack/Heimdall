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

using Heimdall.Core.SessionDiagnostics;
using Heimdall.Ssh;

namespace Heimdall.App.Services.Handlers;

/// <summary>
/// Maps SSH failure signals to the shared session diagnostic contract.
/// </summary>
internal static class SshSessionDiagnosticFactory
{
    internal static SessionDiagnostic FromClassifiedFailure(
        SshFailureInfo failure,
        bool usedGateway = false)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new SessionDiagnostic(
            MapStage(failure.Code, usedGateway),
            GetMessageKey(failure.Code, "ErrorConnectionFailed"),
            (int)failure.Code,
            failure.Message);
    }

    /// <param name="preflight">The failed preflight.</param>
    /// <param name="detail">
    /// The sentence already resolved for the user; the raw preflight message (which may
    /// be a locale key) is used when none is supplied.
    /// </param>
    internal static SessionDiagnostic FromPreflight(PreflightResult preflight, string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(preflight);

        return new SessionDiagnostic(
            SessionFailureStage.SshPreflight,
            GetMessageKey(preflight.FailureCode, "ErrorPreflightFailed"),
            preflight.FailureCode is null ? null : (int)preflight.FailureCode.Value,
            detail ?? preflight.Message);
    }

    internal static SessionDiagnostic CreateGatewayFailure(
        string? detail,
        string messageKey = "ErrorConnectionFailed",
        SshFailureCode? code = null)
    {
        return new SessionDiagnostic(
            SessionFailureStage.SshGateway,
            GetMessageKey(code, messageKey),
            code is null ? null : (int)code.Value,
            detail);
    }

    internal static SessionDiagnostic CreatePreflightFailure(
        string messageKey,
        string? detail,
        SshFailureCode? code = null)
    {
        return new SessionDiagnostic(
            SessionFailureStage.SshPreflight,
            messageKey,
            code is null ? null : (int)code.Value,
            detail);
    }

    internal static SessionDiagnostic CreatePlinkFallbackFailure(
        string messageKey,
        string? detail,
        SshFailureCode? code = null)
    {
        return new SessionDiagnostic(
            SessionFailureStage.SshPlinkFallback,
            messageKey,
            code is null ? null : (int)code.Value,
            detail);
    }

    internal static SessionDiagnostic CreatePipeModeFailure(string? detail)
    {
        return new SessionDiagnostic(
            SessionFailureStage.SshPipeMode,
            "ErrorConnectionFailed",
            null,
            detail);
    }

    internal static SessionDiagnostic CreateHostKeyMismatchFailure(
        string storedFingerprint,
        string presentedFingerprint,
        string host,
        int port)
    {
        var detail =
            $"SSH host key mismatch for {host}:{port}. " +
            $"Stored={storedFingerprint}. Presented={presentedFingerprint}.";

        return new SessionDiagnostic(
            SessionFailureStage.SshHostKey,
            "ErrorHostKeyMismatch",
            (int)SshFailureCode.HostKeyMismatch,
            detail);
    }

    internal static SessionDiagnostic CreateHostKeyUnavailableFailure(
        string? detail,
        string host,
        int port)
    {
        var diagnosticDetail = string.IsNullOrWhiteSpace(detail)
            ? $"SSH host key unavailable for {host}:{port}."
            : $"{detail} ({host}:{port})";

        return new SessionDiagnostic(
            SessionFailureStage.SshHostKey,
            "ErrorSshHostKeyUnavailable",
            (int)SshFailureCode.HostKeyUnavailable,
            diagnosticDetail);
    }

    internal static SessionDiagnostic CreateGenericFailure(string messageKey, string? detail)
    {
        return new SessionDiagnostic(
            SessionFailureStage.GenericFailure,
            messageKey,
            null,
            detail);
    }

    internal static SessionFailureStage MapStage(
        SshFailureCode? code,
        bool usedGateway = false)
    {
        return code switch
        {
            SshFailureCode.AuthRejected
                or SshFailureCode.KeyRejected
                or SshFailureCode.PassphraseRejected
                or SshFailureCode.PasswordRejected
                or SshFailureCode.NoSupportedAuth
                or SshFailureCode.TooManyAuthFailures
                or SshFailureCode.KeyboardInteractiveNoPassword
                or SshFailureCode.AuthTimeout
                => SessionFailureStage.SshAuth,

            SshFailureCode.HostKeyMismatch
                or SshFailureCode.HostKeyUnavailable
                => SessionFailureStage.SshHostKey,

            SshFailureCode.NetworkRefused
                or SshFailureCode.NetworkTimedOut
                or SshFailureCode.NetworkReset
                or SshFailureCode.NetworkUnreachable
                => usedGateway ? SessionFailureStage.SshGateway : SessionFailureStage.GenericFailure,

            SshFailureCode.ForwardingFailed
                or SshFailureCode.PortInUse
                or SshFailureCode.TunnelBroken
                or SshFailureCode.TunnelPortOwnedByDifferentProcess
                or SshFailureCode.TunnelPortNotListening
                or SshFailureCode.TunnelPortOwnershipIndeterminate
                or SshFailureCode.ChainDepthExceeded
                or SshFailureCode.CircularChainDependency
                => SessionFailureStage.SshGateway,

            SshFailureCode.PageantKeyUnavailable
                or SshFailureCode.PageantNoIdentities
                or SshFailureCode.PassphraseRequired
                or SshFailureCode.KeyFileInvalid
                or SshFailureCode.KeyFileNotFound
                => SessionFailureStage.SshPreflight,

            _ => SessionFailureStage.GenericFailure,
        };
    }

    internal static string GetMessageKey(SshFailureCode? code, string fallbackKey)
    {
        return code switch
        {
            null => fallbackKey,
            SshFailureCode.PageantKeyUnavailable => "ErrorNoSshAgentRunning",
            SshFailureCode.PageantNoIdentities => "ErrorSshAgentHasNoIdentities",
            SshFailureCode.TunnelPortOwnedByDifferentProcess
                or SshFailureCode.TunnelPortNotListening
                or SshFailureCode.TunnelPortOwnershipIndeterminate
                => "ErrorSshTunnelPortOwnershipUnattested",
            _ => $"ErrorSsh{code.Value}"
        };
    }
}
