using DoraDesk.Domain.Integration;

namespace DoraDesk.Tests;

public class ImportTests
{
    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    static readonly DateTimeOffset ImportedAt = new(2026, 10, 31, 10, 40, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_real_LogSentinel_output()
    {
        var alerts = LogSentinelImport.Parse(Fixture("logsentinel-payment-fraud.json"));
        Assert.Equal(3, alerts.Count);
        var fin = alerts.Single(a => a.RuleId == "LS-FIN-001");
        Assert.Equal("High", fin.Severity);
        Assert.Equal("T1657", fin.TechniqueId);
        Assert.Equal("Impact", fin.Tactic);
        Assert.Equal("lena@example.de", fin.User);
        Assert.Equal(2, fin.EventCount);
    }

    [Fact]
    public void Draft_starts_at_the_first_alert_and_takes_the_worst_title()
    {
        var draft = LogSentinelImport.Draft(LogSentinelImport.Parse(Fixture("logsentinel-payment-fraud.json")), ImportedAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 10, 2, 0, TimeSpan.Zero), draft.OccurredAt);
        Assert.Equal(ImportedAt, draft.DetectedAt);
        Assert.Equal("'lena@example.de' logged in from a new IP after a phishing email", draft.Title);
        Assert.Equal(["T1078 Valid Accounts", "T1566 Phishing", "T1657 Financial Theft"], draft.Techniques);
        Assert.Contains("3 alert(s)", draft.Description);
        Assert.Contains("lena@example.de", draft.Description);
        Assert.Equal(draft.Alerts.OrderBy(a => a.FirstSeen), draft.Alerts);
    }

    [Fact]
    public void Hints_point_to_the_DORA_criteria_without_deciding()
    {
        var draft = LogSentinelImport.Draft(LogSentinelImport.Parse(Fixture("logsentinel-payment-fraud.json")), ImportedAt);
        Assert.Contains(draft.Hints, h => h.Contains("Art. 8(1)(a)"));
        Assert.Contains(draft.Hints, h => h.StartsWith("Financial theft"));
        Assert.Contains(draft.Hints, h => h.StartsWith("Phishing"));
        Assert.Contains(draft.Hints, h => h.StartsWith("At least one critical alert"));
        Assert.DoesNotContain(draft.Hints, h => h.Contains("Event logs were cleared"));
    }

    [Fact]
    public void Detection_time_is_never_before_the_first_alert()
    {
        var alerts = LogSentinelImport.Parse(Fixture("logsentinel-payment-fraud.json"));
        var draft = LogSentinelImport.Draft(alerts, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(draft.OccurredAt, draft.DetectedAt);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("{\"ruleId\":\"x\"}", "JSON array")]
    [InlineData("[1]", "not an object")]
    [InlineData("[{\"title\":\"t\",\"severity\":\"High\",\"firstSeen\":\"2026-10-31T10:00:00Z\",\"lastSeen\":\"2026-10-31T10:00:00Z\"}]", "\"ruleId\" is missing")]
    [InlineData("[{\"ruleId\":\"r\",\"title\":\"t\",\"severity\":\"Severe\",\"firstSeen\":\"2026-10-31T10:00:00Z\",\"lastSeen\":\"2026-10-31T10:00:00Z\"}]", "unknown severity")]
    [InlineData("[{\"ruleId\":\"r\",\"title\":\"t\",\"severity\":\"High\",\"firstSeen\":\"yesterday\",\"lastSeen\":\"2026-10-31T10:00:00Z\"}]", "\"firstSeen\" is missing or not a timestamp")]
    [InlineData("[{\"a\":", "Not valid JSON")]
    public void Rejects_broken_input_with_a_clear_message(string json, string message)
    {
        var e = Assert.Throws<ImportException>(() => LogSentinelImport.Parse(json));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void Deeply_nested_input_is_rejected()
    {
        var json = "[" + new string('[', 100) + new string(']', 100) + "]";
        Assert.Throws<ImportException>(() => LogSentinelImport.Parse(json));
    }

    [Fact]
    public void Empty_array_gives_no_draft()
    {
        Assert.Empty(LogSentinelImport.Parse("[]"));
        Assert.Throws<ImportException>(() => LogSentinelImport.Draft([], ImportedAt));
    }

    [Fact]
    public void Full_sample_with_host_based_attack_mentions_log_clearing_and_persistence()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "logsentinel-alerts.json");
        var draft = LogSentinelImport.Draft(LogSentinelImport.Parse(File.ReadAllText(path)), ImportedAt);
        Assert.Equal(22, draft.Alerts.Count);
        Assert.Contains(draft.Hints, h => h.Contains("Event logs were cleared"));
        Assert.Contains(draft.Hints, h => h.Contains("persistence"));
        Assert.Contains("WS-FIN-07", draft.Description);
    }
}
