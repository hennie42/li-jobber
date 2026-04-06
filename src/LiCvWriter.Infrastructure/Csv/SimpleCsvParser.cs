using System.Text;

namespace LiCvWriter.Infrastructure.Csv;

public sealed class SimpleCsvParser
{
    public async Task<CsvRecordSet> ParseFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        return Parse(content);
    }

    public CsvRecordSet Parse(string content)
    {
        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        var currentField = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < content.Length; index++)
        {
            var current = content[index];

            if (index == 0 && current == '\uFEFF')
            {
                continue;
            }

            if (inQuotes)
            {
                if (current == '"')
                {
                    if (index + 1 < content.Length && content[index + 1] == '"')
                    {
                        currentField.Append('"');
                        index++;
                        continue;
                    }

                    inQuotes = false;
                    continue;
                }

                if (current == '\r')
                {
                    if (index + 1 < content.Length && content[index + 1] == '\n')
                    {
                        index++;
                    }

                    currentField.Append('\n');
                    continue;
                }

                currentField.Append(current);
                continue;
            }

            switch (current)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    currentRow.Add(currentField.ToString());
                    currentField.Clear();
                    break;
                case '\r':
                    FinalizeRow(rows, currentRow, currentField);
                    if (index + 1 < content.Length && content[index + 1] == '\n')
                    {
                        index++;
                    }

                    break;
                case '\n':
                    FinalizeRow(rows, currentRow, currentField);
                    break;
                default:
                    currentField.Append(current);
                    break;
            }
        }

        FinalizeRow(rows, currentRow, currentField);

        if (rows.Count == 0)
        {
            return new CsvRecordSet(Array.Empty<string>(), Array.Empty<IReadOnlyDictionary<string, string>>());
        }

        var headers = rows[0];
        var records = new List<IReadOnlyDictionary<string, string>>();

        foreach (var row in rows.Skip(1))
        {
            var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < headers.Count; index++)
            {
                dictionary[headers[index]] = index < row.Count ? row[index] : string.Empty;
            }

            records.Add(dictionary);
        }

        return new CsvRecordSet(headers, records);
    }

    private static void FinalizeRow(List<List<string>> rows, List<string> currentRow, StringBuilder currentField)
    {
        currentRow.Add(currentField.ToString());
        currentField.Clear();

        if (currentRow.Count == 1 && string.IsNullOrWhiteSpace(currentRow[0]))
        {
            currentRow.Clear();
            return;
        }

        rows.Add(currentRow.ToList());
        currentRow.Clear();
    }
}
