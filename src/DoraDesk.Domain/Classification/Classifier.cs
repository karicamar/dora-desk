using System.Globalization;

namespace DoraDesk.Domain.Classification;

/// <summary>
/// Classifies an ICT-related incident as major or not, following Delegated Regulation (EU) 2024/1772:
/// an incident is major when it affected critical services (Art. 6) and either a malicious unauthorised access that
/// may cause data losses was identified, or the materiality thresholds of at least two other criteria are met (Art. 8, 9).
/// The result explains every criterion, so the decision can be reviewed and defended in an audit.
/// </summary>
public static class Classifier
{
    public const decimal EconomicThresholdEur = 100_000m;
    public const long ClientsAbsoluteThreshold = 100_000;
    public static readonly TimeSpan DurationThreshold = TimeSpan.FromHours(24);
    public static readonly TimeSpan DowntimeThreshold = TimeSpan.FromHours(2);

    public static ClassificationResult Classify(ImpactAssessment a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var critical = CriticalServices(a);
        var others = new[] { Clients(a), Reputation(a), Duration(a), Geography(a), DataLosses(a), Economic(a) };
        var criteria = new[] { critical }.Concat(others).ToList();
        var othersMet = others.Count(c => c.Met);

        if (!critical.Met)
            return new(IncidentClass.NotMajor, criteria,
                "Not major: no critical service was affected, which Art. 8(1) requires for every major incident."
                + (othersMet > 0 ? $" {othersMet} other threshold(s) met: keep monitoring and check for recurrence (Art. 16)." : ""));

        if (a.MaliciousAccessMayCauseDataLoss)
            return new(IncidentClass.Major, criteria,
                "Major: critical services affected and a malicious unauthorised access that may result in data losses (Art. 8(1)(a)).");

        return othersMet >= 2
            ? new(IncidentClass.Major, criteria,
                $"Major: critical services affected and {othersMet} further materiality thresholds met (Art. 8(1)(b)): "
                + string.Join(", ", others.Where(c => c.Met).Select(c => Name(c.Criterion))) + ".")
            : new(IncidentClass.NotMajor, criteria,
                $"Not major: critical services affected, but only {othersMet} further threshold met (two are required, Art. 8(1)(b)).");
    }

    public static string Name(Criterion c) => c switch
    {
        Criterion.CriticalServices => "critical services affected",
        Criterion.ClientsCounterpartsTransactions => "clients, counterparts and transactions",
        Criterion.Reputational => "reputational impact",
        Criterion.DurationAndDowntime => "duration and service downtime",
        Criterion.GeographicalSpread => "geographical spread",
        Criterion.DataLosses => "data losses",
        Criterion.EconomicImpact => "economic impact",
        _ => c.ToString(),
    };

    static CriterionResult CriticalServices(ImpactAssessment a)
    {
        var reasons = new List<string>();
        if (a.SupportsCriticalOrImportantFunction) reasons.Add("ICT services supporting a critical or important function");
        if (a.AuthorisedFinancialServiceAffected) reasons.Add("a financial service that requires authorisation");
        if (a.MaliciousAccessMayCauseDataLoss) reasons.Add("successful malicious unauthorised access to network and information systems");
        return new(Criterion.CriticalServices, reasons.Count > 0,
            reasons.Count > 0 ? "Affected: " + string.Join("; ", reasons) + "." : "No critical or important function or authorised service affected.",
            "Art. 6");
    }

    static CriterionResult Clients(ImpactAssessment a)
    {
        var hits = new List<string>();
        if (Above(a.ClientsAffected, a.ClientsUsingService, 0.10m))
            hits.Add($"{Pct(a.ClientsAffected, a.ClientsUsingService)} of clients of the service (> 10 %)");
        if (a.ClientsAffected > ClientsAbsoluteThreshold)
            hits.Add($"{a.ClientsAffected.Value.ToString("N0", CultureInfo.InvariantCulture)} clients (> 100,000)");
        if (Above(a.CounterpartsAffected, a.CounterpartsOfService, 0.30m))
            hits.Add($"{Pct(a.CounterpartsAffected, a.CounterpartsOfService)} of financial counterparts (> 30 %)");
        if (Above(a.TransactionsAffected, a.DailyAverageTransactions, 0.10m))
            hits.Add($"{Pct(a.TransactionsAffected, a.DailyAverageTransactions)} of the daily average number of transactions (> 10 %)");
        if (Above(a.TransactionValueAffectedEur, a.DailyAverageTransactionValueEur, 0.10m))
            hits.Add($"{Pct(a.TransactionValueAffectedEur, a.DailyAverageTransactionValueEur)} of the daily average transaction value (> 10 %)");
        if (a.RelevantClientsOrCounterpartsAffected)
            hits.Add("clients or counterparts identified as relevant are affected");
        return Result(Criterion.ClientsCounterpartsTransactions, hits,
            "Up to 10 % of clients and transactions, 30 % of counterparts and 100,000 clients.", "Art. 1, Art. 9");
    }

    static CriterionResult Reputation(ImpactAssessment a)
    {
        var hits = new List<string>();
        if (a.Reputation.HasFlag(ReputationalImpact.MediaAttention)) hits.Add("media attention");
        if (a.Reputation.HasFlag(ReputationalImpact.RepeatedComplaints)) hits.Add("repeated complaints from clients or counterparts");
        if (a.Reputation.HasFlag(ReputationalImpact.RegulatoryRequirementsNotMet)) hits.Add("regulatory requirements could not be met");
        if (a.Reputation.HasFlag(ReputationalImpact.LikelyLossOfClients)) hits.Add("likely loss of clients or counterparts");
        return Result(Criterion.Reputational, hits, "No media attention, complaints, regulatory breach or client loss.", "Art. 2, Art. 9");
    }

    static CriterionResult Duration(ImpactAssessment a)
    {
        var hits = new List<string>();
        if (a.Duration > DurationThreshold) hits.Add($"duration {Hours(a.Duration.Value)} (> 24 h)");
        if (a.ServiceDowntime > DowntimeThreshold && a.SupportsCriticalOrImportantFunction)
            hits.Add($"downtime of a service supporting a critical function {Hours(a.ServiceDowntime.Value)} (> 2 h)");
        var below = a.ServiceDowntime > DowntimeThreshold
            ? "Downtime over 2 h counts only for services that support a critical or important function; duration up to 24 h."
            : "Duration up to 24 h and downtime of critical services up to 2 h.";
        return Result(Criterion.DurationAndDowntime, hits, below, "Art. 3, Art. 9");
    }

    static CriterionResult Geography(ImpactAssessment a)
    {
        var states = a.MemberStatesAffected.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().Order().ToList();
        return states.Count >= 2
            ? new(Criterion.GeographicalSpread, true, $"Impact in {states.Count} member states ({string.Join(", ", states)}).", "Art. 4, Art. 9")
            : new(Criterion.GeographicalSpread, false,
                states.Count == 1 ? $"Impact only in {states[0]}." : "No impact recorded in more than one member state.", "Art. 4, Art. 9");
    }

    static CriterionResult DataLosses(ImpactAssessment a)
    {
        var hits = new List<string>();
        if (a.Data != DataImpact.None && a.DataImpactHarmsBusinessOrCompliance)
            hits.Add($"impact on {a.Data.ToString().ToLowerInvariant()} of data, with an adverse effect on business objectives or compliance");
        if (a.MaliciousAccessMayCauseDataLoss)
            hits.Add("malicious unauthorised access that may result in data losses");
        var below = a.Data != DataImpact.None
            ? "Data was affected, but without an adverse effect on business objectives or regulatory requirements."
            : "No impact on availability, authenticity, integrity or confidentiality of data.";
        return Result(Criterion.DataLosses, hits, below, "Art. 5, Art. 9");
    }

    static CriterionResult Economic(ImpactAssessment a) =>
        a.EconomicImpactEur > EconomicThresholdEur
            ? new(Criterion.EconomicImpact, true, $"Costs and losses of {Eur(a.EconomicImpactEur.Value)} (> 100,000 €).", "Art. 7, Art. 9")
            : new(Criterion.EconomicImpact, false,
                a.EconomicImpactEur is { } v ? $"Costs and losses of {Eur(v)}, up to 100,000 €." : "No estimate of costs and losses yet.", "Art. 7, Art. 9");

    static CriterionResult Result(Criterion c, List<string> hits, string below, string reference) =>
        hits.Count > 0 ? new(c, true, Capitalize(string.Join("; ", hits)) + ".", reference) : new(c, false, below, reference);

    /// <summary>Strictly above the share ("higher than"). Unknown or zero baselines never meet a relative threshold.</summary>
    static bool Above(decimal? part, decimal? whole, decimal share) => part is { } p && whole is > 0 && p > whole.Value * share;
    static bool Above(long? part, long? whole, decimal share) => Above((decimal?)part, (decimal?)whole, share);

    static string Pct(decimal? part, decimal? whole) => (part!.Value / whole!.Value * 100).ToString("0.#", CultureInfo.InvariantCulture) + " %";
    static string Pct(long? part, long? whole) => Pct((decimal?)part, (decimal?)whole);
    static string Hours(TimeSpan t) => t.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " h";
    static string Eur(decimal v) => v.ToString("#,0", CultureInfo.InvariantCulture) + " €";
    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
