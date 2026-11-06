using System.Text.Json;

namespace DoraDesk.Domain.Integration;

public sealed record ImportedAlert(
    string RuleId,
    string Title,
    string Severity,
    string TechniqueId,
    string TechniqueName,
    string Tactic,
    string Description,
    string? SourceIp,
    string? User,
    string? Host,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int EventCount);

/// <summary>A proposal for a new incident, built from detections. A person decides; nothing is classified automatically.</summary>
public sealed record IncidentDraft(
    string Title,
    string Description,
    DateTimeOffset OccurredAt,
    DateTimeOffset DetectedAt,
    IReadOnlyList<string> Techniques,
    IReadOnlyList<string> Hints,
    IReadOnlyList<ImportedAlert> Alerts);

public sealed class ImportException(string message) : Exception(message);

/// <summary>
/// Reads the alert JSON of LogSentinel (<c>logsentinel analyze ... --format json</c>, or <c>/api/alerts</c> of its web
/// dashboard) and turns it into an incident draft: when the attack started, what was seen, and which DORA criteria the
/// detected techniques point to.
/// </summary>
public static class LogSentinelImport
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxAlerts = 5_000;

    static readonly string[] Severities = ["Low", "Medium", "High", "Critical"];

    public static IReadOnlyList<ImportedAlert> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ImportException("The file is empty.");
        if (json.Length > MaxBytes) throw new ImportException("The file is larger than 5 MB.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException e) { throw new ImportException($"Not valid JSON: {e.Message}"); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new ImportException("Expected a JSON array of LogSentinel alerts.");
            if (doc.RootElement.GetArrayLength() > MaxAlerts)
                throw new ImportException($"More than {MaxAlerts} alerts in one file.");

            var alerts = new List<ImportedAlert>();
            var index = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                index++;
                if (e.ValueKind != JsonValueKind.Object) throw new ImportException($"Alert {index} is not an object.");
                var technique = e.TryGetProperty("technique", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
                var severity = Required(e, "severity", index);
                if (!Severities.Contains(severity, StringComparer.OrdinalIgnoreCase))
                    throw new ImportException($"Alert {index}: unknown severity \"{severity}\".");
                alerts.Add(new ImportedAlert(
                    Required(e, "ruleId", index),
                    Required(e, "title", index),
                    Severities.First(s => s.Equals(severity, StringComparison.OrdinalIgnoreCase)),
                    technique.ValueKind == JsonValueKind.Object ? Optional(technique, "id") ?? "" : "",
                    technique.ValueKind == JsonValueKind.Object ? Optional(technique, "name") ?? "" : "",
                    technique.ValueKind == JsonValueKind.Object ? Optional(technique, "tactic") ?? "" : "",
                    Optional(e, "description") ?? "",
                    Optional(e, "sourceIp"),
                    Optional(e, "user"),
                    Optional(e, "host"),
                    Time(e, "firstSeen", index),
                    Time(e, "lastSeen", index),
                    e.TryGetProperty("eventCount", out var c) && c.TryGetInt32(out var n) ? n : 1));
            }
            return alerts;
        }
    }

    public static IncidentDraft Draft(IReadOnlyList<ImportedAlert> alerts, DateTimeOffset importedAt)
    {
        if (alerts.Count == 0) throw new ImportException("The file contains no alerts.");
        var ordered = alerts.OrderBy(a => a.FirstSeen).ToList();
        var worst = alerts.OrderByDescending(a => Array.IndexOf(Severities, a.Severity)).ThenBy(a => a.FirstSeen).First();
        var techniques = alerts.Where(a => a.TechniqueId.Length > 0)
            .Select(a => $"{a.TechniqueId} {a.TechniqueName}".Trim()).Distinct().Order(StringComparer.Ordinal).ToList();
        var users = alerts.Select(a => a.User).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
        var hosts = alerts.Select(a => a.Host).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();

        var description = $"Imported from LogSentinel: {alerts.Count} alert(s) between {ordered[0].FirstSeen.UtcDateTime:yyyy-MM-dd HH:mm} and "
            + $"{alerts.Max(a => a.LastSeen).UtcDateTime:yyyy-MM-dd HH:mm} UTC. Most severe: [{worst.Severity}] {worst.Title}."
            + (users.Count > 0 ? $" Accounts involved: {string.Join(", ", users)}." : "")
            + (hosts.Count > 0 ? $" Hosts: {string.Join(", ", hosts)}." : "");

        // The incident occurred with the first detected activity. "Detected" (aware) is when a person reviews the alerts,
        // here the import; the form shows both so the responder can correct them before saving.
        var detectedAt = importedAt < ordered[0].FirstSeen ? ordered[0].FirstSeen : importedAt;
        return new IncidentDraft(Shorten($"{worst.Title}", 120), description, ordered[0].FirstSeen, detectedAt, techniques, Hints(alerts), ordered);
    }

    static IReadOnlyList<string> Hints(IReadOnlyList<ImportedAlert> alerts)
    {
        var ids = alerts.Select(a => a.TechniqueId).ToHashSet(StringComparer.Ordinal);
        var tactics = alerts.Select(a => a.Tactic).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hints = new List<string>();
        if (ids.Contains("T1078") || ids.Contains("T1550.001") || ids.Contains("T1098") || ids.Contains("T1190"))
            hints.Add("Signs of successful unauthorised access (valid accounts, stolen tokens or an exploited application): "
                      + "check whether data may have been lost. If so, the incident is major as soon as a critical service is affected (Art. 8(1)(a)).");
        if (ids.Contains("T1657"))
            hints.Add("Financial theft was attempted: count the affected transactions and their value against the daily average, and estimate the losses.");
        if (ids.Contains("T1566"))
            hints.Add("Phishing: find out who opened the email or entered credentials, and whether clients received it too (reputational impact).");
        if (ids.Contains("T1070.001"))
            hints.Add("Event logs were cleared: preserve the remaining evidence before systems are rebuilt.");
        if (tactics.Contains("Persistence"))
            hints.Add("The attacker created persistence (accounts, services or tasks): the incident is not resolved until these are removed.");
        if (alerts.Any(a => a.Severity == "Critical"))
            hints.Add("At least one critical alert: classify now. The initial notification is due 4 hours after classification, and no later than 24 hours after detection.");
        return hints;
    }

    static string Required(JsonElement e, string name, int index) =>
        Optional(e, name) is { Length: > 0 } v ? v : throw new ImportException($"Alert {index}: \"{name}\" is missing.");

    static string? Optional(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Shorten(v.GetString()!, 2000) : null;

    static DateTimeOffset Time(JsonElement e, string name, int index) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.TryGetDateTimeOffset(out var t)
            ? t : throw new ImportException($"Alert {index}: \"{name}\" is missing or not a timestamp.");

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..max];
}
