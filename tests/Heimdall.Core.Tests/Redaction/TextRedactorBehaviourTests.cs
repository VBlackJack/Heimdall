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

using System.Text.RegularExpressions;
using Heimdall.Core.Redaction;

namespace Heimdall.Core.Tests.Redaction;

public sealed class TextRedactorBehaviourTests
{
    private const string MixedSample =
        "$ ssh bob@hostA\n" +
        "Password: hunter2\n" +
        "Last login: Mon Oct 19 on pts/0\n" +
        "bob@hostA:~$ export DB_PASSWORD=hunter2\n" +
        "bob@hostA:~$ mysql -u root -pS3cret mydb\n" +
        "curl -H 'Authorization: Bearer eyJhbGciOiJI.eyJzdWIiOiIx.c2lnbmF0dXJl' https://web.example.com\n" +
        "token=abc123 postgres://admin:s3cr3t@10.1.2.3:5432/app\n" +
        "AKIAIOSFODNN7EXAMPLE xoxb-1234567890-abcdefghij\n" +
        "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\n-----END RSA PRIVATE KEY-----\n" +
        "Password:\nhunter2\n" +
        "max_tokens=4096 v2026.091401.1\n";

    // Producer: TextRedactor.Redact, RedactionResult.Counts (one entry per catalogue rule).
    [Fact]
    public void Counts_ContainEveryCatalogueRule_WithZeroForUntouchedRules()
    {
        RedactionResult result = TextRedactor.Redact("hello world");

        Assert.Equal(RedactionRuleCatalog.Default.Select(rule => rule.Id), result.Counts.Keys);
        Assert.All(result.Counts.Values, count => Assert.Equal(0, count));
        Assert.False(result.Refused);
        Assert.Null(result.RefusedRuleId);
    }

    [Fact]
    public void Counts_TwoPlantedSecretsOfOneRule_CountTwo_OthersZero()
    {
        RedactionResult result = TextRedactor.Redact("password=a1b2\nDB_PASSWORD=c3d4\n");

        Assert.Equal("password=[masked:key-value-secret]\nDB_PASSWORD=[masked:key-value-secret]\n", result.Text);
        Assert.Equal(2, result.Counts["key-value-secret"]);
        Assert.Equal(0, result.Counts["jwt"]);
        Assert.Equal(0, result.Counts["cli-password"]);
    }

    // Producer: RedactionRuleCatalog.Build order, authorization-header before jwt.
    [Fact]
    public void Overlap_BearerJwt_IsOneToken_CountedForTheAuthorizationRule()
    {
        RedactionResult result = TextRedactor.Redact("Authorization: Bearer eyJhbGciOiJI.eyJzdWIiOiIx.c2lnbmF0dXJl");

        Assert.Equal("Authorization: Bearer [masked:authorization-header]", result.Text);
        Assert.Equal(1, result.Counts["authorization-header"]);
        Assert.Equal(0, result.Counts["jwt"]);
    }

    [Fact]
    public void Overlap_JwtAsKeyValue_IsCountedOnceForKeyValueSecret()
    {
        RedactionResult result = TextRedactor.Redact("token=eyJhbGciOiJI.eyJzdWIiOiIx.c2lnbmF0dXJl");

        Assert.Equal("token=[masked:key-value-secret]", result.Text);
        Assert.Equal(1, result.Counts["key-value-secret"]);
        Assert.Equal(0, result.Counts["jwt"]);
    }

    [Fact]
    public void Overlap_PromptWithAnEarlierMaskedValue_IsNotCountedTwice()
    {
        RedactionResult result = TextRedactor.Redact("Password: --password hunter2");

        Assert.Equal(1, result.Counts.Values.Sum());
    }

    [Fact]
    public void Redact_MixedSample_MasksEverySecret_AndKeepsTheRest()
    {
        RedactionResult result = TextRedactor.Redact(MixedSample);

        Assert.Equal(
            "$ ssh bob@hostA\n" +
            "Password: [masked:password-prompt]\n" +
            "Last login: Mon Oct 19 on pts/0\n" +
            "bob@hostA:~$ export DB_PASSWORD=[masked:cli-password]\n" +
            "bob@hostA:~$ mysql -u root -p[masked:cli-password] mydb\n" +
            "curl -H 'Authorization: Bearer [masked:authorization-header]' https://web.example.com\n" +
            "token=[masked:key-value-secret] postgres://admin:[masked:url-credentials]@10.1.2.3:5432/app\n" +
            "[masked:aws-access-key] [masked:slack-token]\n" +
            "[masked:private-key-block]\n" +
            "Password:\n[masked:password-prompt]\n" +
            "max_tokens=4096 v2026.091401.1\n",
            result.Text);
    }

    // Producer: RedactionMask / every rule, a mask token is never matched again.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Redact_IsIdempotent_AndSecondPassCountsAreAllZero(bool pseudonymize)
    {
        var options = new RedactionOptions { PseudonymizeHosts = pseudonymize };
        string sample = MixedSample + "ping 10.1.2.4 web.example.com carol@hostB\nPassword: x y z\n";

        RedactionResult first = TextRedactor.Redact(sample, options);
        RedactionResult second = TextRedactor.Redact(first.Text, options);

        Assert.Equal(first.Text, second.Text);
        Assert.All(second.Counts.Values, count => Assert.Equal(0, count));
    }

    // Producer: RedactionRun.HostPseudonym, stable within one call.
    [Fact]
    public void Pseudonyms_SameHostSamePseudonym_NewHostNextNumber()
    {
        var options = new RedactionOptions { PseudonymizeHosts = true };

        RedactionResult result = TextRedactor.Redact("a@hostA x\nb@hostA y\nc@hostB z\n10.0.0.1 10.0.0.2 10.0.0.1", options);

        Assert.Equal("a@host-1 x\nb@host-1 y\nc@host-2 z\nip-1 ip-2 ip-1", result.Text);
        Assert.Equal(3, result.Counts["host-name"]);
        Assert.Equal(3, result.Counts["ipv4-address"]);
    }

    [Fact]
    public void Pseudonyms_AreOffByDefault()
    {
        const string input = "a@hostA 10.0.0.1 web.example.com";

        Assert.Equal(input, TextRedactor.Redact(input, new RedactionOptions()).Text);
        Assert.Equal(input, TextRedactor.Redact(input).Text);
        Assert.False(new RedactionOptions().PseudonymizeHosts);
    }

    // Producer: TextRedactor.Redact catch of RegexMatchTimeoutException: fail closed, never continue.
    [Fact]
    public void Redact_RuleTimesOut_RefusesWithEmptyText_AndLaterRulesDoNotRun()
    {
        var slow = new Regex("(a+)+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        bool laterRuleRan = false;
        var rules = new List<RedactionRule>
        {
            new("slow-rule", [slow], (text, run) => slow.Replace(text, "x")),
            new("later-rule", [slow], (text, run) =>
            {
                laterRuleRan = true;
                return text;
            }),
        };

        RedactionResult result = TextRedactor.Redact(rules, new string('a', 64) + "!", new RedactionOptions());

        Assert.True(result.Refused);
        Assert.Equal("slow-rule", result.RefusedRuleId);
        Assert.Equal(string.Empty, result.Text);
        Assert.False(laterRuleRan);
    }

    [Fact]
    public void Redact_NullText_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TextRedactor.Redact(null!));
    }
}
