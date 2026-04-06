namespace LiCvWriter.Core.Profiles;

public sealed record DateRange(PartialDate? StartedOn = null, PartialDate? FinishedOn = null)
{
    public bool IsCurrent => FinishedOn is null || !FinishedOn.HasValue;

    public string DisplayValue
    {
        get
        {
            var start = StartedOn?.ToString() ?? string.Empty;
            var end = FinishedOn?.ToString();

            if (string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end))
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(end)
                ? $"{start} - Present"
                : $"{start} - {end}";
        }
    }
}
