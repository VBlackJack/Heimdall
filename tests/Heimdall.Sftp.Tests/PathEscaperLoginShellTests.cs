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

namespace Heimdall.Sftp.Tests;

/// <summary>Pins <see cref="PathEscaper.EscapeForLoginShell(string)"/>.</summary>
public sealed class PathEscaperLoginShellTests
{
    [Theory]
    [InlineData("/srv/plain", "'/srv/plain'")]
    [InlineData("/srv/a b", "'/srv/a b'")]
    [InlineData("/srv/it's", "'/srv/it'\\''s'")]
    [InlineData("/srv/wow!", "'/srv/wow'\\!''")]
    [InlineData("!a!b", "''\\!'a'\\!'b'")]
    [InlineData("/srv/it's!", "'/srv/it'\\''s'\\!''")]
    public void EscapeForLoginShell_QuotesLikeEscapeForShell_AndNeutralizesEveryBang(string path, string expected)
    {
        Assert.Equal(expected, PathEscaper.EscapeForLoginShell(path));
    }

    [Theory]
    [InlineData("/srv/plain")]
    [InlineData("/srv/a b")]
    [InlineData("/srv/it's")]
    public void EscapeForLoginShell_IsEscapeForShell_WhenThereIsNoBang(string path)
    {
        Assert.Equal(PathEscaper.EscapeForShell(path), PathEscaper.EscapeForLoginShell(path));
    }

    [Fact]
    public void EscapeForLoginShell_RefusesAControlCharacterLikeEscapeForShell()
    {
        Assert.Throws<ArgumentException>(() => PathEscaper.EscapeForLoginShell("/srv/a\nb"));
    }

    [Fact]
    public void EscapeForLoginShell_RefusesNull()
    {
        Assert.Throws<ArgumentNullException>(() => PathEscaper.EscapeForLoginShell(null!));
    }
}
