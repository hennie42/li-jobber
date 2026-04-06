namespace LiCvWriter.Core.Profiles;

public sealed record RecommendationEntry(
    PersonName Author,
    string? Company,
    string? JobTitle,
    string Text,
    string VisibilityStatus,
    PartialDate? CreatedOn = null);
