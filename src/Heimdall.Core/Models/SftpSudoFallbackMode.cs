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

namespace Heimdall.Core.Models;

/// <summary>
/// Whether a file operation the SFTP browser was refused may be retried as root through sudo.
/// </summary>
/// <remarks>
/// The numeric order is the strictness order: a lower value is stricter. The organisation cap
/// and the user's choice are combined by taking the lower of the two, so the order is part of
/// the contract and a new member must slot in by strictness, not by name.
/// </remarks>
public enum SftpSudoFallbackMode
{
    /// <summary>A refused operation fails with the server's refusal; root is never tried.</summary>
    Never = 0,

    /// <summary>A refused operation asks the user once before it is retried as root. The default.</summary>
    Ask = 1,

    /// <summary>A refused operation is retried as root without a question.</summary>
    Automatic = 2,
}
