namespace LiCvWriter.Application.Models;

public sealed record OllamaModelAvailability(
    string Version,
    string Model,
    bool Installed,
    IReadOnlyList<string> AvailableModels,
    IReadOnlyList<OllamaRunningModel>? RunningModels = null)
{
    public IReadOnlyList<OllamaRunningModel> EffectiveRunningModels { get; } = RunningModels ?? Array.Empty<OllamaRunningModel>();

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
