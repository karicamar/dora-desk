namespace DoraDesk.Domain.Reporting;

public enum ReportType { Initial, Intermediate, Final }

public sealed record DeadlineInfo(ReportType Type, DateTimeOffset Due, DateTimeOffset? SubmittedAt)
{
    public bool IsSubmitted => SubmittedAt.HasValue;
    public bool SubmittedLate => SubmittedAt > Due;
    public bool IsOverdue(DateTimeOffset now) => !IsSubmitted && now > Due;
    public TimeSpan Remaining(DateTimeOffset now) => Due - now;
}

/// <summary>
/// Time limits for reporting a major incident to the competent authority (for German institutions: BaFin), from
/// Commission Delegated Regulation (EU) 2025/301, Art. 5:
/// initial notification within 4 hours of classifying the incident as major, but no later than 24 hours after
/// becoming aware of it; intermediate report within 72 hours of the initial notification; final report no later than
/// one month after the latest intermediate report.
/// </summary>
public static class Deadlines
{
    public static readonly TimeSpan InitialAfterClassification = TimeSpan.FromHours(4);
    public static readonly TimeSpan InitialAfterDetection = TimeSpan.FromHours(24);
    public static readonly TimeSpan IntermediateAfterInitial = TimeSpan.FromHours(72);

    public static DateTimeOffset InitialDue(DateTimeOffset detectedAt, DateTimeOffset classifiedMajorAt)
    {
        if (classifiedMajorAt < detectedAt) throw new ArgumentException("An incident cannot be classified before it is detected.", nameof(classifiedMajorAt));
        var fromClassification = classifiedMajorAt + InitialAfterClassification;
        var cap = detectedAt + InitialAfterDetection;
        return fromClassification < cap ? fromClassification : cap;
    }

    public static DateTimeOffset IntermediateDue(DateTimeOffset initialSubmittedAt) => initialSubmittedAt + IntermediateAfterInitial;

    public static DateTimeOffset FinalDue(DateTimeOffset latestIntermediateSubmittedAt) => latestIntermediateSubmittedAt.AddMonths(1);

    /// <summary>
    /// The chain of deadlines known so far. A later report has a deadline only once the report before it was sent,
    /// because its clock starts with that submission.
    /// </summary>
    public static IReadOnlyList<DeadlineInfo> Timeline(
        DateTimeOffset detectedAt,
        DateTimeOffset classifiedMajorAt,
        DateTimeOffset? initialSubmittedAt,
        DateTimeOffset? latestIntermediateSubmittedAt,
        DateTimeOffset? finalSubmittedAt)
    {
        var list = new List<DeadlineInfo> { new(ReportType.Initial, InitialDue(detectedAt, classifiedMajorAt), initialSubmittedAt) };
        if (initialSubmittedAt is { } initial)
        {
            // The first intermediate report is due 72 hours after the initial notification
            list.Add(new(ReportType.Intermediate, IntermediateDue(initial), latestIntermediateSubmittedAt));
            if (latestIntermediateSubmittedAt is { } intermediate)
                list.Add(new(ReportType.Final, FinalDue(intermediate), finalSubmittedAt));
        }
        return list;
    }

    /// <summary>The first report that has not been sent yet, or null when the final report is out.</summary>
    public static DeadlineInfo? Next(IReadOnlyList<DeadlineInfo> timeline) => timeline.FirstOrDefault(d => !d.IsSubmitted);

    public static string Describe(TimeSpan remaining)
    {
        var late = remaining < TimeSpan.Zero;
        var t = late ? remaining.Negate() : remaining;
        var text = t.TotalDays >= 2 ? $"{(int)t.TotalDays} d {t.Hours} h"
            : t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min"
            : $"{Math.Max(t.Minutes, 0)} min";
        return late ? $"overdue by {text}" : $"due in {text}";
    }
}
