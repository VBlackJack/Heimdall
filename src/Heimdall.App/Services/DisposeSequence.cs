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

/// <summary>
/// A disposal in two parts: releases that may fail, then a teardown that must run.
/// </summary>
/// <remarks>
/// The RDP view's dispose ran roughly forty releases ahead of its COM teardown with no
/// try at all, so any one of them throwing skipped the teardown: the ActiveX control
/// was never disconnected, its sink never detached, it never went back to the pool,
/// and the disposed flag was already set so nothing would retry. The shape is a pure
/// function so the guarantee can be tested without a WPF host; the view asserts only
/// that it goes through it.
/// </remarks>
internal static class DisposeSequence
{
    /// <summary>
    /// Runs <paramref name="prologue"/>, reports its failure if it throws, and runs
    /// <paramref name="teardown"/> in every case - even when reporting itself throws.
    /// </summary>
    public static void Run(Action prologue, Action teardown, Action<Exception> onPrologueFailure)
    {
        ArgumentNullException.ThrowIfNull(prologue);
        ArgumentNullException.ThrowIfNull(teardown);
        ArgumentNullException.ThrowIfNull(onPrologueFailure);

        try
        {
            prologue();
        }
        catch (Exception ex)
        {
            onPrologueFailure(ex);
        }
        finally
        {
            teardown();
        }
    }
}
