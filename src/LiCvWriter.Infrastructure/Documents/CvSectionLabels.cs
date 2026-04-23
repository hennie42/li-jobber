using LiCvWriter.Application.Models;
using LiCvWriter.Core.Documents;

namespace LiCvWriter.Infrastructure.Documents;

/// <summary>
/// Central resource map of CV section labels used by the renderer and the
/// template binder. Keeping the strings here (rather than scattered as inline
/// <c>Translate(...)</c> calls) ensures the markdown emitted by
/// <see cref="MarkdownDocumentRenderer"/> uses the same heading text the
/// extractor in <see cref="CvMarkdownSectionExtractor"/> looks for, and that
/// any new output language only needs adding in one place.
/// </summary>
internal static class CvSectionLabels
{
    /// <summary>Returns the localized H2 heading text for a CV section.</summary>
    public static string Heading(CvSection section, OutputLanguage language)
        => (section, language) switch
        {
            (CvSection.ProfileSummary, OutputLanguage.Danish) => "Professionel profil",
            (CvSection.ProfileSummary, _) => "Professional Profile",
            (CvSection.KeySkills, OutputLanguage.Danish) => "Nøgleteknologier og kompetencer",
            (CvSection.KeySkills, _) => "Key Technologies & Competencies",
            (CvSection.ExperienceHighlights, OutputLanguage.Danish) => "Erhvervserfaring",
            (CvSection.ExperienceHighlights, _) => "Professional Experience",
            (CvSection.ProjectHighlights, OutputLanguage.Danish) => "Projekter",
            (CvSection.ProjectHighlights, _) => "Projects",
            (CvSection.Education, OutputLanguage.Danish) => "Uddannelse",
            (CvSection.Education, _) => "Education",
            (CvSection.Languages, OutputLanguage.Danish) => "Sprog",
            (CvSection.Languages, _) => "Languages",
            _ => section.ToString()
        };

    /// <summary>English heading literal — used by the section extractor.</summary>
    public static string EnglishHeading(CvSection section) => Heading(section, OutputLanguage.English);

    /// <summary>Danish heading literal — used by the section extractor.</summary>
    public static string DanishHeading(CvSection section) => Heading(section, OutputLanguage.Danish);
}
