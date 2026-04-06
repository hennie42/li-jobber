using LiCvWriter.Infrastructure.Csv;

namespace LiCvWriter.Tests.Csv;

public sealed class SimpleCsvParserTests
{
    [Fact]
    public void Parse_HandlesQuotedMultilineFields()
    {
        var parser = new SimpleCsvParser();
        var csv = "Name,Description\r\nRole,\"Line 1\r\nLine 2, with comma\"\r\n";

        var result = parser.Parse(csv);

        Assert.Equal(["Name", "Description"], result.Headers);
        Assert.Single(result.Records);
        Assert.Equal("Role", result.Records[0]["Name"]);
        Assert.Equal("Line 1\nLine 2, with comma", result.Records[0]["Description"]);
    }
}
