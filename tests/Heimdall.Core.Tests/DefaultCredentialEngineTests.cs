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

using Heimdall.Core.Security;

namespace Heimdall.Core.Tests;

public class DefaultCredentialEngineTests
{
    [Theory]
    [InlineData("HTTP/1.1 200 OK", true)]
    [InlineData("HTTP/1.0 301 Moved", true)]
    [InlineData("HTTP/1.1 401 Unauthorized", false)]
    [InlineData("HTTP/1.1 403 Forbidden", false)]
    [InlineData("HTTP/1.1 500 Internal Server Error", false)]
    [InlineData("", false)]
    [InlineData("garbage", false)]
    public void IsHttpSuccessResponse_ReturnsExpected(string input, bool expected)
    {
        Assert.Equal(expected, DefaultCredentialEngine.IsHttpSuccessResponse(input));
    }

    [Theory]
    [InlineData(CredTestStatus.Default, "ToolDefCredStatusDefault")]
    [InlineData(CredTestStatus.Changed, "ToolDefCredStatusChanged")]
    [InlineData(CredTestStatus.Error, "ToolDefCredStatusError")]
    public void StatusToLabel_ReturnsExpectedKey(CredTestStatus status, string expectedKey)
    {
        Assert.Equal(expectedKey, DefaultCredentialEngine.StatusToLabel(status));
    }

    [Fact]
    public void StatusToDetail_Default_ContainsServiceWithLocalizedTemplate()
    {
        var detail = DefaultCredentialEngine.StatusToDetail(
            CredTestStatus.Default,
            "SSH",
            Localize);

        Assert.Contains("SSH", detail);
    }

    [Fact]
    public void BuildSummaryText_WithDefaults_ContainsCounts()
    {
        var results = new List<CredTestResultDto>
        {
            MakeResult("SSH", 22, CredTestStatus.Default),
            MakeResult("SSH", 22, CredTestStatus.Changed),
            MakeResult("FTP", 21, CredTestStatus.Default),
        };

        var summary = DefaultCredentialEngine.BuildSummaryText(results, Localize);

        Assert.Equal("2 defaults on 2 services", summary);
    }

    [Fact]
    public void BuildSummaryText_NoDefaults_UsesNoDefaultsMessage()
    {
        var results = new List<CredTestResultDto>
        {
            MakeResult("SSH", 22, CredTestStatus.Changed),
        };

        var summary = DefaultCredentialEngine.BuildSummaryText(results, Localize);

        Assert.Equal("No defaults found", summary);
    }

    [Fact]
    public void BuildReportText_ContainsHeaderAndData()
    {
        var results = new List<CredTestResultDto>
        {
            MakeResult("SSH", 22, CredTestStatus.Default),
        };

        var report = DefaultCredentialEngine.BuildReportText(results, Localize);

        Assert.Contains("Service", report);
        Assert.Contains("SSH", report);
        Assert.Contains("22", report);
    }

    [Fact]
    public void BuildCsvExport_ContainsHeaderAndSanitizedData()
    {
        var results = new List<CredTestResultDto>
        {
            new()
            {
                Service = "=SSH",
                Port = 22,
                Username = "root",
                Password = "root",
                Status = CredTestStatus.Default,
            },
        };

        var csv = DefaultCredentialEngine.BuildCsvExport(results, Localize);

        Assert.Contains("Service", csv);
        Assert.Contains("\"'=SSH\"", csv);
    }

    [Fact]
    public void BuildCsvExport_EscapesQuotesInPassword()
    {
        var results = new List<CredTestResultDto>
        {
            new()
            {
                Service = "FTP",
                Port = 21,
                Username = "admin",
                Password = "pass\"word",
                Status = CredTestStatus.Changed,
            },
        };

        var csv = DefaultCredentialEngine.BuildCsvExport(results, Localize);

        Assert.Contains("pass\"\"word", csv);
    }

    [Fact]
    public void BuildCsvExport_ErrorStatus_IncludesErrorDetail()
    {
        var results = new List<CredTestResultDto>
        {
            new()
            {
                Service = "MySQL",
                Port = 3306,
                Username = "root",
                Password = "",
                Status = CredTestStatus.Error,
                ErrorDetail = "Connection refused",
            },
        };

        var csv = DefaultCredentialEngine.BuildCsvExport(results, Localize);

        Assert.Contains("Connection refused", csv);
    }

    [Fact]
    public void BuildReportText_ErrorStatus_IncludesErrorDetail()
    {
        var results = new List<CredTestResultDto>
        {
            new()
            {
                Service = "Redis",
                Port = 6379,
                Username = "",
                Password = "",
                Status = CredTestStatus.Error,
                ErrorDetail = "Timed out",
            },
        };

        var report = DefaultCredentialEngine.BuildReportText(results, Localize);

        Assert.Contains("Timed out", report);
    }

    private static CredTestResultDto MakeResult(string service, int port, CredTestStatus status)
    {
        return new CredTestResultDto
        {
            Service = service,
            Port = port,
            Username = "root",
            Password = "root",
            Status = status,
        };
    }

    private static string Localize(string key)
    {
        return key switch
        {
            "ToolDefCredSummary" => "{0} on {1}",
            "ToolDefCredSummaryCredentials" => "{0} defaults",
            "ToolDefCredSummaryCredentialsOne" => "{0} default",
            "ToolDefCredSummaryServices" => "{0} services",
            "ToolDefCredSummaryServicesOne" => "{0} service",
            "ToolDefCredNoDefaults" => "No defaults found",
            "ToolDefCredColService" => "Service",
            "ToolDefCredColPort" => "Port",
            "ToolDefCredColUser" => "User",
            "ToolDefCredColPass" => "Password",
            "ToolDefCredColStatus" => "Status",
            "ToolDefCredColDetail" => "Detail",
            "ToolDefCredStatusDefault" => "Default",
            "ToolDefCredStatusChanged" => "Changed",
            "ToolDefCredStatusError" => "Error",
            "ToolDefCredDetailAccepted" => "{0} accepted",
            "ToolDefCredDetailRejected" => "{0} rejected",
            _ => key
        };
    }
}
