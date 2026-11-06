using DoraDesk.Domain.Classification;
using DoraDesk.Domain.Integration;
using DoraDesk.Domain.Reporting;
using DoraDesk.Web.Data;
using DoraDesk.Web.Services;
using Microsoft.EntityFrameworkCore;
using static DoraDesk.Tests.DeskApp;

namespace DoraDesk.Tests;

public class WorkflowTests : IAsyncLifetime
{
    readonly DeskApp app = new();
    IncidentService svc = default!;

    static readonly ImpactAssessment MajorImpact = new()
    {
        SupportsCriticalOrImportantFunction = true,
        ClientsAffected = 150_000,
        Duration = TimeSpan.FromHours(30),
    };

    public Task InitializeAsync()
    {
        svc = app.Incidents;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await app.DisposeAsync();

    DateTimeOffset Now => app.Time.GetUtcNow();

    async Task<int> NewIncident(string title = "Online banking down") =>
        await svc.CreateAsync(Sam, new NewIncident(title, "Login page returns 503.", Now.AddMinutes(-30), Now, null));

    async Task<Incident> Get(int id) => await svc.GetAsync(id);

    async Task<int> MajorIncident()
    {
        var id = await NewIncident();
        await svc.SaveAssessmentAsync(Sam, id, (await Get(id)).Version, MajorImpact);
        await svc.ClassifyAsync(Sam, id, (await Get(id)).Version);
        return id;
    }

    static async Task<DeskException> Fails(DeskError error, Func<Task> action)
    {
        var e = await Assert.ThrowsAsync<DeskException>(action);
        Assert.Equal(error, e.Error);
        return e;
    }

    [Fact]
    public async Task References_are_numbered_per_year()
    {
        var a = await NewIncident();
        var b = await NewIncident();
        Assert.Equal("INC-2026-0001", (await Get(a)).Reference);
        Assert.Equal("INC-2026-0002", (await Get(b)).Reference);
        Assert.Equal("incident.created", Assert.Single((await Get(a)).Timeline).Action);
    }

    [Fact]
    public async Task Auditor_can_read_but_not_change()
    {
        await Fails(DeskError.Forbidden, () => svc.CreateAsync(Ana, new NewIncident("x", "", null, Now, null)));
        var id = await NewIncident();
        await Fails(DeskError.Forbidden, () => svc.SaveAssessmentAsync(Ana, id, 0, MajorImpact));
        await Fails(DeskError.Forbidden, () => svc.ClassifyAsync(Ana, id, 0));
        Assert.NotNull(await Get(id));
    }

    [Fact]
    public async Task Implausible_times_are_rejected()
    {
        await Fails(DeskError.Invalid, () => svc.CreateAsync(Sam, new NewIncident("x", "", null, Now.AddHours(1), null)));
        await Fails(DeskError.Invalid, () => svc.CreateAsync(Sam, new NewIncident("x", "", Now, Now.AddMinutes(-1), null)));
        await Fails(DeskError.Invalid, () => svc.CreateAsync(Sam, new NewIncident("   ", "", null, Now, null)));
    }

    [Fact]
    public async Task Invalid_assessment_values_are_rejected()
    {
        var id = await NewIncident();
        await Fails(DeskError.Invalid, () => svc.SaveAssessmentAsync(Sam, id, 0, new ImpactAssessment { EconomicImpactEur = -1 }));
        await Fails(DeskError.Invalid, () => svc.SaveAssessmentAsync(Sam, id, 0, new ImpactAssessment { Duration = TimeSpan.FromHours(1), ServiceDowntime = TimeSpan.FromHours(2) }));
        await Fails(DeskError.Invalid, () => svc.SaveAssessmentAsync(Sam, id, 0, new ImpactAssessment { ClientsAffected = 11, ClientsUsingService = 10 }));
    }

    [Fact]
    public async Task Saving_an_assessment_does_not_classify()
    {
        var id = await NewIncident();
        await svc.SaveAssessmentAsync(Sam, id, 0, MajorImpact with { MemberStatesAffected = ["de", " at", "DE", "Germany"] });
        var i = await Get(id);
        Assert.Null(i.Class);
        Assert.Null(i.ClassifiedMajorAt);
        Assert.Equal(["AT", "DE"], i.Assessment.MemberStatesAffected);
        Assert.Equal("assessment.saved", i.Timeline.Last().Action);
    }

    [Fact]
    public async Task First_major_classification_starts_the_clock_and_a_downgrade_does_not_stop_it()
    {
        var id = await NewIncident();
        await svc.SaveAssessmentAsync(Sam, id, 0, MajorImpact);
        app.Time.Advance(TimeSpan.FromHours(1));
        var result = await svc.ClassifyAsync(Iris, id, 1);
        Assert.True(result.IsMajor);
        var i = await Get(id);
        var classifiedAt = Now;
        Assert.Equal(classifiedAt, i.ClassifiedMajorAt);
        Assert.Equal("iris.isb", i.ClassifiedBy);
        Assert.Equal(classifiedAt.AddHours(4), Assert.Single(IncidentService.Timeline(i)).Due);

        app.Time.Advance(TimeSpan.FromHours(1));
        await svc.SaveAssessmentAsync(Sam, id, i.Version, new ImpactAssessment());
        await svc.ClassifyAsync(Sam, id, (await Get(id)).Version);
        i = await Get(id);
        Assert.Equal(IncidentClass.NotMajor, i.Class);
        Assert.Equal(classifiedAt, i.ClassifiedMajorAt);
        Assert.Contains("Previously major.", i.Timeline.Last().Detail);
    }

    [Fact]
    public async Task Stale_version_is_refused_instead_of_overwriting()
    {
        var id = await NewIncident();
        await svc.SaveAssessmentAsync(Sam, id, 0, MajorImpact);
        var e = await Fails(DeskError.Conflict, () => svc.SaveAssessmentAsync(Iris, id, 0, new ImpactAssessment()));
        Assert.Contains("changed by someone else", e.Message);
        Assert.Equal(150_000, (await Get(id)).Assessment.ClientsAffected);
    }

    [Fact]
    public async Task Only_major_incidents_are_reported_and_in_the_right_order()
    {
        var minor = await NewIncident();
        await Fails(DeskError.Conflict, () => svc.PrepareReportAsync(Sam, minor, ReportType.Initial));

        var id = await MajorIncident();
        var e = await Fails(DeskError.Conflict, () => svc.PrepareReportAsync(Sam, id, ReportType.Intermediate));
        Assert.Contains("next report is the initial report", e.Message);
        Assert.Equal([ReportType.Initial], IncidentService.NextReportTypes(await Get(id)));
    }

    [Fact]
    public async Task Four_eyes_the_preparer_cannot_submit_and_the_attempt_is_recorded()
    {
        var id = await MajorIncident();
        var reportId = await svc.PrepareReportAsync(Iris, id, ReportType.Initial);
        var e = await Fails(DeskError.Forbidden, () => svc.SubmitReportAsync(Iris, reportId, null));
        Assert.Contains("Four-eyes", e.Message);
        var i = await Get(id);
        Assert.Equal("report.four_eyes_denied", i.Timeline.Last().Action);
        Assert.Null(i.Reports.Single().SubmittedAt);

        await svc.SubmitReportAsync(Jonas, reportId, "MVP-1");
        Assert.Equal("jonas.isb", (await Get(id)).Reports.Single().SubmittedBy);
    }

    [Fact]
    public async Task Responders_prepare_but_cannot_submit()
    {
        var id = await MajorIncident();
        var reportId = await svc.PrepareReportAsync(Sam, id, ReportType.Initial);
        await Fails(DeskError.Forbidden, () => svc.SubmitReportAsync(Sam, reportId, null));
    }

    [Fact]
    public async Task A_report_of_outdated_facts_cannot_be_submitted()
    {
        var id = await MajorIncident();
        var reportId = await svc.PrepareReportAsync(Sam, id, ReportType.Initial);
        var i = await Get(id);
        await svc.SaveAssessmentAsync(Sam, id, i.Version, MajorImpact with { ClientsAffected = 200_000 });
        var e = await Fails(DeskError.Conflict, () => svc.SubmitReportAsync(Iris, reportId, null));
        Assert.Contains("changed after this report was prepared", e.Message);
    }

    [Fact]
    public async Task Only_the_newest_draft_can_be_submitted()
    {
        var id = await MajorIncident();
        var first = await svc.PrepareReportAsync(Sam, id, ReportType.Initial);
        app.Time.Advance(TimeSpan.FromMinutes(5));
        var second = await svc.PrepareReportAsync(Sam, id, ReportType.Initial);
        await Fails(DeskError.Conflict, () => svc.SubmitReportAsync(Iris, first, null));
        await svc.SubmitReportAsync(Iris, second, null);
        await Fails(DeskError.Conflict, () => svc.SubmitReportAsync(Iris, second, null));
    }

    [Fact]
    public async Task An_incomplete_report_cannot_be_submitted()
    {
        var id = await svc.CreateAsync(Sam, new NewIncident("No description", "", null, Now, null));
        await svc.SaveAssessmentAsync(Sam, id, 0, MajorImpact);
        await svc.ClassifyAsync(Sam, id, 1);
        var reportId = await svc.PrepareReportAsync(Sam, id, ReportType.Initial);
        Assert.Contains("Missing: description", (await Get(id)).Timeline.Last().Detail);
        var e = await Fails(DeskError.Invalid, () => svc.SubmitReportAsync(Iris, reportId, null));
        Assert.Contains("description", e.Message);
    }

    [Fact]
    public async Task Full_lifecycle_from_detection_to_closure()
    {
        var id = await MajorIncident();

        // Initial notification after 3 hours: on time
        app.Time.Advance(TimeSpan.FromHours(3));
        await svc.SubmitReportAsync(Iris, await svc.PrepareReportAsync(Sam, id, ReportType.Initial), "MVP-100");
        Assert.Contains("On time.", (await Get(id)).Timeline.Last().Detail);

        // Intermediate report after 80 hours: late (72 hours allowed)
        app.Time.Advance(TimeSpan.FromHours(80));
        var i = await Get(id);
        await svc.UpdateDetailsAsync(Sam, id, i.Version, Details(i) with { RecoveredAt = Now.AddHours(-70) });
        await svc.SubmitReportAsync(Iris, await svc.PrepareReportAsync(Sam, id, ReportType.Intermediate), null);
        Assert.Contains("Late: overdue by 8 h 00 min.", (await Get(id)).Timeline.Last().Detail);
        Assert.Equal([ReportType.Intermediate, ReportType.Final], IncidentService.NextReportTypes(await Get(id)));

        // Closing needs the final report
        i = await Get(id);
        await svc.ResolveAsync(Sam, id, i.Version);
        i = await Get(id);
        await Fails(DeskError.Conflict, () => svc.CloseAsync(Iris, id, i.Version));

        // Final report: incomplete until the root cause and costs are recorded
        app.Time.Advance(TimeSpan.FromDays(10));
        i = await Get(id);
        await svc.UpdateDetailsAsync(Sam, id, i.Version, Details(i) with
        {
            RootCause = RootCauseCategory.SystemFailure,
            RootCauseKey = "load balancer firmware",
            RootCauseDescription = "Firmware bug after an update.",
            MeasuresTaken = "Rolled back; staged rollout for firmware.",
        });
        i = await Get(id);
        await svc.SaveAssessmentAsync(Sam, id, i.Version, i.Assessment with { EconomicImpactEur = 70_000m });
        var final = await svc.PrepareReportAsync(Sam, id, ReportType.Final);
        await svc.SubmitReportAsync(Jonas, final, "MVP-100-F");
        Assert.Empty(IncidentService.NextReportTypes(await Get(id)));
        Assert.Null(Deadlines.Next(IncidentService.Timeline(await Get(id))));
        await Fails(DeskError.Conflict, () => svc.PrepareReportAsync(Sam, id, ReportType.Intermediate));

        i = await Get(id);
        await Fails(DeskError.Forbidden, () => svc.CloseAsync(Sam, id, i.Version));
        await svc.CloseAsync(Iris, id, i.Version);
        i = await Get(id);
        Assert.Equal(IncidentStatus.Closed, i.Status);
        await Fails(DeskError.Conflict, () => svc.SaveAssessmentAsync(Sam, id, i.Version, new ImpactAssessment()));
    }

    [Fact]
    public async Task Minor_incidents_close_only_with_a_root_cause()
    {
        var id = await NewIncident();
        await svc.ClassifyAsync(Sam, id, 0);
        await svc.ResolveAsync(Sam, id, 1);
        var i = await Get(id);
        var e = await Fails(DeskError.Conflict, () => svc.CloseAsync(Iris, id, i.Version));
        Assert.Contains("recurring", e.Message);
        await svc.UpdateDetailsAsync(Sam, id, i.Version, Details(i) with { RootCause = RootCauseCategory.HumanError, RootCauseKey = "wrong firewall rule" });
        await svc.CloseAsync(Iris, id, i.Version + 1);
    }

    [Fact]
    public async Task Resolve_requires_a_classification()
    {
        var id = await NewIncident();
        await Fails(DeskError.Conflict, () => svc.ResolveAsync(Sam, id, 0));
    }

    [Fact]
    public async Task Detection_cannot_move_after_the_classification()
    {
        var id = await MajorIncident();
        var i = await Get(id);
        await Fails(DeskError.Invalid, () => svc.UpdateDetailsAsync(Sam, id, i.Version, Details(i) with { DetectedAt = Now.AddHours(1) }));
    }

    [Fact]
    public async Task Dashboard_puts_the_most_urgent_deadline_first()
    {
        var calm = await MajorIncident();
        app.Time.Advance(TimeSpan.FromHours(2));
        await svc.SubmitReportAsync(Iris, await svc.PrepareReportAsync(Sam, calm, ReportType.Initial), null);
        var unclassified = await NewIncident("Phishing wave");
        app.Time.Advance(TimeSpan.FromHours(1));
        var urgent = await MajorIncident();
        app.Time.Advance(TimeSpan.FromHours(5));

        var rows = await svc.DashboardAsync();
        Assert.Equal([urgent, unclassified, calm], rows.Select(r => r.Id));
        Assert.True(rows[0].Next!.IsOverdue(Now));
        Assert.True(rows[1].AwaitingClassification);
        Assert.Equal(ReportType.Intermediate, rows[2].Next!.Type);
    }

    [Fact]
    public async Task Recurring_minor_incidents_are_found()
    {
        foreach (var hours in new[] { 14, 13 })
        {
            var id = await NewIncident("Certificate expired");
            await svc.SaveAssessmentAsync(Sam, id, 0, new ImpactAssessment
            {
                SupportsCriticalOrImportantFunction = true, Duration = TimeSpan.FromHours(hours), EconomicImpactEur = 60_000m,
            });
            await svc.ClassifyAsync(Sam, id, 1);
            var i = await Get(id);
            await svc.UpdateDetailsAsync(Sam, id, i.Version, Details(i) with { RootCause = RootCauseCategory.ProcessFailure, RootCauseKey = "Expired TLS certificate" });
            app.Time.Advance(TimeSpan.FromDays(20));
        }
        var g = Assert.Single(await svc.RecurringAsync());
        Assert.Equal(["INC-2026-0001", "INC-2026-0002"], g.References);
        Assert.True(g.Classification.IsMajor);
    }

    [Fact]
    public async Task Import_attaches_the_alerts_as_evidence()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "logsentinel-payment-fraud.json"));
        app.Time.AdjustTime(new DateTimeOffset(2026, 10, 31, 10, 40, 0, TimeSpan.Zero));
        var draft = LogSentinelImport.Draft(LogSentinelImport.Parse(json), Now);
        var id = await svc.ImportAsync(Sam, draft);
        var i = await Get(id);
        Assert.Equal("logsentinel", i.Source);
        Assert.Equal(3, i.Alerts.Count);
        Assert.Contains("T1657 Financial Theft", i.Techniques);
        Assert.Equal(["incident.created", "incident.imported"], i.Timeline.Select(t => t.Action));
        Assert.Null(i.Class);
    }

    [Fact]
    public async Task Control_characters_are_stripped_from_text()
    {
        var id = await svc.CreateAsync(Sam, new NewIncident("Outage\u0000\u001b[31m", "line 1\r\nline 2", null, Now, null));
        var i = await Get(id);
        Assert.Equal("Outage[31m", i.Title);
        Assert.Equal("line 1\nline 2", i.Description);
    }

    [Fact]
    public async Task Timeline_is_stored_with_every_change()
    {
        var id = await MajorIncident();
        await using var db = app.Db();
        var actions = await db.Timeline.Where(t => t.IncidentId == id).OrderBy(t => t.Id).Select(t => t.Action).ToListAsync();
        Assert.Equal(["incident.created", "assessment.saved", "incident.classified"], actions);
    }

    static IncidentDetails Details(Incident i) => new()
    {
        Title = i.Title,
        Description = i.Description,
        OccurredAt = i.OccurredAt,
        DetectedAt = i.DetectedAt,
        RecoveredAt = i.RecoveredAt,
        ResolvedAt = i.ResolvedAt,
        RootCause = i.RootCause,
        RootCauseKey = i.RootCauseKey,
        RootCauseDescription = i.RootCauseDescription,
        MeasuresTaken = i.MeasuresTaken,
        LessonsLearned = i.LessonsLearned,
        ClientsInformed = i.ClientsInformed,
        ThirdPartyProvider = i.ThirdPartyProvider,
    };
}
