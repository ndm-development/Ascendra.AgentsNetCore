using System.Data;
using System.Globalization;
using System.Text;
using ExcelDataReader;

namespace Ascendra.Agents.Web.Infrastructure;

internal sealed class WorkbookTable
{
    private WorkbookTable(IReadOnlyList<WorkbookRow> rows) => Rows = rows;

    public IReadOnlyList<WorkbookRow> Rows { get; }

    public static WorkbookTable Read(string path, string? sheetName = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration
        {
            ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = true },
        });
        var table = string.IsNullOrWhiteSpace(sheetName)
            ? dataSet.Tables.Cast<DataTable>().FirstOrDefault()
            : dataSet.Tables.Cast<DataTable>().FirstOrDefault(x => x.TableName.Equals(sheetName, StringComparison.OrdinalIgnoreCase));
        if (table is null) throw new InvalidDataException($"El archivo {Path.GetFileName(path)} no contiene una hoja utilizable.");

        var rows = table.Rows.Cast<DataRow>().Select(row => new WorkbookRow(
            table.Columns.Cast<DataColumn>().ToDictionary(
                column => TextNormalization.Header(column.ColumnName),
                column => row.IsNull(column) ? null : row[column],
                StringComparer.Ordinal))).ToArray();
        return new WorkbookTable(rows);
    }
}

internal sealed class WorkbookRow(IReadOnlyDictionary<string, object?> values)
{
    public string Text(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!values.TryGetValue(TextNormalization.Header(candidate), out var value) || value is null) continue;
            var text = value switch
            {
                DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            };
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return string.Empty;
    }

    public DateOnly? Date(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!values.TryGetValue(TextNormalization.Header(candidate), out var value) || value is null) continue;
            if (value is DateTime dateTime) return DateOnly.FromDateTime(dateTime);
            if (DateTime.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), new CultureInfo("es-MX"), DateTimeStyles.AllowWhiteSpaces, out var parsed))
                return DateOnly.FromDateTime(parsed);
        }
        return null;
    }

    public decimal Decimal(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!values.TryGetValue(TextNormalization.Header(candidate), out var value) || value is null) continue;
            if (value is decimal number) return number;
            if (value is double doubleNumber) return Convert.ToDecimal(doubleNumber);
            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
            text = text.Replace("$", string.Empty).Replace(",", string.Empty);
            if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            if (decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, new CultureInfo("es-MX"), out parsed)) return parsed;
        }
        return 0m;
    }

    public int? Integer(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var text = Text(candidate);
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue)) return decimal.ToInt32(decimal.Truncate(decimalValue));
        }
        return null;
    }

    public IReadOnlyList<string> TextValuesMatching(Func<string, bool> predicate) => values
        .Where(pair => predicate(pair.Key))
        .Select(pair => pair.Value is null ? string.Empty : Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .ToArray();
}
