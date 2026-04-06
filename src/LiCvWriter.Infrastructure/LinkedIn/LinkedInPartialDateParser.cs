using System.Globalization;
using LiCvWriter.Core.Profiles;

namespace LiCvWriter.Infrastructure.LinkedIn;

public sealed class LinkedInPartialDateParser
{
    private static readonly string[] MonthYearFormats = ["MMM yyyy", "MMMM yyyy"];
    private static readonly string[] TimestampFormats =
    [
        "M/d/yy, h:mm tt",
        "M/d/yyyy, h:mm tt",
        "MM/dd/yy, hh:mm tt",
        "yyyy-MM-dd HH:mm 'UTC'",
        "yyyy-MM-dd H:mm 'UTC'",
        "yyyy/MM/dd HH:mm:ss 'UTC'",
        "yyyy-MM-dd"
    ];

    public PartialDate? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length == 4 && int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var yearOnly))
        {
            return new PartialDate(trimmed, yearOnly);
        }

        if (DateTime.TryParseExact(trimmed, MonthYearFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var monthYear))
        {
            return new PartialDate(trimmed, monthYear.Year, monthYear.Month);
        }

        if (DateTimeOffset.TryParseExact(trimmed, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var offset))
        {
            return new PartialDate(trimmed, offset.Year, offset.Month, offset.Day);
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out offset))
        {
            return new PartialDate(trimmed, offset.Year, offset.Month, offset.Day);
        }

        return new PartialDate(trimmed);
    }
}
