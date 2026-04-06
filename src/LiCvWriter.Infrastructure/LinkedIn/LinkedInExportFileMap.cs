namespace LiCvWriter.Infrastructure.LinkedIn;

public static class LinkedInExportFileMap
{
    public const string Profile = "Profile.csv";
    public const string Positions = "Positions.csv";
    public const string Education = "Education.csv";
    public const string Skills = "Skills.csv";
    public const string Certifications = "Certifications.csv";
    public const string Projects = "Projects.csv";
    public const string Recommendations = "Recommendations_Received.csv";
    public const string VolunteeringExperiences = "Volunteering_Experiences.csv";
    public const string Languages = "Languages.csv";
    public const string Publications = "Publications.csv";
    public const string Patents = "Patents.csv";
    public const string Honors = "Honors.csv";
    public const string Courses = "Courses.csv";
    public const string Organizations = "Organizations.csv";

    public static IReadOnlyList<string> FirstClassFiles { get; } =
    [
        Profile,
        Positions,
        Education,
        Skills,
        Certifications
    ];

    public static IReadOnlyList<string> OptionalFiles { get; } =
    [
        Projects,
        Recommendations,
        VolunteeringExperiences,
        Languages,
        Publications,
        Patents,
        Honors,
        Courses,
        Organizations
    ];
}
