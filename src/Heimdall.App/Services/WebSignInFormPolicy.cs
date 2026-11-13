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

using System.Text.Json;

namespace Heimdall.App.Services;

/// <summary>
/// The sign-in form of an embedded web console: the account the profile stores, written into the
/// fields the profile names, on the console's own origin only.
/// </summary>
/// <remarks>
/// <para>The engine's own autofill stays off in every console profile, so no store outside the
/// profile learns the account. What is written here comes from the profile that was opened and
/// goes into the page that profile addresses: a console that redirects to another host is left to
/// the user rather than handed the password.</para>
/// <para>The form is filled once per tab, remembered in the page's own session storage, for the
/// reason an HTTP challenge is answered once: a form that comes back means the account was
/// refused, and a console that counts failed sign-ins would count the repeat.</para>
/// <para>The script runs on every document, frames included, and waits for the fields to be drawn.
/// Consoles that build their form after a request of their own are the common case (vCenter,
/// iDRAC 9), so a fill attempted when the document loads would find an empty page.</para>
/// <para>The password reaches the page as a JavaScript string literal, which is the only way a
/// page can be signed in from outside: it is serialized rather than concatenated, so a quote or a
/// backslash in it stays part of the password (the serializer spells a quote as an escape the page
/// reads back as the same character), and neither it nor the script is ever logged.</para>
/// </remarks>
internal static class WebSignInFormPolicy
{
    /// <summary>How long a document waits for its sign-in form before giving up, in milliseconds.</summary>
    internal const int FormWaitMilliseconds = 15000;

    /// <summary>How often that wait looks at the page again, in milliseconds.</summary>
    internal const int FormPollMilliseconds = 400;

    /// <summary>The session storage entry that records the tab's one attempt, per origin.</summary>
    internal const string AttemptKey = "heimdall-sign-in";

    /// <summary>
    /// Whether the profile names a form that can be filled: a password field, and a password to
    /// write into it. The user name selector is optional, and a form that names only the password
    /// field is filled with the password alone.
    /// </summary>
    public static bool IsConfigured(WebSessionResult? session)
        => session is not null
            && !string.IsNullOrWhiteSpace(session.SignInPasswordSelector)
            && !string.IsNullOrEmpty(session.Password);

    /// <summary>
    /// The origin a password may be written on: scheme, host and port, spelled as the page spells
    /// it in <c>location</c>. The path is left out on purpose, since a console signs in through a
    /// form of its own choosing.
    /// </summary>
    public static string PageOrigin(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return $"{address.Scheme}://{address.Authority}".ToLowerInvariant();
    }

    /// <summary>
    /// The script that fills the profile's sign-in form, or null when the profile names none. It
    /// writes the account the profile stores and clicks the submit button when the profile names
    /// one; without that button the fields are left filled and the press is the user's.
    /// </summary>
    public static string? BuildScript(WebSessionResult? session)
    {
        if (!IsConfigured(session))
        {
            return null;
        }

        WebSessionResult form = session!;
        string origin = PageOrigin(form.Address);
        string userSelector = JsonSerializer.Serialize(form.SignInUsernameSelector ?? string.Empty);
        string passwordSelector = JsonSerializer.Serialize(form.SignInPasswordSelector!);
        string submitSelector = JsonSerializer.Serialize(form.SignInSubmitSelector ?? string.Empty);
        string user = JsonSerializer.Serialize(form.Username ?? string.Empty);
        string password = JsonSerializer.Serialize(form.Password!);

        return $$"""
            (function () {
                "use strict";
                var origin = "{{origin}}";
                var attemptKey = "{{AttemptKey}}";
                var userSelector = {{userSelector}};
                var passwordSelector = {{passwordSelector}};
                var submitSelector = {{submitSelector}};
                var account = { user: {{user}}, password: {{password}} };
                var deadline = Date.now() + {{FormWaitMilliseconds}};

                function onConsole() {
                    return (window.location.protocol + "//" + window.location.host).toLowerCase() === origin;
                }

                function find(selector) {
                    if (!selector) {
                        return null;
                    }
                    try {
                        return document.querySelector(selector);
                    } catch (error) {
                        return null;
                    }
                }

                // The value goes in through the prototype's own setter and the events the
                // frameworks listen to: assigning element.value alone leaves Angular and React
                // showing an empty field while the page visibly holds the account.
                function write(element, value) {
                    if (!element || !value) {
                        return false;
                    }
                    var prototype = element.tagName === "TEXTAREA" ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                    var setter = Object.getOwnPropertyDescriptor(prototype, "value");
                    element.focus();
                    if (setter && setter.set) {
                        setter.set.call(element, value);
                    } else {
                        element.value = value;
                    }
                    element.dispatchEvent(new Event("input", { bubbles: true }));
                    element.dispatchEvent(new Event("change", { bubbles: true }));
                    return true;
                }

                function attempt() {
                    if (!onConsole()) {
                        return true;
                    }
                    var field = find(passwordSelector);
                    if (!field) {
                        return false;
                    }
                    try {
                        if (window.sessionStorage.getItem(attemptKey) === origin) {
                            return true;
                        }
                        window.sessionStorage.setItem(attemptKey, origin);
                    } catch (error) {
                        // Storage refused: this document still gets its own attempt.
                    }
                    write(find(userSelector), account.user);
                    write(field, account.password);
                    if (submitSelector) {
                        var button = find(submitSelector);
                        if (button) {
                            button.click();
                        }
                    }
                    return true;
                }

                if (attempt()) {
                    return;
                }

                var timer = window.setInterval(function () {
                    if (attempt() || Date.now() > deadline) {
                        window.clearInterval(timer);
                    }
                }, {{FormPollMilliseconds}});
            })();
            """;
    }

    /// <summary>
    /// The script that gives the tab its attempt back, run when the user reloads: a reload is the
    /// user asking for the same sign-in again, and the empty form the first attempt left behind
    /// would otherwise stay empty.
    /// </summary>
    public static string BuildAttemptResetScript() => $$"""
        (function () {
            "use strict";
            try {
                window.sessionStorage.removeItem("{{AttemptKey}}");
            } catch (error) {
                // Storage refused: there is nothing to give back.
            }
        })();
        """;
}
