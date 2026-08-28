using System.Globalization;

namespace OsuTracker.Web.Services;

/// <summary>The projected finish, already worded for the panel that shows it.</summary>
public sealed record PaceEta(string Headline, string Away);

/// <summary>
/// Straight-line projection of when a mode runs out of maps at its recent rate. It lives
/// out here rather than in the page because the interesting part is not the arithmetic
/// but the far end of it: a slow month against 130,000 untouched difficulties lands tens
/// of thousands of years out, where DateTimeOffset.AddDays throws rather than returns.
/// </summary>
public static class PaceProjection
{
    private const double DaysPerYear = 365.2425;
    private const double DaysPerMonth = 30.437;

    /// <summary>
    /// Past this the calendar has stopped saying anything a person can use, and year 9999
    /// is not far behind. Report the size of the number instead of a date.
    /// </summary>
    private const double UsefulYears = 200;

    /// <param name="remaining">Difficulties still to pass. Assumed positive; a finished mode is the caller's own case.</param>
    /// <param name="perWeek">Passes per week over the recent window. Zero or less means never.</param>
    public static PaceEta Project(int remaining, double perWeek, DateTimeOffset now)
    {
        var days = perWeek <= 0 ? double.PositiveInfinity : remaining / perWeek * 7;
        var years = days / DaysPerYear;

        if (double.IsInfinity(days) || double.IsNaN(days))
            return new PaceEta("Never", "no recent passes to project from");

        if (years >= UsefulYears)
            return new PaceEta(
                $"{years.ToString("N0", CultureInfo.InvariantCulture)} years",
                "at the current rate — which is rather the point");

        var when = now.AddDays(days);
        var headline2 = days < 90
            ? when.ToString("d MMM yyyy", CultureInfo.InvariantCulture)
            : when.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        return new PaceEta(headline2, Away(days));
    }

    /// <summary>Distance in the largest unit that still says something useful.</summary>
    public static string Away(double days)
    {
        if (days < 45) return $"{Unit(Math.Max(1, (int)Math.Round(days)), "day")} away";

        var months = (int)Math.Round(days / DaysPerMonth);
        if (months < 24) return $"{Unit(months, "month")} away";

        var years = (int)(days / DaysPerYear);
        var rest = (int)Math.Round((days - years * DaysPerYear) / DaysPerMonth);

        // Rounding the remainder can land on a full year; carry it rather than print "12 months".
        if (rest >= 12) { years++; rest = 0; }

        return rest == 0
            ? $"{Unit(years, "year")} away"
            : $"{Unit(years, "year")} {Unit(rest, "month")} away";
    }

    private static string Unit(int n, string noun) =>
        $"{n.ToString("N0", CultureInfo.InvariantCulture)} {noun}{(n == 1 ? "" : "s")}";
}
