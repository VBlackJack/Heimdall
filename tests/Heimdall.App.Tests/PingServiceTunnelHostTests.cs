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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

public class PingServiceTunnelHostTests
{
    [Theory]
    [InlineData("-f")]
    [InlineData("--help")]
    [InlineData("")]
    public void EnsureValidTunnelHost_OptionLikeHost_Throws(string host)
    {
        Assert.Throws<ArgumentException>(() => PingService.EnsureValidTunnelHost(host));
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("10.0.0.1")]
    [InlineData("example.com")]
    public void EnsureValidTunnelHost_ValidHost_DoesNotThrow(string host)
    {
        PingService.EnsureValidTunnelHost(host);
    }
}
