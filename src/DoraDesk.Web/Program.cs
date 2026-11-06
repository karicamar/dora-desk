using System.Security.Claims;
using DoraDesk.Web.Components;
using DoraDesk.Web.Data;
using DoraDesk.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DeskOptions>(builder.Configuration.GetSection("Desk"));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Clock>();
builder.Services.AddScoped<IncidentService>();
builder.Services.AddDbContextFactory<DeskDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Desk") ?? "Data Source=dora-desk.db"));
builder.Services.AddHealthChecks().AddDbContextCheck<DeskDb>();

// Sign-in: OpenID Connect against the bank's identity provider (for example Entra ID) when configured.
// Without it, only the passwordless demo login exists, and only where Desk:DemoLogin is enabled.
var oidc = builder.Configuration.GetSection("Auth:Oidc");
var auth = builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        if (oidc["Authority"] is { Length: > 0 }) o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        o.Cookie.Name = "dora-desk";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    });
if (oidc["Authority"] is { Length: > 0 } authority)
    auth.AddOpenIdConnect(o =>
    {
        o.Authority = authority;
        o.ClientId = oidc["ClientId"];
        o.ClientSecret = oidc["ClientSecret"];
        o.ResponseType = "code";
        o.UsePkce = true;
        o.MapInboundClaims = false;
        o.TokenValidationParameters.NameClaimType = "preferred_username";
        o.TokenValidationParameters.RoleClaimType = "roles";
        o.Scope.Add("profile");
        o.Events.OnTokenValidated = ctx =>
        {
            // Roles arrive as "roles" claims (app roles in Entra ID); the app checks ClaimTypes.Role
            var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
            foreach (var role in identity.FindAll("roles").Select(c => c.Value).Where(r => Roles.All.Contains(r)).ToList())
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
            identity.AddClaim(new Claim(ClaimTypes.Name, identity.Name ?? ctx.Principal.FindFirstValue("sub") ?? "unknown"));
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.Edit, p => p.RequireRole(Roles.Responder, Roles.Officer))
    .AddPolicy(Policies.Submit, p => p.RequireRole(Roles.Officer));
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DeskDb>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    if (app.Configuration.GetValue("Desk:SeedDemoData", app.Environment.IsDevelopment()))
        await DemoData.SeedAsync(db, app.Services.GetRequiredService<TimeProvider>().GetUtcNow(),
            app.Services.GetRequiredService<IOptions<DeskOptions>>().Value.Entity);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    // Blazor Server needs no inline script or style: everything comes from the app itself, over its own WebSocket
    h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; "
                              + "object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    await next();
});

// Friendly "not found" page for browsers. Only for GET: re-running a rejected POST (for example a bad antiforgery
// token) through the pipeline would validate the same form a second time and fail with an exception instead of 400.
app.UseWhen(ctx => HttpMethods.IsGet(ctx.Request.Method),
    b => b.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();

app.MapPost("/account/login", async ([FromForm] string user, [FromForm] string? returnUrl, HttpContext ctx, IOptions<DeskOptions> options) =>
{
    if (!options.Value.DemoLogin) return Results.NotFound();
    var demo = DemoUser.All.FirstOrDefault(u => u.Name == user);
    if (demo is null) return Results.Redirect("/login?error=1");
    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, demo.Name), new Claim(ClaimTypes.Role, demo.Role)],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(new ClaimsPrincipal(identity));
    return Results.LocalRedirect(SafeReturn(returnUrl));
}).AllowAnonymous();

app.MapGet("/account/sso", (string? returnUrl) =>
    oidc["Authority"] is { Length: > 0 }
        ? Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) }, [OpenIdConnectDefaults.AuthenticationScheme])
        : Results.NotFound()).AllowAnonymous();

app.MapPost("/account/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/login");
});

app.MapGet("/reports/{id:int}.json", async (int id, IDbContextFactory<DeskDb> dbs) =>
{
    await using var db = await dbs.CreateDbContextAsync();
    var report = await db.Reports.AsNoTracking().Include(r => r.Incident).SingleOrDefaultAsync(r => r.Id == id);
    return report is null
        ? Results.NotFound()
        : Results.File(System.Text.Encoding.UTF8.GetBytes(report.Json), "application/json",
            $"{report.Incident!.Reference}-{report.Type.ToString().ToLowerInvariant()}-{report.Id}.json");
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

// Only local paths: an attacker-supplied returnUrl must not turn the login into an open redirect
static string SafeReturn(string? url) =>
    url is { Length: > 0 } r && r[0] == '/' && (r.Length == 1 || (r[1] != '/' && r[1] != (char)92)) && !r.Contains("://") ? r : "/";

public partial class Program;
