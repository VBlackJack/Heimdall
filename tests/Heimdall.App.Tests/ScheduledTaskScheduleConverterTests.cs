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

using System.Globalization;
using System.IO;
using System.Text.Json;
using Heimdall.App.Converters;
using Heimdall.App.Services;
using Heimdall.Core.Configuration;
using Heimdall.Core.Models;

namespace Heimdall.App.Tests;

/// <summary>
/// The Scheduled grid describes a task's cadence from the data it stores, in the language of the
/// session, instead of showing English text written when the task was saved.
/// </summary>
public sealed class ScheduledTaskScheduleConverterTests
{
    private const string EveryKey = "ScheduledTaskScheduleEveryNMin";
    private const string DailyKey = "ScheduledTaskScheduleDailyAt";

    [Theory]
    [InlineData("en", "Every 30 min", "Daily at 08:15")]
    [InlineData("fr", "Toutes les 30 min", "Tous les jours à 08:15")]
    [InlineData("es", "Cada 30 min", "Cada día a las 08:15")]
    public void Convert_DescribesBothScheduleTypesInTheLanguageOfTheLocale(
        string locale, string expectedInterval, string expectedDaily)
    {
        Dictionary<string, string> table = ReadLocale(locale);
        ScheduledTaskScheduleConverter converter = new();

        object interval = converter.Convert(
            Values(table, nameof(ScheduleType.Interval), 30, null, string.Empty),
            typeof(string), null!, CultureInfo.InvariantCulture);
        object daily = converter.Convert(
            Values(table, nameof(ScheduleType.Daily), 0, "08:15", string.Empty),
            typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal(expectedInterval, interval);
        Assert.Equal(expectedDaily, daily);
    }

    /// <summary>
    /// A task saved by an earlier version carries its English text next to the data. The text
    /// must not win over the data, or a French session keeps reading "Every 30 min".
    /// </summary>
    [Theory]
    [InlineData("fr", "Toutes les 45 min")]
    [InlineData("es", "Cada 45 min")]
    public void Convert_IgnoresTheEnglishTextAnEarlierVersionStored(string locale, string expected)
    {
        Dictionary<string, string> table = ReadLocale(locale);

        object text = new ScheduledTaskScheduleConverter().Convert(
            Values(table, nameof(ScheduleType.Interval), 45, null, "Every 45 min"),
            typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Convert_ShowsTheStoredTextOfAFileThatRecordedNoTimeOfDay()
    {
        Dictionary<string, string> table = ReadLocale("fr");

        object text = new ScheduledTaskScheduleConverter().Convert(
            Values(table, nameof(ScheduleType.Daily), 0, null, "Daily 06:30"),
            typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal("Daily 06:30", text);
    }

    [Fact]
    public void Describe_AppliesTheCadenceTheSchedulerAppliesWhenTheDataIsIncomplete()
    {
        Dictionary<string, string> table = ReadLocale("en");

        string interval = ScheduledTaskScheduleConverter.Describe(
            table[EveryKey], table[DailyKey], nameof(ScheduleType.Interval), 0, string.Empty, string.Empty);
        string daily = ScheduledTaskScheduleConverter.Describe(
            table[EveryKey], table[DailyKey], nameof(ScheduleType.Daily), 0, string.Empty, string.Empty);

        Assert.Equal("Every 60 min", interval);
        Assert.Equal("Daily at 08:00", daily);
    }

    [Fact]
    public void Convert_AnswersEmptyWhenTheBindingsAreNotComplete()
    {
        object text = new ScheduledTaskScheduleConverter().Convert(
            [], typeof(string), null!, CultureInfo.InvariantCulture);

        Assert.Equal(string.Empty, text);
    }

    /// <summary>
    /// The text is described at display time, so saving and rescheduling a task no longer put a
    /// language into the stored field.
    /// </summary>
    [Fact]
    public void ComputeNextRun_DoesNotWriteTheScheduleText()
    {
        ScheduledTaskDto interval = new()
        {
            ScheduleType = nameof(ScheduleType.Interval),
            IntervalMinutes = 15,
        };
        ScheduledTaskDto daily = new()
        {
            ScheduleType = nameof(ScheduleType.Daily),
            TimeOfDay = "07:45",
        };
        ScheduledTaskDto invalid = new()
        {
            ScheduleType = nameof(ScheduleType.Daily),
            TimeOfDay = "not a time",
        };

        DateTime now = new(2026, 10, 27, 12, 0, 0);
        TaskSchedulerService.ComputeNextRun(interval, now);
        TaskSchedulerService.ComputeNextRun(daily, now);
        TaskSchedulerService.ComputeNextRun(invalid, now);

        Assert.Equal(string.Empty, interval.Schedule);
        Assert.Equal(string.Empty, daily.Schedule);
        Assert.Equal(string.Empty, invalid.Schedule);
        Assert.Equal(now.AddMinutes(15), interval.NextRun);
    }

    private static object[] Values(
        Dictionary<string, string> table, string type, int minutes, string? time, string legacy) =>
        [table[EveryKey], table[DailyKey], type, minutes, time ?? string.Empty, legacy];

    private static Dictionary<string, string> ReadLocale(string locale)
    {
        string path = Path.Combine(FindRepoRoot(), "locales", locale + ".json");
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream);
        Dictionary<string, string> table = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                table[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return table;
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Heimdall.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException(
            $"Cannot find repository root containing Heimdall.slnx from: {AppContext.BaseDirectory}");
    }
}
