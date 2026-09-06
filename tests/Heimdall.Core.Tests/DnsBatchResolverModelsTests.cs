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

using System.Net;
using Heimdall.Core.Network;

namespace Heimdall.Core.Tests;

public sealed class DnsBatchResolverModelsTests
{
    [Fact]
    public void Placeholder_UsesEmDash()
    {
        Assert.Equal("-", DnsBatchResolveResult.Placeholder);
    }

    [Fact]
    public void Ok_MapsFirstIpv4AndIpv6()
    {
        var addresses = new IPAddress[]
        {
            IPAddress.Parse("2001:db8::1"),
            IPAddress.Parse("192.0.2.10"),
            IPAddress.Parse("192.0.2.11"),
        };

        var result = DnsBatchResolveResult.Ok("example.com", addresses, 42);

        Assert.Equal("example.com", result.Hostname);
        Assert.Equal("192.0.2.10", result.Ipv4);
        Assert.Equal("2001:db8::1", result.Ipv6);
        Assert.Equal("OK", result.Status);
        Assert.True(result.Success);
        Assert.Equal(42, result.ResolveTimeMs);
    }

    [Fact]
    public void Ok_NoIpv4_UsesPlaceholder()
    {
        var result = DnsBatchResolveResult.Ok("example.com", [IPAddress.Parse("2001:db8::1")], 10);

        Assert.Equal(DnsBatchResolveResult.Placeholder, result.Ipv4);
        Assert.Equal("2001:db8::1", result.Ipv6);
    }

    [Fact]
    public void Ok_NoIpv6_UsesPlaceholder()
    {
        var result = DnsBatchResolveResult.Ok("example.com", [IPAddress.Parse("192.0.2.10")], 10);

        Assert.Equal("192.0.2.10", result.Ipv4);
        Assert.Equal(DnsBatchResolveResult.Placeholder, result.Ipv6);
    }

    [Fact]
    public void Ok_NullAddresses_UsesPlaceholders()
    {
        var result = DnsBatchResolveResult.Ok("example.com", null, 5);

        Assert.Equal(DnsBatchResolveResult.Placeholder, result.Ipv4);
        Assert.Equal(DnsBatchResolveResult.Placeholder, result.Ipv6);
        Assert.True(result.Success);
    }

    [Fact]
    public void Failed_UsesPlaceholdersAndStatus()
    {
        var result = DnsBatchResolveResult.Failed("bad.example", 7, "HostNotFound");

        Assert.Equal("bad.example", result.Hostname);
        Assert.Equal(DnsBatchResolveResult.Placeholder, result.Ipv4);
        Assert.Equal(DnsBatchResolveResult.Placeholder, result.Ipv6);
        Assert.Equal("HostNotFound", result.Status);
        Assert.False(result.Success);
        Assert.Equal(7, result.ResolveTimeMs);
    }

    [Fact]
    public void Failed_NullStatus_NormalizesToEmpty()
    {
        var result = DnsBatchResolveResult.Failed("bad.example", 7, null);

        Assert.Equal(string.Empty, result.Status);
    }

    [Fact]
    public void RecordEquality_Works()
    {
        var left = DnsBatchResolveResult.Failed("a", 1, "x");
        var right = DnsBatchResolveResult.Failed("a", 1, "x");

        Assert.Equal(left, right);
    }
}
