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

using System.Diagnostics;
using System.Text;
using Heimdall.Core.Redaction;

namespace Heimdall.Core.Tests.Redaction;

public sealed class TextRedactorPerformanceTests
{
    private const int BenignBufferBytes = 5 * 1024 * 1024;

    // Reference budget for a 5 MB buffer; the assertion allows ten times it because CI runners are slow.
    private static readonly TimeSpan BenignBudget = TimeSpan.FromSeconds(5);

    private const int BoundMultiplier = 10;

    private const int RunLength = 4096;

    private const int BeginLineCount = 1000;

    private const int RepeatedKeyCount = 100_000;

    private static readonly string[] BenignLines =
    [
        "drwxr-xr-x  2 bob bob 4096 Oct 19 12:00 .",
        "-rw-r--r--  1 bob bob  512 Oct 19 12:00 token.json",
        "bob@host:~$ ls -la /var/log",
        "max_tokens=4096 TOKEN_TTL=3600 retries=3",
        "ssh -p 2222 host && ps -p 1234 && docker run -p8080:80 img",
        "commit 3f786850e387550fdab836ed7e6dc881de23001b (tag: v2026.091401.1)",
        "Microsoft Windows [Version 10.0.26200.1234]",
        "[INFO] Heimdall.Core.Redaction started in 12 ms",
        "-----BEGIN CERTIFICATE-----",
        "MIIDdzCCAl+gAwIBAgIJAKx2",
        "-----END CERTIFICATE-----",
        "Last login: Mon Oct 19 10:00:00 2026 from 10.0.0.5",
    ];

    // Producer: TextRedactor.Redact over every rule: adversarial inputs finish without a timeout refusal.
    public static TheoryData<string, string> AdversarialInputs()
    {
        return new TheoryData<string, string>
        {
            { "word run", new string('a', RunLength) },
            { "underscore run", string.Concat(Enumerable.Repeat("a_", RunLength / 2)) },
            { "dot chain", string.Concat(Enumerable.Repeat("ab.", RunLength / 3)) },
            { "dash chain", string.Concat(Enumerable.Repeat("a-", RunLength / 2)) },
            { "password word run", string.Concat(Enumerable.Repeat("password", RunLength / 8)) },
            { "jwt prefix run", string.Concat(Enumerable.Repeat("eyJ", RunLength / 3)) },
            { "at-sign run", string.Concat(Enumerable.Repeat("a@", RunLength / 2)) },
            { "begin lines without end", string.Concat(Enumerable.Repeat("-----BEGIN RSA PRIVATE KEY-----\n", BeginLineCount)) },
            { "end lines without begin", string.Concat(Enumerable.Repeat("-----END RSA PRIVATE KEY-----\n", BeginLineCount)) },
            { "password prompts", string.Concat(Enumerable.Repeat("Password:\n", BeginLineCount)) },
            { "repeated keys on one line", string.Concat(Enumerable.Repeat("password=", RepeatedKeyCount)) },
            { "repeated keys on lines", string.Concat(Enumerable.Repeat("password=x\n", RepeatedKeyCount)) },
            { "mysql run", "mysql " + string.Concat(Enumerable.Repeat("-x ", RunLength / 3)) },
        };
    }

    [Theory]
    [MemberData(nameof(AdversarialInputs))]
    public void Redact_AdversarialInput_FinishesWithoutRefusal(string name, string input)
    {
        Assert.NotNull(name);

        foreach (bool pseudonymize in new[] { false, true })
        {
            RedactionResult result = TextRedactor.Redact(input, new RedactionOptions { PseudonymizeHosts = pseudonymize });

            Assert.False(result.Refused, $"{name}: refused by {result.RefusedRuleId}");
        }
    }

    // Producer: RedactionApply.Replace chunking, a 5 MB buffer is linear, not one giant match budget.
    [Fact]
    public void Redact_FiveMegabyteBuffer_FinishesWithinTenTimesTheBudget()
    {
        string buffer = BuildBenignBuffer(BenignBufferBytes);
        var stopwatch = Stopwatch.StartNew();

        RedactionResult result = TextRedactor.Redact(buffer, new RedactionOptions { PseudonymizeHosts = true });

        stopwatch.Stop();
        Assert.False(result.Refused, $"refused by {result.RefusedRuleId}");
        Assert.True(buffer.Length >= BenignBufferBytes);
        Assert.True(
            stopwatch.Elapsed < BenignBudget * BoundMultiplier,
            $"took {stopwatch.Elapsed}, bound {BenignBudget * BoundMultiplier}");
        Assert.Equal(0, result.Counts["key-value-secret"] + result.Counts["jwt"] + result.Counts["cli-password"] + result.Counts["private-key-block"]);
    }

    private static string BuildBenignBuffer(int minimumLength)
    {
        var builder = new StringBuilder(minimumLength + 256);
        int line = 0;
        while (builder.Length < minimumLength)
        {
            builder.Append(BenignLines[line % BenignLines.Length]).Append('\n');
            line++;
        }

        return builder.ToString();
    }
}
