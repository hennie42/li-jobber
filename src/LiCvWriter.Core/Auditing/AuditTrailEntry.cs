namespace LiCvWriter.Core.Auditing;

public sealed record AuditTrailEntry(
    DateTimeOffset CreatedAtUtc,
    string EventType,
    string Summary,
    IReadOnlyDictionary<string, string> Metadata);
