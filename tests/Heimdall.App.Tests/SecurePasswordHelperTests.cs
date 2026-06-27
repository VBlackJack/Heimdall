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

using System.Runtime.Versioning;
using System.Security;
using Heimdall.App.Services;

namespace Heimdall.App.Tests;

[SupportedOSPlatform("windows")]
public sealed class SecurePasswordHelperTests
{
    private static SecureString Secure(string value)
    {
        var secure = new SecureString();
        foreach (var c in value)
        {
            secure.AppendChar(c);
        }

        secure.MakeReadOnly();
        return secure;
    }

    [Fact]
    public void ToChars_ReturnsExactCharacters()
    {
        using var secure = Secure("P@ssw0rd-master-2026");

        var chars = SecurePasswordHelper.ToChars(secure);
        try
        {
            Assert.Equal("P@ssw0rd-master-2026", new string(chars));
        }
        finally
        {
            Array.Clear(chars);
        }
    }

    [Fact]
    public void ToChars_EmptySecureString_ReturnsEmpty()
    {
        using var secure = new SecureString();
        secure.MakeReadOnly();

        Assert.Empty(SecurePasswordHelper.ToChars(secure));
    }

    [Fact]
    public void ToChars_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SecurePasswordHelper.ToChars(null!));
    }
}
