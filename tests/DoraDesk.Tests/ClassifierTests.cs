using DoraDesk.Domain.Classification;

namespace DoraDesk.Tests;

public class ClassifierTests
{
    static readonly ImpactAssessment Critical = new() { SupportsCriticalOrImportantFunction = true };

    static CriterionResult Get(ImpactAssessment a, Criterion c) => Classifier.Classify(a).Criteria.Single(r => r.Criterion == c);

    [Fact]
    public void Empty_assessment_is_not_major_and_explains_every_criterion()
    {
        var result = Classifier.Classify(new ImpactAssessment());
        Assert.False(result.IsMajor);
        Assert.Equal(7, result.Criteria.Count);
        Assert.All(result.Criteria, c => Assert.False(c.Met));
        Assert.All(result.Criteria, c => Assert.False(string.IsNullOrWhiteSpace(c.Reason)));
        Assert.Contains("Art. 8(1)", result.Rationale);
    }

    [Fact]
    public void Without_critical_services_even_all_other_thresholds_are_not_enough()
    {
        var a = new ImpactAssessment
        {
            ClientsAffected = 500_000,
            Reputation = ReputationalImpact.MediaAttention,
            Duration = TimeSpan.FromHours(30),
            MemberStatesAffected = ["DE", "AT", "FR"],
            EconomicImpactEur = 2_000_000m,
        };
        var result = Classifier.Classify(a);
        Assert.False(result.IsMajor);
        Assert.Equal(5, result.Met.Count());
        Assert.Contains("recurrence", result.Rationale);
    }

    [Fact]
    public void Critical_services_plus_two_thresholds_is_major()
    {
        var result = Classifier.Classify(Critical with { Duration = TimeSpan.FromHours(25), EconomicImpactEur = 150_000m });
        Assert.True(result.IsMajor);
        Assert.Contains("Art. 8(1)(b)", result.Rationale);
        Assert.Contains("duration and service downtime", result.Rationale);
        Assert.Contains("economic impact", result.Rationale);
    }

    [Fact]
    public void Critical_services_plus_one_threshold_is_not_major()
    {
        var result = Classifier.Classify(Critical with { EconomicImpactEur = 150_000m });
        Assert.False(result.IsMajor);
        Assert.Contains("only 1 further threshold", result.Rationale);
    }

    [Fact]
    public void Malicious_access_that_may_cause_data_loss_is_major_on_its_own()
    {
        var result = Classifier.Classify(new ImpactAssessment { MaliciousAccessMayCauseDataLoss = true });
        Assert.True(result.IsMajor);
        Assert.Contains("Art. 8(1)(a)", result.Rationale);
        Assert.True(Get(new ImpactAssessment { MaliciousAccessMayCauseDataLoss = true }, Criterion.CriticalServices).Met);
        Assert.True(Get(new ImpactAssessment { MaliciousAccessMayCauseDataLoss = true }, Criterion.DataLosses).Met);
    }

    [Fact]
    public void An_authorised_financial_service_counts_as_critical()
    {
        Assert.True(Get(new ImpactAssessment { AuthorisedFinancialServiceAffected = true }, Criterion.CriticalServices).Met);
    }

    [Theory]
    [InlineData(1_000, 10_000, false)]   // exactly 10 % is not "higher than 10 %"
    [InlineData(1_001, 10_000, true)]
    [InlineData(50, 0, false)]           // unknown baseline never meets a relative threshold
    public void Clients_relative_threshold_is_strictly_above_ten_percent(long affected, long total, bool met)
    {
        var a = new ImpactAssessment { ClientsAffected = affected, ClientsUsingService = total };
        Assert.Equal(met, Get(a, Criterion.ClientsCounterpartsTransactions).Met);
    }

    [Theory]
    [InlineData(100_000, false)]
    [InlineData(100_001, true)]
    public void Clients_absolute_threshold_is_100000(long affected, bool met)
    {
        // 100,001 of 5 million is only 2 %, but the absolute threshold applies on its own
        var a = new ImpactAssessment { ClientsAffected = affected, ClientsUsingService = 5_000_000 };
        Assert.Equal(met, Get(a, Criterion.ClientsCounterpartsTransactions).Met);
    }

    [Fact]
    public void Counterparts_need_more_than_thirty_percent()
    {
        Assert.False(Get(new ImpactAssessment { CounterpartsAffected = 30, CounterpartsOfService = 100 }, Criterion.ClientsCounterpartsTransactions).Met);
        Assert.True(Get(new ImpactAssessment { CounterpartsAffected = 31, CounterpartsOfService = 100 }, Criterion.ClientsCounterpartsTransactions).Met);
    }

    [Fact]
    public void Transactions_by_number_or_by_value()
    {
        var byNumber = Get(new ImpactAssessment { TransactionsAffected = 12_000, DailyAverageTransactions = 100_000 }, Criterion.ClientsCounterpartsTransactions);
        Assert.True(byNumber.Met);
        Assert.Contains("12 %", byNumber.Reason);

        var byValue = Get(new ImpactAssessment { TransactionValueAffectedEur = 2_500_000m, DailyAverageTransactionValueEur = 20_000_000m }, Criterion.ClientsCounterpartsTransactions);
        Assert.True(byValue.Met);
        Assert.Contains("12.5 %", byValue.Reason);
    }

    [Fact]
    public void Relevant_clients_meet_the_threshold_regardless_of_numbers()
    {
        Assert.True(Get(new ImpactAssessment { RelevantClientsOrCounterpartsAffected = true }, Criterion.ClientsCounterpartsTransactions).Met);
    }

    [Fact]
    public void Any_reputational_impact_meets_the_threshold()
    {
        foreach (var r in new[] { ReputationalImpact.MediaAttention, ReputationalImpact.RepeatedComplaints, ReputationalImpact.RegulatoryRequirementsNotMet, ReputationalImpact.LikelyLossOfClients })
            Assert.True(Get(new ImpactAssessment { Reputation = r }, Criterion.Reputational).Met);
        var both = Get(new ImpactAssessment { Reputation = ReputationalImpact.MediaAttention | ReputationalImpact.RepeatedComplaints }, Criterion.Reputational);
        Assert.Equal("Media attention; repeated complaints from clients or counterparts.", both.Reason);
    }

    [Theory]
    [InlineData(24.0, false)]
    [InlineData(24.1, true)]
    public void Duration_must_exceed_24_hours(double hours, bool met)
    {
        Assert.Equal(met, Get(new ImpactAssessment { Duration = TimeSpan.FromHours(hours) }, Criterion.DurationAndDowntime).Met);
    }

    [Fact]
    public void Downtime_over_two_hours_counts_only_for_critical_functions()
    {
        var notCritical = Get(new ImpactAssessment { ServiceDowntime = TimeSpan.FromHours(3) }, Criterion.DurationAndDowntime);
        Assert.False(notCritical.Met);
        Assert.Contains("only for services that support a critical", notCritical.Reason);

        Assert.True(Get(Critical with { ServiceDowntime = TimeSpan.FromHours(2.5) }, Criterion.DurationAndDowntime).Met);
        Assert.False(Get(Critical with { ServiceDowntime = TimeSpan.FromHours(2) }, Criterion.DurationAndDowntime).Met);
    }

    [Fact]
    public void Geographical_spread_needs_two_distinct_member_states()
    {
        Assert.False(Get(new ImpactAssessment { MemberStatesAffected = ["DE", "de ", "DE"] }, Criterion.GeographicalSpread).Met);
        var two = Get(new ImpactAssessment { MemberStatesAffected = ["fr", "DE"] }, Criterion.GeographicalSpread);
        Assert.True(two.Met);
        Assert.Equal("Impact in 2 member states (DE, FR).", two.Reason);
    }

    [Fact]
    public void Data_impact_counts_only_with_an_adverse_effect()
    {
        var harmless = Get(new ImpactAssessment { Data = DataImpact.Availability }, Criterion.DataLosses);
        Assert.False(harmless.Met);
        Assert.Contains("without an adverse effect", harmless.Reason);

        var harmful = Get(new ImpactAssessment { Data = DataImpact.Integrity | DataImpact.Confidentiality, DataImpactHarmsBusinessOrCompliance = true }, Criterion.DataLosses);
        Assert.True(harmful.Met);
        Assert.Contains("integrity, confidentiality", harmful.Reason);
    }

    [Theory]
    [InlineData(100_000, false)]
    [InlineData(100_000.01, true)]
    public void Economic_impact_must_exceed_100000_euro(double eur, bool met)
    {
        Assert.Equal(met, Get(new ImpactAssessment { EconomicImpactEur = (decimal)eur }, Criterion.EconomicImpact).Met);
    }

    [Fact]
    public void Missing_economic_estimate_is_called_out()
    {
        Assert.Equal("No estimate of costs and losses yet.", Get(new ImpactAssessment(), Criterion.EconomicImpact).Reason);
    }

    [Fact]
    public void Every_criterion_cites_its_article()
    {
        var refs = Classifier.Classify(new ImpactAssessment()).Criteria.ToDictionary(c => c.Criterion, c => c.Reference);
        Assert.Equal("Art. 6", refs[Criterion.CriticalServices]);
        Assert.Equal("Art. 1, Art. 9", refs[Criterion.ClientsCounterpartsTransactions]);
        Assert.Equal("Art. 7, Art. 9", refs[Criterion.EconomicImpact]);
    }

    [Fact]
    public void Reasons_do_not_depend_on_the_server_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var r = Get(new ImpactAssessment { EconomicImpactEur = 1_234_567m }, Criterion.EconomicImpact);
            Assert.Equal("Costs and losses of 1,234,567 € (> 100,000 €).", r.Reason);
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    [Fact]
    public void Realistic_payment_outage_is_major()
    {
        // Online banking and SEPA transfers down for 5 hours on a Monday morning; 18 % of daily transfers delayed
        var a = new ImpactAssessment
        {
            SupportsCriticalOrImportantFunction = true,
            AuthorisedFinancialServiceAffected = true,
            ClientsAffected = 42_000, ClientsUsingService = 310_000,
            TransactionsAffected = 21_600, DailyAverageTransactions = 120_000,
            ServiceDowntime = TimeSpan.FromHours(5), Duration = TimeSpan.FromHours(7),
            Reputation = ReputationalImpact.RepeatedComplaints,
            EconomicImpactEur = 38_000m,
        };
        var result = Classifier.Classify(a);
        Assert.True(result.IsMajor);
        Assert.Equal(
            [Criterion.CriticalServices, Criterion.ClientsCounterpartsTransactions, Criterion.Reputational, Criterion.DurationAndDowntime],
            result.Met.Select(m => m.Criterion));
    }
}
