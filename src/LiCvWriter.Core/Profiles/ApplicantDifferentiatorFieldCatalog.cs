namespace LiCvWriter.Core.Profiles;

public sealed record ApplicantDifferentiatorFieldDefinition(
    string Key,
    string Label,
    string Meaning,
    string Usage);

public static class ApplicantDifferentiatorFieldCatalog
{
    public static ApplicantDifferentiatorFieldDefinition WorkStyle { get; } = new(
        "workStyle",
        "Work style",
        "How the applicant prefers to operate day to day, including pace, structure, autonomy, collaboration, and problem-solving rhythm.",
        "Used to steer culture-fit framing, summary language, and how the app describes the applicant in role-specific drafts.");

    public static ApplicantDifferentiatorFieldDefinition CommunicationStyle { get; } = new(
        "communicationStyle",
        "Communication style",
        "How the applicant explains complexity, listens, adapts to different audiences, and keeps discussions constructive and clear.",
        "Used in cover letters, executive summaries, interview preparation, and stakeholder-fit language.");

    public static ApplicantDifferentiatorFieldDefinition LeadershipStyle { get; } = new(
        "leadershipStyle",
        "Leadership style",
        "How the applicant leads direction, decisions, teams, or technical work with or without formal line authority.",
        "Used when the target role expects ownership, coaching, architectural direction, or cross-team leadership.");

    public static ApplicantDifferentiatorFieldDefinition StakeholderStyle { get; } = new(
        "stakeholderStyle",
        "Stakeholder style",
        "How the applicant builds trust with clients, executives, peers, and delivery teams while handling alignment and trade-offs.",
        "Used for consulting, advisory, matrix, and influence-heavy roles where relationship management affects delivery.");

    public static ApplicantDifferentiatorFieldDefinition Motivators { get; } = new(
        "motivators",
        "Motivators",
        "What kinds of problems, environments, outcomes, or responsibilities create energy and long-term commitment for the applicant.",
        "Used to shape the role-fit rationale, job-change narrative, and why-this-role sections.");

    public static ApplicantDifferentiatorFieldDefinition TargetNarrative { get; } = new(
        "targetNarrative",
        "Target narrative",
        "The concise positioning story that links the applicant's strengths and working style to the kinds of roles they want next.",
        "Used as the north-star message across CV summaries, introductions, and interview talking points.");

    public static ApplicantDifferentiatorFieldDefinition Watchouts { get; } = new(
        "watchouts",
        "Watchouts",
        "Potential blind spots, context risks, or framing issues the app should manage carefully when presenting the applicant.",
        "Used to avoid overclaiming, surface mitigations, and prepare careful wording for sensitive areas.");

    public static ApplicantDifferentiatorFieldDefinition AboutApplicantBasis { get; } = new(
        "aboutApplicantBasis",
        "About applicant basis",
        "The strongest evidence themes or proof points the app should rely on when drafting high-level descriptions of the applicant.",
        "Used to decide which experiences, patterns, and examples deserve emphasis in about-the-applicant sections.");

    public static IReadOnlyList<ApplicantDifferentiatorFieldDefinition> All { get; } =
    [
        WorkStyle,
        CommunicationStyle,
        LeadershipStyle,
        StakeholderStyle,
        Motivators,
        TargetNarrative,
        Watchouts,
        AboutApplicantBasis
    ];
}