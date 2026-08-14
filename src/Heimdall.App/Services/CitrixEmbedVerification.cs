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

namespace Heimdall.App.Services;

/// <summary>Which step of the Citrix window embedding failed, if any.</summary>
internal enum CitrixEmbedFailure
{
    /// <summary>Every step reported success and the reparent took effect.</summary>
    None,

    /// <summary>Reading the current window style failed, so the style computed from it is garbage.</summary>
    ReadWindowStyle,

    /// <summary>Writing the child window style failed.</summary>
    ApplyWindowStyle,

    /// <summary>Reparenting the window into the host panel failed.</summary>
    Reparent,

    /// <summary>Every call reported success, but the window is not actually parented to the host.</summary>
    ParentMismatch,
}

/// <summary>Outcome of the embedding verification.</summary>
/// <param name="Failure">The failing step, or <see cref="CitrixEmbedFailure.None"/>.</param>
internal readonly record struct CitrixEmbedVerdict(CitrixEmbedFailure Failure)
{
    /// <summary>True when the window is verifiably hosted in the embedded panel.</summary>
    public bool Succeeded => Failure == CitrixEmbedFailure.None;
}

/// <summary>
/// Decides whether a Citrix window was really embedded, from the values observed around the
/// native calls. Pure: it performs no P/Invoke and receives numbers, so the decision is testable
/// without a live session.
/// </summary>
/// <remarks>
/// A zero return is ambiguous for the two window-style calls: both return the previous value, and
/// zero is a perfectly ordinary previous style. Microsoft documents the disambiguation as
/// clear-the-last-error, call, then check the last error when the return is zero. Each call
/// therefore carries its OWN captured error code here - sharing one code across the three would
/// make the failing step indistinguishable.
/// </remarks>
internal static class CitrixEmbedVerification
{
    /// <param name="readStyleResult">Value returned by <c>GetWindowLong</c>.</param>
    /// <param name="readStyleLastError">Last error captured immediately after that call.</param>
    /// <param name="applyStyleResult">Value returned by <c>SetWindowLong</c>.</param>
    /// <param name="applyStyleLastError">Last error captured immediately after that call.</param>
    /// <param name="reparentResult">Previous parent returned by <c>SetParent</c>; <c>NULL</c> on failure.</param>
    /// <param name="observedParent">Parent read back with <c>GetParent</c> after the reparent.</param>
    /// <param name="expectedParent">Handle of the host panel the window must be parented to.</param>
    internal static CitrixEmbedVerdict Verify(
        uint readStyleResult,
        int readStyleLastError,
        int applyStyleResult,
        int applyStyleLastError,
        nint reparentResult,
        nint observedParent,
        nint expectedParent)
    {
        if (readStyleResult == 0 && readStyleLastError != 0)
        {
            return new CitrixEmbedVerdict(CitrixEmbedFailure.ReadWindowStyle);
        }

        if (applyStyleResult == 0 && applyStyleLastError != 0)
        {
            return new CitrixEmbedVerdict(CitrixEmbedFailure.ApplyWindowStyle);
        }

        if (reparentResult == nint.Zero)
        {
            return new CitrixEmbedVerdict(CitrixEmbedFailure.Reparent);
        }

        // The only signal that catches a reparent which reported success but did not take.
        if (observedParent != expectedParent)
        {
            return new CitrixEmbedVerdict(CitrixEmbedFailure.ParentMismatch);
        }

        return new CitrixEmbedVerdict(CitrixEmbedFailure.None);
    }
}
