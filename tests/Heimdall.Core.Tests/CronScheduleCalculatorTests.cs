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

using Heimdall.Core.CronJob;

namespace Heimdall.Core.Tests;

public sealed class CronScheduleCalculatorTests
{
    private static readonly DateTime Anchor = new(2026, 4, 18, 10, 0, 0, DateTimeKind.Local);

    [Fact]
    public void CalculateNextRuns_CountLessThanOne_ReturnsEmpty()
    {
        Assert.Empty(CronScheduleCalculator.CalculateNextRuns(["*", "*", "*", "*", "*"], 0, Anchor));
    }

    [Fact]
    public void CalculateNextRuns_TooFewFields_ReturnsEmpty()
    {
        Assert.Empty(CronScheduleCalculator.CalculateNextRuns(["*", "*", "*"], 1, Anchor));
    }

    [Fact]
    public void CalculateNextRuns_EveryMinute_StartsOnNextMinute()
    {
        var runs = CronScheduleCalculator.CalculateNextRuns(["*", "*", "*", "*", "*"], 2, Anchor);

        Assert.Equal(["2026-04-18 10:01 (Sat)", "2026-04-18 10:02 (Sat)"], runs);
    }

    [Fact]
    public void CalculateNextRuns_DailyAtSpecificTime_Works()
    {
        var runs = CronScheduleCalculator.CalculateNextRuns(["5", "12", "*", "*", "*"], 2, Anchor);

        Assert.Equal(["2026-04-18 12:05 (Sat)", "2026-04-19 12:05 (Sun)"], runs);
    }

    [Fact]
    public void CalculateNextRuns_StepPattern_Works()
    {
        var runs = CronScheduleCalculator.CalculateNextRuns(["*/15", "*", "*", "*", "*"], 3, Anchor);

        Assert.Equal(["2026-04-18 10:15 (Sat)", "2026-04-18 10:30 (Sat)", "2026-04-18 10:45 (Sat)"], runs);
    }

    [Fact]
    public void CalculateNextRuns_RangePattern_Works()
    {
        var runs = CronScheduleCalculator.CalculateNextRuns(["0", "9-11", "*", "*", "*"], 3, Anchor);

        Assert.Equal(["2026-04-18 11:00 (Sat)", "2026-04-19 09:00 (Sun)", "2026-04-19 10:00 (Sun)"], runs);
    }

    [Fact]
    public void CalculateNextRuns_SundaySeven_WrapsToZero()
    {
        var saturday = new DateTime(2026, 4, 18, 8, 0, 0, DateTimeKind.Local);
        var runs = CronScheduleCalculator.CalculateNextRuns(["0", "9", "*", "*", "7"], 1, saturday);

        Assert.Single(runs);
        Assert.Equal("2026-04-19 09:00 (Sun)", runs[0]);
    }

    [Fact]
    public void CalculateNextRuns_InvalidField_ReturnsEmpty()
    {
        Assert.Empty(CronScheduleCalculator.CalculateNextRuns(["abc", "*", "*", "*", "*"], 1, Anchor));
    }

    // Anchor is Saturday 2026-04-18 10:00. Each row: the five fields, how many runs to ask for, the runs.
    public static TheoryData<string, int, string[]> ScheduleTable => new()
    {
        // Both day fields restricted: cron runs on the 1st OR on any Monday, it does not AND them.
        {
            "0 0 1 * 1",
            4,
            ["2026-04-20 00:00 (Mon)", "2026-04-27 00:00 (Mon)", "2026-05-01 00:00 (Fri)", "2026-05-04 00:00 (Mon)"]
        },
        // One day field a star: the other one alone decides, so only the 1st (not every day).
        {
            "0 0 1 * *",
            2,
            ["2026-05-01 00:00 (Fri)", "2026-06-01 00:00 (Mon)"]
        },
        {
            "0 0 * * 1",
            2,
            ["2026-04-20 00:00 (Mon)", "2026-04-27 00:00 (Mon)"]
        },
        // A step on a star still counts as a star, as in cron: the days must match both fields,
        // so only the Mondays that fall on 1, 11, 21 or 31.
        {
            "0 0 */10 * 1",
            3,
            ["2026-05-11 00:00 (Mon)", "2026-06-01 00:00 (Mon)", "2026-08-31 00:00 (Mon)"]
        },
        // A range with a step starts at its first value and stops at its last.
        {
            "1-10/2 * * * *",
            6,
            ["2026-04-18 10:01 (Sat)", "2026-04-18 10:03 (Sat)", "2026-04-18 10:05 (Sat)", "2026-04-18 10:07 (Sat)", "2026-04-18 10:09 (Sat)", "2026-04-18 11:01 (Sat)"]
        },
        // A start with a step runs to the end of the field: 55 and 58, then the next hour.
        {
            "55/3 * * * *",
            3,
            ["2026-04-18 10:55 (Sat)", "2026-04-18 10:58 (Sat)", "2026-04-18 11:55 (Sat)"]
        },
        // Day names and a range of them.
        {
            "0 9 * * MON-FRI",
            3,
            ["2026-04-20 09:00 (Mon)", "2026-04-21 09:00 (Tue)", "2026-04-22 09:00 (Wed)"]
        },
        {
            "0 9 * * mon,sun",
            3,
            ["2026-04-19 09:00 (Sun)", "2026-04-20 09:00 (Mon)", "2026-04-26 09:00 (Sun)"]
        },
        {
            "0 9 * * FRI-SUN",
            4,
            ["2026-04-19 09:00 (Sun)", "2026-04-24 09:00 (Fri)", "2026-04-25 09:00 (Sat)", "2026-04-26 09:00 (Sun)"]
        },
        // Month names in a list.
        {
            "0 0 * JAN,JUL *",
            2,
            ["2026-07-01 00:00 (Wed)", "2026-07-02 00:00 (Thu)"]
        },
        {
            "30 6 1 JUN-AUG *",
            3,
            ["2026-06-01 06:30 (Mon)", "2026-07-01 06:30 (Wed)", "2026-08-01 06:30 (Sat)"]
        },
        // Names and numbers read the same.
        {
            "0 9 * 7 1",
            2,
            ["2026-07-06 09:00 (Mon)", "2026-07-13 09:00 (Mon)"]
        },
    };

    [Theory]
    [MemberData(nameof(ScheduleTable))]
    public void CalculateNextRuns_ScheduleTable_MatchesCron(string schedule, int count, string[] expected)
    {
        var runs = CronScheduleCalculator.CalculateNextRuns(schedule.Split(' '), count, Anchor);

        Assert.Equal(expected, runs);
    }

    [Theory]
    [InlineData("0 9 * * XYZ")]
    [InlineData("0 9 * FOO *")]
    [InlineData("0 9 JAN * *")]
    [InlineData("0 9 * * MON-")]
    [InlineData("*/0 * * * *")]
    [InlineData("1-10/x * * * *")]
    public void CalculateNextRuns_UnknownNameOrBadStep_ReturnsEmpty(string schedule)
    {
        Assert.Empty(CronScheduleCalculator.CalculateNextRuns(schedule.Split(' '), 1, Anchor));
    }
}
