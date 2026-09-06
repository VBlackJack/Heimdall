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

public sealed class RemotePathNormalizerTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData("/srv/data", "/srv/data")]
    [InlineData("/srv/data/", "/srv/data")]
    [InlineData("srv/data", "/srv/data")]
    [InlineData("/srv//data", "/srv/data")]
    [InlineData("/srv/./data", "/srv/data")]
    [InlineData("/srv/data/..", "/srv")]
    [InlineData("/srv/data/../elsewhere", "/srv/elsewhere")]
    [InlineData("/../../etc", "/etc")]
    [InlineData("/..", "/")]
    [InlineData("/a/b/../../c", "/c")]
    public void Collapse_AppliesDotSegmentsWithoutLeavingTheRoot(string input, string expected)
    {
        Assert.Equal(expected, RemotePathNormalizer.Collapse(input));
    }

    [Fact]
    public void Collapse_LeavesDotDotInsideNamesAlone()
    {
        Assert.Equal("/srv/my..file", RemotePathNormalizer.Collapse("/srv/my..file"));
    }
}
