namespace LiCvWriter.Application.Models;

public sealed record LlmModelAvailability(
    string Version,
    string Model,
    bool Installed,
    IReadOnlyList<string> AvailableModels,
    IReadOnlyList<LlmRunningModel>? RunningModels = null,
    LlmProviderKind Provider = LlmProviderKind.Ollama)
{
    public IReadOnlyList<LlmRunningModel> EffectiveRunningModels { get; } = RunningModels ?? Array.Empty<LlmRunningModel>();

    public bool IsConfiguredModelLoaded
        => IsModelLoaded(Model);

    public bool IsModelLoaded(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return false;
        }

        var normalizedModel = model.Trim();
        return EffectiveRunningModels.Any(runningModel =>
            runningModel.Name.Equals(normalizedModel, StringComparison.OrdinalIgnoreCase)
            || runningModel.Model.Equals(normalizedModel, StringComparison.OrdinalIgnoreCase));
    }
}
