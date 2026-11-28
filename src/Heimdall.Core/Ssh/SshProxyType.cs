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

namespace Heimdall.Core.Ssh;

/// <summary>
/// Outbound proxy protocol used to reach an SSH server or SSH gateway when direct TCP port 22
/// connectivity is blocked (for example behind a corporate HTTP CONNECT or SOCKS5 proxy).
/// </summary>
public enum SshProxyType
{
    None = 0,
    Http = 1,
    Socks5 = 2,
    Socks4 = 3
}
