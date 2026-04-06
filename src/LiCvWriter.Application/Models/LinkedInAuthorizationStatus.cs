namespace LiCvWriter.Application.Models;

public sealed record LinkedInAuthorizationStatus(
    bool IsAuthorized,
    string Message,
    DateTimeOffset? AuthorizedAtUtc,
    DateTimeOffset? AccessTokenExpiresAtUtc,
    string? Scope,
    bool HasPendingAuthorization = false,
    DateTimeOffset? PendingAuthorizationExpiresAtUtc = null,
    string? ErrorCode = null);
