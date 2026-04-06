namespace LiCvWriter.Infrastructure.Csv;

public sealed record CsvRecordSet(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Records);
