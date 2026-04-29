using System.Reflection;

namespace LiCvWriter.Infrastructure.Documents.Templates;

/// <summary>
/// Loads embedded Word template resources (<c>.dotx</c>) shipped with the
/// infrastructure assembly. Templates are emitted into a unique temp file
/// per call so the export pipeline can copy them to the final destination
/// and modify the copy without mutating the embedded original.
/// </summary>
public static class EmbeddedTemplateProvider
{
    /// <summary>Default embedded resource name for the CV template.</summary>
    public const string CvTemplateResourceName = "LiCvWriter.Infrastructure.Documents.Templates.cv-template.dotx";

    /// <summary>Default embedded resource name for focused non-CV application materials.</summary>
    public const string ApplicationMaterialTemplateResourceName = "LiCvWriter.Infrastructure.Documents.Templates.application-material-template.dotx";

    /// <summary>Default embedded resource name for recommendation documents.</summary>
    public const string RecommendationsTemplateResourceName = "LiCvWriter.Infrastructure.Documents.Templates.recommendations-template.dotx";

    /// <summary>
    /// Copies the embedded CV template to a unique temporary <c>.dotx</c> file
    /// and returns its path. The caller is responsible for deleting the file.
    /// </summary>
    public static string ExtractCvTemplate()
        => ExtractTemplate(CvTemplateResourceName);

    /// <summary>
    /// Copies the embedded recommendations template to a unique temporary
    /// <c>.dotx</c> file and returns its path. The caller is responsible for deleting the file.
    /// </summary>
    public static string ExtractRecommendationsTemplate()
        => ExtractTemplate(RecommendationsTemplateResourceName);

    /// <summary>
    /// Copies the named embedded template resource to a unique temporary
    /// <c>.dotx</c> file and returns its path. The caller is responsible
    /// for deleting the file.
    /// </summary>
    /// <param name="resourceName">Fully qualified embedded resource name.</param>
    public static string ExtractTemplate(string resourceName)
    {
        var assembly = typeof(EmbeddedTemplateProvider).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded template resource '{resourceName}' was not found in {assembly.FullName}.");

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"licvwriter-template-{Guid.NewGuid():N}.dotx");

        using var fileStream = File.Create(tempPath);
        stream.CopyTo(fileStream);

        return tempPath;
    }

    /// <summary>
    /// Opens a read-only stream over the named embedded template resource.
    /// Use this when copying directly into another file without an intermediate
    /// temp file.
    /// </summary>
    public static Stream OpenTemplate(string resourceName)
    {
        var assembly = typeof(EmbeddedTemplateProvider).Assembly;
        return assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded template resource '{resourceName}' was not found in {assembly.FullName}.");
    }
}
