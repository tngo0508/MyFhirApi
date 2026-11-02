using System.Globalization;

namespace MyFhirApi.Api.Search;

/// <summary>
/// FHIR concept: dates have <b>precision</b>. "1980" means the whole year 1980, and "1980-05" means all of May 1980.
/// So both stored values and search values are treated as a time range [Start, End).
/// Date searches then compare ranges: https://hl7.org/fhir/R4/search.html#date
/// </summary>
public readonly record struct FhirDateRange(DateTimeOffset Start, DateTimeOffset End)
{
    public static readonly DateTimeOffset MinValue = new(1, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset MaxValue = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Parses "yyyy", "yyyy-MM", "yyyy-MM-dd" or a full ISO date-time.</summary>
    public static bool TryParse(string? value, out FhirDateRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var inv = CultureInfo.InvariantCulture;

        switch (value.Length)
        {
            case 4 when int.TryParse(value, inv, out var year):
                var y = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                range = new(y, y.AddYears(1));
                return true;
            case 7 when DateTime.TryParseExact(value, "yyyy-MM", inv, DateTimeStyles.None, out var month):
                var m = new DateTimeOffset(month, TimeSpan.Zero);
                range = new(m, m.AddMonths(1));
                return true;
            case 10 when DateTime.TryParseExact(value, "yyyy-MM-dd", inv, DateTimeStyles.None, out var day):
                var d = new DateTimeOffset(day, TimeSpan.Zero);
                range = new(d, d.AddDays(1));
                return true;
        }

        // A full date-time. A missing time zone is treated as UTC (FHIR leaves this up to the server).
        if (DateTimeOffset.TryParse(value, inv, DateTimeStyles.AssumeUniversal, out var instant))
        {
            range = new(instant, instant.AddSeconds(1));
            return true;
        }
        return false;
    }
}
