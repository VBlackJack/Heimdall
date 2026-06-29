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

using System.Text;
using Heimdall.App.Views;

namespace Heimdall.App.Tests;

public sealed class EmbeddedSshViewCurrentDirectoryTests
{
    [Fact]
    public void TryDecodeCurrentDirectoryPayload_DecodesOsc7PathForwardedByTerminal()
    {
        const string path = "/var/log/project space";
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));

        bool decoded = EmbeddedSshView.TryDecodeCurrentDirectoryPayload(payload, out string? result);

        Assert.True(decoded);
        Assert.Equal(path, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64")]
    public void TryDecodeCurrentDirectoryPayload_RejectsMalformedPayload(string payload)
    {
        bool decoded = EmbeddedSshView.TryDecodeCurrentDirectoryPayload(payload, out string? result);

        Assert.False(decoded);
        Assert.Null(result);
    }

    [Fact]
    public void TryDecodeCurrentDirectoryPayload_RejectsEmptyDecodedPath()
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(" "));

        bool decoded = EmbeddedSshView.TryDecodeCurrentDirectoryPayload(payload, out string? result);

        Assert.False(decoded);
        Assert.Null(result);
    }
}
