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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// Verifies the Win32 verdict used before a Citrix session is announced as connected. The pairs
/// that matter are the zero returns: a window-style call returning zero is a success when the
/// previous style was genuinely zero, and a failure only when the last error is non-zero.
/// </summary>
public sealed class CitrixEmbedVerificationTests
{
    private static readonly nint HostPanel = 0x1000;
    private static readonly nint PreviousParent = 0x2000;

    [Fact]
    public void Verify_EveryCallSucceededAndParentMatches_ReportsSuccess()
    {
        CitrixEmbedVerdict verdict = CitrixEmbedVerification.Verify(
            readStyleResult: 0x10000,
            readStyleLastError: 0,
            applyStyleResult: 0x10000,
            applyStyleLastError: 0,
            reparentResult: PreviousParent,
            observedParent: HostPanel,
            expectedParent: HostPanel);

        Assert.True(verdict.Succeeded);
        Assert.Equal(CitrixEmbedFailure.None, verdict.Failure);
    }

    [Fact]
    public void Verify_ReadStyleReturnsZeroWithoutError_IsASuccessBecauseTheStyleWasGenuinelyZero()
    {
        CitrixEmbedVerdict verdict = CitrixEmbedVerification.Verify(
            readStyleResult: 0,
            readStyleLastError: 0,
            applyStyleResult: 0x10000,
            applyStyleLastError: 0,
            reparentResult: PreviousParent,
            observedParent: HostPanel,
            expectedParent: HostPanel);

        Assert.True(verdict.Succeeded);
    }

    [Fact]
    public void Verify_ReadStyleReturnsZeroWithError_FailsOnTheReadStep()
    {
        CitrixEmbedVerdict verdict = CitrixEmbedVerification.Verify(
            readStyleResult: 0,
            readStyleLastError: 1400,
            applyStyleResult: 0x10000,
            applyStyleLastError: 0,
            reparentResult: PreviousParent,
            observedParent: HostPanel,
            expectedParent: HostPanel);

        Assert.False(verdict.Succeeded);
        Assert.Equal(CitrixEmbedFailure.ReadWindowStyle, verdict.Failure);
    }

    [Fact]
    public void Verify_ApplyStyleReturnsZeroWithoutError_IsASuccessBecauseThePreviousValueWasZero()
    {
        CitrixEmbedVerdict verdict = CitrixEmbedVerification.Verify(
            readStyleResult: 0x10000,
            readStyleLastError: 0,
            applyStyleResult: 0,
            applyStyleLastError: 0,
            reparentResult: PreviousParent,
            observedParent: HostPanel,
            expectedParent: HostPanel);

        Assert.True(verdict.Succeeded);
    }

    [Fact]
    public void Verify_ApplyStyleReturnsZeroWithError_FailsOnTheApplyStep()
    {
        CitrixEmbedVerdict verdict = CitrixEmbedVerification.Verify(
            readStyleResult: 0x10000,
            readStyleLastError: 0,
            applyStyleResult: 0,
            applyStyleLastError: 1400,
            reparentResult: PreviousParent,
            observedParent: HostPanel,
            expectedParent: HostPanel);

        Assert.False(verdict.Succeeded);
        Assert.Equal(CitrixEmbedFailure.ApplyWindowStyle, verdict.Failure);
    }

    [Fact]
    public void Verify_ReparentReturnsNull_FailsOnTheReparentStep()
    {
        CitrixEmbedVerdict verdict = CitrixEmbedVerification.Verify(
            readStyleResult: 0x10000,
            readStyleLastError: 0,
            applyStyleResult: 0x10000,
            applyStyleLastError: 0,
            reparentResult: nint.Zero,
            observedParent: HostPanel,
            expectedParent: HostPanel);

        Assert.False(verdict.Succeeded);
        Assert.Equal(CitrixEmbedFailure.Reparent, verdict.Failure);
    }

    // The postcondition: every return value looked fine, yet the window is elsewhere. Nothing but
    // reading the parent back catches a reparent that reported success and did not take.
    [Fact]
    public void Verify_ParentDiffersFromTheHostPanel_FailsEvenThoughEveryReturnLookedFine()
    {
        CitrixEmbedVerdict verdict = CitrixEmbedVerification.Verify(
            readStyleResult: 0x10000,
            readStyleLastError: 0,
            applyStyleResult: 0x10000,
            applyStyleLastError: 0,
            reparentResult: PreviousParent,
            observedParent: 0x9999,
            expectedParent: HostPanel);

        Assert.False(verdict.Succeeded);
        Assert.Equal(CitrixEmbedFailure.ParentMismatch, verdict.Failure);
    }
}
