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

using Heimdall.Core.Security.Vault;

namespace Heimdall.Core.Tests.Vault;

public sealed class MasterPasswordPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Short1!")]            // 7 chars
    [InlineData("Abcdef12!")]          // 9 chars
    [InlineData("Abcdefghij1")]        // 11 chars
    public void Validate_BelowMinLength_RejectsTooShort(string password)
    {
        var result = MasterPasswordPolicy.Validate(password);

        Assert.False(result.IsAcceptable);
        Assert.Equal(MasterPasswordPolicyError.TooShort, result.Error);
    }

    [Theory]
    [InlineData("abcdefghijkl")]       // 12, lowercase only (1 class)
    [InlineData("ABCDEFGHIJKL")]       // 12, uppercase only (1 class)
    [InlineData("abcdefgh1234")]       // 12, lower + digit (2 classes)
    public void Validate_TwelvePlusButLowComplexity_RejectsInsufficientComplexity(string password)
    {
        var result = MasterPasswordPolicy.Validate(password);

        Assert.False(result.IsAcceptable);
        Assert.Equal(MasterPasswordPolicyError.InsufficientComplexity, result.Error);
    }

    [Theory]
    [InlineData("Abcdefghij12")]       // 12, upper + lower + digit (3 classes)
    [InlineData("Abcdefghij1!")]       // 12, 4 classes
    [InlineData("Str0ng-Master-Pass")] // mixed, > 12
    public void Validate_TwelvePlusWithComplexity_Accepts(string password)
    {
        var result = MasterPasswordPolicy.Validate(password);

        Assert.True(result.IsAcceptable);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrst")]              // 20, single class -> exempt
    [InlineData("correct horse battery staple")]      // long passphrase, lower + space
    public void Validate_LongPassphrase_ExemptFromComplexity(string password)
    {
        var result = MasterPasswordPolicy.Validate(password);

        Assert.True(result.IsAcceptable);
    }

    [Fact]
    public void Thresholds_MatchDocumentedPolicy()
    {
        Assert.Equal(12, MasterPasswordPolicy.MinLength);
        Assert.Equal(3, MasterPasswordPolicy.MinCharacterClasses);
        Assert.Equal(20, MasterPasswordPolicy.PassphraseExemptionLength);
    }
}
