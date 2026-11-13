/*
 * Copyright 2026 Nestor Gonzalez Medina
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

using Heimdall.App.Services;

namespace Heimdall.App.Tests;

/// <summary>
/// The sign-in form of an embedded web console: the origin the account may be written on, the
/// fields it is written into, and the one attempt a tab gets at it.
/// </summary>
public sealed class WebSignInFormTests
{
    private static WebSessionResult Form(
        string? userSelector = "#username",
        string? passwordSelector = "#password",
        string? submitSelector = null,
        string? username = "admin",
        string? password = "s3cret")
        => new("web-test", new Uri("https://ilo.example.test:8443/login.html"), username, password)
        {
            SignInUsernameSelector = userSelector,
            SignInPasswordSelector = passwordSelector,
            SignInSubmitSelector = submitSelector,
        };

    [Fact]
    public void NoSession_IsGivenNoScript() => Assert.Null(WebSignInFormPolicy.BuildScript(null));

    [Fact]
    public void AProfileThatNamesNoField_IsGivenNoScript()
        => Assert.Null(WebSignInFormPolicy.BuildScript(Form(userSelector: null, passwordSelector: null)));

    [Fact]
    public void AUserFieldAlone_FillsNothing()
        => Assert.Null(WebSignInFormPolicy.BuildScript(Form(passwordSelector: null)));

    [Fact]
    public void ABlankPasswordSelector_IsTreatedAsNone()
        => Assert.Null(WebSignInFormPolicy.BuildScript(Form(passwordSelector: "   ")));

    [Fact]
    public void APasswordFieldWithoutAPassword_IsGivenNoScript()
        => Assert.Null(WebSignInFormPolicy.BuildScript(Form(password: null)));

    [Fact]
    public void TheAccountIsWrittenOnTheConsolesOwnOriginOnly()
    {
        string script = WebSignInFormPolicy.BuildScript(Form())!;

        Assert.Contains("window.location.protocol + \"//\" + window.location.host", script);
        Assert.Contains("var origin = \"https://ilo.example.test:8443\"", script);

        // An origin has no path: a console signs in through a form of its own choosing, and the
        // profile's address says which host it is, not which page holds the fields.
        Assert.DoesNotContain("/login.html", script);
    }

    [Fact]
    public void ADefaultPortIsLeftOutOfTheOrigin()
    {
        Assert.Equal(
            "https://ilo.example.test",
            WebSignInFormPolicy.PageOrigin(new Uri("https://ilo.example.test/login")));
        Assert.Equal(
            "http://ilo.example.test:8080",
            WebSignInFormPolicy.PageOrigin(new Uri("http://ILO.example.test:8080/")));
    }

    [Fact]
    public void TheAccountIsSerialized_SoAQuoteStaysPartOfThePassword()
    {
        string script = WebSignInFormPolicy.BuildScript(Form(username: "ad\"min", password: "p\"a\\ss"))!;

        Assert.Contains("user: \"ad\\\"min\"", script);
        Assert.Contains("password: \"p\\\"a\\\\ss\"", script);
    }

    [Fact]
    public void TheFormIsWaitedFor_InsteadOfAssumedAtLoadTime()
    {
        string script = WebSignInFormPolicy.BuildScript(Form())!;

        Assert.Contains("window.setInterval", script);
        Assert.Contains("Date.now() + " + WebSignInFormPolicy.FormWaitMilliseconds, script);
        Assert.Contains("}, " + WebSignInFormPolicy.FormPollMilliseconds + ");", script);
    }

    [Fact]
    public void TheTabRemembersItsOneAttempt_InThePagesOwnStorage()
    {
        string script = WebSignInFormPolicy.BuildScript(Form())!;

        // A form that comes back means the account was refused, and a console that counts failed
        // sign-ins would count the repeat: the attempt is what keeps it from being sent twice.
        Assert.Contains("sessionStorage.getItem", script);
        Assert.Contains("sessionStorage.setItem", script);
        Assert.Contains(WebSignInFormPolicy.AttemptKey, script);
    }

    [Fact]
    public void TheFieldsAreWrittenSoTheFrameworkNotices()
    {
        string script = WebSignInFormPolicy.BuildScript(Form())!;

        Assert.Contains("Object.getOwnPropertyDescriptor(prototype, \"value\")", script);
        Assert.Contains("new Event(\"input\"", script);
        Assert.Contains("new Event(\"change\"", script);
    }

    [Fact]
    public void TheButtonIsPressedOnlyWhenTheProfileNamesOne()
    {
        Assert.DoesNotContain(".click()", WebSignInFormPolicy.BuildScript(Form())!);
        Assert.Contains(".click()", WebSignInFormPolicy.BuildScript(Form(submitSelector: "#submit"))!);
    }

    [Fact]
    public void NothingAboutTheAccountIsLogged()
    {
        string script = WebSignInFormPolicy.BuildScript(Form())!;

        Assert.DoesNotContain("console.", script);
    }

    [Fact]
    public void TheReloadGivesTheAttemptBack()
    {
        string script = WebSignInFormPolicy.BuildAttemptResetScript();

        Assert.Contains("sessionStorage.removeItem", script);
        Assert.Contains(WebSignInFormPolicy.AttemptKey, script);
    }
}
