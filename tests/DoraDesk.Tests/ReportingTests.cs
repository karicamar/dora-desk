using System.Text.Json;
using DoraDesk.Domain.Classification;
using DoraDesk.Domain.Reporting;

namespace DoraDesk.Tests;

public class DeadlineTests
{
    static readonly DateTimeOffset Detected = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Initial_notification_is_due_four_hours_after_classification()
    {
        Assert.Equal(Detected.AddHours(6), Deadlines.InitialDue(Detected, Detected.AddHours(2)));
    }

    [Fact]
    public void Initial_notification_is_never_later_than_24_hours_after_detection()
    {
        // classified late in the evening of the next day: the 24-hour cap applies
        Assert.Equal(Detected.AddHours(24), Deadlines.InitialDue(Detected, Detected.AddHours(22)));
        // classified after the cap: already overdue at the moment of classification
        var due = Deadlines.InitialDue(Detected, Detected.AddHours(30));
        Assert.True(new DeadlineInfo(ReportType.Initial, due, null).IsOverdue(Detected.AddHours(30)));
    }

    [Fact]
    public void Classification_before_detection_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => Deadlines.InitialDue(Detected, Detected.AddMinutes(-1)));
    }

    [Fact]
    public void Each_later_deadline_starts_with_the_previous_submission()
    {
        var classified = Detected.AddHours(1);
        Assert.Single(Deadlines.Timeline(Detected, classified, null, null, null));

        var initialSent = Detected.AddHours(4);
        var t = Deadlines.Timeline(Detected, classified, initialSent, null, null);
        Assert.Equal(2, t.Count);
        Assert.Equal(initialSent.AddHours(72), t[1].Due);
        Assert.Equal(ReportType.Intermediate, Deadlines.Next(t)!.Type);

        var intermediateSent = new DateTimeOffset(2026, 3, 31, 9, 0, 0, TimeSpan.Zero);
        t = Deadlines.Timeline(Detected, classified, initialSent, intermediateSent, null);
        Assert.Equal(new DateTimeOffset(2026, 4, 30, 9, 0, 0, TimeSpan.Zero), t[2].Due); // one calendar month, clamped to April 30
        Assert.True(t[1].SubmittedLate);

        t = Deadlines.Timeline(Detected, classified, initialSent, intermediateSent, intermediateSent.AddDays(20));
        Assert.Null(Deadlines.Next(t));
    }

    [Fact]
    public void Submitted_reports_are_never_overdue()
    {
        var d = new DeadlineInfo(ReportType.Initial, Detected, Detected.AddHours(1));
        Assert.False(d.IsOverdue(Detected.AddDays(5)));
        Assert.True(d.SubmittedLate);
    }

    [Theory]
    [InlineData(3 * 60 + 12, "due in 3 h 12 min")]
    [InlineData(45, "due in 45 min")]
    [InlineData(-90, "overdue by 1 h 30 min")]
    [InlineData(3 * 24 * 60 + 5 * 60, "due in 3 d 5 h")]
    public void Remaining_time_is_human_readable(int minutes, string expected)
    {
        Assert.Equal(expected, Deadlines.Describe(TimeSpan.FromMinutes(minutes)));
    }
}

public class LeiTests
{
    [Theory]
    [InlineData("7LTWFZYICNSX8D621K86", true)]   // Deutsche Bank AG, from the GLEIF register
    [InlineData("7LTWFZYICNSX8D621K87", false)]  // wrong check digits
    [InlineData("7ltwfzyicnsx8d621k86", false)]  // lower case is not a valid LEI
    [InlineData("7LTWFZYICNSX8D621K8", false)]
    [InlineData("7LTWFZYICNSX8D621KAB", false)]  // check digits must be digits
    [InlineData(null, false)]
    public void Validates_check_digits(string? lei, bool valid) => Assert.Equal(valid, Lei.IsValid(lei));

    [Fact]
    public void Generated_check_digits_validate()
    {
        var lei = Lei.WithCheckDigits("529900DEMOBANK0001");
        Assert.True(Lei.IsValid(lei));
        Assert.StartsWith("529900DEMOBANK0001", lei);
    }
}

public class IncidentReportTests
{
    static readonly ReportingEntity Entity = new("Mainufer Bank AG", Lei.WithCheckDigits("529900MAINUFER0001"), "BaFin", "DE");
    static readonly DateTimeOffset Detected = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    static IncidentFacts Major => new()
    {
        Reference = "INC-2026-0007",
        Title = "Online banking outage",
        Description = "Load balancer certificate expired; online banking and SEPA unavailable.",
        OccurredAt = Detected.AddMinutes(-20),
        DetectedAt = Detected,
        ClassifiedMajorAt = Detected.AddHours(1),
        Assessment = new ImpactAssessment
        {
            SupportsCriticalOrImportantFunction = true,
            ClientsAffected = 120_000,
            Duration = TimeSpan.FromHours(26),
            MemberStatesAffected = ["DE"],
        },
    };

    [Fact]
    public void Initial_report_contains_classification_and_entity()
    {
        var r = IncidentReport.Prepare(ReportType.Initial, Entity, Major, Detected.AddHours(2));
        Assert.True(r.IsComplete, string.Join(", ", r.Missing));
        using var doc = JsonDocument.Parse(r.Json);
        var root = doc.RootElement;
        Assert.Equal("initial", root.GetProperty("reportType").GetString());
        Assert.Equal("BaFin", root.GetProperty("entity").GetProperty("competentAuthority").GetString());
        Assert.Equal("2026-03-02T08:00:00Z", root.GetProperty("incident").GetProperty("detectedAt").GetString());
        var c = root.GetProperty("incident").GetProperty("classification");
        Assert.Equal("major", c.GetProperty("result").GetString());
        Assert.Equal(3, c.GetProperty("criteriaMet").GetArrayLength());
        Assert.False(root.TryGetProperty("intermediate", out _));
        Assert.False(root.TryGetProperty("final", out _));
        Assert.Matches("^[0-9a-f]{64}$", r.Sha256);
    }

    [Fact]
    public void Final_report_lists_what_is_still_missing()
    {
        var r = IncidentReport.Prepare(ReportType.Final, Entity, Major, Detected.AddDays(20));
        Assert.False(r.IsComplete);
        Assert.Equal(["root cause category", "root cause description", "time of resolution", "measures taken", "costs and losses"], r.Missing);

        var complete = Major with
        {
            RootCause = RootCauseCategory.ProcessFailure,
            RootCauseDescription = "Certificate renewal depended on one person's calendar reminder.",
            ResolvedAt = Detected.AddHours(27),
            MeasuresTaken = "Automated renewal with monitoring 30 days before expiry.",
            Assessment = Major.Assessment with { EconomicImpactEur = 85_000m },
        };
        r = IncidentReport.Prepare(ReportType.Final, Entity, complete, Detected.AddDays(20));
        Assert.True(r.IsComplete, string.Join(", ", r.Missing));
        using var doc = JsonDocument.Parse(r.Json);
        Assert.Equal("ProcessFailure", doc.RootElement.GetProperty("final").GetProperty("rootCauseCategory").GetString());
        Assert.Equal(26, doc.RootElement.GetProperty("intermediate").GetProperty("durationHours").GetDouble());
    }

    [Fact]
    public void Non_major_incident_or_invalid_lei_cannot_be_reported()
    {
        var minor = Major with { Assessment = new ImpactAssessment() };
        var r = IncidentReport.Prepare(ReportType.Initial, Entity with { Lei = "529900MAINUFER000100" }, minor, Detected);
        Assert.Contains("valid LEI of the reporting entity", r.Missing);
        Assert.Contains("classification as major (only major incidents are reported)", r.Missing);
    }

    [Fact]
    public void Same_facts_give_the_same_hash()
    {
        var a = IncidentReport.Prepare(ReportType.Initial, Entity, Major, Detected.AddHours(2));
        var b = IncidentReport.Prepare(ReportType.Initial, Entity, Major, Detected.AddHours(2));
        var c = IncidentReport.Prepare(ReportType.Initial, Entity, Major with { Title = "Changed" }, Detected.AddHours(2));
        Assert.Equal(a.Sha256, b.Sha256);
        Assert.NotEqual(a.Sha256, c.Sha256);
    }

    [Fact]
    public void Umlauts_stay_readable_and_markup_is_just_text()
    {
        var r = IncidentReport.Prepare(ReportType.Initial, Entity, Major with { Title = "Störung <script>" }, Detected);
        Assert.Contains("Störung", r.Json);
        using var doc = JsonDocument.Parse(r.Json);
        Assert.Equal("Störung <script>", doc.RootElement.GetProperty("incident").GetProperty("title").GetString());
    }
}

public class RecurrenceTests
{
    static readonly DateTimeOffset Now = new(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);

    static PastIncident Outage(string reference, int daysAgo, string cause = "Expired TLS certificate", bool major = false) =>
        new(reference, Now.AddDays(-daysAgo), cause, major, new ImpactAssessment
        {
            SupportsCriticalOrImportantFunction = true,
            Duration = TimeSpan.FromHours(14),
            EconomicImpactEur = 60_000m,
        });

    [Fact]
    public void Two_minor_incidents_with_the_same_root_cause_add_up_to_a_major_one()
    {
        var groups = Recurrence.Analyze([Outage("INC-1", 100), Outage("INC-2", 10, "expired  tls certificate ")], Now);
        var g = Assert.Single(groups);
        Assert.Equal(["INC-1", "INC-2"], g.References);
        Assert.Equal(TimeSpan.FromHours(28), g.Combined.Duration);
        Assert.Equal(120_000m, g.Combined.EconomicImpactEur);
        Assert.True(g.Classification.IsMajor);
    }

    [Fact]
    public void Incidents_older_than_six_months_do_not_count()
    {
        Assert.Empty(Recurrence.Analyze([Outage("INC-1", 200), Outage("INC-2", 10)], Now));
    }

    [Fact]
    public void Different_root_causes_or_already_major_incidents_do_not_count()
    {
        Assert.Empty(Recurrence.Analyze([Outage("INC-1", 30), Outage("INC-2", 10, "Database failover")], Now));
        Assert.Empty(Recurrence.Analyze([Outage("INC-1", 30, major: true), Outage("INC-2", 10)], Now));
    }

    [Fact]
    public void Recurring_incidents_that_stay_below_the_thresholds_are_not_reported()
    {
        var small = new PastIncident("INC-3", Now.AddDays(-5), "Expired TLS certificate", false,
            new ImpactAssessment { SupportsCriticalOrImportantFunction = true, Duration = TimeSpan.FromHours(1) });
        var small2 = small with { Reference = "INC-4", OccurredAt = Now.AddDays(-2) };
        Assert.Empty(Recurrence.Analyze([small, small2], Now));
    }

    [Fact]
    public void Combine_keeps_unknown_values_unknown_and_unites_member_states()
    {
        var c = Recurrence.Combine([
            new ImpactAssessment { MemberStatesAffected = ["DE"], ClientsUsingService = 1000, Reputation = ReputationalImpact.MediaAttention },
            new ImpactAssessment { MemberStatesAffected = ["at", "DE"], ClientsUsingService = 1200, ClientsAffected = 50 },
        ]);
        Assert.Null(c.EconomicImpactEur);
        Assert.Null(c.Duration);
        Assert.Equal(50, c.ClientsAffected);
        Assert.Equal(1200, c.ClientsUsingService);
        Assert.Equal(["AT", "DE"], c.MemberStatesAffected);
        Assert.Equal(ReputationalImpact.MediaAttention, c.Reputation);
    }
}
