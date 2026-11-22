namespace Ascendra.Agents.Web.Infrastructure;

/// <summary>
/// Cambios incrementales de esquema. Para agregar una tabla o una columna en
/// el futuro sólo se añade una entrada con un identificador nuevo: PostgreSQL
/// la ejecuta una vez y la registra en application_schema_migrations.
/// </summary>
public sealed record DatabaseMigration(string Id, string Sql);

public static class DatabaseMigrations
{
    public static IReadOnlyList<DatabaseMigration> All { get; } =
    [
        new("2026-11-22-01-promesas-historial", """
            CREATE TABLE IF NOT EXISTS promesas_historial (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                id_promesa integer NOT NULL,
                accion varchar(30) NOT NULL,
                registrado_en timestamptz NOT NULL,
                registrado_por varchar(100) NOT NULL,
                datos_anteriores jsonb NOT NULL,
                datos_nuevos jsonb NULL
            );
            CREATE INDEX IF NOT EXISTS ix_promesas_historial_promesa
                ON promesas_historial (id_promesa, registrado_en DESC);
            """),
    ];
}
