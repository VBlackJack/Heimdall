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

using Heimdall.Core.Redaction;

namespace Heimdall.Core.Tests.Redaction;

/// <summary>
/// Exact-output positive controls and survival cases per rule. Every call goes through
/// <see cref="RedactionRuleCatalog.Default"/> via <see cref="TextRedactor"/>.
/// </summary>
public sealed class RedactionRuleControlsTests
{
    private static string Mask(string input) => TextRedactor.Redact(input, new RedactionOptions()).Text;

    private static string Pseudonymize(string input) =>
        TextRedactor.Redact(input, new RedactionOptions { PseudonymizeHosts = true }).Text;

    // Producer: PrivateKeyBlockMasker.Apply, trigger: a "-----BEGIN ... PRIVATE KEY-----" marker line.
    [Theory]
    [InlineData(
        "before\n-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA\nabcd+/==\n-----END RSA PRIVATE KEY-----\nafter",
        "before\n[masked:private-key-block]\nafter")]
    [InlineData(
        "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkq\n-----END PRIVATE KEY-----",
        "[masked:private-key-block]")]
    [InlineData(
        "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXk=\n-----END OPENSSH PRIVATE KEY-----\nok",
        "[masked:private-key-block]\nok")]
    [InlineData(
        "-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,ABCDEF\n\nMIIEowIBAAKC\n-----END RSA PRIVATE KEY-----\nafter",
        "[masked:private-key-block]\nafter")]
    [InlineData(
        "-----BEGIN PRIVATE KEY-----\r\nMIIEvQIB\r\n-----END PRIVATE KEY-----\r\nafter",
        "[masked:private-key-block]\r\nafter")]
    [InlineData(
        "{\"k\":\"-----BEGIN PRIVATE KEY-----\\nMIIEvQ\\n-----END PRIVATE KEY-----\\n\"}",
        "{\"k\":\"[masked:private-key-block]\\n\"}")]
    public void PrivateKeyBlock_FullBlock_IsReplacedByOneToken(string input, string expected)
    {
        Assert.Equal(expected, Mask(input));
    }

    // Producer: PrivateKeyBlockMasker.Apply, trigger: an END marker with no BEGIN (the text starts mid-key).
    [Fact]
    public void PrivateKeyBlock_EndWithoutBegin_MasksTheBase64LinesBeforeIt()
    {
        string input = "MIIEowIBAAKCAQEA\nabcd+/==\n-----END RSA PRIVATE KEY-----\nuser@host:~$ ls";

        Assert.Equal("[masked:private-key-block]\nuser@host:~$ ls", Mask(input));
    }

    // Producer: PrivateKeyBlockMasker.Apply, trigger: a BEGIN marker with no END (the key is cut).
    [Fact]
    public void PrivateKeyBlock_BeginWithoutEnd_MasksUpToTheEndOfTheBase64Lines()
    {
        string input = "-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkq\nAbCdEfGh\nuser@host:~$ ls";

        Assert.Equal("[masked:private-key-block]\nuser@host:~$ ls", Mask(input));
    }

    [Theory]
    [InlineData("-----BEGIN CERTIFICATE-----\nMIIDdzCCAl+gAwIBAgI\n-----END CERTIFICATE-----")]
    [InlineData("-----BEGIN PUBLIC KEY-----\nMFwwDQYJKoZIhvcNAQE\n-----END PUBLIC KEY-----")]
    public void PrivateKeyBlock_CertificatesAndPublicKeys_Survive(string input)
    {
        Assert.Equal(input, Mask(input));
    }

    // Producer: RedactionPatterns.AuthorizationHeader, trigger: "Authorization: Bearer|Basic <token>".
    [Theory]
    [InlineData(
        "curl -H 'Authorization: Bearer abcdef123456' https://x",
        "curl -H 'Authorization: Bearer [masked:authorization-header]' https://x")]
    [InlineData(
        "Authorization: Basic dXNlcjpwYXNzd29yZA==",
        "Authorization: Basic [masked:authorization-header]")]
    [InlineData(
        "authorization:bearer abcdef123456",
        "authorization:bearer [masked:authorization-header]")]
    public void AuthorizationHeader_TokenIsMasked(string input, string expected)
    {
        Assert.Equal(expected, Mask(input));
    }

    [Theory]
    [InlineData("WWW-Authenticate: Basic realm=\"x\"")]
    [InlineData("Visual Basic 6.0")]
    [InlineData("Basic auth enabled")]
    [InlineData("Authorization: Basic abc")]
    public void AuthorizationHeader_UnrelatedText_Survives(string input)
    {
        Assert.Equal(input, Mask(input));
    }

    // Producer: RedactionPatterns.UrlCredentials, trigger: "<scheme>://user:PASSWORD@host".
    [Theory]
    [InlineData(
        "postgres://admin:s3cr3t@db.local:5432/app",
        "postgres://admin:[masked:url-credentials]@db.local:5432/app")]
    [InlineData("ssh://bob:p%40ss@host", "ssh://bob:[masked:url-credentials]@host")]
    public void UrlCredentials_PasswordIsMasked(string input, string expected)
    {
        Assert.Equal(expected, Mask(input));
    }

    [Theory]
    [InlineData("user@host")]
    [InlineData("scp file user@host:/tmp")]
    [InlineData("12:30:45@x")]
    [InlineData("http://host:8080/path")]
    [InlineData("ssh://user@host:22")]
    public void UrlCredentials_UnrelatedText_Survives(string input)
    {
        Assert.Equal(input, Mask(input));
    }

    // Producer: RedactionPatterns.Cli* (CliPasswordOption, CliSshpass, CliExport, CliEnvironmentPassword, CliMysqlPassword).
    [Theory]
    [InlineData("mysql -u root -pS3cret mydb", "mysql -u root -p[masked:cli-password] mydb")]
    [InlineData("mysql -u root -p S3cret mydb", "mysql -u root -p [masked:cli-password] mydb")]
    [InlineData("mysqldump -h db -p'a b c' x", "mysqldump -h db -p'[masked:cli-password]' x")]
    [InlineData("mariadb -pS3cret", "mariadb -p[masked:cli-password]")]
    [InlineData("mysqladmin -uroot -pS3cret status", "mysqladmin -uroot -p[masked:cli-password] status")]
    [InlineData("run --password hunter2", "run --password [masked:cli-password]")]
    [InlineData("run --password=hunter2", "run --password=[masked:cli-password]")]
    [InlineData("run --passwd=hunter2", "run --passwd=[masked:cli-password]")]
    [InlineData("run --password=\"a b\"", "run --password=\"[masked:cli-password]\"")]
    [InlineData("sshpass -p hunter2 ssh bob@host", "sshpass -p [masked:cli-password] ssh bob@host")]
    [InlineData("sshpass -p'x y' ssh h", "sshpass -p'[masked:cli-password]' ssh h")]
    [InlineData("export DB_PASSWORD=hunter2", "export DB_PASSWORD=[masked:cli-password]")]
    [InlineData("export ADMIN_PWD=x1y2", "export ADMIN_PWD=[masked:cli-password]")]
    [InlineData("export DBPASSWD=hunter2", "export DBPASSWD=[masked:cli-password]")]
    [InlineData("PGPASSWORD=hunter2 psql -h db", "PGPASSWORD=[masked:cli-password] psql -h db")]
    [InlineData("MYSQL_PWD=hunter2 mysql -u root", "MYSQL_PWD=[masked:cli-password] mysql -u root")]
    public void CliPassword_PasswordIsMasked(string input, string expected)
    {
        Assert.Equal(expected, Mask(input));
    }

    [Theory]
    [InlineData("ssh -p22 host")]
    [InlineData("ssh -p 2222 host")]
    [InlineData("ps -p 1234")]
    [InlineData("lsof -p1234")]
    [InlineData("docker run -p8080:80")]
    [InlineData("mkdir -pv a")]
    [InlineData("tcpdump -pni eth0")]
    [InlineData("gcc -pthread")]
    [InlineData("grep -P x")]
    [InlineData("PWD=/home/user")]
    [InlineData("OLDPWD=/tmp")]
    [InlineData("cmd --password-file /etc/x")]
    [InlineData("mysql -P 3306 -h host")]
    [InlineData("mysql -u root -p")]
    [InlineData("mysql --protocol=tcp -h h")]
    public void CliPassword_UnrelatedText_Survives(string input)
    {
        Assert.Equal(input, Mask(input));
    }

    // Producer: RedactionPatterns.KeyValueSecret, trigger: a key ending in a secret word, a separator, a value.
    [Theory]
    [InlineData("password=hunter2", "password=[masked:key-value-secret]")]
    [InlineData("Password=Hunter2", "Password=[masked:key-value-secret]")]
    [InlineData("DB_PASSWORD_X=1 DB_PASSWORD=\"hunter two\"", "DB_PASSWORD_X=1 DB_PASSWORD=\"[masked:key-value-secret]\"")]
    [InlineData("{\"password\":\"x1y2\",\"user\":\"a\"}", "{\"password\":\"[masked:key-value-secret]\",\"user\":\"a\"}")]
    [InlineData("aws_secret_access_key = AbC/dEf+1", "aws_secret_access_key = [masked:key-value-secret]")]
    [InlineData("client_secret: abc123", "client_secret: [masked:key-value-secret]")]
    [InlineData("api-key=zzz999&x=1", "api-key=[masked:key-value-secret]&x=1")]
    [InlineData("apikey: zzz999", "apikey: [masked:key-value-secret]")]
    [InlineData("client-key-data: LS0tLS1CRUdJTg==", "client-key-data: [masked:key-value-secret]")]
    [InlineData("GET /x?token=abc123&y=1", "GET /x?token=[masked:key-value-secret]&y=1")]
    [InlineData("'secret': 'abc 123'", "'secret': '[masked:key-value-secret]'")]
    public void KeyValueSecret_ValueIsMasked(string input, string expected)
    {
        Assert.Equal(expected, Mask(input));
    }

    [Theory]
    [InlineData("max_tokens=4096")]
    [InlineData("TOKEN_TTL=3600")]
    [InlineData("token.json")]
    [InlineData("./secrets:")]
    [InlineData("cat secret.txt")]
    [InlineData("tokenizer = x")]
    [InlineData("secret=")]
    public void KeyValueSecret_UnrelatedText_Survives(string input)
    {
        Assert.Equal(input, Mask(input));
    }

    // Producer: RedactionPatterns.AwsAccessKey, GithubToken, SlackToken, Jwt.
    [Theory]
    [InlineData("key AKIAIOSFODNN7EXAMPLE end", "key [masked:aws-access-key] end")]
    [InlineData("ASIAIOSFODNN7EXAMPLE", "[masked:aws-access-key]")]
    [InlineData("xoxb-1234567890-abcdefghij", "[masked:slack-token]")]
    [InlineData("hook xoxp-1234567890-abcdefghij-zz ok", "hook [masked:slack-token] ok")]
    [InlineData("x eyJhbGciOiJI.eyJzdWIiOiIx.c2lnbmF0dXJl y", "x [masked:jwt] y")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijkl_mnopqrstuv", "[masked:github-token]")]
    public void TokenShapes_AreMasked(string input, string expected)
    {
        Assert.Equal(expected, Mask(input));
    }

    [Fact]
    public void GithubToken_ClassicShape_IsMasked()
    {
        string token = "ghp_" + new string('a', 36);

        Assert.Equal("push [masked:github-token] done", Mask($"push {token} done"));
    }

    [Theory]
    [InlineData("3f786850e387550fdab836ed7e6dc881de23001b")]
    [InlineData("v2026.091401.1")]
    [InlineData("10.0.26200")]
    [InlineData("api.github.com")]
    [InlineData("Heimdall.Core.Redaction")]
    [InlineData("ghp_test.sh")]
    [InlineData("fooxoxb-1234567890abc")]
    [InlineData("AKIAIOSFODNN7EXAMPLEX")]
    [InlineData("xAKIAIOSFODNN7EXAMPLE")]
    [InlineData("eyJhbGci.short")]
    public void TokenShapes_UnrelatedText_Survives(string input)
    {
        Assert.Equal(input, Mask(input));
    }

    // Producer: PasswordPromptMasker.Apply, trigger: a prompt word followed by a colon.
    [Theory]
    [InlineData("Password: hunter2", "Password: [masked:password-prompt]")]
    [InlineData("[sudo] password for bob: hunter2", "[sudo] password for bob: [masked:password-prompt]")]
    [InlineData("Password for 'https://bob@host': hunter2", "Password for 'https://bob@host': [masked:password-prompt]")]
    [InlineData("Enter passphrase for key '/home/bob/.ssh/id': my phrase", "Enter passphrase for key '/home/bob/.ssh/id': [masked:password-prompt]")]
    [InlineData("Mot de passe : secret", "Mot de passe : [masked:password-prompt]")]
    [InlineData("Passwort: geheim", "Passwort: [masked:password-prompt]")]
    [InlineData("Contrase\u00F1a: clave", "Contrase\u00F1a: [masked:password-prompt]")]
    [InlineData("Contrasena: clave", "Contrasena: [masked:password-prompt]")]
    [InlineData("Password:\nhunter2\nuser@host:~$ ls", "Password:\n[masked:password-prompt]\nuser@host:~$ ls")]
    [InlineData("Password:\n-bash: hunter2: command not found", "Password:\n-bash: [masked:password-prompt]: command not found")]
    [InlineData("Login incorrect\nbash: hunter2: command not found", "Login incorrect\nbash: [masked:password-prompt]: command not found")]
    [InlineData("Password:\r\nhunter2\r\nok now", "Password:\r\n[masked:password-prompt]\r\nok now")]
    public void PasswordPrompt_EchoedPassword_IsMasked(string input, string expected)
    {
        Assert.Equal(expected, Mask(input));
    }

    [Theory]
    [InlineData("Password:\nLast login: Mon Oct 19 10:00:00 on pts/0")]
    [InlineData("Password:\nuser@host:~$ ls")]
    [InlineData("Password:\nuser@host:~$")]
    [InlineData("Password:\nroot#")]
    [InlineData("Password:\n\nhunter2")]
    [InlineData("Password:\nSorry, try again.")]
    [InlineData("Password:\nPermission denied, please try again.")]
    [InlineData("Password:\nPassword:")]
    [InlineData("Password:")]
    [InlineData("Password: \"\"")]
    [InlineData("Password: ''")]
    [InlineData("bash: foo: command not found")]
    [InlineData("Password:\nLast login: x\nfoo bar\nbaz qux\nbash: x: command not found")]
    [InlineData("the password policy is strict")]
    public void PasswordPrompt_UnrelatedText_Survives(string input)
    {
        Assert.Equal(input, Mask(input));
    }

    // Producer: PseudonymMasker.ApplyIpv4 / ApplyHostNames, trigger: option PseudonymizeHosts.
    [Theory]
    [InlineData("ssh 10.1.2.3 and 10.1.2.4 then 10.1.2.3", "ssh ip-1 and ip-2 then ip-1")]
    [InlineData("ping 192.168.0.10.", "ping ip-1.")]
    [InlineData("bob@hostA:~$ ls", "bob@host-1:~$ ls")]
    [InlineData("curl https://web.example.com/x", "curl https://host-1/x")]
    [InlineData("a.b.example.org", "host-1")]
    public void Pseudonyms_WhenEnabled_ReplaceAddressesAndHosts(string input, string expected)
    {
        Assert.Equal(expected, Pseudonymize(input));
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("255.255.255.0")]
    [InlineData("255.255.240.0")]
    [InlineData("255.0.0.0")]
    [InlineData("999.1.1.1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("10.0.26200")]
    [InlineData("10.0.26200.1234")]
    [InlineData("config.yaml")]
    [InlineData("Heimdall.Core.dll")]
    [InlineData("a.b.json")]
    [InlineData("v2026.091401.1")]
    [InlineData("ip-1 host-1")]
    [InlineData("bob@host-1:~$")]
    public void Pseudonyms_WhenEnabled_UnrelatedText_Survives(string input)
    {
        Assert.Equal(input, Pseudonymize(input));
    }

    [Theory]
    [InlineData("ssh 10.1.2.3 as bob@hostA")]
    [InlineData("web.example.com 192.168.0.10")]
    public void Pseudonyms_WhenDisabled_EverythingSurvives(string input)
    {
        Assert.Equal(input, Mask(input));
    }
}
