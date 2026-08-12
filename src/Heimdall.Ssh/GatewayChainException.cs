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

namespace Heimdall.Ssh;

/// <summary>
/// Raised by <see cref="GatewayChainResolver"/> when gateway chain resolution
/// fails due to a structural problem in the chain (circular dependency or
/// excessive depth). Derives from <see cref="InvalidOperationException"/> so
/// existing callers that catch that type, or the broader <see cref="Exception"/>,
/// keep working unchanged while gaining access to a structured
/// <see cref="SshFailureCode"/> for i18n-aware error reporting.
/// </summary>
public sealed class GatewayChainException : InvalidOperationException
{
    public GatewayChainException(SshFailureCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public GatewayChainException(SshFailureCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public SshFailureCode Code { get; }
}
