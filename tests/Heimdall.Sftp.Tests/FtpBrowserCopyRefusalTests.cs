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

using Heimdall.Sftp;

namespace Heimdall.Sftp.Tests;

/// <summary>
/// Proves the FTP transport refuses a remote copy rather than performing one it cannot make safe.
/// </summary>
/// <remarks>
/// The refusal must be asserted as a thrown, typed failure. Asserting only that no destination was
/// overwritten would pass just as well if the method silently did nothing, and a caller that
/// believes a copy happened is exactly the outcome to avoid. No connection is needed: the refusal
/// precedes every use of the client.
/// </remarks>
public sealed class FtpBrowserCopyRefusalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyAsync_AlwaysRefuses_WithTheTypedFailure(bool recursive)
    {
        using FtpBrowser browser = new();

        RemoteCopyUnsupportedException refusal =
            await Assert.ThrowsAsync<RemoteCopyUnsupportedException>(
                () => browser.CopyAsync("/src/file.txt", "/dst/file.txt", recursive));

        Assert.Equal("/src/file.txt", refusal.SourcePath);
        Assert.Equal("/dst/file.txt", refusal.DestinationPath);
        Assert.Equal("FTP", refusal.Transport);
    }

    // The documented contract of IRemoteBrowser.CopyAsync is an IOException, so callers that only
    // catch that keep working. The typed subclass is what lets the caller localize the reason.
    [Fact]
    public async Task CopyAsync_Refusal_StillSatisfiesTheDocumentedIOExceptionContract()
    {
        using FtpBrowser browser = new();

        IOException refusal = await Assert.ThrowsAnyAsync<IOException>(
            () => browser.CopyAsync("/src", "/dst", recursive: true));

        Assert.IsType<RemoteCopyUnsupportedException>(refusal);
    }

    [Theory]
    [InlineData("", "/dst")]
    [InlineData("   ", "/dst")]
    [InlineData("/src", "")]
    [InlineData("/src", "   ")]
    public async Task CopyAsync_RejectsBlankPaths_BeforeRefusing(string source, string destination)
    {
        using FtpBrowser browser = new();

        await Assert.ThrowsAsync<ArgumentException>(
            () => browser.CopyAsync(source, destination, recursive: false));
    }
}
