using System.Net;
using System.Text.RegularExpressions;
using DoraDesk.Domain.Classification;
using DoraDesk.Domain.Reporting;
using DoraDesk.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using static DoraDesk.Tests.DeskApp;

namespace DoraDesk.Tests;

public partial class HttpTests : IAsyncLifetime
{
    readonly DeskApp app = new(seed: true);

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await app.DisposeAsync();

    HttpClient Client(bool follow = false) => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = follow, HandleCookies = true, BaseAddress = new Uri("https://localhost") });

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    static async Task<HttpResponseMessage> Login(HttpClient client, string user, string? returnUrl = null)
    {
        var page = await client.GetStringAsync("/login");
        var token = TokenPattern().Match(page).Groups[1].Value;
        var form = new Dictionary<string, string> { ["user"] = user, ["__RequestVerificationToken"] = token };
        if (returnUrl is not null) form["returnUrl"] = returnUrl;
        return await client.PostAsync("/account/login", new FormUrlEncodedContent(form));
    }

    [Fact]
    public async Task Everything_requires_sign_in()
    {
        var client = Client();
        foreach (var path in new[] { "/", "/incidents/1", "/import", "/recurring", "/reports/1.json" })
        {
            var r = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.StartsWith("/login", r.Headers.Location!.PathAndQuery);
        }
    }

    [Fact]
    public async Task Login_page_health_and_assets_are_public()
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/app.css")).StatusCode);
    }

    [Fact]
    public async Task Security_headers_are_set()
    {
        var r = await Client().GetAsync("/login");
        var csp = string.Join(";", r.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Demo_login_signs_in_with_the_role_of_the_user()
    {
        var client = Client();
        var r = await Login(client, "iris.isb");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location!.OriginalString);
        var cookie = string.Join(";", r.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var home = await client.GetStringAsync("/");
        Assert.Contains("iris.isb", home);
        Assert.Contains("officer", home);
        Assert.Contains("INC-2026-0004", home);
    }

    [Theory]
    [InlineData("/incidents/3", "/incidents/3")]
    [InlineData("//evil.example/", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    public async Task Return_url_must_be_local(string returnUrl, string expected)
    {
        var r = await Login(Client(), "sam.soc", returnUrl);
        Assert.Equal(expected, r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Unknown_user_is_not_signed_in()
    {
        var r = await Login(Client(), "mallory");
        Assert.Equal("/login?error=1", r.Headers.Location!.OriginalString);
        Assert.False(r.Headers.Contains("Set-Cookie") && r.Headers.GetValues("Set-Cookie").Any(c => c.StartsWith("dora-desk=")));
    }

    [Fact]
    public async Task Login_without_antiforgery_token_is_rejected()
    {
        var r = await Client().PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = "iris.isb" }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Demo_login_can_be_switched_off()
    {
        await using var prod = new DeskApp(demoLogin: false);
        var client = prod.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        var page = await client.GetStringAsync("/login");
        Assert.DoesNotContain("sam.soc", page);
        Assert.Contains("No sign-in method is configured", page);
        var token = TokenPattern().Match(page).Groups[1].Value;
        var r = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["user"] = "iris.isb", ["__RequestVerificationToken"] = token }));
        Assert.False(r.IsSuccessStatusCode || r.StatusCode == HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Prepared_report_downloads_as_json()
    {
        var svc = app.Incidents;
        var id = await svc.CreateAsync(Sam, new NewIncident("Payments down", "SEPA gateway unreachable.", null, app.Time.GetUtcNow(), null));
        await svc.SaveAssessmentAsync(Sam, id, 0, new ImpactAssessment { MaliciousAccessMayCauseDataLoss = true });
        await svc.ClassifyAsync(Sam, id, 1);
        var reportId = await svc.PrepareReportAsync(Sam, id, ReportType.Initial);

        var client = Client();
        await Login(client, "ana.audit");
        var r = await client.GetAsync($"/reports/{reportId}.json");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/json", r.Content.Headers.ContentType!.MediaType);
        Assert.Contains($"{(await svc.GetAsync(id)).Reference}-initial-{reportId}.json", r.Content.Headers.ContentDisposition!.ToString());
        Assert.Contains("\"reportType\": \"initial\"", await r.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/reports/99999.json")).StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        var client = Client();
        await Login(client, "sam.soc");
        var home = await client.GetStringAsync("/");
        var token = TokenPattern().Match(home).Groups[1].Value;
        var r = await client.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal("/login", r.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Demo_data_shows_every_situation()
    {
        var rows = await app.Incidents.DashboardAsync(includeClosed: true);
        var now = app.Time.GetUtcNow();
        Assert.Equal(5, rows.Count);
        Assert.Contains(rows, r => r.Next?.IsOverdue(now) == true);
        Assert.Contains(rows, r => r.Next is { Type: ReportType.Intermediate } d && !d.IsOverdue(now));
        Assert.Contains(rows, r => r.AwaitingClassification);
        Assert.Equal(2, rows.Count(r => r.Status == Web.Data.IncidentStatus.Closed));
        Assert.Single(await app.Incidents.RecurringAsync());
    }
}
