namespace Ascendra.Agents.Infrastructure;

public static class OperationalImportSchema
{
    public const string MigrationId = "2026-11-27-02-operational-imports";
    public const string Sql = """
        CREATE TABLE IF NOT EXISTS crm_import_lotes (
            id_lote uuid PRIMARY KEY,
            conjunto text NULL,
            clave_origen text NULL,
            archivo_origen text NULL,
            hoja_origen text NULL,
            sha256 text NULL,
            version_lector integer NULL,
            filas integer NULL,
            columnas jsonb NULL,
            vigente boolean NULL,
            cargado_en timestamptz NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_import_lotes_huella
            ON crm_import_lotes (conjunto, clave_origen, sha256, version_lector);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_import_lotes_vigente
            ON crm_import_lotes (conjunto, clave_origen) WHERE vigente IS TRUE;
        """;
}
