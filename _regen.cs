using LiCvWriter.Infrastructure.Documents.Templates;

var templateName = args.Length > 1 ? args[1] : "cv";
if (templateName.Equals("recommendations", System.StringComparison.OrdinalIgnoreCase))
{
    RecommendationsWordTemplateGenerator.Generate(args[0]);
}
else
{
    CvWordTemplateGenerator.Generate(args[0]);
}

System.Console.WriteLine("Template generated: " + args[0]);
