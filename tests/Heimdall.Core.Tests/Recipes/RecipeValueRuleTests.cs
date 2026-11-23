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

using Heimdall.Core.Recipes;

namespace Heimdall.Core.Tests.Recipes;

/// <summary>The rule every substituted value passes at run time.</summary>
public sealed class RecipeValueRuleTests
{
    private static RecipeInput Field(RecipeInputType type) => RecipeTestData.Input("f", type);

    private static RecipeValueRefusal Check(RecipeInputType type, string? value, bool startsToken = false)
        => RecipeValueRule.Check(Field(type), value, startsToken);

    [Fact]
    public void AnUnpairedSurrogate_IsRefusedForEveryType()
    {
        // Built at run time: a lone surrogate does not survive being passed as attribute data.
        string value = new(['a', '\uD800', 'b']);

        foreach (RecipeInputType type in Enum.GetValues<RecipeInputType>())
            Assert.Equal(RecipeValueRefusal.ControlCharacter, Check(type, value));

        Assert.Equal(RecipeValueRefusal.ControlCharacter, RecipeValueRule.CheckHost(value, false));
    }

    [Theory]
    [InlineData("a\u0001b")]
    [InlineData("a\tb")]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData("a\0b")]
    [InlineData("a\u007Fb")]
    [InlineData("a\u0085b")]
    [InlineData("a\u202Eb")]
    [InlineData("\u202Etxt.exe")]
    [InlineData("a\u200Bb")]
    [InlineData("a\u2028b")]
    public void AControlOrFormatCharacter_IsRefusedForEveryType(string value)
    {
        foreach (RecipeInputType type in Enum.GetValues<RecipeInputType>())
        {
            RecipeValueRefusal verdict = Check(type, value);
            Assert.Equal(RecipeValueRefusal.ControlCharacter, verdict);
        }

        Assert.Equal(RecipeValueRefusal.ControlCharacter, RecipeValueRule.CheckHost(value, false));
        Assert.Equal(RecipeValueRefusal.ControlCharacter, RecipeValueRule.CheckPort(value, false));
    }

    [Theory]
    [InlineData("-v", RecipeValueRefusal.LeadingDash)]
    [InlineData("--x", RecipeValueRefusal.LeadingDash)]
    [InlineData("/c", RecipeValueRefusal.LeadingSlash)]
    [InlineData("@file", RecipeValueRefusal.LeadingAt)]
    public void ALeadingDashSlashOrAt_IsRefusedWhenThePlaceholderStartsItsToken(string value, RecipeValueRefusal expected)
    {
        Assert.Equal(expected, Check(RecipeInputType.String, value, startsToken: true));
    }

    [Theory]
    [InlineData("-v")]
    [InlineData("/c")]
    [InlineData("@file")]
    [InlineData("a-b")]
    [InlineData("a/b@c")]
    public void ALeadingDashSlashOrAt_IsAcceptedWhenThePlaceholderDoesNotStartItsToken(string value)
    {
        Assert.Equal(RecipeValueRefusal.None, Check(RecipeInputType.String, value, startsToken: false));
    }

    [Theory]
    [InlineData("a-b")]
    [InlineData("a/b")]
    [InlineData("a@b")]
    [InlineData(" lead")]
    public void ADashSlashOrAtInsideTheValue_IsAcceptedEvenAtTokenStart(string value)
    {
        RecipeValueRefusal verdict = Check(RecipeInputType.String, value, startsToken: true);

        Assert.Equal(RecipeValueRefusal.None, verdict);
    }

    [Fact]
    public void AnEmptyValue_IsRefusedAsEmpty()
    {
        Assert.Equal(RecipeValueRefusal.Empty, Check(RecipeInputType.String, string.Empty));
        Assert.Equal(RecipeValueRefusal.Empty, Check(RecipeInputType.String, null));
        Assert.Equal(RecipeValueRefusal.Empty, RecipeValueRule.CheckHost(null, false));
        Assert.Equal(RecipeValueRefusal.Empty, RecipeValueRule.CheckPort(string.Empty, false));
    }

    // ---- path ----

    [Theory]
    [InlineData(@"C:\Users\me\file.txt")]
    [InlineData(@"C:\")]
    [InlineData("C:")]
    [InlineData("c:/temp/x")]
    [InlineData(@"relative\dir\file.txt")]
    [InlineData("file.txt")]
    [InlineData(@"..\up\file.txt")]
    [InlineData(@".\here.txt")]
    [InlineData(@"C:\dir\.\x")]
    [InlineData("console.txt")]
    [InlineData("COM10")]
    [InlineData("COM0")]
    [InlineData("LPT")]
    [InlineData("my.con.txt")]
    public void AnOrdinaryPath_IsAccepted(string path)
    {
        Assert.Equal(RecipeValueRefusal.None, Check(RecipeInputType.Path, path));
    }

    [Theory]
    [InlineData(@"\\server\share\file", RecipeValueRefusal.UncPath)]
    [InlineData(@"\\server", RecipeValueRefusal.UncPath)]
    [InlineData("//server/share", RecipeValueRefusal.UncPath)]
    [InlineData(@"\/server", RecipeValueRefusal.UncPath)]
    [InlineData(@"\\?\C:\x", RecipeValueRefusal.DevicePath)]
    [InlineData(@"\\.\PhysicalDrive0", RecipeValueRefusal.DevicePath)]
    [InlineData("//?/C:/x", RecipeValueRefusal.DevicePath)]
    [InlineData("//./pipe/x", RecipeValueRefusal.DevicePath)]
    public void AUncOrDevicePath_IsRefused(string path, RecipeValueRefusal expected)
    {
        Assert.Equal(expected, Check(RecipeInputType.Path, path));
    }

    [Theory]
    [InlineData(@"\rooted\dir")]
    [InlineData("/rooted/dir")]
    [InlineData(@"\")]
    [InlineData(@"\??\C:\x")]
    public void AnyPathStartingWithASingleSeparator_IsRefusedAsRooted(string path)
    {
        Assert.Equal(RecipeValueRefusal.RootedPath, Check(RecipeInputType.Path, path));
        Assert.Equal(RecipeValueRefusal.RootedPath, Check(RecipeInputType.Path, path, startsToken: false));
    }

    [Fact]
    public void TheSeparatorJoinRule_LooksAtBothNeighbours()
    {
        Assert.Equal(RecipeValueRefusal.SeparatorJoin, RecipeValueRule.CheckSeparatorJoin("/x", '/', null));
        Assert.Equal(RecipeValueRefusal.SeparatorJoin, RecipeValueRule.CheckSeparatorJoin("x/", null, '/'));
        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.CheckSeparatorJoin("x", '/', '/'));
        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.CheckSeparatorJoin("/x", 'a', null));
        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.CheckSeparatorJoin("x/", null, 'a'));
        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.CheckSeparatorJoin("/x", null, null));
    }

    [Theory]
    [InlineData(@"C:\dir\file.txt:stream")]
    [InlineData("file.txt:stream")]
    [InlineData(":x")]
    [InlineData("1:x")]
    [InlineData("C:file.txt")]
    [InlineData(@"C:\a:b")]
    [InlineData(@"C:\??\x:y")]
    [InlineData("a::$DATA")]
    public void AColonAfterTheDrivePosition_IsRefused(string path)
    {
        Assert.Equal(RecipeValueRefusal.ColonInPath, Check(RecipeInputType.Path, path));
    }

    [Theory]
    [InlineData(@"C:\dir\file.")]
    [InlineData(@"C:\dir\file ")]
    [InlineData("file.")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"C:\dir.\file.txt")]
    [InlineData(@"C:\dir \file.txt")]
    public void ATrailingDotOrSpace_IsRefused(string path)
    {
        Assert.Equal(RecipeValueRefusal.TrailingDotOrSpace, Check(RecipeInputType.Path, path));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("LPT1")]
    [InlineData("lpt9.log")]
    [InlineData("COM\u00B9")]
    [InlineData("LPT\u00B2.txt")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData(@"C:\dir\NUL")]
    [InlineData(@"C:\dir\con.tar.gz")]
    [InlineData(@"C:\dir\AUX\file.txt")]
    public void ADosDeviceName_IsRefusedWithOrWithoutExtension(string path)
    {
        Assert.Equal(RecipeValueRefusal.DeviceName, Check(RecipeInputType.Path, path));
    }

    // ---- per-type validation ----

    [Theory]
    [InlineData("0", true)]
    [InlineData("42", true)]
    [InlineData("-7", true)]
    [InlineData("+7", true)]
    [InlineData("2147483647", true)]
    [InlineData("2147483648", false)]
    [InlineData("1.5", false)]
    [InlineData("1e3", false)]
    [InlineData(" 7", false)]
    [InlineData("7 ", false)]
    [InlineData("1,000", false)]
    [InlineData("abc", false)]
    [InlineData("\u0661\u0662", false)]
    public void AnIntegerValue_IsCheckedWithTheInvariantCulture(string value, bool valid)
    {
        Assert.Equal(valid, Check(RecipeInputType.Int, value) == RecipeValueRefusal.None);
    }

    [Theory]
    [InlineData("1.5", true)]
    [InlineData("-0.25", true)]
    [InlineData("3", true)]
    [InlineData("1e3", true)]
    [InlineData(".5", true)]
    [InlineData("1,5", false)]
    [InlineData("1 5", false)]
    [InlineData("NaN", false)]
    [InlineData("Infinity", false)]
    [InlineData("-Infinity", false)]
    [InlineData("1e999", false)]
    [InlineData("abc", false)]
    public void ANumberValue_IsCheckedWithTheInvariantCulture(string value, bool valid)
    {
        using CultureScope scope = new("fr-FR");

        Assert.Equal(valid, Check(RecipeInputType.Number, value) == RecipeValueRefusal.None);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("FALSE", true)]
    [InlineData("True", true)]
    [InlineData("yes", false)]
    [InlineData("1", false)]
    public void ABoolValue_IsTrueOrFalse(string value, bool valid)
    {
        Assert.Equal(valid, Check(RecipeInputType.Bool, value) == RecipeValueRefusal.None);
    }

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("host-1", true)]
    [InlineData("a.b.c.d", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("-host", false)]
    [InlineData("host-", false)]
    [InlineData("a..b", false)]
    [InlineData("host name", false)]
    [InlineData("host_name", false)]
    [InlineData("host:80", false)]
    [InlineData("host/path", false)]
    [InlineData("ho$t", false)]
    public void AHostnameValue_UsesTheExistingHostnameValidation(string value, bool valid)
    {
        Assert.Equal(valid, Check(RecipeInputType.Hostname, value) == RecipeValueRefusal.None);
    }

    [Theory]
    [InlineData(" example.com")]
    [InlineData("example.com ")]
    [InlineData("\u00A0example.com")]
    public void AHostnameWithSurroundingWhitespace_IsRefusedAsIs(string value)
    {
        Assert.Equal(RecipeValueRefusal.SurroundingWhitespace, Check(RecipeInputType.Hostname, value));
        Assert.Equal(RecipeValueRefusal.SurroundingWhitespace, RecipeValueRule.CheckHost(value, false));
        Assert.Equal(RecipeValueRefusal.SurroundingWhitespace, Check(RecipeInputType.IpAddress, value));
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:1.2.3.4", true)]
    [InlineData("256.1.1.1", false)]
    [InlineData("1.2.3", false)]
    [InlineData("1.2.3.4.5", false)]
    [InlineData("fe80::1%eth0", false)]
    [InlineData("[::1]", false)]
    [InlineData("::g", false)]
    [InlineData("example.com", false)]
    [InlineData("1", false)]
    [InlineData("0x7f.1", false)]
    public void AnIpAddressValue_IsCheckedStrictly(string value, bool valid)
    {
        Assert.Equal(valid, Check(RecipeInputType.IpAddress, value) == RecipeValueRefusal.None);
    }

    [Fact]
    public void AStringValue_IsCheckedAgainstItsLengthAndItsPattern()
    {
        RecipeInput field = Field(RecipeInputType.String) with { MaxLength = 5, Pattern = "[a-z]+" };

        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.Check(field, "abcde", false));
        Assert.Equal(RecipeValueRefusal.TooLong, RecipeValueRule.Check(field, "abcdef", false));
        Assert.Equal(RecipeValueRefusal.PatternMismatch, RecipeValueRule.Check(field, "ab1", false));
        Assert.Equal(RecipeValueRefusal.PatternMismatch, RecipeValueRule.Check(field, "ABC", false));
    }

    [Fact]
    public void APattern_MustMatchTheWholeValue()
    {
        RecipeInput field = Field(RecipeInputType.String) with { Pattern = "[a-z]+" };

        Assert.Equal(RecipeValueRefusal.PatternMismatch, RecipeValueRule.Check(field, "abc def", false));
        Assert.Equal(RecipeValueRefusal.PatternMismatch, RecipeValueRule.Check(field, "xabc1", false));
    }

    [Fact]
    public void APatternWithAnAlternation_StillMustMatchTheWholeValue()
    {
        RecipeInput field = Field(RecipeInputType.String) with { Pattern = "a|b" };

        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.Check(field, "a", false));
        Assert.Equal(RecipeValueRefusal.PatternMismatch, RecipeValueRule.Check(field, "ab", false));
        Assert.Equal(RecipeValueRefusal.PatternMismatch, RecipeValueRule.Check(field, "xb", false));
    }

    [Fact]
    public void APatternThatDoesNotMatchBeforeTheTimeout_IsRefusedAndNeverHangs()
    {
        RecipeInput field = Field(RecipeInputType.String) with { Pattern = "(a+)+$", MaxLength = RecipeLimits.MaxStringValueLength };
        string hostile = new string('a', 40) + "!";

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        RecipeValueRefusal verdict = RecipeValueRule.Check(field, hostile, false);
        clock.Stop();

        Assert.Equal(RecipeValueRefusal.PatternMismatch, verdict);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void AStringWithoutAPattern_UsesTheDefaultLengthCap()
    {
        Assert.Equal(RecipeValueRefusal.None, Check(RecipeInputType.String, new string('a', RecipeLimits.DefaultStringMaxLength)));
        Assert.Equal(RecipeValueRefusal.TooLong, Check(RecipeInputType.String, new string('a', RecipeLimits.DefaultStringMaxLength + 1)));
    }

    [Fact]
    public void AChoiceValue_MustBeOneOfTheList()
    {
        RecipeInput field = Field(RecipeInputType.Choice) with { Choices = ["fast", "slow"] };

        Assert.Equal(RecipeValueRefusal.None, RecipeValueRule.Check(field, "fast", false));
        Assert.Equal(RecipeValueRefusal.NotInChoices, RecipeValueRule.Check(field, "Fast", false));
        Assert.Equal(RecipeValueRefusal.NotInChoices, RecipeValueRule.Check(field, "fast ", false));
        Assert.Equal(RecipeValueRefusal.NotInChoices, RecipeValueRule.Check(field, "medium", false));
    }

    // ---- session placeholders ----

    [Theory]
    [InlineData("server01", true)]
    [InlineData("server01.example.com", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("-oProxyCommand=x", false)]
    [InlineData("host name", false)]
    [InlineData("host;rm", false)]
    [InlineData("host\"", false)]
    public void TheHostPlaceholder_GoesThroughTheHostnameValidation(string host, bool valid)
    {
        Assert.Equal(valid, RecipeValueRule.CheckHost(host, true) == RecipeValueRefusal.None);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("22", true)]
    [InlineData("65535", true)]
    [InlineData("0", false)]
    [InlineData("65536", false)]
    [InlineData("-1", false)]
    [InlineData("+22", false)]
    [InlineData("22 ", false)]
    [InlineData("2.2", false)]
    [InlineData("abc", false)]
    [InlineData("\u0662\u0662", false)]
    [InlineData("123456", false)]
    public void ThePortPlaceholder_GoesThroughThePortValidation(string port, bool valid)
    {
        Assert.Equal(valid, RecipeValueRule.CheckPort(port, false) == RecipeValueRefusal.None);
    }

    [Fact]
    public void ALeadingDash_IsRefusedForASessionValueThatStartsItsToken()
    {
        Assert.Equal(RecipeValueRefusal.LeadingDash, RecipeValueRule.CheckHost("-host", true));
        Assert.Equal(RecipeValueRefusal.LeadingDash, RecipeValueRule.CheckPort("-1", true));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name)
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);
        }

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
