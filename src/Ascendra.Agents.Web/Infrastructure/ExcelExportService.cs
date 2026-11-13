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
            "ID de agente", "Fecha de pago", "Cliente único", "Nombre del cliente",
            "Producto", "Pago recibido", "Semana", "Día de la semana",
        };
        var rows = payments.Select(payment => new object?[]
        {
            payment.AgentId, payment.ReceivedOn, payment.ClientId, payment.ClientName,
            payment.Product, payment.Recovery, payment.Week, payment.Day,
        });
        return Create("Pagos", headers, rows, moneyColumns: new HashSet<int> { 6 });
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
