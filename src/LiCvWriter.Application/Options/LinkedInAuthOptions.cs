namespace LiCvWriter.Application.Options;

public sealed class LinkedInAuthOptions
{
    public const string SectionName = "LinkedIn";

    public string PortabilityScope { get; set; } = "r_dma_portability_self_serve";

    public string PortabilityApiVersion { get; set; } = string.Empty;
}
