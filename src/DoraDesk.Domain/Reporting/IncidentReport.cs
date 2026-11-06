using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DoraDesk.Domain.Classification;

namespace DoraDesk.Domain.Reporting;

public enum RootCauseCategory { MaliciousActions, ProcessFailure, SystemFailure, HumanError, ExternalEvent }

public sealed record ReportingEntity(string Name, string Lei, string CompetentAuthority, string MemberState);

/// <summary>Everything known about an incident at the moment a report is prepared.</summary>
public sealed record IncidentFacts
{
    public required string Reference { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public DateTimeOffset? OccurredAt { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
    public DateTimeOffset? ClassifiedMajorAt { get; init; }
    public DateTimeOffset? RecoveredAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public RootCauseCategory? RootCause { get; init; }
    public string RootCauseDescription { get; init; } = "";
    public string MeasuresTaken { get; init; } = "";
    public string LessonsLearned { get; init; } = "";
    public bool ClientsInformed { get; init; }
    public string? ThirdPartyProvider { get; init; }
    public IReadOnlyList<string> Techniques { get; init; } = [];
    public required ImpactAssessment Assessment { get; init; }
}

public sealed record PreparedReport(ReportType Type, string Json, string Sha256, IReadOnlyList<string> Missing)
{
    public bool IsComplete => Missing.Count == 0;
}

/// <summary>
/// Builds the content of an incident report. The field set follows the structure of the reporting templates in
/// Implementing Regulation (EU) 2025/302 (general information, initial, intermediate and final sections), simplified:
/// the real submission goes through the authority's portal in its own format. Each report repeats everything from the
/// earlier ones, as the templates do, and lists the fields that are still missing for its type.
/// </summary>
public static class IncidentReport
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static PreparedReport Prepare(ReportType type, ReportingEntity entity, IncidentFacts f, DateTimeOffset preparedAt)
    {
        var classification = Classifier.Classify(f.Assessment);
        var missing = Missing(type, entity, f, classification);
        var a = f.Assessment;

        var root = new JsonObject
        {
            ["reportType"] = type.ToString().ToLowerInvariant(),
            ["preparedAt"] = Utc(preparedAt),
            ["entity"] = new JsonObject
            {
                ["name"] = entity.Name,
                ["lei"] = entity.Lei,
                ["competentAuthority"] = entity.CompetentAuthority,
                ["memberState"] = entity.MemberState,
            },
            ["incident"] = new JsonObject
            {
                ["referenceCode"] = f.Reference,
                ["title"] = f.Title,
                ["occurredAt"] = f.OccurredAt is { } o ? Utc(o) : null,
                ["detectedAt"] = Utc(f.DetectedAt),
                ["classifiedMajorAt"] = f.ClassifiedMajorAt is { } c ? Utc(c) : null,
                ["description"] = f.Description,
                ["classification"] = new JsonObject
                {
                    ["result"] = classification.IsMajor ? "major" : "not major",
                    ["rationale"] = classification.Rationale,
                    ["criteriaMet"] = new JsonArray(classification.Met.Select(m => (JsonNode)new JsonObject
                    {
                        ["criterion"] = Classifier.Name(m.Criterion),
                        ["reason"] = m.Reason,
                        ["reference"] = "Delegated Regulation (EU) 2024/1772, " + m.Reference,
                    }).ToArray()),
                },
                ["thirdPartyProvider"] = f.ThirdPartyProvider,
            },
        };

        if (type >= ReportType.Intermediate)
            root["intermediate"] = new JsonObject
            {
                ["clientsAffected"] = a.ClientsAffected,
                ["counterpartsAffected"] = a.CounterpartsAffected,
                ["transactionsAffected"] = a.TransactionsAffected,
                ["transactionValueAffectedEur"] = a.TransactionValueAffectedEur,
                ["durationHours"] = a.Duration is { } d ? Math.Round(d.TotalHours, 2) : null,
                ["serviceDowntimeHours"] = a.ServiceDowntime is { } s ? Math.Round(s.TotalHours, 2) : null,
                ["memberStatesAffected"] = new JsonArray(a.MemberStatesAffected.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
                ["dataAffected"] = a.Data == DataImpact.None ? null : a.Data.ToString().ToLowerInvariant(),
                ["clientsInformed"] = f.ClientsInformed,
                ["recoveredAt"] = f.RecoveredAt is { } r ? Utc(r) : null,
                ["attackTechniques"] = new JsonArray(f.Techniques.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
            };

        if (type == ReportType.Final)
            root["final"] = new JsonObject
            {
                ["rootCauseCategory"] = f.RootCause?.ToString(),
                ["rootCause"] = f.RootCauseDescription,
                ["resolvedAt"] = f.ResolvedAt is { } r ? Utc(r) : null,
                ["measuresTaken"] = f.MeasuresTaken,
                ["lessonsLearned"] = f.LessonsLearned,
                ["economicImpactEur"] = a.EconomicImpactEur,
            };

        var json = root.ToJsonString(Options);
        return new(type, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), missing);
    }

    public static IReadOnlyList<string> Missing(ReportType type, ReportingEntity entity, IncidentFacts f, ClassificationResult classification)
    {
        var missing = new List<string>();
        if (!Lei.IsValid(entity.Lei)) missing.Add("valid LEI of the reporting entity");
        if (!classification.IsMajor) missing.Add("classification as major (only major incidents are reported)");
        if (f.ClassifiedMajorAt is null) missing.Add("time of classification");
        if (string.IsNullOrWhiteSpace(f.Description)) missing.Add("description");

        if (type >= ReportType.Intermediate)
        {
            if (f.OccurredAt is null) missing.Add("time the incident occurred");
            if (f.Assessment.Duration is null) missing.Add("duration");
            if (f.Assessment.ClientsAffected is null && f.Assessment.TransactionsAffected is null)
                missing.Add("number of affected clients or transactions");
        }

        if (type == ReportType.Final)
        {
            if (f.RootCause is null) missing.Add("root cause category");
            if (string.IsNullOrWhiteSpace(f.RootCauseDescription)) missing.Add("root cause description");
            if (f.ResolvedAt is null) missing.Add("time of resolution");
            if (string.IsNullOrWhiteSpace(f.MeasuresTaken)) missing.Add("measures taken");
            if (f.Assessment.EconomicImpactEur is null) missing.Add("costs and losses");
        }
        return missing;
    }

    static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
