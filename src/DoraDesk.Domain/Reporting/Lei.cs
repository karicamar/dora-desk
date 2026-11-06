namespace DoraDesk.Domain.Reporting;

/// <summary>
/// Legal Entity Identifier (ISO 17442): 18 alphanumeric characters and two check digits (ISO 7064 MOD 97-10, the
/// same scheme as the IBAN). Every report to the supervisor identifies the financial entity by its LEI.
/// </summary>
public static class Lei
{
    public static bool IsValid(string? value)
    {
        if (value is null || value.Length != 20) return false;
        foreach (var c in value)
            if (!(c is >= '0' and <= '9' or >= 'A' and <= 'Z')) return false;
        if (!char.IsAsciiDigit(value[18]) || !char.IsAsciiDigit(value[19])) return false;
        return Mod97(value) == 1;
    }

    /// <summary>Appends the two check digits to an 18-character base (useful for test data).</summary>
    public static string WithCheckDigits(string base18)
    {
        if (base18.Length != 18) throw new ArgumentException("An LEI base has 18 characters.", nameof(base18));
        var check = 98 - Mod97(base18.ToUpperInvariant() + "00");
        return base18.ToUpperInvariant() + check.ToString("00");
    }

    static int Mod97(string s)
    {
        var rest = 0;
        foreach (var c in s)
        {
            var v = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            rest = v >= 10 ? (rest * 100 + v) % 97 : (rest * 10 + v) % 97;
        }
        return rest;
    }
}
