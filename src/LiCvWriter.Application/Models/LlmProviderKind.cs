namespace LiCvWriter.Application.Models;

/// <summary>
/// Identifies which local LLM runtime backs the current session model selection.
/// </summary>
public enum LlmProviderKind
{
    Ollama,
    Foundry
}