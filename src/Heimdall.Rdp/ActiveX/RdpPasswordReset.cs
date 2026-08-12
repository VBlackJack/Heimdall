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

using System.Globalization;
using System.Reflection;

namespace Heimdall.Rdp.ActiveX;

/// <summary>
/// Classifies a single native password reset attempt on the MsTscAx control.
/// </summary>
public enum RdpPasswordResetOutcome
{
    /// <summary>The control was disconnected and <c>ResetPassword</c> completed.</summary>
    Success,

    /// <summary>No ActiveX instance was available, so no COM call was attempted.</summary>
    MissingActiveXInstance,

    /// <summary>Reading the COM <c>Connected</c> property threw, so the state stays unknown.</summary>
    ConnectedStateUnavailable,

    /// <summary>The COM <c>Connected</c> property did not return the documented 16-bit integer.</summary>
    ConnectedStateTypeUnexpected,

    /// <summary>The control reported a connecting or connected state, so the reset was refused.</summary>
    ControlNotDisconnected,

    /// <summary>The control was disconnected but <c>ResetPassword</c> threw.</summary>
    ResetPasswordFailed,
}

/// <summary>
/// Bounded technical evidence describing one native password reset attempt. The record never
/// carries an exception message, a host, a user name, a path or any credential material.
/// </summary>
public sealed record RdpPasswordResetResult
{
    private RdpPasswordResetResult(
        RdpPasswordResetOutcome outcome,
        int? connectedState,
        string? observedStateTypeName,
        string? failureTypeName,
        int? hResult)
    {
        Outcome = outcome;
        ConnectedState = connectedState;
        ObservedStateTypeName = observedStateTypeName;
        FailureTypeName = failureTypeName;
        HResult = hResult;
    }

    /// <summary>Gets the classification of the attempt.</summary>
    public RdpPasswordResetOutcome Outcome { get; }

    /// <summary>
    /// Gets the connected state converted to a 32-bit integer, or <see langword="null"/> when the
    /// state could not be read or was not the documented 16-bit integer.
    /// </summary>
    public int? ConnectedState { get; }

    /// <summary>
    /// Gets the unexpected boxed type name returned by the connected state read, or
    /// <see langword="null"/> when the state was readable and correctly typed.
    /// </summary>
    public string? ObservedStateTypeName { get; }

    /// <summary>
    /// Gets the full type name of the failing exception, or <see langword="null"/> when nothing
    /// threw. The exception message is deliberately never captured.
    /// </summary>
    public string? FailureTypeName { get; }

    /// <summary>Gets the HRESULT of the failing exception, or <see langword="null"/>.</summary>
    public int? HResult { get; }

    /// <summary>Gets a value indicating whether the reset completed.</summary>
    public bool IsSuccess => Outcome == RdpPasswordResetOutcome.Success;

    internal static RdpPasswordResetResult Succeeded(int connectedState)
    {
        return new RdpPasswordResetResult(
            RdpPasswordResetOutcome.Success,
            connectedState,
            observedStateTypeName: null,
            failureTypeName: null,
            hResult: null);
    }

    internal static RdpPasswordResetResult MissingActiveXInstance()
    {
        return new RdpPasswordResetResult(
            RdpPasswordResetOutcome.MissingActiveXInstance,
            connectedState: null,
            observedStateTypeName: null,
            failureTypeName: null,
            hResult: null);
    }

    internal static RdpPasswordResetResult ConnectedStateUnavailable(Exception failure)
    {
        return new RdpPasswordResetResult(
            RdpPasswordResetOutcome.ConnectedStateUnavailable,
            connectedState: null,
            observedStateTypeName: null,
            failure.GetType().FullName,
            failure.HResult);
    }

    internal static RdpPasswordResetResult ConnectedStateTypeUnexpected(string? observedStateTypeName)
    {
        return new RdpPasswordResetResult(
            RdpPasswordResetOutcome.ConnectedStateTypeUnexpected,
            connectedState: null,
            observedStateTypeName,
            failureTypeName: null,
            hResult: null);
    }

    internal static RdpPasswordResetResult ControlNotDisconnected(int connectedState)
    {
        return new RdpPasswordResetResult(
            RdpPasswordResetOutcome.ControlNotDisconnected,
            connectedState,
            observedStateTypeName: null,
            failureTypeName: null,
            hResult: null);
    }

    internal static RdpPasswordResetResult ResetPasswordFailed(int connectedState, Exception failure)
    {
        return new RdpPasswordResetResult(
            RdpPasswordResetOutcome.ResetPasswordFailed,
            connectedState,
            observedStateTypeName: null,
            failure.GetType().FullName,
            failure.HResult);
    }
}

/// <summary>
/// Fail-closed COM boundary that clears every password representation held by the MsTscAx control.
/// </summary>
/// <remarks>
/// Microsoft documents <c>IMsTscNonScriptable::ResetPassword</c> as returning E_FAIL while the
/// control is connected, so the COM <c>Connected</c> property is read immediately before the call
/// and the reset proceeds only on the documented disconnected value. The managed connection flag
/// maintained by the host is never accepted as proof. This type performs no logging: the calling
/// view owns the single bounded diagnostic.
/// </remarks>
public static class RdpPasswordReset
{
    private const string ConnectedPropertyName = "Connected";
    private const int DisconnectedConnectedState = 0;

    /// <summary>
    /// Attempts the reset against a live ActiveX instance.
    /// </summary>
    /// <param name="activeXInstance">
    /// Raw COM instance obtained from the host, or <see langword="null"/> when unavailable.
    /// </param>
    /// <returns>Bounded evidence describing the attempt.</returns>
    public static RdpPasswordResetResult TryReset(object? activeXInstance)
    {
        return TryResetCore(activeXInstance, ReadConnectedState, InvokeResetPassword);
    }

    /// <summary>
    /// Test seam isolating the COM boundary. The two operations are injected so the fail-closed
    /// sequencing can be proved without a live control, a network connection or a desktop session.
    /// </summary>
    internal static RdpPasswordResetResult TryResetCore(
        object? activeXInstance,
        Func<object, object?> readConnectedState,
        Action<object> resetPassword)
    {
        ArgumentNullException.ThrowIfNull(readConnectedState);
        ArgumentNullException.ThrowIfNull(resetPassword);

        if (activeXInstance is null)
        {
            return RdpPasswordResetResult.MissingActiveXInstance();
        }

        object? rawConnectedState;
        try
        {
            rawConnectedState = readConnectedState(activeXInstance);
        }
        catch (Exception exception)
        {
            return RdpPasswordResetResult.ConnectedStateUnavailable(Unwrap(exception));
        }

        if (rawConnectedState is not short connectedState)
        {
            return RdpPasswordResetResult.ConnectedStateTypeUnexpected(
                rawConnectedState?.GetType().FullName);
        }

        int normalizedConnectedState = Convert.ToInt32(connectedState);
        if (normalizedConnectedState != DisconnectedConnectedState)
        {
            return RdpPasswordResetResult.ControlNotDisconnected(normalizedConnectedState);
        }

        try
        {
            resetPassword(activeXInstance);
        }
        catch (Exception exception)
        {
            return RdpPasswordResetResult.ResetPasswordFailed(
                normalizedConnectedState,
                Unwrap(exception));
        }

        return RdpPasswordResetResult.Succeeded(normalizedConnectedState);
    }

    private static object? ReadConnectedState(object activeXInstance)
    {
        return activeXInstance.GetType().InvokeMember(
            ConnectedPropertyName,
            BindingFlags.GetProperty,
            binder: null,
            target: activeXInstance,
            args: null,
            culture: CultureInfo.InvariantCulture);
    }

    private static void InvokeResetPassword(object activeXInstance)
    {
        IMsTscNonScriptable nonScriptable = (IMsTscNonScriptable)activeXInstance;
        nonScriptable.ResetPassword();
    }

    private static Exception Unwrap(Exception exception)
    {
        return exception is TargetInvocationException { InnerException: Exception innerException }
            ? innerException
            : exception;
    }
}
