using DoraDesk.Domain.Classification;
using DoraDesk.Domain.Integration;
using DoraDesk.Domain.Reporting;
using DoraDesk.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DoraDesk.Web.Data;

/// <summary>
/// A fictional bank's incident register, relative to "now", so every deadline on the dashboard is live:
/// two minor incidents with the same root cause (together major, Art. 16), a major third-party outage waiting for
/// its intermediate report, a major incident whose initial notification is overdue, and a fresh LogSentinel import.
/// </summary>
public static class DemoData
{
    public static async Task SeedAsync(DeskDb db, DateTimeOffset now, ReportingEntity entity, CancellationToken ct = default)
    {
        if (await db.Incidents.AnyAsync(ct)) return;
        var year = now.UtcDateTime.Year;
        var n = 0;
        string Next() => $"INC-{year}-{++n:0000}";

        // 1 + 2: same root cause, each below the thresholds on its own
        var tls1 = Closed(Next(), "Online banking unavailable: expired TLS certificate on the load balancer", now.AddDays(-118), TimeSpan.FromHours(14),
            new ImpactAssessment
            {
                SupportsCriticalOrImportantFunction = true,
                ClientsAffected = 9_000, ClientsUsingService = 310_000,
                Duration = TimeSpan.FromHours(14), ServiceDowntime = TimeSpan.FromHours(1.5),
                EconomicImpactEur = 55_000m, MemberStatesAffected = ["DE"],
            },
            "The certificate was renewed by hand; the reminder went to a former employee.",
            "Certificate renewed; monitoring alerts 30 days before expiry.");
        var tls2 = Closed(Next(), "Mobile app API down: expired TLS certificate", now.AddDays(-21), TimeSpan.FromHours(12),
            new ImpactAssessment
            {
                SupportsCriticalOrImportantFunction = true,
                ClientsAffected = 14_500, ClientsUsingService = 310_000,
                Duration = TimeSpan.FromHours(12), ServiceDowntime = TimeSpan.FromHours(1.8),
                EconomicImpactEur = 48_000m, MemberStatesAffected = ["DE"],
            },
            "A second certificate (API gateway) was not in the monitoring inventory.",
            "Inventory of all certificates; automated renewal with ACME.");

        // 3: major, initial notification sent on time, intermediate report due tomorrow
        var detected3 = now.AddHours(-50);
        var card = new Incident
        {
            Reference = Next(),
            Title = "Card payments declined: outage at the card processor",
            Description = "Card authorisations failed for debit and credit cards. The external card processor reported a failed database migration in its primary data centre.",
            Status = IncidentStatus.Open,
            OccurredAt = detected3.AddMinutes(-25),
            DetectedAt = detected3,
            RecoveredAt = detected3.AddHours(9),
            ThirdPartyProvider = "CardNet Processing GmbH (demo)",
            Assessment = new ImpactAssessment
            {
                SupportsCriticalOrImportantFunction = true,
                AuthorisedFinancialServiceAffected = true,
                ClientsAffected = 61_000, ClientsUsingService = 310_000,
                TransactionsAffected = 41_000, DailyAverageTransactions = 180_000,
                TransactionValueAffectedEur = 2_870_000m, DailyAverageTransactionValueEur = 14_400_000m,
                Duration = TimeSpan.FromHours(9), ServiceDowntime = TimeSpan.FromHours(7.5),
                Reputation = ReputationalImpact.RepeatedComplaints | ReputationalImpact.MediaAttention,
                MemberStatesAffected = ["AT", "DE"],
                EconomicImpactEur = 40_000m,
            },
            ClientsInformed = true,
            RootCause = RootCauseCategory.SystemFailure,
            RootCauseKey = "third-party database migration",
            CreatedBy = "sam.soc",
            CreatedAt = detected3,
        };
        Classify(card, detected3.AddMinutes(40), "iris.isb");
        var initial = IncidentReport.Prepare(ReportType.Initial, entity, IncidentService.Facts(card), detected3.AddHours(2));
        card.Reports.Add(new Report
        {
            Type = ReportType.Initial, Json = initial.Json, Sha256 = initial.Sha256, IncidentVersion = card.Version,
            PreparedAt = detected3.AddHours(2), PreparedBy = "sam.soc",
            SubmittedAt = detected3.AddHours(3.5), SubmittedBy = "iris.isb", AuthorityReference = "MVP-2026-118204",
        });
        Log(card, detected3, "sam.soc", "incident.created", "Card authorisations failing; processor status page shows an outage.");
        Log(card, detected3.AddMinutes(40), "iris.isb", "incident.classified", card.ClassificationRationale);
        Log(card, detected3.AddHours(2), "sam.soc", "report.prepared", $"initial report, SHA-256 {initial.Sha256[..12]}…");
        Log(card, detected3.AddHours(3.5), "iris.isb", "report.submitted", $"initial report submitted to {entity.CompetentAuthority} (reference MVP-2026-118204). On time.");
        Log(card, detected3.AddHours(9), "sam.soc", "incident.updated", "Changed: recovered. Processor failed over to its secondary site.");

        // 4: classified major five hours ago, initial notification still not sent: overdue
        var detected4 = now.AddHours(-6);
        var batch = new Incident
        {
            Reference = Next(),
            Title = "Core banking end-of-day batch posted wrong interest",
            Description = "The overnight batch applied last month's interest rates to 210,000 savings accounts. Balances shown in online banking are wrong.",
            Status = IncidentStatus.Open,
            OccurredAt = detected4.AddHours(-4),
            DetectedAt = detected4,
            Assessment = new ImpactAssessment
            {
                SupportsCriticalOrImportantFunction = true,
                ClientsAffected = 210_000, ClientsUsingService = 650_000,
                Data = DataImpact.Integrity, DataImpactHarmsBusinessOrCompliance = true,
                Duration = TimeSpan.FromHours(10),
            },
            CreatedBy = "sam.soc",
            CreatedAt = detected4,
        };
        Classify(batch, detected4.AddHours(1), "iris.isb");
        Log(batch, detected4, "sam.soc", "incident.created", "Customer service reports wrong balances.");
        Log(batch, detected4.AddHours(1), "iris.isb", "incident.classified", batch.ClassificationRationale);

        // 5: fresh import from LogSentinel, not yet classified
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "Seed", "logsentinel-payment-fraud.json");
        db.Incidents.AddRange(tls1, tls2, card, batch);
        if (File.Exists(path))
        {
            var alerts = LogSentinelImport.Parse(await File.ReadAllTextAsync(path, ct));
            var shift = now.AddMinutes(-30) - alerts.Max(a => a.LastSeen);
            alerts = alerts.Select(a => a with { FirstSeen = a.FirstSeen + shift, LastSeen = a.LastSeen + shift }).ToList();
            var draft = LogSentinelImport.Draft(alerts, now.AddMinutes(-25));
            var fraud = new Incident
            {
                Reference = Next(),
                Title = draft.Title,
                Description = draft.Description,
                Status = IncidentStatus.Open,
                Source = "logsentinel",
                OccurredAt = draft.OccurredAt,
                DetectedAt = draft.DetectedAt,
                Techniques = draft.Techniques.ToList(),
                Assessment = new ImpactAssessment { SupportsCriticalOrImportantFunction = true },
                CreatedBy = "sam.soc",
                CreatedAt = draft.DetectedAt,
            };
            fraud.Alerts.AddRange(draft.Alerts.Select(a => new AlertEvidence
            {
                RuleId = a.RuleId, Title = a.Title, Severity = a.Severity, Technique = $"{a.TechniqueId} {a.TechniqueName}".Trim(),
                User = a.User, SourceIp = a.SourceIp, Host = a.Host, FirstSeen = a.FirstSeen, LastSeen = a.LastSeen, EventCount = a.EventCount,
            }));
            Log(fraud, draft.DetectedAt, "sam.soc", "incident.imported", $"{draft.Alerts.Count} LogSentinel alert(s) attached.");
            db.Incidents.Add(fraud);
        }
        await db.SaveChangesAsync(ct);
    }

    static Incident Closed(string reference, string title, DateTimeOffset detected, TimeSpan duration, ImpactAssessment a, string rootCause, string measures)
    {
        var i = new Incident
        {
            Reference = reference,
            Title = title,
            Description = title + ".",
            Status = IncidentStatus.Closed,
            OccurredAt = detected.AddMinutes(-10),
            DetectedAt = detected,
            RecoveredAt = detected + duration,
            ResolvedAt = detected + duration,
            Assessment = a,
            RootCause = RootCauseCategory.ProcessFailure,
            RootCauseKey = "expired TLS certificate",
            RootCauseDescription = rootCause,
            MeasuresTaken = measures,
            CreatedBy = "sam.soc",
            CreatedAt = detected,
        };
        Classify(i, detected.AddHours(1), "iris.isb");
        Log(i, detected, "sam.soc", "incident.created", "");
        Log(i, detected.AddHours(1), "iris.isb", "incident.classified", i.ClassificationRationale);
        Log(i, detected + duration, "sam.soc", "incident.resolved", "");
        Log(i, detected + duration + TimeSpan.FromDays(3), "iris.isb", "incident.closed", "Closed.");
        return i;
    }

    static void Classify(Incident i, DateTimeOffset at, string by)
    {
        var c = Classifier.Classify(i.Assessment);
        i.Class = c.Class;
        i.ClassifiedAt = at;
        i.ClassifiedBy = by;
        i.ClassificationRationale = c.Rationale;
        if (c.IsMajor) i.ClassifiedMajorAt = at;
    }

    static void Log(Incident i, DateTimeOffset at, string actor, string action, string detail) =>
        i.Timeline.Add(new TimelineEntry { At = at, Actor = actor, Action = action, Detail = detail });
}
