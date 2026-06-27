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

namespace Heimdall.Core.Tests.Vault;

/// <summary>
/// xUnit collection that serializes every test class mutating the static state
/// of <c>CredentialProtector</c> (the legacy HMAC key slot and the vault DEK
/// slot). Without this, the default per-class parallelism would let one class
/// install a vault DEK while another asserts legacy Protect/Unprotect output,
/// producing flaky cross-talk. Members run sequentially relative to each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CredentialProtectorStaticCollection
{
    /// <summary>The collection name shared by all members.</summary>
    public const string Name = "CredentialProtectorStatic";
}
