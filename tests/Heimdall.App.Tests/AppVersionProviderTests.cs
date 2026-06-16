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

public sealed class AppVersionProviderTests
{
    [Fact]
    public void WithShaSuffix_PreservesRawAndDerivesVersionAndDate()
    {
        var provider = new AppVersionProvider("2026.061501+abc123");

        Assert.Equal("2026.061501+abc123", provider.InformationalVersion);
        Assert.NotNull(provider.Current);
        Assert.Equal("2026.061501", provider.Current!.Value.ToString());
        Assert.Equal(new DateOnly(2026, 6, 15), provider.BuildDate);
    }

    [Fact]
    public void WithoutShaSuffix_DerivesVersionAndDate()
    {
        var provider = new AppVersionProvider("2026.061501");

        Assert.NotNull(provider.Current);
        Assert.Equal("2026.061501", provider.Current!.Value.ToString());
        Assert.Equal(new DateOnly(2026, 6, 15), provider.BuildDate);
    }

    [Fact]
    public void UnknownVersion_CurrentAndBuildDateNull()
    {
        var provider = new AppVersionProvider("unknown");

        Assert.Null(provider.Current);
        Assert.Null(provider.BuildDate);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("")]
    [InlineData("2026")]
    public void MalformedOrIncomplete_CurrentOrBuildDateNullWithoutThrow(string informationalVersion)
    {
        var provider = new AppVersionProvider(informationalVersion);

        // Either the version does not parse, or it parses but no valid date can be derived.
        Assert.Null(provider.BuildDate);
    }
}
