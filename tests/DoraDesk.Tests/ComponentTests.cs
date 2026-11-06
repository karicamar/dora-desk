using Bunit;
using DoraDesk.Domain.Classification;
using DoraDesk.Domain.Reporting;
using DoraDesk.Web.Components;
using DoraDesk.Web.Components.Shared;
using DoraDesk.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DoraDesk.Tests;

public class ComponentTests : BunitContext
{
    static readonly DateTimeOffset Now = new(2026, 3, 2, 8, 0, 0, TimeSpan.Zero);

    public ComponentTests()
    {
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        Services.AddSingleton(Options.Create(new DeskOptions()));
        Services.AddSingleton<Clock>();
    }

    [Fact]
    public void Classification_panel_lists_all_criteria_and_marks_the_met_ones()
    {
        var result = Classifier.Classify(new ImpactAssessment { SupportsCriticalOrImportantFunction = true, EconomicImpactEur = 500_000m, Duration = TimeSpan.FromHours(30) });
        var cut = Render<ClassificationPanel>(p => p.Add(c => c.Result, result));

        Assert.Equal("Major incident", cut.Find(".verdict").TextContent);
        Assert.Contains("major", cut.Find("section").ClassList);
        Assert.Equal(7, cut.FindAll(".criteria li").Count);
        Assert.Equal(3, cut.FindAll(".criteria li.met").Count);
        Assert.Contains("500,000 €", cut.Find("li[data-criterion=EconomicImpact] .reason").TextContent);
    }

    [Fact]
    public void Classification_text_is_encoded_not_rendered_as_markup()
    {
        var result = new ClassificationResult(IncidentClass.NotMajor,
            [new CriterionResult(Criterion.CriticalServices, false, "<img src=x onerror=alert(1)>", "Art. 6")], "<b>bold</b>");
        var cut = Render<ClassificationPanel>(p => p.Add(c => c.Result, result));
        Assert.Empty(cut.FindAll("img"));
        Assert.Empty(cut.FindAll(".rationale b"));
        Assert.Contains("&lt;img", cut.Markup);
    }

    [Fact]
    public void Assessment_editor_updates_the_classification_as_you_type()
    {
        var model = new AssessmentModel();
        var cut = Render<AssessmentEditor>(p => p.Add(c => c.Model, model));
        Assert.Equal("Not major", cut.Find(".verdict").TextContent);

        cut.FindAll("label.check").First(l => l.TextContent.Contains("Successful malicious")).QuerySelector("input")!.Change(true);
        Assert.True(model.MaliciousAccessMayCauseDataLoss);
        Assert.Equal("Major incident", cut.Find(".verdict").TextContent);
        Assert.Contains("Art. 8(1)(a)", cut.Find(".rationale").TextContent);
    }

    [Fact]
    public void Assessment_editor_computes_thresholds_from_numbers()
    {
        var model = new AssessmentModel { SupportsCriticalOrImportantFunction = true, ClientsUsingService = 1000 };
        var cut = Render<AssessmentEditor>(p => p.Add(c => c.Model, model));
        var inputs = cut.FindAll("input[type=number]");
        inputs[0].Change("150");                                    // clients affected: 15 %
        cut.FindAll("input[type=number]")[^1].Change("250000");     // economic impact
        Assert.Equal("Major incident", cut.Find(".verdict").TextContent);
        Assert.Contains("15 % of clients", cut.Find("li[data-criterion=ClientsCounterpartsTransactions] .reason").TextContent);
    }

    [Fact]
    public void Read_only_editor_disables_every_input()
    {
        var cut = Render<AssessmentEditor>(p => p.Add(c => c.Model, new AssessmentModel()).Add(c => c.ReadOnly, true));
        Assert.True(cut.Find(".criteria-form > fieldset").HasAttribute("disabled"));
    }

    [Fact]
    public void Assessment_model_round_trips()
    {
        var a = new ImpactAssessment
        {
            ClientsAffected = 5, ClientsUsingService = 10, TransactionValueAffectedEur = 1.5m,
            Reputation = ReputationalImpact.MediaAttention | ReputationalImpact.LikelyLossOfClients,
            Duration = TimeSpan.FromHours(3.5), ServiceDowntime = TimeSpan.FromHours(1),
            MemberStatesAffected = ["AT", "DE"], Data = DataImpact.Integrity | DataImpact.Confidentiality,
            DataImpactHarmsBusinessOrCompliance = true, SupportsCriticalOrImportantFunction = true, EconomicImpactEur = 99m,
        };
        var back = AssessmentModel.From(a).ToAssessment();
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(a), System.Text.Json.JsonSerializer.Serialize(back));
    }

    [Theory]
    [InlineData(-30, null, "overdue", "overdue by 30 min")]
    [InlineData(90, null, "urgent", "due in 1 h 30 min")]
    [InlineData(600, null, "pending", "due in 10 h 00 min")]
    [InlineData(60, -30, "done", "sent 02.03.2026 09:30")]          // Frankfurt time (CET)
    [InlineData(-60, 0, "late", "sent 02.03.2026 09:00 (late)")]
    public void Deadline_badge_shows_urgency(int dueInMinutes, int? submittedMinutesAgo, string css, string text)
    {
        var d = new DeadlineInfo(ReportType.Initial, Now.AddMinutes(dueInMinutes), submittedMinutesAgo is { } s ? Now.AddMinutes(-s) : null);
        var cut = Render<DeadlineBadge>(p => p.Add(c => c.Deadline, d).Add(c => c.Now, Now));
        Assert.Contains(css, cut.Find(".deadline").ClassList);
        Assert.Equal(text, cut.Find(".when").TextContent);
        Assert.Equal("Initial notification", cut.Find(".kind").TextContent);
    }
}
