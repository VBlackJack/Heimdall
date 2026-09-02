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

namespace Heimdall.App.Views.EmbeddedRdp;

/// <summary>
/// Writes a text payload to the clipboard and reports whether it landed.
/// </summary>
/// <remarks>
/// The clipboard is a single shared Win32 resource: another process holding it makes the write
/// throw. Swallowing that leaves a button that visibly does nothing, and the user pastes whatever
/// the clipboard held before into a support ticket. The result is returned so the caller can say so.
/// </remarks>
internal static class RdpClipboardCopy
{
    internal static bool TryCopy(Action<string> setter, string payload, Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(setter);
        ArgumentNullException.ThrowIfNull(onFailure);

        try
        {
            setter(payload);
            return true;
        }
        catch (Exception ex)
        {
            onFailure(ex);
            return false;
        }
    }
}
