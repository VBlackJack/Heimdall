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

using Heimdall.Core.Configuration;

namespace Heimdall.Core.Tests;

public sealed class CitrixCacheScannerTests
{
    [Fact]
    public void ToServerProfiles_EmptyInput_ReturnsEmptyList()
    {
        CitrixResource[] resources = [];

        List<ServerProfileDto> profiles = CitrixCacheScanner.ToServerProfiles(resources);

        Assert.Empty(profiles);
    }

    [Fact]
    public void ToServerProfiles_SingleResource_MapsConstantAndPassthroughFields()
    {
        CitrixResource resource = BuildResource(
            citrixId: "store-01@@Excel2024",
            friendlyName: "Published Excel",
            launchCommandLine: @"SelfService.exe -qlaunch ""Published Excel""",
            storeFrontUrl: "https://store.example.test");
        CitrixResource[] resources = [resource];

        List<ServerProfileDto> profiles = CitrixCacheScanner.ToServerProfiles(resources);

        ServerProfileDto profile = Assert.Single(profiles);
        Assert.Equal("store-01@@Excel2024", profile.CitrixId);
        Assert.Equal("Published Excel", profile.DisplayName);
        Assert.Equal("Published Excel", profile.CitrixAppName);
        Assert.Equal("Citrix", profile.ConnectionType);
        Assert.Equal(@"SelfService.exe -qlaunch ""Published Excel""", profile.CitrixLaunchCommandLine);
        Assert.Equal("https://store.example.test", profile.CitrixStoreFrontUrl);
        Assert.True(profile.CitrixUseSso);
        Assert.True(profile.UseDirectConnection);
    }

    [Theory]
    [InlineData(null, "Citrix Local")]
    [InlineData("", "Citrix Local")]
    [InlineData("   ", "Citrix Local")]
    [InlineData("https://sf.example.test", "https://sf.example.test")]
    public void ToServerProfiles_StoreFrontUrl_MapsRemoteServerFallback(
        string? storeFrontUrl,
        string expectedRemoteServer)
    {
        CitrixResource[] resources = [BuildResource(storeFrontUrl: storeFrontUrl)];

        List<ServerProfileDto> profiles = CitrixCacheScanner.ToServerProfiles(resources);

        ServerProfileDto profile = Assert.Single(profiles);
        Assert.Equal(expectedRemoteServer, profile.RemoteServer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToServerProfiles_BlankCategory_MapsNullGroup(string? category)
    {
        CitrixResource[] resources = [BuildResource(category: category)];

        List<ServerProfileDto> profiles = CitrixCacheScanner.ToServerProfiles(resources);

        ServerProfileDto profile = Assert.Single(profiles);
        Assert.Null(profile.Group);
    }

    [Fact]
    public void ToServerProfiles_PlainCategory_PrefixesGroup()
    {
        CitrixResource[] resources = [BuildResource(category: "Productivity")];

        List<ServerProfileDto> profiles = CitrixCacheScanner.ToServerProfiles(resources);

        ServerProfileDto profile = Assert.Single(profiles);
        Assert.Equal("Citrix/Productivity", profile.Group);
    }

    [Fact]
    public void ToServerProfiles_CategoryBackslashes_NormalizesGroupSeparators()
    {
        CitrixResource[] resources = [BuildResource(category: @"Dept\Team\Sub")];

        List<ServerProfileDto> profiles = CitrixCacheScanner.ToServerProfiles(resources);

        ServerProfileDto profile = Assert.Single(profiles);
        Assert.Equal("Citrix/Dept/Team/Sub", profile.Group);
    }

    [Fact]
    public void ToServerProfiles_MultipleResources_AssignsDistinctParseableIds()
    {
        CitrixResource[] resources =
        [
            BuildResource(friendlyName: "First App"),
            BuildResource(friendlyName: "Second App")
        ];

        List<ServerProfileDto> profiles = CitrixCacheScanner.ToServerProfiles(resources);

        Assert.Equal(2, profiles.Count);
        Assert.True(Guid.TryParse(profiles[0].Id, out Guid firstId));
        Assert.True(Guid.TryParse(profiles[1].Id, out Guid secondId));
        Assert.NotEqual(firstId, secondId);
        Assert.NotEqual(profiles[0].Id, profiles[1].Id);
    }

    [Fact]
    public void ToServerProfiles_MultipleResources_PreservesCountAndOrder()
    {
        CitrixResource[] resources =
        [
            BuildResource(friendlyName: "First App"),
            BuildResource(friendlyName: "Second App"),
            BuildResource(friendlyName: "Third App")
        ];

        List<ServerProfileDto> profiles = CitrixCacheScanner.ToServerProfiles(resources);

        Assert.Equal(3, profiles.Count);
        Assert.Equal("First App", profiles[0].DisplayName);
        Assert.Equal("Second App", profiles[1].DisplayName);
        Assert.Equal("Third App", profiles[2].DisplayName);
    }

    [Theory]
    [InlineData(
        "-launch -s store-01 -CitrixID store-01@@PuTTy -ica https://store.example.test/Citrix/Store/resources/v2/abc/launch/ica -cmdline",
        "store-01@@PuTTy")]
    [InlineData(
        "-launch -s store-01 -CitrixID \"store-01@@PuTTy Admin\" -ica https://store.example.test/Citrix/Store/resources/v2/def/launch/ica -cmdline",
        "store-01@@PuTTy Admin")]
    [InlineData(
        "-launch -s store-01 -CitrixID \"store-01@@PuTTy 🛡️\" -ica https://store.example.test/Citrix/Store/resources/v2/ghi/launch/ica -cmdline",
        "store-01@@PuTTy 🛡️")]
    [InlineData(
        "-launch -s store-01 -CitrixID store-01@@PuTTy_🛡️ -ica https://store.example.test/Citrix/Store/resources/v2/jkl/launch/ica -cmdline",
        "store-01@@PuTTy_🛡️")]
    public void ExtractCitrixId_ValidFlag_ReturnsExpectedId(string launchCommandLine, string expectedCitrixId)
    {
        string? extracted = CitrixCacheScanner.ExtractCitrixId(launchCommandLine);

        Assert.Equal(expectedCitrixId, extracted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"SelfService.exe -qlaunch ""Published App""")]
    [InlineData("-launch -s store-01 -CitrixIdentity store-01@@WrongFlag -cmdline")]
    [InlineData("-launch -s store-01 -CitrixID")]
    [InlineData("-launch -s store-01 -CitrixID   ")]
    [InlineData("-launch -s store-01 -CitrixID \"\" -cmdline")]
    [InlineData("-launch -s store-01 -CitrixID \"   \" -cmdline")]
    [InlineData("-launch -s store-01 -CitrixID \"unterminated -cmdline")]
    [InlineData("-launch -s store-01 -CitrixID -ica https://store.example.test/Citrix/Store/resources/v2/abc/launch/ica -cmdline")]
    public void ExtractCitrixId_AbsentOrEmptyValue_ReturnsNull(string launchCommandLine)
    {
        string? extracted = CitrixCacheScanner.ExtractCitrixId(launchCommandLine);

        Assert.Null(extracted);
    }

    [Fact]
    public void ToServerProfiles_TwoPuttyVariants_PreservesDistinctCitrixIds()
    {
        CitrixResource[] resources =
        [
            BuildResource(
                citrixId: "store-01@@PuTTy.Standard",
                friendlyName: "PuTTy",
                launchCommandLine: "-launch -s store-01 -CitrixID store-01@@PuTTy.Standard -ica https://store.example.test/Citrix/Store/resources/v2/a/launch/ica -cmdline"),
            BuildResource(
                citrixId: "store-01@@PuTTy.Elevated",
                friendlyName: "PuTTy 🛡️",
                launchCommandLine: "-launch -s store-01 -CitrixID \"store-01@@PuTTy.Elevated\" -ica https://store.example.test/Citrix/Store/resources/v2/b/launch/ica -cmdline")
        ];

        List<ServerProfileDto> profiles = CitrixCacheScanner.ToServerProfiles(resources);

        Assert.Equal(2, profiles.Count);
        Assert.Equal("PuTTy", profiles[0].DisplayName);
        Assert.Equal("PuTTy 🛡️", profiles[1].DisplayName);
        Assert.Equal("store-01@@PuTTy.Standard", profiles[0].CitrixId);
        Assert.Equal("store-01@@PuTTy.Elevated", profiles[1].CitrixId);
        Assert.NotEqual(profiles[0].CitrixId, profiles[1].CitrixId);
    }

    private static CitrixResource BuildResource(
        string? citrixId = null,
        string friendlyName = "Published App",
        string? category = "Applications",
        string launchCommandLine = @"SelfService.exe -qlaunch ""Published App""",
        string? storeFrontUrl = "https://store.example.test")
    {
        return new CitrixResource
        {
            CitrixId = citrixId,
            FriendlyName = friendlyName,
            Category = category,
            LaunchCommandLine = launchCommandLine,
            StoreFrontUrl = storeFrontUrl
        };
    }
}
