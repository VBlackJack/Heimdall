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

public sealed class RemoteDeleteCommandTests
{
    [Fact]
    public void Build_UsesStableLocaleRecursiveRmAndEndOfOptionsGuard()
    {
        string command = RemoteDeleteCommand.Build("/srv/tree");

        Assert.Equal("LC_ALL=C rm -r -- '/srv/tree'", command);
    }

    [Fact]
    public void Build_EscapesPathWithSpaces()
    {
        string command = RemoteDeleteCommand.Build("/srv/my tree");

        Assert.Equal("LC_ALL=C rm -r -- '/srv/my tree'", command);
    }

    [Fact]
    public void Build_EscapesEmbeddedSingleQuote()
    {
        string command = RemoteDeleteCommand.Build("/srv/it's mine");

        Assert.Equal("LC_ALL=C rm -r -- '/srv/it'\\''s mine'", command);
    }

    [Fact]
    public void Build_GuardsOptionLookingPath()
    {
        string command = RemoteDeleteCommand.Build("-rf");

        Assert.Equal("LC_ALL=C rm -r -- '-rf'", command);
    }

    [Theory]
    [InlineData("/srv/bad\nname")]
    [InlineData("/srv/bad\tname")]
    public void Build_RejectsControlCharacters(string path)
    {
        Assert.Throws<ArgumentException>(() => RemoteDeleteCommand.Build(path));
    }
}
