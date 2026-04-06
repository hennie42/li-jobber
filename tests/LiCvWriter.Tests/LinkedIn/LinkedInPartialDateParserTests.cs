using LiCvWriter.Infrastructure.LinkedIn;

namespace LiCvWriter.Tests.LinkedIn;

public sealed class LinkedInPartialDateParserTests
{
    [Theory]
    [InlineData("Oct 2025", 2025, 10, null)]
    [InlineData("1999", 1999, null, null)]
    [InlineData("03/29/26, 03:40 PM", 2026, 3, 29)]
    public void Parse_RecognizesObservedLinkedInFormats(string input, int year, int? month, int? day)
    {
        var parser = new LinkedInPartialDateParser();

        var result = parser.Parse(input);

        Assert.NotNull(result);
        Assert.Equal(year, result!.Year);
        Assert.Equal(month, result.Month);
        Assert.Equal(day, result.Day);
    }
}
