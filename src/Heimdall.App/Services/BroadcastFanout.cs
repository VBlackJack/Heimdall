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

using System.Text;
using Heimdall.Terminal;

namespace Heimdall.App.Services;

/// <summary>
/// Routes broadcast (type-once, send-to-many) payloads through the same
/// <see cref="SmartPasteGuard"/> the normal terminal paste path uses, so a
/// dangerous or multi-line payload cannot silently fan out to every subscribed
/// terminal. Pure except via the injected write/confirm callbacks, so it is
/// unit-testable without any UI.
/// </summary>
public static class BroadcastFanout
{
    /// <summary>
    /// Evaluates a broadcast payload by decoding it as UTF-8 text and delegating
    /// to <see cref="SmartPasteGuard.Evaluate(string, bool)"/>.
    /// </summary>
    public static SmartPasteGuard.PasteRisk Evaluate(byte[] data, bool isProduction = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        return SmartPasteGuard.Evaluate(Encoding.UTF8.GetString(data), isProduction);
    }

    /// <summary>
    /// Writes <paramref name="data"/> to every writer in <paramref name="writers"/>
    /// after passing the SmartPasteGuard check. A payload classified as
    /// <see cref="SmartPasteGuard.PasteRisk.Safe"/> fans out unconditionally;
    /// any riskier payload is written only when <paramref name="confirmRisky"/>
    /// returns <c>true</c>. Returns the number of writers actually invoked.
    /// Writers throwing <see cref="ObjectDisposedException"/> (closed sessions)
    /// are skipped without aborting the fan-out, preserving sender-exclusion
    /// already applied by the caller's writer list.
    /// </summary>
    public static int Dispatch(
        byte[] data,
        bool isProduction,
        IReadOnlyList<Action<byte[]>> writers,
        Func<SmartPasteGuard.PasteRisk, bool> confirmRisky)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(writers);
        ArgumentNullException.ThrowIfNull(confirmRisky);

        var risk = Evaluate(data, isProduction);
        if (risk != SmartPasteGuard.PasteRisk.Safe && !confirmRisky(risk))
        {
            return 0;
        }

        int written = 0;
        foreach (var writer in writers)
        {
            try
            {
                writer(data);
                written++;
            }
            catch (ObjectDisposedException)
            {
                // Subscriber session already closed; skip it.
            }
        }

        return written;
    }
}
