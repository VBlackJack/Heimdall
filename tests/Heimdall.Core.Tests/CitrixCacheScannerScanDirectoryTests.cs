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

public sealed class CitrixCacheScannerScanDirectoryTests : IDisposable
{
    private readonly string _tempDir;

    public CitrixCacheScannerScanDirectoryTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "heimdall-citrix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup should not mask assertion failures.
        }
    }

    [Fact]
    public void ScanDirectory_MissingDirectory_ReturnsDirectoryWarning()
    {
        string missingDir = Path.Combine(_tempDir, "missing");

        CitrixScanResult result = CitrixCacheScanner.ScanDirectory(missingDir);

        Assert.Empty(result.Resources);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("Citrix SelfService cache directory not found.", warning);
    }

    [Fact]
    public void ScanDirectory_EmptyDirectory_ReturnsNoCacheFilesWarning()
    {
        CitrixScanResult result = CitrixCacheScanner.ScanDirectory(_tempDir);

        Assert.Empty(result.Resources);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("No Citrix cache files found.", warning);
    }

    [Fact]
    public void ScanDirectory_ValidCacheFile_ParsesResourceFieldsAndSourceFile()
    {
        WriteCacheFile(
            "Store_Cache.xml",
            """
            <root>
              <resource>
                <FriendlyName>Published Excel</FriendlyName>
                <Description>Office spreadsheet</Description>
                <resourceType>Desktop</resourceType>
                <Category>COMMONAPPS - PCI</Category>
                <LaunchCommandLine>SelfService.exe -qlaunch "Published Excel"</LaunchCommandLine>
              </resource>
            </root>
            """);

        CitrixScanResult result = CitrixCacheScanner.ScanDirectory(_tempDir);

        Assert.Empty(result.Warnings);
        CitrixResource resource = Assert.Single(result.Resources);
        Assert.Equal("Published Excel", resource.FriendlyName);
        Assert.Equal("Office spreadsheet", resource.Description);
        Assert.Equal("Desktop", resource.ResourceType);
        Assert.Equal("COMMONAPPS - PCI", resource.Category);
        Assert.Equal(@"SelfService.exe -qlaunch ""Published Excel""", resource.LaunchCommandLine);
        Assert.Equal("Store_Cache.xml", resource.SourceFile);
    }

    [Fact]
    public void ScanDirectory_DefaultXmlNamespace_ParsesResourceByLocalName()
    {
        WriteCacheFile(
            "Namespaced_Cache.xml",
            """
            <root xmlns="urn:citrix:selfservice:test">
              <resource>
                <FriendlyName>Namespaced App</FriendlyName>
                <LaunchCommandLine>SelfService.exe -qlaunch "Namespaced App"</LaunchCommandLine>
              </resource>
            </root>
            """);

        CitrixScanResult result = CitrixCacheScanner.ScanDirectory(_tempDir);

        Assert.Empty(result.Warnings);
        CitrixResource resource = Assert.Single(result.Resources);
        Assert.Equal("Namespaced App", resource.FriendlyName);
    }

    [Fact]
    public void ScanDirectory_MissingFriendlyNameOrLaunchCommandLine_SkipsInvalidResources()
    {
        WriteCacheFile(
            "Mixed_Cache.xml",
            """
            <root>
              <resource>
                <LaunchCommandLine>SelfService.exe -qlaunch "Missing Name"</LaunchCommandLine>
              </resource>
              <resource>
                <FriendlyName>Missing Command</FriendlyName>
              </resource>
              <resource>
                <FriendlyName>Valid App</FriendlyName>
                <LaunchCommandLine>SelfService.exe -qlaunch "Valid App"</LaunchCommandLine>
              </resource>
            </root>
            """);

        CitrixScanResult result = CitrixCacheScanner.ScanDirectory(_tempDir);

        Assert.Empty(result.Warnings);
        CitrixResource resource = Assert.Single(result.Resources);
        Assert.Equal("Valid App", resource.FriendlyName);
    }

    [Fact]
    public void ScanDirectory_FirstValidIcaLaunchUrl_MapsStoreFrontUrlForResources()
    {
        WriteCacheFile(
            "StoreUrl_Cache.xml",
            """
            <root>
              <resource>
                <FriendlyName>First App</FriendlyName>
                <LaunchCommandLine>SelfService.exe -qlaunch "First App"</LaunchCommandLine>
                <icaLaunchUrl>https://first.example.com/Citrix/Store/resources/v2/Q29udG9zbw--</icaLaunchUrl>
              </resource>
              <resource>
                <FriendlyName>Second App</FriendlyName>
                <LaunchCommandLine>SelfService.exe -qlaunch "Second App"</LaunchCommandLine>
                <icaLaunchUrl>https://second.example.com/Citrix/Store/resources/v2/RmFicmlrYW0-</icaLaunchUrl>
              </resource>
            </root>
            """);

        CitrixScanResult result = CitrixCacheScanner.ScanDirectory(_tempDir);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Resources.Count);
        Assert.Equal("https://first.example.com", result.Resources[0].StoreFrontUrl);
        Assert.Equal("https://first.example.com", result.Resources[1].StoreFrontUrl);
    }

    [Fact]
    public void ScanDirectory_MalformedIcaLaunchUrl_DoesNotThrowAndLeavesStoreFrontUrlNull()
    {
        WriteCacheFile(
            "MalformedUrl_Cache.xml",
            """
            <root>
              <resource>
                <FriendlyName>Malformed Url App</FriendlyName>
                <LaunchCommandLine>SelfService.exe -qlaunch "Malformed Url App"</LaunchCommandLine>
                <icaLaunchUrl>://not-a-valid-uri</icaLaunchUrl>
              </resource>
            </root>
            """);

        CitrixScanResult result = CitrixCacheScanner.ScanDirectory(_tempDir);

        Assert.Empty(result.Warnings);
        CitrixResource resource = Assert.Single(result.Resources);
        Assert.Equal("://not-a-valid-uri", resource.IcaLaunchUrl);
        Assert.Null(resource.StoreFrontUrl);
    }

    [Fact]
    public void ScanDirectory_DoctypeExternalEntity_RejectsFileAndDoesNotLeakEntityContent()
    {
        string secret = "LEAKED-ENTITY-CONTENT";
        string secretPath = Path.Combine(_tempDir, "secret.txt");
        File.WriteAllText(secretPath, secret);
        string secretUri = new Uri(secretPath).AbsoluteUri;

        WriteCacheFile(
            "Xxe_Cache.xml",
            $"""
            <!DOCTYPE root [
              <!ENTITY xxe SYSTEM "{secretUri}">
            ]>
            <root>
              <resource>
                <FriendlyName>&xxe;</FriendlyName>
                <LaunchCommandLine>SelfService.exe -qlaunch "XXE"</LaunchCommandLine>
              </resource>
            </root>
            """);

        CitrixScanResult result = CitrixCacheScanner.ScanDirectory(_tempDir);

        Assert.Empty(result.Resources);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("Xxe_Cache.xml", warning);
        Assert.DoesNotContain(result.Resources, resource => ResourceContains(resource, secret));
    }

    [Fact]
    public void ScanDirectory_MalformedFile_WarnsAndContinuesWithOtherFiles()
    {
        WriteCacheFile(
            "Valid_Cache.xml",
            """
            <root>
              <resource>
                <FriendlyName>Valid From Other File</FriendlyName>
                <LaunchCommandLine>SelfService.exe -qlaunch "Valid From Other File"</LaunchCommandLine>
              </resource>
            </root>
            """);
        WriteCacheFile(
            "Broken_Cache.xml",
            """
            <root>
              <resource>
                <FriendlyName>Broken App</FriendlyName>
            """);

        CitrixScanResult result = CitrixCacheScanner.ScanDirectory(_tempDir);

        CitrixResource resource = Assert.Single(result.Resources);
        Assert.Equal("Valid From Other File", resource.FriendlyName);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("Broken_Cache.xml", warning);
    }

    [Fact]
    public void ScanDirectory_ServerStateFilter_KeepsOnlySubscribedAndMissingServerState()
    {
        WriteCacheFile(
            "ServerState_Cache.xml",
            """
            <root>
              <resource>
                <FriendlyName>Subscribed App</FriendlyName>
                <ServerState>Subscribed</ServerState>
                <LaunchCommandLine>-launch -s store-01 -CitrixID store-01@@SubscribedApp -ica https://store.example.test/Citrix/Store/resources/v2/sub1/launch/ica -cmdline</LaunchCommandLine>
              </resource>
              <resource>
                <FriendlyName>Case Insensitive Subscribed App</FriendlyName>
                <ServerState>subscribed</ServerState>
                <LaunchCommandLine>-launch -s store-01 -CitrixID "store-01@@Subscribed App Two" -ica https://store.example.test/Citrix/Store/resources/v2/sub2/launch/ica -cmdline</LaunchCommandLine>
              </resource>
              <resource>
                <FriendlyName>Unsubscribed Catalog App</FriendlyName>
                <ServerState>NotSpecified</ServerState>
                <LaunchCommandLine>-launch -s store-01 -CitrixID store-01@@UnsubscribedApp -ica https://store.example.test/Citrix/Store/resources/v2/unsub/launch/ica -cmdline</LaunchCommandLine>
              </resource>
              <resource>
                <FriendlyName>Legacy OnPrem App Without ServerState</FriendlyName>
                <LaunchCommandLine>SelfService.exe -qlaunch "Legacy OnPrem App Without ServerState"</LaunchCommandLine>
              </resource>
            </root>
            """);

        CitrixScanResult result = CitrixCacheScanner.ScanDirectory(_tempDir);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Resources.Count);
        Assert.Equal("Subscribed App", result.Resources[0].FriendlyName);
        Assert.Equal("store-01@@SubscribedApp", result.Resources[0].CitrixId);
        Assert.Equal("Case Insensitive Subscribed App", result.Resources[1].FriendlyName);
        Assert.Equal("store-01@@Subscribed App Two", result.Resources[1].CitrixId);
        Assert.Equal("Legacy OnPrem App Without ServerState", result.Resources[2].FriendlyName);
        Assert.Null(result.Resources[2].CitrixId);
    }

    [Fact]
    public void ScanDirectory_DuplicateLaunchCommandLineAndTwoPuttyVariants_UsesFirstLaunchCommandAndExtractsDistinctCitrixIds()
    {
        WriteCacheFile(
            "CloudStore_Cache.xml",
            """
            <root>
              <resource>
                <FriendlyName>PuTTy</FriendlyName>
                <ServerState>Subscribed</ServerState>
                <LaunchCommandLine>-launch -s store-01 -CitrixID store-01@@PuTTy -ica https://store.example.test/Citrix/Store/resources/v2/id1/launch/ica -cmdline</LaunchCommandLine>
                <LaunchCommandLine>-launch -s store-01 -CitrixID store-01@@PuTTy -ica https://store.example.test/Citrix/Store/resources/v2/id1/launch/ica -cmdline</LaunchCommandLine>
              </resource>
              <resource>
                <FriendlyName>PuTTy 🛡️</FriendlyName>
                <ServerState>Subscribed</ServerState>
                <LaunchCommandLine>-launch -s store-01 -CitrixID "store-01@@PuTTy 🛡️" -ica https://store.example.test/Citrix/Store/resources/v2/id2/launch/ica -cmdline</LaunchCommandLine>
                <LaunchCommandLine>-launch -s store-01 -CitrixID "store-01@@PuTTy 🛡️" -ica https://store.example.test/Citrix/Store/resources/v2/id2/launch/ica -cmdline</LaunchCommandLine>
              </resource>
            </root>
            """);

        CitrixScanResult result = CitrixCacheScanner.ScanDirectory(_tempDir);

        Assert.Empty(result.Warnings);
        Assert.Equal(2, result.Resources.Count);
        Assert.Equal("PuTTy", result.Resources[0].FriendlyName);
        Assert.Equal("store-01@@PuTTy", result.Resources[0].CitrixId);
        Assert.Equal(
            "-launch -s store-01 -CitrixID store-01@@PuTTy -ica https://store.example.test/Citrix/Store/resources/v2/id1/launch/ica -cmdline",
            result.Resources[0].LaunchCommandLine);
        Assert.Equal("PuTTy 🛡️", result.Resources[1].FriendlyName);
        Assert.Equal("store-01@@PuTTy 🛡️", result.Resources[1].CitrixId);
        Assert.NotEqual(result.Resources[0].CitrixId, result.Resources[1].CitrixId);
    }

    private void WriteCacheFile(string fileName, string content)
    {
        string filePath = Path.Combine(_tempDir, fileName);
        File.WriteAllText(filePath, content);
    }

    private static bool ResourceContains(CitrixResource resource, string text)
    {
        return Contains(resource.CitrixId, text)
            || Contains(resource.FriendlyName, text)
            || Contains(resource.Description, text)
            || Contains(resource.ResourceType, text)
            || Contains(resource.Category, text)
            || Contains(resource.LaunchCommandLine, text)
            || Contains(resource.IcaLaunchUrl, text)
            || Contains(resource.StoreFrontUrl, text)
            || Contains(resource.SourceFile, text);
    }

    private static bool Contains(string? value, string text)
    {
        return value?.Contains(text, StringComparison.Ordinal) == true;
    }
}
