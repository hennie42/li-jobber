namespace LiCvWriter.Application.Services;

/// <summary>
/// Hardcoded bidirectional transferable-skills matrix.
/// When a candidate is Missing a required skill, this matrix identifies
/// adjacent skills that demonstrate transferable capability.
/// </summary>
public static class TransferableSkillsMatrix
{
    private static readonly (string, string)[] Pairs =
    [
        // Language families
        ("C#", "Java"),
        ("C#", "TypeScript"),
        ("Java", "Kotlin"),
        ("Python", "Ruby"),
        ("JavaScript", "TypeScript"),

        // Cloud platforms
        ("AWS", "Azure"),
        ("AWS", "GCP"),
        ("Azure", "GCP"),

        // Container & orchestration
        ("Docker", "Kubernetes"),
        ("Docker", "Container orchestration"),

        // Databases
        ("PostgreSQL", "SQL Server"),
        ("PostgreSQL", "MySQL"),
        ("SQL Server", "MySQL"),
        ("MongoDB", "DynamoDB"),
        ("MongoDB", "CosmosDB"),

        // CI/CD
        ("GitHub Actions", "Azure DevOps"),
        ("GitHub Actions", "Jenkins"),
        ("Azure DevOps", "Jenkins"),
        ("GitLab CI", "GitHub Actions"),

        // IaC
        ("Terraform", "Pulumi"),
        ("Terraform", "CloudFormation"),
        ("ARM templates", "Bicep"),
        ("ARM templates", "Terraform"),

        // Messaging
        ("Kafka", "RabbitMQ"),
        ("Kafka", "Azure Service Bus"),
        ("RabbitMQ", "Azure Service Bus"),

        // Leadership
        ("Team Lead", "Engineering Manager"),
        ("Tech Lead", "Engineering Manager"),
        ("Tech Lead", "Team Lead"),
        ("Scrum Master", "Agile Coach"),
        ("Mentoring", "Team Lead"),

        // Architecture
        ("Microservices", "Service-oriented architecture"),
        ("REST API", "GraphQL"),
        ("Event-driven architecture", "Microservices"),

        // Frontend
        ("React", "Angular"),
        ("React", "Vue"),
        ("Angular", "Vue"),
        ("Blazor", "React"),

        // Observability
        ("Datadog", "Prometheus"),
        ("Grafana", "Datadog"),
        ("ELK Stack", "Splunk"),
        ("Application Insights", "Datadog"),
    ];

    private static readonly Dictionary<string, List<string>> Index = BuildIndex();

    /// <summary>
    /// Returns the transferable skills for a given requirement, or empty if none are known.
    /// </summary>
    public static IReadOnlyList<string> GetTransferableSkills(string requirement)
    {
        var normalized = requirement.Trim();
        if (Index.TryGetValue(normalized, out var skills))
        {
            return skills;
        }

        // Try case-insensitive fallback.
        var match = Index.Keys.FirstOrDefault(k => k.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        return match is not null ? Index[match] : Array.Empty<string>();
    }

    private static Dictionary<string, List<string>> BuildIndex()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (a, b) in Pairs)
        {
            if (!index.TryGetValue(a, out var listA))
            {
                listA = [];
                index[a] = listA;
            }
            listA.Add(b);

            if (!index.TryGetValue(b, out var listB))
            {
                listB = [];
                index[b] = listB;
            }
            listB.Add(a);
        }

        return index;
    }
}
