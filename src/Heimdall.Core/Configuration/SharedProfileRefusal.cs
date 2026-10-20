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

namespace Heimdall.Core.Configuration;

/// <summary>Why a session read from a shared file is not loaded as a shared session.</summary>
/// <remarks>The order is the order the checks run in.</remarks>
public enum SharedProfileRefusal
{
    /// <summary>Its identifier is outside the shared namespace.</summary>
    ForeignId,

    /// <summary>Its protocol is neither SSH nor RDP.</summary>
    UnsupportedProtocol,

    /// <summary>Its host is one <see cref="SchemaValidator.IsValidHost"/> refuses.</summary>
    InvalidHost,

    /// <summary>Its gateway reference is outside the shared namespace.</summary>
    ForeignGateway,

    /// <summary>
    /// Its gateway is not among the usable gateways of its file: the file does not define it, or
    /// defines it with a refused host. Decided by the reader of the file, which alone holds them.
    /// </summary>
    UnknownGateway,

    /// <summary>Its RD Gateway is a host <see cref="SchemaValidator.IsValidHost"/> refuses.</summary>
    InvalidRdGateway,
}
