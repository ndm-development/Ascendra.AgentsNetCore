using Ascendra.Agents.Web.Domain;
using ClosedXML.Excel;

namespace Ascendra.Agents.Web.Infrastructure;

/// <summary>Genera los Excel de consulta sin persistir información en el servidor.</summary>
public sealed class ExcelExportService
{
    public const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public byte[] Payments(IReadOnlyList<PaymentRecord> payments)
    {
        var headers = new[]
        {
            "Agente", "ID de agente", "Fecha de pago", "Cliente único", "Nombre del cliente",
            "Producto", "Pago recibido", "Semana", "Día de la semana", "Tipo de asignación", "Clasificación",
        };
        var rows = payments.Select(payment => new object?[]
        {
            payment.Agent, payment.AgentId, payment.ReceivedOn, payment.ClientId, payment.ClientName,
            payment.Product, payment.Recovery, payment.Week, payment.Day, payment.AssignmentType,
            TextNormalization.IsOrganic(payment.Agent) || TextNormalization.IsOrganic(payment.AssignmentType) ? "Orgánico" : "Equipo",
        });
        return Create("Pagos", headers, rows, moneyColumns: new HashSet<int> { 7 });
    }

    public byte[] Promises(IReadOnlyList<PromiseRecord> promises)
    {
        var headers = new[]
        {
            "Id de promesa", "Cliente único", "Nombre del cliente", "Teléfono contactado",
            "Agente", "ID de agente", "Fecha de promesa", "Monto inicial", "Monto semanal",
            "Tipo de promesa", "Número de semanas", "Fecha de creación", "Estatus",
        };
        var rows = promises.Select(promise => new object?[]
        {
            promise.Id, promise.ClientId, promise.ClientName, promise.ClientNumber,
            promise.Agent, promise.AgentId, promise.PromiseDate, promise.InitialAmount,
            promise.WeeklyAmount, promise.Type, promise.NumberOfWeeks, promise.CreatedOn, promise.Status,
        });
        return Create("Promesas", headers, rows, moneyColumns: new HashSet<int> { 8, 9 });
    }

    /// <summary>Exportación de seguimiento para la tabla PostgreSQL de Gerencia.</summary>
    public byte[] DatabasePromises(IReadOnlyList<DatabasePromiseEntry> promises)
    {
        var headers = new[]
        {
            "Id de promesa", "Cliente único", "Nombre del cliente", "Teléfono contactado",
            "Agente", "ID de agente", "Fecha de promesa", "Monto inicial", "Monto semanal",
            "Tipo de promesa", "Número de semanas", "Fecha de creación", "Estatus", "Origen",
            "Modificado por", "Fecha de modificación",
        };
        var rows = promises.Select(promise => new object?[]
        {
            promise.IdPromise, promise.ClientId, promise.ClientName, promise.ClientNumber,
            promise.AgentName, promise.AgentId, promise.PromiseDate, promise.InitialAmount,
            promise.WeeklyAmount, promise.PromiseType, promise.NumberOfWeeks, promise.CreatedAt,
            promise.Status, promise.Origin, promise.ModifiedBy, promise.ModifiedAt,
        });
        return Create("Promesas PostgreSQL", headers, rows, moneyColumns: new HashSet<int> { 8, 9 });
    }

    /// <summary>
    /// Fuente para el cruce externo de pagos. IdPromesa_Ascendra debe volver
    /// intacto en promesas_estatus.xlsx; no sustituye el IdPromesa que use el
    /// proceso de cruce, sólo identifica de forma inequívoca su origen.
    /// </summary>
    public byte[] PendingPromiseRegistrations(IReadOnlyList<PromiseRegistrationEntry> promises)
    {
        var headers = new[]
        {
            "IdPromesa_Ascendra", "Cliente_Unico", "Nombre_Cliente", "Agente", "Agente_ID",
            "Numero_Cliente", "Fecha_Promesa", "Monto_Inicial", "Monto_Semanal",
            "Tipo_Promesa", "Numero_Semanas", "Fecha_Creacion", "UsuAlta",
        };
        var rows = promises.Select(promise => new object?[]
        {
            promise.IdPromise, promise.ClientId, promise.ClientName, promise.AgentName,
            promise.CreatedByAgentId, promise.ClientNumber, promise.PromiseDate,
            promise.InitialAmount, promise.WeeklyAmount, promise.PromiseType,
            promise.NumberOfWeeks, promise.CreatedAt, promise.CreatedByAgentId,
        });
        return Create("Promesas Ascendra", headers, rows, moneyColumns: new HashSet<int> { 8, 9 });
    }

    /// <summary>
    /// Fuente completa que sustituye la consulta a la base anterior. La
    /// columna IdPromesa_Ascendra sólo identifica registros creados en la
    /// aplicación y debe conservarse hasta el archivo de estatus final.
    /// </summary>
    public byte[] PromiseCrossSource(IReadOnlyList<PromiseCrossSourceEntry> promises)
    {
        var headers = new[]
        {
            "IdPromesa", "IdPromesa_Ascendra", "Cliente_Unico", "Numero_Cliente",
            "Agente", "Agente_ID", "Fecha_Promesa", "Monto_Inicial",
            "Monto_Semanal", "Tipo_Promesa", "Numero_Semanas", "Fecha_Creacion",
        };
        var rows = promises.Select(promise => new object?[]
        {
            promise.PromiseId, promise.AscendraRegistrationId, promise.ClientId,
            promise.ClientNumber, promise.AgentName, promise.AgentId,
            promise.PromiseDate, promise.InitialAmount, promise.WeeklyAmount,
            promise.PromiseType, promise.NumberOfWeeks, promise.CreatedAt,
        });
        return Create("Promesas para cruce", headers, rows, moneyColumns: new HashSet<int> { 8, 9 });
    }

    public byte[] Coding(IReadOnlyList<ClientDispositionSummary> summaries)
    {
        var headers = new[]
        {
            "Cliente único", "Fecha", "ID agente", "Agente", "Tipificación", "Canal", "Teléfono",
        };
        var rows = summaries.Select(summary => new object?[]
        {
            summary.ClientId,
            OperationalClock.Format(summary.RecordedAt, "dd/MM/yyyy HH:mm"),
            summary.AgentId,
            summary.AgentName,
            $"{summary.Category} · {summary.Result}",
            summary.Channel,
            summary.Phone,
        });
        return Create("Codificación", headers, rows, moneyColumns: new HashSet<int>());
    }

    private static byte[] Create(
        string sheetName,
        IReadOnlyList<string> headers,
        IEnumerable<object?[]> rows,
        IReadOnlySet<int> moneyColumns)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(sheetName);

        for (var column = 0; column < headers.Count; column++)
        {
            worksheet.Cell(1, column + 1).Value = headers[column];
        }

        var rowNumber = 2;
        foreach (var row in rows)
        {
            for (var column = 0; column < row.Length; column++)
            {
                var cell = worksheet.Cell(rowNumber, column + 1);
                SetValue(cell, row[column]);
                if (moneyColumns.Contains(column + 1)) cell.Style.NumberFormat.Format = "$#,##0.00";
            }
            rowNumber++;
        }

        var header = worksheet.Range(1, 1, 1, headers.Count);
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("1D5FBF");
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        worksheet.SheetView.FreezeRows(1);
        worksheet.Range(1, 1, Math.Max(1, rowNumber - 1), headers.Count).SetAutoFilter();

        for (var column = 1; column <= headers.Count; column++)
        {
            var worksheetColumn = worksheet.Column(column);
            worksheetColumn.AdjustToContents();
            worksheetColumn.Width = Math.Clamp(worksheetColumn.Width + 1d, 12d, 42d);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void SetValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                cell.Value = string.Empty;
                break;
            case DateOnly date:
                cell.Value = date.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "dd/MM/yyyy";
                break;
            case DateTimeOffset timestamp:
                cell.Value = timestamp.LocalDateTime;
                cell.Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
                break;
            case decimal amount:
                cell.Value = amount;
                break;
            case int number:
                cell.Value = number;
                break;
            default:
                cell.Value = value.ToString() ?? string.Empty;
                break;
        }
    }
}
