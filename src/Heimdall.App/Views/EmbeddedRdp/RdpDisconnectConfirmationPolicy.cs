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
/// Runs the disconnect confirmation and turns its outcome - including a failure to ask at all -
/// into a single go/no-go answer.
/// </summary>
/// <remarks>
/// A confirmation that could not be answered is not a Yes. The sibling confirmation for a
/// resolution-driven reconnect already fails closed on the same exception; this one used to swallow
/// it and tear the session down with no prompt ever having been shown.
/// </remarks>
internal static class RdpDisconnectConfirmationPolicy
{
    /// <summary>
    /// Asks for confirmation and reports whether the disconnect may proceed.
    /// </summary>
    /// <param name="ask">Shows the confirmation and returns the user's answer.</param>
    /// <param name="onError">Receives an exception raised while asking.</param>
    internal static async Task<bool> ConfirmAsync(
        Func<Task<bool>> ask,
        Action<Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(onError);

        try
        {
            return await ask().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            onError(ex);
            return false;
        }
    }
}
