namespace LiCvWriter.Core.Profiles;

public sealed record PersonName(string First = "", string Last = "", string? Maiden = null)
{
    public static PersonName Empty { get; } = new();

    public string FullName => string.Join(' ', new[] { First, Last }.Where(static value => !string.IsNullOrWhiteSpace(value)));
}
