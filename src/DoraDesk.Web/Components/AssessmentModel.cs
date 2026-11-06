using DoraDesk.Domain.Classification;

namespace DoraDesk.Web.Components;

/// <summary>Mutable form model for the impact assessment (the domain record is immutable).</summary>
public sealed class AssessmentModel
{
    public long? ClientsAffected { get; set; }
    public long? ClientsUsingService { get; set; }
    public long? CounterpartsAffected { get; set; }
    public long? CounterpartsOfService { get; set; }
    public long? TransactionsAffected { get; set; }
    public long? DailyAverageTransactions { get; set; }
    public decimal? TransactionValueAffectedEur { get; set; }
    public decimal? DailyAverageTransactionValueEur { get; set; }
    public bool RelevantClientsOrCounterpartsAffected { get; set; }

    public bool MediaAttention { get; set; }
    public bool RepeatedComplaints { get; set; }
    public bool RegulatoryRequirementsNotMet { get; set; }
    public bool LikelyLossOfClients { get; set; }

    public double? DurationHours { get; set; }
    public double? DowntimeHours { get; set; }
    public string MemberStates { get; set; } = "";

    public bool Availability { get; set; }
    public bool Authenticity { get; set; }
    public bool Integrity { get; set; }
    public bool Confidentiality { get; set; }
    public bool DataImpactHarmsBusinessOrCompliance { get; set; }
    public bool MaliciousAccessMayCauseDataLoss { get; set; }

    public bool SupportsCriticalOrImportantFunction { get; set; }
    public bool AuthorisedFinancialServiceAffected { get; set; }
    public decimal? EconomicImpactEur { get; set; }

    public ImpactAssessment ToAssessment() => new()
    {
        ClientsAffected = ClientsAffected,
        ClientsUsingService = ClientsUsingService,
        CounterpartsAffected = CounterpartsAffected,
        CounterpartsOfService = CounterpartsOfService,
        TransactionsAffected = TransactionsAffected,
        DailyAverageTransactions = DailyAverageTransactions,
        TransactionValueAffectedEur = TransactionValueAffectedEur,
        DailyAverageTransactionValueEur = DailyAverageTransactionValueEur,
        RelevantClientsOrCounterpartsAffected = RelevantClientsOrCounterpartsAffected,
        Reputation = (MediaAttention ? ReputationalImpact.MediaAttention : 0)
                     | (RepeatedComplaints ? ReputationalImpact.RepeatedComplaints : 0)
                     | (RegulatoryRequirementsNotMet ? ReputationalImpact.RegulatoryRequirementsNotMet : 0)
                     | (LikelyLossOfClients ? ReputationalImpact.LikelyLossOfClients : 0),
        Duration = DurationHours is { } d && double.IsFinite(d) ? TimeSpan.FromHours(Math.Clamp(d, -1e6, 1e6)) : null,
        ServiceDowntime = DowntimeHours is { } t && double.IsFinite(t) ? TimeSpan.FromHours(Math.Clamp(t, -1e6, 1e6)) : null,
        MemberStatesAffected = MemberStates.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.ToUpperInvariant()).Distinct().ToList(),
        Data = (Availability ? DataImpact.Availability : 0) | (Authenticity ? DataImpact.Authenticity : 0)
               | (Integrity ? DataImpact.Integrity : 0) | (Confidentiality ? DataImpact.Confidentiality : 0),
        DataImpactHarmsBusinessOrCompliance = DataImpactHarmsBusinessOrCompliance,
        MaliciousAccessMayCauseDataLoss = MaliciousAccessMayCauseDataLoss,
        SupportsCriticalOrImportantFunction = SupportsCriticalOrImportantFunction,
        AuthorisedFinancialServiceAffected = AuthorisedFinancialServiceAffected,
        EconomicImpactEur = EconomicImpactEur,
    };

    public static AssessmentModel From(ImpactAssessment a) => new()
    {
        ClientsAffected = a.ClientsAffected,
        ClientsUsingService = a.ClientsUsingService,
        CounterpartsAffected = a.CounterpartsAffected,
        CounterpartsOfService = a.CounterpartsOfService,
        TransactionsAffected = a.TransactionsAffected,
        DailyAverageTransactions = a.DailyAverageTransactions,
        TransactionValueAffectedEur = a.TransactionValueAffectedEur,
        DailyAverageTransactionValueEur = a.DailyAverageTransactionValueEur,
        RelevantClientsOrCounterpartsAffected = a.RelevantClientsOrCounterpartsAffected,
        MediaAttention = a.Reputation.HasFlag(ReputationalImpact.MediaAttention),
        RepeatedComplaints = a.Reputation.HasFlag(ReputationalImpact.RepeatedComplaints),
        RegulatoryRequirementsNotMet = a.Reputation.HasFlag(ReputationalImpact.RegulatoryRequirementsNotMet),
        LikelyLossOfClients = a.Reputation.HasFlag(ReputationalImpact.LikelyLossOfClients),
        DurationHours = a.Duration?.TotalHours,
        DowntimeHours = a.ServiceDowntime?.TotalHours,
        MemberStates = string.Join(", ", a.MemberStatesAffected),
        Availability = a.Data.HasFlag(DataImpact.Availability),
        Authenticity = a.Data.HasFlag(DataImpact.Authenticity),
        Integrity = a.Data.HasFlag(DataImpact.Integrity),
        Confidentiality = a.Data.HasFlag(DataImpact.Confidentiality),
        DataImpactHarmsBusinessOrCompliance = a.DataImpactHarmsBusinessOrCompliance,
        MaliciousAccessMayCauseDataLoss = a.MaliciousAccessMayCauseDataLoss,
        SupportsCriticalOrImportantFunction = a.SupportsCriticalOrImportantFunction,
        AuthorisedFinancialServiceAffected = a.AuthorisedFinancialServiceAffected,
        EconomicImpactEur = a.EconomicImpactEur,
    };
}
