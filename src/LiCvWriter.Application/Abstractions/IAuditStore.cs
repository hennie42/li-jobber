using LiCvWriter.Core.Auditing;

namespace LiCvWriter.Application.Abstractions;

public interface IAuditStore
{
    Task SaveAsync(AuditTrailEntry entry, CancellationToken cancellationToken = default);
}
