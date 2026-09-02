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

using System.Runtime.ExceptionServices;
using System.Threading;

namespace Heimdall.App.Tests.Views.EmbeddedRdp;

/// <summary>
/// Runs a body on a private STA thread.
/// </summary>
/// <remarks>
/// <para>Constructing any <c>FrameworkElement</c> requires an STA apartment, and the xUnit runner
/// is MTA. A private thread is used rather than the shared WPF test host on purpose: nothing here
/// builds a <c>Window</c> or an <c>Application</c>, so no app-level style is sealed onto a shared
/// dispatcher and no other test in the assembly can be affected by thread affinity.</para>
/// <para>The thread is joined before the assertion runs, so the element and its automation peer are
/// created and read entirely inside it.</para>
/// </remarks>
internal static class StaRunner
{
    internal static T Run<T>(Func<T> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        T result = default!;
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        failure?.Throw();
        return result;
    }
}
