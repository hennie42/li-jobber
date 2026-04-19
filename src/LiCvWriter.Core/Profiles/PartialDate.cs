using System.Globalization;

namespace LiCvWriter.Core.Profiles;

public sealed record PartialDate(string RawValue, int? Year = null, int? Month = null, int? Day = null)
{
    public static PartialDate Empty { get; } = new(string.Empty);

    public bool HasValue => !string.IsNullOrWhiteSpace(RawValue);

    public DateOnly? ToDateOnly()
    {
        if (Year is null)
        {
            return null;
        }

        return new DateOnly(Year.Value, Month ?? 1, Day ?? 1);
    }

    public override string ToString()
    {
        // Prefer the structured date components over RawValue so that
        // input quirks ("May2008", extra whitespace, locale differences)
        // are normalised to a consistent display format.
        if (Year is not null)
        {
            if (Month is null)
            {
                return Year.Value.ToString(CultureInfo.InvariantCulture);
            }

            if (Day is null)
            {
                return new DateTime(Year.Value, Month.Value, 1).ToString("MMM yyyy", CultureInfo.InvariantCulture);
            }

            return new DateTime(Year.Value, Month.Value, Day.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return string.IsNullOrWhiteSpace(RawValue) ? string.Empty : RawValue;
    }
}
