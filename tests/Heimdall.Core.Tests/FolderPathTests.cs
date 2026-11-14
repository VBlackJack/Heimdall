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

public class FolderPathTests
{
    [Fact]
    public void Canonicalize_MatchesTheTreeSplit()
    {
        Assert.Equal("A/B/C", FolderPath.Canonicalize("  A\\B//C "));
        Assert.Equal("A/ B", FolderPath.Canonicalize("A/ B"));
        Assert.Equal("A/B", FolderPath.Canonicalize("/A/B/"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("//")]
    public void Canonicalize_TopLevel_IsEmpty(string? path)
    {
        Assert.Equal(string.Empty, FolderPath.Canonicalize(path));
    }
}
