using System.Security.Claims;
using DoraDesk.Domain.Reporting;

namespace DoraDesk.Web.Services;

public static class Roles
{
    /// <summary>Incident responders (SOC, IT operations): record incidents, assess impact, prepare reports.</summary>
    public const string Responder = "responder";

    /// <summary>Information security officer (ISB): confirms the classification and submits reports to the authority.</summary>
    public const string Officer = "officer";

    /// <summary>Internal audit: reads everything, changes nothing.</summary>
    public const string Auditor = "auditor";

    public static readonly string[] All = [Responder, Officer, Auditor];
}

public static class Policies
{
    public const string Edit = "edit";
    public const string Submit = "submit";
}

/// <summary>Who is acting. Taken from the signed-in user, never from form input.</summary>
public sealed record Actor(string Name, IReadOnlySet<string> Roles)
{
    public bool CanEdit => Roles.Contains(Services.Roles.Responder) || Roles.Contains(Services.Roles.Officer);
    public bool CanSubmit => Roles.Contains(Services.Roles.Officer);

    public static Actor From(ClaimsPrincipal user) =>
        new(user.Identity?.Name ?? throw new DeskException(DeskError.Forbidden, "Not signed in."),
            user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet(StringComparer.Ordinal));
}

public enum DeskError { NotFound, Forbidden, Conflict, Invalid }

public sealed class DeskException(DeskError error, string message) : Exception(message)
{
    public DeskError Error { get; } = error;
}

public sealed class DeskOptions
{
    public string EntityName { get; set; } = "Mainufer Bank AG (demo)";
    public string EntityLei { get; set; } = Lei.WithCheckDigits("DEMO00MAINUFER0001");
    public string CompetentAuthority { get; set; } = "BaFin";
    public string MemberState { get; set; } = "DE";

    /// <summary>Time zone for display. Stored and reported times are UTC.</summary>
    public string TimeZone { get; set; } = "Europe/Berlin";

    /// <summary>Sign in as a demo user without a password. Development only, or explicitly enabled for a demo.</summary>
    public bool DemoLogin { get; set; }

    public ReportingEntity Entity => new(EntityName, EntityLei, CompetentAuthority, MemberState);
}

public sealed class DemoUser(string name, string displayName, string role, string description)
{
    public string Name { get; } = name;
    public string DisplayName { get; } = displayName;
    public string Role { get; } = role;
    public string Description { get; } = description;

    public static readonly DemoUser[] All =
    [
        new("sam.soc", "Sam (SOC analyst)", Roles.Responder, "records incidents, imports alerts, assesses impact, prepares reports"),
        new("iris.isb", "Iris (information security officer)", Roles.Officer, "confirms classifications and submits reports to BaFin"),
        new("jonas.isb", "Jonas (deputy security officer)", Roles.Officer, "second officer, for the four-eyes principle"),
        new("ana.audit", "Ana (internal audit)", Roles.Auditor, "read-only"),
    ];
}

public sealed class Clock(TimeProvider time, Microsoft.Extensions.Options.IOptions<DeskOptions> options)
{
    readonly TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTimeOffset Now => time.GetUtcNow();

    public DateTimeOffset Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, zone);

    public string Format(DateTimeOffset? t) => t is { } v ? Local(v).ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "–";

    /// <summary>Reads a local date and time typed into a form (Berlin time) as an absolute instant.</summary>
    public DateTimeOffset FromLocal(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified));
    }
}
