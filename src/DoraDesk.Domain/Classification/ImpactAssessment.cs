namespace DoraDesk.Domain.Classification;

/// <summary>
/// What is known about an ICT-related incident's impact, in the terms of the classification criteria of
/// Commission Delegated Regulation (EU) 2024/1772 (Articles 1 to 7). Every field is optional: classification
/// happens early, with estimates, and is repeated as the picture gets clearer.
/// </summary>
public sealed record ImpactAssessment
{
    // Art. 1: clients, financial counterparts and transactions
    public long? ClientsAffected { get; init; }
    public long? ClientsUsingService { get; init; }
    public long? CounterpartsAffected { get; init; }
    public long? CounterpartsOfService { get; init; }
    public long? TransactionsAffected { get; init; }
    public long? DailyAverageTransactions { get; init; }
    public decimal? TransactionValueAffectedEur { get; init; }
    public decimal? DailyAverageTransactionValueEur { get; init; }
    public bool RelevantClientsOrCounterpartsAffected { get; init; }

    // Art. 2: reputational impact
    public ReputationalImpact Reputation { get; init; }

    // Art. 3: duration and service downtime
    public TimeSpan? Duration { get; init; }
    public TimeSpan? ServiceDowntime { get; init; }

    // Art. 4: geographical spread (ISO 3166 codes of EU/EEA member states with an impact)
    public IReadOnlyList<string> MemberStatesAffected { get; init; } = [];

    // Art. 5: data losses
    public DataImpact Data { get; init; }
    public bool DataImpactHarmsBusinessOrCompliance { get; init; }
    public bool MaliciousAccessMayCauseDataLoss { get; init; }

    // Art. 6: critical services affected
    public bool SupportsCriticalOrImportantFunction { get; init; }
    public bool AuthorisedFinancialServiceAffected { get; init; }

    // Art. 7: economic impact (direct and indirect costs and losses, estimate)
    public decimal? EconomicImpactEur { get; init; }
}

[Flags]
public enum ReputationalImpact
{
    None = 0,
    MediaAttention = 1,
    RepeatedComplaints = 2,
    RegulatoryRequirementsNotMet = 4,
    LikelyLossOfClients = 8,
}

[Flags]
public enum DataImpact
{
    None = 0,
    Availability = 1,
    Authenticity = 2,
    Integrity = 4,
    Confidentiality = 8,
}

public enum Criterion
{
    CriticalServices,
    ClientsCounterpartsTransactions,
    Reputational,
    DurationAndDowntime,
    GeographicalSpread,
    DataLosses,
    EconomicImpact,
}

public sealed record CriterionResult(Criterion Criterion, bool Met, string Reason, string Reference);

public enum IncidentClass { NotMajor, Major }

public sealed record ClassificationResult(IncidentClass Class, IReadOnlyList<CriterionResult> Criteria, string Rationale)
{
    public bool IsMajor => Class == IncidentClass.Major;
    public IEnumerable<CriterionResult> Met => Criteria.Where(c => c.Met);
}
