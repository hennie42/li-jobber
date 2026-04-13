namespace LiCvWriter.Infrastructure.Workflows;

/// <summary>
/// Shared constraint strings reused across LLM prompt builders to reduce token duplication.
/// </summary>
internal static class PromptConstraints
{
    /// <summary>
    /// Appended to any prompt that must return raw JSON with no markdown or prose.
    /// </summary>
    internal const string JsonOnlyOutput = "Return JSON only. No markdown fences, no commentary outside the JSON object.";

    /// <summary>
    /// Core grounding rule for prompts that operate on candidate evidence:
    /// use only what is supplied, omit anything that is missing or uncertain.
    /// </summary>
    internal const string EvidenceGrounding = "Use only facts explicitly present in the supplied evidence. Omit anything missing or ambiguous. Do not invent facts.";

    /// <summary>
    /// Rule for draft-generation prompts: never surface negative framing about the candidate.
    /// </summary>
    internal const string NoNegativeTraits = "Do not mention gaps, weaknesses, missing skills, or negative traits of the applicant.";
}
