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

using System.Reflection;
using System.Runtime.InteropServices;
using Heimdall.Rdp.ActiveX;

namespace Heimdall.Rdp.Tests.ActiveX;

/// <summary>
/// Proves the fail-closed sequencing of the native password reset through the internal COM seam.
/// No test needs a live control, a network connection or an interactive desktop.
/// </summary>
public sealed class RdpPasswordResetTests
{
    private static readonly object ActiveXInstance = new();

    [Fact]
    public void TryResetCore_MissingActiveXInstance_InvokesNeitherOperation()
    {
        int readCount = 0;
        int resetCount = 0;

        RdpPasswordResetResult result = RdpPasswordReset.TryResetCore(
            activeXInstance: null,
            _ =>
            {
                readCount++;
                return (short)0;
            },
            _ => resetCount++);

        Assert.Equal(RdpPasswordResetOutcome.MissingActiveXInstance, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, readCount);
        Assert.Equal(0, resetCount);
        Assert.Null(result.ConnectedState);
    }

    [Fact]
    public void TryResetCore_ConnectedStateReadThrows_NeverInvokesReset()
    {
        int resetCount = 0;
        COMException failure = new("connected state unavailable", unchecked((int)0x80010108));

        RdpPasswordResetResult result = RdpPasswordReset.TryResetCore(
            ActiveXInstance,
            _ => throw failure,
            _ => resetCount++);

        Assert.Equal(RdpPasswordResetOutcome.ConnectedStateUnavailable, result.Outcome);
        Assert.Equal(0, resetCount);
        Assert.Equal(typeof(COMException).FullName, result.FailureTypeName);
        Assert.Equal(unchecked((int)0x80010108), result.HResult);
        Assert.Null(result.ConnectedState);
    }

    [Fact]
    public void TryResetCore_ReflectionWrappedReadFailure_ReportsInnerComEvidence()
    {
        int resetCount = 0;
        COMException inner = new("connected state unavailable", unchecked((int)0x800706BA));
        TargetInvocationException wrapper = new(inner);

        RdpPasswordResetResult result = RdpPasswordReset.TryResetCore(
            ActiveXInstance,
            _ => throw wrapper,
            _ => resetCount++);

        Assert.Equal(RdpPasswordResetOutcome.ConnectedStateUnavailable, result.Outcome);
        Assert.Equal(0, resetCount);
        Assert.Equal(typeof(COMException).FullName, result.FailureTypeName);
        Assert.Equal(unchecked((int)0x800706BA), result.HResult);
    }

    [Fact]
    public void TryResetCore_BoxedInt32Zero_IsRejectedAsUnexpectedStateType()
    {
        int resetCount = 0;

        RdpPasswordResetResult result = RdpPasswordReset.TryResetCore(
            ActiveXInstance,
            _ => 0,
            _ => resetCount++);

        Assert.Equal(RdpPasswordResetOutcome.ConnectedStateTypeUnexpected, result.Outcome);
        Assert.Equal(0, resetCount);
        Assert.Equal(typeof(int).FullName, result.ObservedStateTypeName);
        Assert.Null(result.ConnectedState);
    }

    [Fact]
    public void TryResetCore_ConnectedState_NeverInvokesReset()
    {
        int resetCount = 0;

        RdpPasswordResetResult result = RdpPasswordReset.TryResetCore(
            ActiveXInstance,
            _ => (short)1,
            _ => resetCount++);

        Assert.Equal(RdpPasswordResetOutcome.ControlNotDisconnected, result.Outcome);
        Assert.Equal(0, resetCount);
        Assert.Equal(1, result.ConnectedState);
    }

    [Fact]
    public void TryResetCore_ConnectingState_NeverInvokesReset()
    {
        int resetCount = 0;

        RdpPasswordResetResult result = RdpPasswordReset.TryResetCore(
            ActiveXInstance,
            _ => (short)2,
            _ => resetCount++);

        Assert.Equal(RdpPasswordResetOutcome.ControlNotDisconnected, result.Outcome);
        Assert.Equal(0, resetCount);
        Assert.Equal(2, result.ConnectedState);
    }

    [Fact]
    public void TryResetCore_DisconnectedState_InvokesResetExactlyOnce()
    {
        int resetCount = 0;

        RdpPasswordResetResult result = RdpPasswordReset.TryResetCore(
            ActiveXInstance,
            _ => (short)0,
            instance =>
            {
                Assert.Same(ActiveXInstance, instance);
                resetCount++;
            });

        Assert.Equal(RdpPasswordResetOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(1, resetCount);
        Assert.Equal(0, result.ConnectedState);
        Assert.Null(result.ObservedStateTypeName);
        Assert.Null(result.FailureTypeName);
        Assert.Null(result.HResult);
    }

    [Fact]
    public void TryResetCore_ResetThrows_ReturnsBoundedResetFailure()
    {
        COMException failure = new("reset refused", unchecked((int)0x80004005));

        RdpPasswordResetResult result = RdpPasswordReset.TryResetCore(
            ActiveXInstance,
            _ => (short)0,
            _ => throw failure);

        Assert.Equal(RdpPasswordResetOutcome.ResetPasswordFailed, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Equal(typeof(COMException).FullName, result.FailureTypeName);
        Assert.Equal(unchecked((int)0x80004005), result.HResult);
        Assert.Equal(0, result.ConnectedState);
    }
}
