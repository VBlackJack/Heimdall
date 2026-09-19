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
using System.Windows.Threading;

namespace Heimdall.App.Tests;

/// <summary>
/// Runs an asynchronous body on a dedicated STA thread whose dispatcher is the current
/// synchronization context.
/// </summary>
/// <remarks>
/// <para>
/// Two things need this. Bindings and <c>Selector</c> selection logic are dispatcher
/// affine, so exercising them at all requires a dispatcher thread. And an await without
/// <c>ConfigureAwait(false)</c> only resumes where it started when there is a
/// synchronization context to capture, which is what makes thread affinity observable
/// rather than accidental.
/// </para>
/// <para>
/// No <c>Window</c> and no <c>Application</c> are created here on purpose: building
/// either seals the application-level resources for the whole test process and takes
/// unrelated tests down with it. Controls are exercised unrendered, which is enough for
/// items, selection and binding behaviour.
/// </para>
/// </remarks>
internal static class StaDispatcherRunner
{
    public static void Run(Func<Task> body)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));

            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    dispatcher.InvokeShutdown();
                }
            });

            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
