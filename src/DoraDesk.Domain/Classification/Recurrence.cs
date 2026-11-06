namespace DoraDesk.Domain.Classification;

/// <summary>An incident that was not classified as major on its own, as input for the recurrence check.</summary>
public sealed record PastIncident(string Reference, DateTimeOffset OccurredAt, string RootCauseKey, bool WasMajor, ImpactAssessment Assessment);

public sealed record RecurringIncidentGroup(
    string RootCauseKey,
    IReadOnlyList<string> References,
    ImpactAssessment Combined,
    ClassificationResult Classification);

/// <summary>
/// Recurring incidents (Art. 16 of Delegated Regulation 2024/1772): incidents that are not major on their own but
/// occurred at least twice within six months with the same apparent root cause are treated as one major incident
/// when, taken together, they meet the classification criteria.
/// </summary>
/// <remarks>
/// The regulation does not define how impacts add up. This implementation is deliberately conservative (it errs
/// towards reporting): counts, amounts, durations and costs are summed, flags are combined, member states are
/// united, and the largest known baseline (clients of the service, daily average) is used for the relative thresholds.
/// </remarks>
public static class Recurrence
{
    public static IReadOnlyList<RecurringIncidentGroup> Analyze(IEnumerable<PastIncident> incidents, DateTimeOffset now) =>
        incidents
            .Where(i => !i.WasMajor && !string.IsNullOrWhiteSpace(i.RootCauseKey))
            .Where(i => i.OccurredAt <= now && i.OccurredAt > now.AddMonths(-6))
            .GroupBy(i => Normalize(i.RootCauseKey))
            .Where(g => g.Count() >= 2)
            .Select(g =>
            {
                var list = g.OrderBy(i => i.OccurredAt).ToList();
                var combined = Combine(list.Select(i => i.Assessment));
                return new RecurringIncidentGroup(list[0].RootCauseKey.Trim(), list.Select(i => i.Reference).ToList(), combined, Classifier.Classify(combined));
            })
            .Where(r => r.Classification.IsMajor)
            .OrderBy(r => r.RootCauseKey, StringComparer.Ordinal)
            .ToList();

    public static ImpactAssessment Combine(IEnumerable<ImpactAssessment> assessments)
    {
        var all = assessments.ToList();
        if (all.Count == 0) throw new ArgumentException("Nothing to combine.", nameof(assessments));
        return new ImpactAssessment
        {
            ClientsAffected = Sum(all, a => a.ClientsAffected),
            ClientsUsingService = Max(all, a => a.ClientsUsingService),
            CounterpartsAffected = Sum(all, a => a.CounterpartsAffected),
            CounterpartsOfService = Max(all, a => a.CounterpartsOfService),
            TransactionsAffected = Sum(all, a => a.TransactionsAffected),
            DailyAverageTransactions = Max(all, a => a.DailyAverageTransactions),
            TransactionValueAffectedEur = SumD(all, a => a.TransactionValueAffectedEur),
            DailyAverageTransactionValueEur = all.Max(a => a.DailyAverageTransactionValueEur),
            RelevantClientsOrCounterpartsAffected = all.Any(a => a.RelevantClientsOrCounterpartsAffected),
            Reputation = all.Aggregate(ReputationalImpact.None, (r, a) => r | a.Reputation),
            Duration = all.Any(a => a.Duration.HasValue) ? all.Aggregate(TimeSpan.Zero, (t, a) => t + (a.Duration ?? TimeSpan.Zero)) : null,
            ServiceDowntime = all.Any(a => a.ServiceDowntime.HasValue) ? all.Aggregate(TimeSpan.Zero, (t, a) => t + (a.ServiceDowntime ?? TimeSpan.Zero)) : null,
            MemberStatesAffected = all.SelectMany(a => a.MemberStatesAffected).Select(s => s.Trim().ToUpperInvariant()).Distinct().Order().ToList(),
            Data = all.Aggregate(DataImpact.None, (d, a) => d | a.Data),
            DataImpactHarmsBusinessOrCompliance = all.Any(a => a.DataImpactHarmsBusinessOrCompliance),
            MaliciousAccessMayCauseDataLoss = all.Any(a => a.MaliciousAccessMayCauseDataLoss),
            SupportsCriticalOrImportantFunction = all.Any(a => a.SupportsCriticalOrImportantFunction),
            AuthorisedFinancialServiceAffected = all.Any(a => a.AuthorisedFinancialServiceAffected),
            EconomicImpactEur = SumD(all, a => a.EconomicImpactEur),
        };
    }

    /// <summary>"Expired TLS certificate" and "expired  tls certificate " are the same root cause.</summary>
    public static string Normalize(string key) => string.Join(' ', key.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    static long? Sum(List<ImpactAssessment> all, Func<ImpactAssessment, long?> f) => all.Any(a => f(a).HasValue) ? all.Sum(a => f(a) ?? 0) : null;
    static decimal? SumD(List<ImpactAssessment> all, Func<ImpactAssessment, decimal?> f) => all.Any(a => f(a).HasValue) ? all.Sum(a => f(a) ?? 0) : null;
    static long? Max(List<ImpactAssessment> all, Func<ImpactAssessment, long?> f) => all.Max(f);
}
