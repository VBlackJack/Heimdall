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
/// A host that can be returned to a neutral state and serve another session.
/// </summary>
/// <remarks>
/// Exists so the pooling decision can be written, and tested, without a COM apartment:
/// the real host derives from AxHost and cannot be constructed off an STA thread.
/// </remarks>
public interface IReusableHost : IDisposable
{
    /// <summary>
    /// False once something has made reuse unsafe. Such a host is discarded, never pooled.
    /// </summary>
    bool IsReusable { get; }

    /// <summary>
    /// Returns the host to the state a new one would be in.
    /// </summary>
    /// <returns>True when the host may be reused; false when it must be disposed instead.</returns>
    bool ResetForReuse();
}
