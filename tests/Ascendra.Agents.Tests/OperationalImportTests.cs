using Ascendra.Agents.Application;
using Ascendra.Agents.Domain;
using Ascendra.Agents.Infrastructure;
using ClosedXML.Excel;
using Npgsql;

namespace Ascendra.Agents.Tests;

public sealed class OperationalImportTests
{
    [Fact]
    public void UnformattedExcelDatesAreImportedAndPlaceholderDatesRemainNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ascendra-dates-{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Datos");
                sheet.Cell(1, 1).Value = "Cliente_Unico"; sheet.Cell(1, 2).Value = "Fecha_Inicio";
                sheet.Cell(2, 1).Value = "001"; sheet.Cell(2, 2).Value = new DateTime(2026, 11, 27).ToOADate();
                sheet.Cell(3, 1).Value = "002"; sheet.Cell(3, 2).Value = "01/01/0001";
                workbook.SaveAs(path);
            }
            var batch = Assert.Single(new OperationalWorkbookReader().Read("demograficos", path));
            Assert.Equal(new DateTime(2026, 11, 27), batch.Rows[0].Values[1]);
            Assert.Null(batch.Rows[1].Values[1]);
            Assert.Contains("01/01/0001", batch.Rows[1].Issues);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PaymentReaderPreservesYearWeekNullsAndRepeatedPayments()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ascendra-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "Semana53.xlsx");
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Pagos");
                string[] headers = ["Año", "Semana", "Cliente_Unico", "Fecha_Recepción", "Recuperación_por_Gestión", "DIQUE", "DIQUE"];
                for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
                for (var r = 2; r <= 3; r++)
                {
                    sheet.Cell(r, 1).Value = 2020; sheet.Cell(r, 2).Value = 53;
                    sheet.Cell(r, 3).Value = "00123"; sheet.Cell(r, 4).Value = new DateTime(2021, 1, 1);
                    sheet.Cell(r, 5).Value = 100.25m;
                }
                sheet.Cell(4, 1).Value = 2021; sheet.Cell(4, 3).Value = "OTRO";
                sheet.Cell(4, 5).Value = "pendiente";
                sheet.Cell(4, 6).Value = "uno"; sheet.Cell(4, 7).Value = "dos";
                workbook.SaveAs(path);
            }
            var batches = new OperationalWorkbookReader().Read("pagos", path);
            Assert.Equal(2, batches.Count);
            var first = batches.Single(batch => batch.SourceKey == "pagos/2020/semana-53");
            Assert.Equal(2, first.Rows.Count);
            object? Get(DataImportBatch batch, int row, string name) => batch.Rows[row].Values[batch.Columns.ToList().FindIndex(column => column.Name == name)];
            Assert.Equal("00123", Get(first, 0, "cliente_unico"));
            Assert.Equal(2020, Get(first, 0, "anio_iso"));
            Assert.Equal(53, Get(first, 0, "semana_iso"));
            Assert.Equal(53, Get(first, 0, "semana_archivo"));
            Assert.Equal(100.25m, Get(first, 0, "recuperacion_por_gestion"));
            var second = batches.Single(batch => batch.SourceKey == "pagos/2021/semana-53");
            Assert.Null(Get(second, 0, "recuperacion_por_gestion"));
            Assert.Null(Get(second, 0, "fecha_recepcion"));
            Assert.Equal(53, Get(second, 0, "semana"));
            Assert.Contains("pendiente", second.Rows[0].Issues);
            Assert.Equal("uno", Get(second, 0, "dique")); Assert.Equal("dos", Get(second, 0, "dique_2"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [PostgresFact]
    public async Task RepeatedAndConcurrentLoadsDoNotDuplicateLegitimateRepeatedPayments()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var dataSource = NpgsqlDataSource.Create(test.ConnectionString);
        var service = new OperationalImportService(new PostgresOperationalImportRepository(dataSource));
        var results = await Task.WhenAll(service.ImportAsync(Batch()), service.ImportAsync(Batch()));
        Assert.Single(results, result => result.Imported);
        Assert.Equal(2L, await Scalar(dataSource, "SELECT count(*) FROM crm_pagos_vigentes"));
        Assert.Equal(200.50m, await Scalar(dataSource, "SELECT sum(recuperacion_por_gestion) FROM crm_pagos_vigentes"));
        Assert.Equal(1L, await Scalar(dataSource, "SELECT count(*) FROM crm_import_lotes"));
    }

    [PostgresFact]
    public async Task RevisedFilePublishesAtomicallyAndOldFileCannotBecomeCurrentAgain()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var dataSource = NpgsqlDataSource.Create(test.ConnectionString);
        var service = new OperationalImportService(new PostgresOperationalImportRepository(dataSource));
        await service.ImportAsync(Batch());
        var revised = Batch() with { Fingerprint = "revision-2", Rows = [new(2, ["001", 2026, 48, 20m])] };
        await service.ImportAsync(revised);
        Assert.Equal(3L, await Scalar(dataSource, "SELECT count(*) FROM crm_pagos"));
        Assert.Equal(1L, await Scalar(dataSource, "SELECT count(*) FROM crm_pagos_vigentes"));
        Assert.Equal(20m, await Scalar(dataSource, "SELECT sum(recuperacion_por_gestion) FROM crm_pagos_vigentes"));
        var old = await service.ImportAsync(Batch());
        Assert.False(old.Imported); Assert.False(old.Current);
        Assert.Equal(20m, await Scalar(dataSource, "SELECT sum(recuperacion_por_gestion) FROM crm_pagos_vigentes"));
    }

    [PostgresFact]
    public async Task AllNewColumnsExceptPrimaryKeysAcceptNullAndBusinessNullsStayNull()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var dataSource = NpgsqlDataSource.Create(test.ConnectionString);
        var service = new OperationalImportService(new PostgresOperationalImportRepository(dataSource));
        await service.ImportAsync(Batch() with { Rows = [new(2, [null, null, null, null])] });
        Assert.Equal(0L, await Scalar(dataSource, "SELECT count(*) FROM information_schema.columns WHERE table_schema='public' AND table_name IN ('crm_pagos','crm_import_lotes') AND is_nullable='NO' AND column_name NOT IN ('_id','id_lote')"));
        Assert.Equal(1L, await Scalar(dataSource, "SELECT count(*) FROM crm_pagos WHERE cliente_unico IS NULL AND recuperacion_por_gestion IS NULL AND anio IS NULL AND semana IS NULL"));
        await using var command = dataSource.CreateCommand("INSERT INTO crm_pagos DEFAULT VALUES;");
        await command.ExecuteNonQueryAsync();
        Assert.Equal(2L, await Scalar(dataSource, "SELECT count(*) FROM crm_pagos"));
    }

    [PostgresFact]
    public async Task CopyFailureRollsBackBatchAndKeepsPreviousPublishedData()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var dataSource = NpgsqlDataSource.Create(test.ConnectionString);
        var service = new OperationalImportService(new PostgresOperationalImportRepository(dataSource));
        await service.ImportAsync(Batch());
        await using (var command = dataSource.CreateCommand("ALTER TABLE crm_pagos ADD CONSTRAINT simulated_copy_failure CHECK (_fila_origen <> 999)"))
            await command.ExecuteNonQueryAsync();
        var bad = Batch() with { Fingerprint = "failed-copy", Rows = [new(999, ["001", 2026, 48, 20m])] };
        await Assert.ThrowsAsync<PostgresException>(() => service.ImportAsync(bad));
        Assert.Equal(1L, await Scalar(dataSource, "SELECT count(*) FROM crm_import_lotes"));
        Assert.Equal(2L, await Scalar(dataSource, "SELECT count(*) FROM crm_pagos_vigentes"));
    }

    [PostgresFact]
    public async Task AddingAnOptionalSourceColumnKeepsExistingRowsAndViewValid()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var dataSource = NpgsqlDataSource.Create(test.ConnectionString);
        var service = new OperationalImportService(new PostgresOperationalImportRepository(dataSource));
        await service.ImportAsync(Batch());
        var updated = Batch() with { Fingerprint = "new-column",
            Columns = Batch().Columns.Append(new("producto", "Producto", ImportValueType.Text)).ToArray(),
            Rows = [new(2, ["001", 2026, 48, 20m, "Producto de prueba"])] };
        await service.ImportAsync(updated);
        Assert.Equal("Producto de prueba", await Scalar(dataSource, "SELECT producto FROM crm_pagos_vigentes"));
        Assert.Equal(2L, await Scalar(dataSource, "SELECT count(*) FROM crm_pagos WHERE producto IS NULL"));
    }

    [PostgresFact]
    public async Task ProcessingPublishesOneAtomicDatabaseCutAndAuditsChangedPromiseStatus()
    {
        await using var test = await TestDatabase.CreateAsync();
        await using var source = NpgsqlDataSource.Create(test.ConnectionString);
        // Inicializa esquema CRM y nuevas tablas de resultado antes de cargar
        // las fuentes crudas, igual que ocurrirá en la aplicación.
        await using (var initialized = await test.Database.OpenConnectionAsync()) { }
        var imports = new OperationalImportService(new PostgresOperationalImportRepository(source));
        await imports.ImportAsync(new("pagos", "pagos/2026/semana-32", "Semana32.xlsx", "Segmento", "payments-v1",
            [new("cliente_unico", "Cliente_Unico", ImportValueType.Text), new("fecha_recepcion", "Fecha_Recepción", ImportValueType.Timestamp),
             new("recuperacion_por_gestion", "Recuperación_por_Gestión", ImportValueType.Decimal), new("semana", "Semana", ImportValueType.Integer)],
            [new(2, ["001", new DateTime(2026, 8, 3), 100m, 32]) ]));
        await imports.ImportAsync(new("cartera", "cartera", "Descarga_cartera.xlsx", "Cartera", "portfolio-v1",
            [new("cliente_unico", "CLIENTE_UNICO", ImportValueType.Text), new("nombre_cte", "NOMBRE_CTE", ImportValueType.Text),
             new("producto", "PRODUCTO", ImportValueType.Text), new("saldo_total", "SALDO_TOTAL", ImportValueType.Decimal),
             new("telefono1", "TELEFONO1", ImportValueType.Text)],
            [new(2, ["001", "Cliente DB", "Producto", 99m, "5511112222"]) ]));
        await imports.ImportAsync(new("referencias", "referencias", "Referencias.xlsx", "Dashboard", "references-v1",
            [new("cliente_unico", "CLIENTE_UNICO", ImportValueType.Text), new("telefono_1", "Telefono 1", ImportValueType.Text),
             new("referencia", "Referencia", ImportValueType.Text), new("parentesco", "Parentesco", ImportValueType.Text)],
            [new(2, ["001", "5533334444", "Referencia", "Amiga"]) ]));
        await imports.ImportAsync(new("catalogo_agentes", "catalogo_agentes", "catalogo.xlsx", "Agentes", "agents-v1",
            [new("agente_id", "Agente_ID", ImportValueType.Text), new("agente", "Agente", ImportValueType.Text)],
            [new(2, ["10", "Alicia López García"]) ]));
        await using (var connection = await test.Database.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("""
            INSERT INTO promesas (cliente_unico, agente, numero_cliente, fecha_promesa, monto_inicial, monto_semanal,
                tipo_promesa, numero_semanas, fecha_creacion, usu_alta, estatus)
            VALUES ('001', 'Alias anterior', '5511112222', '2026-08-03', 1, 1, 'Parcial', 1,
                '2026-08-01 15:00:00+00', '10', 'Pendiente de pago');
            """, connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var processing = new OperationalProcessingService(new PostgresOperationalProcessingRepository(test.Database));
        var first = await processing.ProcessAsync(new(2026, 8, 10));
        var second = await processing.ProcessAsync(new(2026, 8, 10));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Vigente", Assert.Single(first.Result.Payments).AssignmentType);
        Assert.Equal("Activa", Assert.Single(first.Result.Promises).Status);
        Assert.Equal("Activa", await Scalar(source, "SELECT estatus FROM promesas WHERE id_promesa=1"));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM promesas_historial WHERE accion='Conciliación automática'"));
        Assert.Equal(2L, await Scalar(source, "SELECT count(*) FROM crm_proceso_contactos_vigentes"));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM crm_proceso_pagos_vigentes"));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM crm_proceso_promesas_vigentes"));

        // El segundo corte ya no contiene 001. Sus teléfonos/referencia no se
        // borran, pero el cliente deja de estar activo; la cartera anterior
        // sigue disponible por ejecución para saldo/producto/zona históricos.
        await imports.ImportAsync(new("cartera", "cartera", "Descarga_cartera.xlsx", "Cartera", "portfolio-v2",
            [new("cliente_unico", "CLIENTE_UNICO", ImportValueType.Text), new("nombre_cte", "NOMBRE_CTE", ImportValueType.Text),
             new("producto", "PRODUCTO", ImportValueType.Text), new("saldo_total", "SALDO_TOTAL", ImportValueType.Decimal),
             new("telefono1", "TELEFONO1", ImportValueType.Text)],
            [new(2, ["002", "Cliente nuevo", "Producto nuevo", 77m, "5544444444"]) ]));
        await processing.ProcessAsync(new(2026, 8, 10));
        Assert.False((bool)(await Scalar(source, "SELECT activo FROM crm_clientes_operativos WHERE cliente_unico='001'"))!);
        Assert.Equal(2L, await Scalar(source, "SELECT count(*) FROM crm_contactos_operativos WHERE cliente_unico='001'"));
        Assert.Equal(1L, await Scalar(source, "SELECT count(*) FROM crm_proceso_cartera WHERE cliente_unico='001'"));
    }

    private static DataImportBatch Batch() => new("pagos", "pagos/2026/semana-48", "Semana48.xlsx", "Segmento", "source-hash",
        [new("cliente_unico", "Cliente_Unico", ImportValueType.Text), new("anio", "Año", ImportValueType.Integer),
            new("semana", "Semana", ImportValueType.Integer), new("recuperacion_por_gestion", "Recuperación_por_Gestión", ImportValueType.Decimal)],
        [new(2, ["001", 2026, 48, 100.25m]), new(3, ["001", 2026, 48, 100.25m])]);

    private static async Task<object?> Scalar(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }
}
