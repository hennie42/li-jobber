using LiCvWriter.Application.Models;
using LiCvWriter.Core.Documents;

namespace LiCvWriter.Application.Abstractions;

public interface IDocumentRenderer
{
    Task<GeneratedDocument> RenderAsync(DocumentRenderRequest request, CancellationToken cancellationToken = default);
}
