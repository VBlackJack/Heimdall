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

namespace Heimdall.Rdp.ActiveX;

/// <summary>
/// Gives a released host a live window while its asynchronous disconnect settles.
/// </summary>
/// <remarks>
/// <para>
/// The view takes the control out of its window before the disconnect has finished, and the
/// RDP ActiveX control cannot complete a disconnect without a window of its own. Parking the
/// host somewhere that has one, out of sight, lets the disconnect finish; the pool unparks it
/// on every way out of the settling list.
/// </para>
/// <para>
/// <b>Must not throw.</b> The pool guards both calls anyway: a failed park disposes the host
/// at once, and a failed unpark is logged and the exit carries on. The pool does not own the
/// lifetime of the parking object; whoever supplied it disposes it. <b>UI thread only.</b>
/// </para>
/// </remarks>
public interface IHostParking<in T> : IDisposable
{
    /// <summary>Puts a released host where it has a live window while it disconnects.</summary>
    void Park(T host);

    /// <summary>Takes the host back out of the parking window, before it is reset or disposed.</summary>
    void Unpark(T host);
}
