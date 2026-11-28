using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace Ascendra.Agents.Infrastructure;

public sealed class OperationalWorkbookReader
{
    private static readonly HashSet<string> DecimalColumns = new(StringComparer.Ordinal)
    {
        "recuperacion_por_gestion", "saldo", "saldo_total", "saldo_atrasado", "saldo_requerido", "moratorios",
        "pago_normal", "imp_ultimo_pago", "cargos_automaticos", "captacion", "cobranza_total", "porcentaje_comision",
        "monto_comision", "monto_inicial", "monto_semanal", "monto_plan", "monto_abonado", "monto_pagos",
        "abono_semanal", "pago_inicial", "pago_semanal", "monto_promesa_pago", "saldo_antes_del_plan"
    };

    public IReadOnlyList<DataImportBatch> Read(string dataset, string path, string? sheetName = null, string? sourceKey = null)
    {
        OperationalDatasets.Table(dataset);
        var bytes = File.ReadAllBytes(path);
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        if (sheetName is not null)
        {
            while (!reader.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase))
                if (!reader.NextResult()) throw new InvalidDataException($"No se encontró la hoja {sheetName} en {Path.GetFileName(path)}.");
        }
        var selectedSheet = reader.Name;
        if (!reader.Read()) throw new InvalidDataException("La hoja no contiene encabezados.");
        var sourceColumnCount = reader.FieldCount;
        var used = new HashSet<string>(StringComparer.Ordinal);
        var columns = new List<ImportColumn>();
        for (var index = 0; index < sourceColumnCount; index++)
        {
            var original = Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty;
            var name = ColumnName(original, index);
            var baseName = name;
            var suffix = 2;
            while (!used.Add(name)) name = $"{baseName}_{suffix++}";
            columns.Add(new(name, original, ValueType(name)));
        }
        int? fileWeek = null;
        if (dataset == "pagos")
        {
            var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"^Semana[ _-]*(\d{1,2})$", RegexOptions.IgnoreCase);
            if (match.Success) fileWeek = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            foreach (var name in new[] { "anio", "semana", "semana_archivo", "anio_iso", "semana_iso" })
                if (used.Add(name)) columns.Add(new(name, "Derivada de la procedencia/fecha", ImportValueType.Integer));
        }
        var rows = new List<ImportRow>();
        var rowNumber = 1;
        while (reader.Read())
        {
            rowNumber++;
            var raw = Enumerable.Range(0, sourceColumnCount).Select(reader.GetValue).ToArray();
            if (raw.All(value => value is null or DBNull || value is string text && string.IsNullOrWhiteSpace(text))) continue;
            var values = new object?[columns.Count];
            var issues = new Dictionary<string, object?>();
            for (var index = 0; index < sourceColumnCount; index++)
            {
                values[index] = ConvertValue(raw[index], columns[index].Type);
                if (values[index] is null && raw[index] is not null and not DBNull
                    && !string.IsNullOrWhiteSpace(Convert.ToString(raw[index], CultureInfo.InvariantCulture)))
                    issues[columns[index].Name] = raw[index];
            }
            if (dataset == "pagos")
            {
                int Index(string name) => columns.FindIndex(column => column.Name == name);
                values[Index("semana_archivo")] = fileWeek;
                values[Index("semana")] ??= fileWeek;
                var dateIndex = Index("fecha_recepcion");
                if (dateIndex >= 0 && values[dateIndex] is DateTime date)
                {
                    values[Index("anio_iso")] = ISOWeek.GetYear(date);
                    values[Index("semana_iso")] = ISOWeek.GetWeekOfYear(date);
                }
                if (fileWeek is not null && values[Index("semana")] is int week && week != fileWeek)
                    issues["semana_distinta_del_archivo"] = week;
            }
            rows.Add(new(rowNumber, values, issues.Count == 0 ? null : JsonSerializer.Serialize(issues)));
        }
        if (dataset != "pagos")
            return [new(dataset, sourceKey ?? dataset, Path.GetFileName(path), selectedSheet, fingerprint, columns, rows)];

        // La misma semana de otro año es otra fuente. No se reemplaza el año anterior.
        var yearIndex = columns.FindIndex(column => column.Name == "anio");
        var weekIndex = columns.FindIndex(column => column.Name == "semana");
        return rows.GroupBy(row => new { Year = row.Values[yearIndex], Week = fileWeek ?? row.Values[weekIndex] })
            .Select(group => new DataImportBatch(dataset,
                $"pagos/{group.Key.Year ?? "sin-anio"}/semana-{group.Key.Week ?? "sin-semana"}",
                Path.GetFileName(path), selectedSheet, fingerprint, columns, group.ToArray())).ToArray();
    }

    private static string ColumnName(string source, int index)
    {
        var name = Regex.Replace(IdentityNormalization.Normalize(source), "[^a-z0-9]+", "_").Trim('_');
        if (name == "ano") name = "anio";
        if (name.Length == 0) name = $"columna_{index + 1}";
        if (char.IsDigit(name[0])) name = "campo_" + name;
        return name[..Math.Min(name.Length, 48)];
    }

    private static ImportValueType ValueType(string name) => name switch
    {
        "anio" or "semana" => ImportValueType.Integer,
        _ when DecimalColumns.Contains(name) => ImportValueType.Decimal,
        _ when name.StartsWith("fecha_", StringComparison.Ordinal) || name == "fecmod" => ImportValueType.Timestamp,
        _ => ImportValueType.Text
    };

    private static object? ConvertValue(object? raw, ImportValueType type)
    {
        if (raw is null or DBNull) return null;
        var text = (Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (type == ImportValueType.Text)
            return raw is DateTime textDate ? textDate.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture) : text;
        if (type == ImportValueType.Timestamp)
        {
            if (raw is DateTime date) return date == DateTime.MinValue ? null : DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            // Algunos proveedores entregan fechas como números de serie Excel sin formato de celda.
            if (raw is double or float or decimal or int or long)
            {
                var serial = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                if (serial > 0 && serial < 2958466) return DateTime.FromOADate(serial);
            }
            if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("es-MX"), DateTimeStyles.AllowWhiteSpaces, out date))
                return date == DateTime.MinValue ? null : DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            return null;
        }
        if (!decimal.TryParse(text.Replace("$", "").Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        if (type == ImportValueType.Decimal) return number;
        return number == decimal.Truncate(number) && number is >= int.MinValue and <= int.MaxValue ? (object)(int)number : null;
    }
}
