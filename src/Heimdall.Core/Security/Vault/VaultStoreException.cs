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

namespace Heimdall.Core.Security.Vault;

/// <summary>
/// Thrown when a store outside the configuration files could not be read or updated during a vault
/// operation. Names the store so that a damaged one can be found.
/// </summary>
public sealed class VaultStoreException : Exception
{
    /// <summary>Create the exception for a failing store.</summary>
    /// <param name="storeNameKey">The locale key naming the store.</param>
    /// <param name="innerException">The failure.</param>
    public VaultStoreException(string storeNameKey, Exception innerException)
        : base("A store could not be read or updated.", innerException)
    {
        ArgumentException.ThrowIfNullOrEmpty(storeNameKey);
        StoreNameKey = storeNameKey;
    }

    /// <summary>The locale key naming the store.</summary>
    public string StoreNameKey { get; }
}
