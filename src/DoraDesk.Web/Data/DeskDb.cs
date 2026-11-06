using System.Text.Json;
using DoraDesk.Domain.Classification;
using DoraDesk.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DoraDesk.Web.Data;

public enum IncidentStatus { Open, Resolved, Closed }

public sealed class Incident
{
    public int Id { get; set; }
    public required string Reference { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public IncidentStatus Status { get; set; }
    public string Source { get; set; } = "manual";

    public DateTimeOffset? OccurredAt { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
    public DateTimeOffset? RecoveredAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    public ImpactAssessment Assessment { get; set; } = new();

    /// <summary>The last confirmed classification; null until a person has classified the incident.</summary>
    public IncidentClass? Class { get; set; }
    public DateTimeOffset? ClassifiedAt { get; set; }
    public string? ClassifiedBy { get; set; }
    public string ClassificationRationale { get; set; } = "";

    /// <summary>When the incident was first classified as major: the reporting clock starts here and never restarts.</summary>
    public DateTimeOffset? ClassifiedMajorAt { get; set; }

    public RootCauseCategory? RootCause { get; set; }

    /// <summary>Short, reusable root cause label ("expired TLS certificate"), used to find recurring incidents.</summary>
    public string RootCauseKey { get; set; } = "";
    public string RootCauseDescription { get; set; } = "";
    public string MeasuresTaken { get; set; } = "";
    public string LessonsLearned { get; set; } = "";
    public bool ClientsInformed { get; set; }
    public string? ThirdPartyProvider { get; set; }
    public List<string> Techniques { get; set; } = [];

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Optimistic concurrency: two people editing the same incident must not overwrite each other.</summary>
    public int Version { get; set; }

    public List<Report> Reports { get; set; } = [];
    public List<TimelineEntry> Timeline { get; set; } = [];
    public List<AlertEvidence> Alerts { get; set; } = [];
}

public sealed class Report
{
    public int Id { get; set; }
    public int IncidentId { get; set; }
    public Incident? Incident { get; set; }
    public ReportType Type { get; set; }
    public required string Json { get; set; }
    public required string Sha256 { get; set; }

    /// <summary>The incident version the report was built from; a report of outdated facts cannot be submitted.</summary>
    public int IncidentVersion { get; set; }
    public DateTimeOffset PreparedAt { get; set; }
    public required string PreparedBy { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? SubmittedBy { get; set; }
    public string? AuthorityReference { get; set; }
}

public sealed class TimelineEntry
{
    public long Id { get; set; }
    public int IncidentId { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class AlertEvidence
{
    public int Id { get; set; }
    public int IncidentId { get; set; }
    public required string RuleId { get; set; }
    public required string Title { get; set; }
    public required string Severity { get; set; }
    public string Technique { get; set; } = "";
    public string? User { get; set; }
    public string? SourceIp { get; set; }
    public string? Host { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public int EventCount { get; set; }
}

public sealed class DeskDb(DbContextOptions<DeskDb> options) : DbContext(options)
{
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<TimelineEntry> Timeline => Set<TimelineEntry>();
    public DbSet<AlertEvidence> Alerts => Set<AlertEvidence>();

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // SQLite cannot compare or sort DateTimeOffset values; stored as a sortable 64-bit number instead
        b.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        b.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Incident>(e =>
        {
            e.HasIndex(i => i.Reference).IsUnique();
            e.Property(i => i.Reference).HasMaxLength(20);
            e.Property(i => i.Title).HasMaxLength(200);
            e.Property(i => i.Description).HasMaxLength(8000);
            e.Property(i => i.RootCauseKey).HasMaxLength(120);
            e.Property(i => i.Status).HasConversion<string>().HasMaxLength(10);
            e.Property(i => i.Class).HasConversion<string>().HasMaxLength(10);
            e.Property(i => i.RootCause).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.Version).IsConcurrencyToken();
            e.Property(i => i.Assessment).HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<ImpactAssessment>(v, Json) ?? new ImpactAssessment(),
                new ValueComparer<ImpactAssessment>((x, y) => JsonSerializer.Serialize(x, Json) == JsonSerializer.Serialize(y, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(), v => v));
            e.Property(i => i.Techniques).HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<string>>(v, Json) ?? new List<string>(),
                new ValueComparer<List<string>>((x, y) => x!.SequenceEqual(y!), v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s)), v => v.ToList()));
            e.HasMany(i => i.Reports).WithOne(r => r.Incident).HasForeignKey(r => r.IncidentId);
            e.HasMany(i => i.Timeline).WithOne().HasForeignKey(t => t.IncidentId);
            e.HasMany(i => i.Alerts).WithOne().HasForeignKey(a => a.IncidentId);
        });
        b.Entity<Report>(e =>
        {
            e.Property(r => r.Type).HasConversion<string>().HasMaxLength(15);
            e.HasIndex(r => new { r.IncidentId, r.Type });
        });
        b.Entity<TimelineEntry>().HasIndex(t => new { t.IncidentId, t.At });
    }
}
