using DoraDesk.Domain.Classification;
using DoraDesk.Domain.Integration;
using DoraDesk.Domain.Reporting;
using DoraDesk.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DoraDesk.Web.Services;

public sealed record NewIncident(string Title, string Description, DateTimeOffset? OccurredAt, DateTimeOffset DetectedAt, string? ThirdPartyProvider);

public sealed record IncidentDetails
{
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public DateTimeOffset? OccurredAt { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public DateTimeOffset? RecoveredAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public RootCauseCategory? RootCause { get; init; }
    public string RootCauseKey { get; init; } = "";
    public string RootCauseDescription { get; init; } = "";
    public string MeasuresTaken { get; init; } = "";
    public string LessonsLearned { get; init; } = "";
    public bool ClientsInformed { get; init; }
    public string? ThirdPartyProvider { get; init; }
}

public sealed record DashboardRow(
    int Id, string Reference, string Title, IncidentStatus Status, IncidentClass? Class, DateTimeOffset DetectedAt,
    DeadlineInfo? Next, bool AwaitingClassification);

/// <summary>
/// The incident workflow. Every change is one transaction together with its timeline entry, checks the role of the
/// actor, and refuses to overwrite a newer version of the incident.
/// </summary>
public sealed class IncidentService(IDbContextFactory<DeskDb> dbs, TimeProvider time, IOptions<DeskOptions> options)
{
    readonly ReportingEntity entity = options.Value.Entity;

    public async Task<int> CreateAsync(Actor actor, NewIncident input, CancellationToken ct = default)
    {
        RequireEdit(actor);
        var title = Clean(input.Title, 200);
        if (title.Length == 0) throw new DeskException(DeskError.Invalid, "A title is required.");
        var now = time.GetUtcNow();
        if (input.DetectedAt > now.AddMinutes(5)) throw new DeskException(DeskError.Invalid, "The detection time is in the future.");
        if (input.OccurredAt > input.DetectedAt) throw new DeskException(DeskError.Invalid, "An incident cannot occur after it was detected.");

        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var incident = new Incident
        {
            Reference = await NextReferenceAsync(db, now, ct),
            Title = title,
            Description = Clean(input.Description, 8000),
            OccurredAt = input.OccurredAt,
            DetectedAt = input.DetectedAt,
            ThirdPartyProvider = NullIfEmpty(Clean(input.ThirdPartyProvider, 120)),
            CreatedBy = actor.Name,
            CreatedAt = now,
        };
        db.Incidents.Add(incident);
        await db.SaveChangesAsync(ct);
        Log(db, incident, actor, now, "incident.created", $"Detected {Utc(input.DetectedAt)}.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return incident.Id;
    }

    public async Task<int> ImportAsync(Actor actor, IncidentDraft draft, CancellationToken ct = default)
    {
        RequireEdit(actor);
        var id = await CreateAsync(actor, new NewIncident(draft.Title, draft.Description, draft.OccurredAt, draft.DetectedAt, null), ct);
        await using var db = await dbs.CreateDbContextAsync(ct);
        var incident = await db.Incidents.SingleAsync(i => i.Id == id, ct);
        incident.Source = "logsentinel";
        incident.Techniques = draft.Techniques.ToList();
        db.Alerts.AddRange(draft.Alerts.Select(a => new AlertEvidence
        {
            IncidentId = id,
            RuleId = a.RuleId,
            Title = Clean(a.Title, 300),
            Severity = a.Severity,
            Technique = $"{a.TechniqueId} {a.TechniqueName}".Trim(),
            User = a.User,
            SourceIp = a.SourceIp,
            Host = a.Host,
            FirstSeen = a.FirstSeen,
            LastSeen = a.LastSeen,
            EventCount = a.EventCount,
        }));
        Log(db, incident, actor, time.GetUtcNow(), "incident.imported", $"{draft.Alerts.Count} LogSentinel alert(s) attached.");
        await db.SaveChangesAsync(ct);
        return id;
    }

    public async Task<Incident> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        return await db.Incidents.AsNoTracking()
                   .Include(i => i.Reports.OrderBy(r => r.PreparedAt))
                   .Include(i => i.Timeline.OrderBy(t => t.At).ThenBy(t => t.Id))
                   .Include(i => i.Alerts.OrderBy(a => a.FirstSeen))
                   .AsSplitQuery()
                   .SingleOrDefaultAsync(i => i.Id == id, ct)
               ?? throw new DeskException(DeskError.NotFound, $"Incident {id} does not exist.");
    }

    public async Task UpdateDetailsAsync(Actor actor, int id, int expectedVersion, IncidentDetails d, CancellationToken ct = default)
    {
        RequireEdit(actor);
        if (Clean(d.Title, 200).Length == 0) throw new DeskException(DeskError.Invalid, "A title is required.");
        if (d.OccurredAt > d.DetectedAt) throw new DeskException(DeskError.Invalid, "An incident cannot occur after it was detected.");
        if (d.ResolvedAt < d.DetectedAt || d.RecoveredAt < d.DetectedAt)
            throw new DeskException(DeskError.Invalid, "Recovery and resolution cannot be before detection.");

        await ChangeAsync(actor, id, expectedVersion, "incident.updated", (i, _) =>
        {
            if (i.ClassifiedMajorAt is { } c && d.DetectedAt > c)
                throw new DeskException(DeskError.Invalid, "The detection time cannot be after the classification.");
            var changed = new List<string>();
            void Track(string name, object? before, object? after) { if (!Equals(before, after)) changed.Add(name); }
            Track("title", i.Title, Clean(d.Title, 200));
            Track("detected", i.DetectedAt, d.DetectedAt);
            Track("root cause", i.RootCause, d.RootCause);
            Track("resolved", i.ResolvedAt, d.ResolvedAt);
            i.Title = Clean(d.Title, 200);
            i.Description = Clean(d.Description, 8000);
            i.OccurredAt = d.OccurredAt;
            i.DetectedAt = d.DetectedAt;
            i.RecoveredAt = d.RecoveredAt;
            i.ResolvedAt = d.ResolvedAt;
            i.RootCause = d.RootCause;
            i.RootCauseKey = Clean(d.RootCauseKey, 120);
            i.RootCauseDescription = Clean(d.RootCauseDescription, 4000);
            i.MeasuresTaken = Clean(d.MeasuresTaken, 4000);
            i.LessonsLearned = Clean(d.LessonsLearned, 4000);
            if (d.ClientsInformed && !i.ClientsInformed) changed.Add("clients informed");
            i.ClientsInformed = d.ClientsInformed;
            i.ThirdPartyProvider = NullIfEmpty(Clean(d.ThirdPartyProvider, 120));
            return changed.Count > 0 ? "Changed: " + string.Join(", ", changed) + "." : "Details edited.";
        }, ct);
    }

    /// <summary>Saves the impact assessment. Classification stays a separate, deliberate step.</summary>
    public Task SaveAssessmentAsync(Actor actor, int id, int expectedVersion, ImpactAssessment assessment, CancellationToken ct = default)
    {
        RequireEdit(actor);
        Validate(assessment);
        return ChangeAsync(actor, id, expectedVersion, "assessment.saved", (i, _) =>
        {
            i.Assessment = assessment with { MemberStatesAffected = assessment.MemberStatesAffected.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length == 2).Distinct().Order().ToList() };
            var preview = Classifier.Classify(i.Assessment);
            return $"Assessment would classify as {(preview.IsMajor ? "major" : "not major")}.";
        }, ct);
    }

    /// <summary>
    /// A person confirms the classification computed from the saved assessment. The first classification as major
    /// starts the reporting clock; a later downgrade is recorded but does not undo reports already sent.
    /// </summary>
    public async Task<ClassificationResult> ClassifyAsync(Actor actor, int id, int expectedVersion, CancellationToken ct = default)
    {
        RequireEdit(actor);
        ClassificationResult? result = null;
        await ChangeAsync(actor, id, expectedVersion, "incident.classified", (i, now) =>
        {
            if (i.Status == IncidentStatus.Closed) throw new DeskException(DeskError.Conflict, "The incident is closed.");
            result = Classifier.Classify(i.Assessment);
            var previous = i.Class;
            i.Class = result.Class;
            i.ClassifiedAt = now;
            i.ClassifiedBy = actor.Name;
            i.ClassificationRationale = result.Rationale;
            if (result.IsMajor && i.ClassifiedMajorAt is null) i.ClassifiedMajorAt = now;
            var change = previous is null ? "" : previous == result.Class ? " Unchanged." : $" Previously {Label(previous.Value)}.";
            return result.Rationale + change;
        }, ct);
        return result!;
    }

    /// <summary>Builds the next report from the current facts. Responders prepare, officers submit.</summary>
    public async Task<int> PrepareReportAsync(Actor actor, int id, ReportType type, CancellationToken ct = default)
    {
        RequireEdit(actor);
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var incident = await db.Incidents.Include(i => i.Reports).SingleOrDefaultAsync(i => i.Id == id, ct)
                       ?? throw new DeskException(DeskError.NotFound, $"Incident {id} does not exist.");
        if (incident.ClassifiedMajorAt is null)
            throw new DeskException(DeskError.Conflict, "Only incidents classified as major are reported.");

        if (incident.Reports.Any(r => r.Type == ReportType.Final && r.SubmittedAt != null))
            throw new DeskException(DeskError.Conflict, "The final report has been submitted; nothing more to report.");
        var allowed = NextReportTypes(incident);
        if (!allowed.Contains(type))
            throw new DeskException(DeskError.Conflict, $"The next report is the {Label(allowed[0])} report, not the {Label(type)} report.");

        var now = time.GetUtcNow();
        var prepared = IncidentReport.Prepare(type, entity, Facts(incident), now);
        var report = new Report
        {
            IncidentId = id,
            Type = type,
            Json = prepared.Json,
            Sha256 = prepared.Sha256,
            IncidentVersion = incident.Version,
            PreparedAt = now,
            PreparedBy = actor.Name,
        };
        db.Reports.Add(report);
        Log(db, incident, actor, now, "report.prepared",
            $"{Label(type)} report, SHA-256 {prepared.Sha256[..12]}…" + (prepared.IsComplete ? "" : $" Missing: {string.Join(", ", prepared.Missing)}."));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return report.Id;
    }

    /// <summary>
    /// Marks a prepared report as submitted to the authority. Four-eyes principle: the officer who submits cannot be
    /// the person who prepared it. The facts must not have changed since the report was built, and it must be complete.
    /// </summary>
    public async Task SubmitReportAsync(Actor actor, int reportId, string? authorityReference, CancellationToken ct = default)
    {
        if (!actor.CanSubmit) throw new DeskException(DeskError.Forbidden, "Only the information security officer submits reports.");
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var report = await db.Reports.Include(r => r.Incident).ThenInclude(i => i!.Reports).SingleOrDefaultAsync(r => r.Id == reportId, ct)
                     ?? throw new DeskException(DeskError.NotFound, $"Report {reportId} does not exist.");
        var incident = report.Incident!;
        var now = time.GetUtcNow();

        if (report.SubmittedAt is not null) throw new DeskException(DeskError.Conflict, "This report has already been submitted.");
        if (string.Equals(report.PreparedBy, actor.Name, StringComparison.OrdinalIgnoreCase))
        {
            Log(db, incident, actor, now, "report.four_eyes_denied", $"{Label(report.Type)} report prepared by the same person.");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new DeskException(DeskError.Forbidden, "Four-eyes principle: you prepared this report, so another officer must submit it.");
        }
        if (report.IncidentVersion != incident.Version)
            throw new DeskException(DeskError.Conflict, "The incident changed after this report was prepared. Prepare it again so the authority gets the current facts.");
        if (incident.Reports.Any(r => r.Type == report.Type && r.Id != report.Id && r.SubmittedAt is null && r.PreparedAt > report.PreparedAt))
            throw new DeskException(DeskError.Conflict, "A newer draft of this report exists; submit that one.");

        var missing = IncidentReport.Prepare(report.Type, entity, Facts(incident), report.PreparedAt).Missing;
        if (missing.Count > 0)
            throw new DeskException(DeskError.Invalid, "The report is incomplete: " + string.Join(", ", missing) + ".");

        var deadline = Timeline(incident).FirstOrDefault(d => d.Type == report.Type && !d.IsSubmitted);
        var timing = deadline is null ? "" : now > deadline.Due ? $" Late: {Deadlines.Describe(deadline.Due - now)}." : " On time.";
        report.SubmittedAt = now;
        report.SubmittedBy = actor.Name;
        report.AuthorityReference = NullIfEmpty(Clean(authorityReference, 60));
        Log(db, incident, actor, now, "report.submitted",
            $"{Label(report.Type)} report submitted to {entity.CompetentAuthority}" + (report.AuthorityReference is { } r ? $" (reference {r})" : "") + "." + timing);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public Task ResolveAsync(Actor actor, int id, int expectedVersion, CancellationToken ct = default)
    {
        RequireEdit(actor);
        return ChangeAsync(actor, id, expectedVersion, "incident.resolved", (i, now) =>
        {
            if (i.Status != IncidentStatus.Open) throw new DeskException(DeskError.Conflict, "Only open incidents can be resolved.");
            if (i.Class is null) throw new DeskException(DeskError.Conflict, "Classify the incident before resolving it.");
            i.Status = IncidentStatus.Resolved;
            i.ResolvedAt ??= now;
            i.RecoveredAt ??= i.ResolvedAt;
            return $"Resolved {Utc(i.ResolvedAt.Value)}.";
        }, ct);
    }

    /// <summary>A major incident is closed only after the final report went out; a minor one after a root cause is recorded.</summary>
    public Task CloseAsync(Actor actor, int id, int expectedVersion, CancellationToken ct = default)
    {
        if (!actor.CanSubmit) throw new DeskException(DeskError.Forbidden, "Only the information security officer closes incidents.");
        return ChangeAsync(actor, id, expectedVersion, "incident.closed", (i, _) =>
        {
            if (i.Status != IncidentStatus.Resolved) throw new DeskException(DeskError.Conflict, "Resolve the incident first.");
            if (i.ClassifiedMajorAt is not null && !i.Reports.Any(r => r.Type == ReportType.Final && r.SubmittedAt != null))
                throw new DeskException(DeskError.Conflict, "A major incident can only be closed after the final report was submitted.");
            if (i.RootCause is null || i.RootCauseKey.Length == 0)
                throw new DeskException(DeskError.Conflict, "Record the root cause (category and short label) first: it is needed to detect recurring incidents.");
            i.Status = IncidentStatus.Closed;
            return "Closed.";
        }, ct, includeReports: true);
    }

    public async Task<IReadOnlyList<DashboardRow>> DashboardAsync(bool includeClosed = false, CancellationToken ct = default)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var incidents = await db.Incidents.AsNoTracking().Include(i => i.Reports)
            .Where(i => includeClosed || i.Status != IncidentStatus.Closed)
            .ToListAsync(ct);
        return incidents
            .Select(i =>
            {
                var next = i.ClassifiedMajorAt is null ? null : Deadlines.Next(Timeline(i));
                return new DashboardRow(i.Id, i.Reference, i.Title, i.Status, i.Class, i.DetectedAt, next, i.Class is null);
            })
            .OrderBy(r => r.Next?.Due ?? (r.AwaitingClassification ? r.DetectedAt.AddHours(24) : DateTimeOffset.MaxValue))
            .ThenByDescending(r => r.DetectedAt)
            .ToList();
    }

    public async Task<IReadOnlyList<RecurringIncidentGroup>> RecurringAsync(CancellationToken ct = default)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        var past = await db.Incidents.AsNoTracking().Where(i => i.Class == IncidentClass.NotMajor && i.RootCauseKey != "").ToListAsync(ct);
        return Recurrence.Analyze(past.Select(i => new PastIncident(i.Reference, i.OccurredAt ?? i.DetectedAt, i.RootCauseKey, false, i.Assessment)), time.GetUtcNow());
    }

    /// <summary>
    /// Which reports can be prepared now: the initial notification first, then at least one intermediate report;
    /// after that further intermediate updates or the final report. Nothing once the final report is out.
    /// </summary>
    public static IReadOnlyList<ReportType> NextReportTypes(Incident i)
    {
        if (i.ClassifiedMajorAt is null) return [];
        var sent = i.Reports.Where(r => r.SubmittedAt != null).Select(r => r.Type).ToHashSet();
        return sent.Contains(ReportType.Final) ? []
            : !sent.Contains(ReportType.Initial) ? [ReportType.Initial]
            : !sent.Contains(ReportType.Intermediate) ? [ReportType.Intermediate]
            : [ReportType.Intermediate, ReportType.Final];
    }

    public static IReadOnlyList<DeadlineInfo> Timeline(Incident i)
    {
        if (i.ClassifiedMajorAt is not { } classified) return [];
        var submitted = i.Reports.Where(r => r.SubmittedAt != null).ToList();
        return Deadlines.Timeline(i.DetectedAt, classified,
            submitted.Where(r => r.Type == ReportType.Initial).Min(r => r.SubmittedAt),
            submitted.Where(r => r.Type == ReportType.Intermediate).Max(r => r.SubmittedAt),
            submitted.Where(r => r.Type == ReportType.Final).Min(r => r.SubmittedAt));
    }

    public static IncidentFacts Facts(Incident i) => new()
    {
        Reference = i.Reference,
        Title = i.Title,
        Description = i.Description,
        OccurredAt = i.OccurredAt,
        DetectedAt = i.DetectedAt,
        ClassifiedMajorAt = i.ClassifiedMajorAt,
        RecoveredAt = i.RecoveredAt,
        ResolvedAt = i.ResolvedAt,
        RootCause = i.RootCause,
        RootCauseDescription = i.RootCauseDescription,
        MeasuresTaken = i.MeasuresTaken,
        LessonsLearned = i.LessonsLearned,
        ClientsInformed = i.ClientsInformed,
        ThirdPartyProvider = i.ThirdPartyProvider,
        Techniques = i.Techniques,
        Assessment = i.Assessment,
    };

    public static string Label(IncidentClass c) => c == IncidentClass.Major ? "major" : "not major";
    public static string Label(ReportType t) => t.ToString().ToLowerInvariant();

    async Task ChangeAsync(Actor actor, int id, int expectedVersion, string action, Func<Incident, DateTimeOffset, string> change,
        CancellationToken ct, bool includeReports = false)
    {
        await using var db = await dbs.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var query = includeReports ? db.Incidents.Include(i => i.Reports) : db.Incidents.AsQueryable();
        var incident = await query.SingleOrDefaultAsync(i => i.Id == id, ct)
                       ?? throw new DeskException(DeskError.NotFound, $"Incident {id} does not exist.");
        if (incident.Version != expectedVersion)
            throw new DeskException(DeskError.Conflict, $"{incident.Reference} was changed by someone else in the meantime. Reload to see the current version.");
        if (incident.Status == IncidentStatus.Closed && action != "incident.closed")
            throw new DeskException(DeskError.Conflict, $"{incident.Reference} is closed.");

        var now = time.GetUtcNow();
        var detail = change(incident, now);
        incident.Version++;
        Log(db, incident, actor, now, action, detail);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DeskException(DeskError.Conflict, $"{incident.Reference} was changed by someone else in the meantime. Reload to see the current version.");
        }
        await tx.CommitAsync(ct);
    }

    static void Log(DeskDb db, Incident incident, Actor actor, DateTimeOffset at, string action, string detail) =>
        db.Timeline.Add(new TimelineEntry { IncidentId = incident.Id, At = at, Actor = actor.Name, Action = action, Detail = Clean(detail, 2000) });

    static async Task<string> NextReferenceAsync(DeskDb db, DateTimeOffset now, CancellationToken ct)
    {
        var prefix = $"INC-{now.UtcDateTime.Year}-";
        var last = await db.Incidents.Where(i => i.Reference.StartsWith(prefix)).Select(i => i.Reference).ToListAsync(ct);
        var n = last.Select(r => int.TryParse(r[prefix.Length..], out var v) ? v : 0).DefaultIfEmpty(0).Max() + 1;
        return prefix + n.ToString("0000");
    }

    static void RequireEdit(Actor actor)
    {
        if (!actor.CanEdit) throw new DeskException(DeskError.Forbidden, "Your role can read incidents but not change them.");
    }

    static void Validate(ImpactAssessment a)
    {
        static void NotNegative(decimal? v, string name)
        {
            if (v < 0) throw new DeskException(DeskError.Invalid, $"{name} cannot be negative.");
        }
        NotNegative(a.ClientsAffected, "Clients affected");
        NotNegative(a.ClientsUsingService, "Clients using the service");
        NotNegative(a.CounterpartsAffected, "Counterparts affected");
        NotNegative(a.CounterpartsOfService, "Counterparts of the service");
        NotNegative(a.TransactionsAffected, "Transactions affected");
        NotNegative(a.DailyAverageTransactions, "Daily average transactions");
        NotNegative(a.TransactionValueAffectedEur, "Transaction value");
        NotNegative(a.DailyAverageTransactionValueEur, "Daily average value");
        NotNegative(a.EconomicImpactEur, "Economic impact");
        if (a.Duration < TimeSpan.Zero || a.ServiceDowntime < TimeSpan.Zero) throw new DeskException(DeskError.Invalid, "Durations cannot be negative.");
        if (a.ServiceDowntime > a.Duration) throw new DeskException(DeskError.Invalid, "Service downtime cannot be longer than the incident.");
        if (a.ClientsAffected > a.ClientsUsingService && a.ClientsUsingService > 0)
            throw new DeskException(DeskError.Invalid, "More clients affected than use the service.");
        if (a.MemberStatesAffected.Count > 32) throw new DeskException(DeskError.Invalid, "Too many member states.");
    }

    static string Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = s.Trim().Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray();
        var clean = new string(chars).Replace("\r\n", "\n");
        return clean.Length <= max ? clean : clean[..max];
    }

    static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
    static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
}
