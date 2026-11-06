using DoraDesk.Web.Data;
using DoraDesk.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace DoraDesk.Tests;

/// <summary>The real application in memory, with its own SQLite file and a clock the test controls.</summary>
public sealed class DeskApp : WebApplicationFactory<Program>
{
    readonly string dbPath = Path.Combine(Path.GetTempPath(), $"dora-desk-test-{Guid.NewGuid():N}.db");
    readonly bool demoLogin;
    readonly bool seed;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 3, 2, 8, 0, 0, TimeSpan.Zero));

    public DeskApp(bool demoLogin = true, bool seed = false)
    {
        this.demoLogin = demoLogin;
        this.seed = seed;
    }

    public static readonly Actor Sam = new("sam.soc", new HashSet<string> { Roles.Responder });
    public static readonly Actor Iris = new("iris.isb", new HashSet<string> { Roles.Officer });
    public static readonly Actor Jonas = new("jonas.isb", new HashSet<string> { Roles.Officer });
    public static readonly Actor Ana = new("ana.audit", new HashSet<string> { Roles.Auditor });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Desk", $"Data Source={dbPath};Pooling=false");
        builder.UseSetting("Desk:DemoLogin", demoLogin ? "true" : "false");
        builder.UseSetting("Desk:SeedDemoData", seed ? "true" : "false");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
        });
    }

    public IncidentService Incidents => Services.CreateScope().ServiceProvider.GetRequiredService<IncidentService>();

    public DeskDb Db() => Services.GetRequiredService<IDbContextFactory<DeskDb>>().CreateDbContext();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }
}
