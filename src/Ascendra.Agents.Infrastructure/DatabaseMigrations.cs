namespace Ascendra.Agents.Infrastructure;

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
        new("2026-11-22-02-recordatorios", """
            CREATE TABLE IF NOT EXISTS recordatorios (
                id uuid PRIMARY KEY,
                agente_id varchar(100) NOT NULL,
                agente varchar(100) NOT NULL,
                cliente_unico varchar(100) NOT NULL,
                nombre_cliente varchar(300) NOT NULL,
                numero_telefono varchar(100) NOT NULL,
                fecha_recordatorio timestamptz NOT NULL,
                nota varchar(1000) NOT NULL,
                creado_en timestamptz NOT NULL,
                notificado_en timestamptz NULL,
                cancelado_en timestamptz NULL
            );
            CREATE INDEX IF NOT EXISTS ix_recordatorios_agente_fecha
                ON recordatorios (agente_id, fecha_recordatorio);
            CREATE INDEX IF NOT EXISTS ix_recordatorios_pendientes
                ON recordatorios (fecha_recordatorio)
                WHERE notificado_en IS NULL AND cancelado_en IS NULL;
            """),
        new("2026-11-27-01-crm-agent-directory", """
            CREATE TABLE IF NOT EXISTS crm_agent_directory (
                id varchar(20) PRIMARY KEY,
                first_name text NOT NULL,
                paternal_surname text NOT NULL,
                maternal_surname text NOT NULL DEFAULT '',
                display_alias text NOT NULL DEFAULT '',
                username text NOT NULL DEFAULT '',
                role varchar(20) NOT NULL CHECK (role IN ('Agente', 'Supervisor', 'Gerente', 'Director')),
                is_active boolean NOT NULL,
                revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
                updated_at timestamptz NOT NULL DEFAULT now(),
                CHECK (btrim(id) <> '' AND btrim(first_name) <> '' AND btrim(paternal_surname) <> ''),
                CHECK (NOT is_active OR btrim(username) <> '')
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_agent_directory_id
                ON crm_agent_directory (lower(id));
            CREATE UNIQUE INDEX IF NOT EXISTS ux_crm_agent_directory_active_username
                ON crm_agent_directory (username) WHERE is_active;
            CREATE TABLE IF NOT EXISTS crm_agent_directory_history (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                agent_id varchar(20) NOT NULL,
                action text NOT NULL,
                recorded_at timestamptz NOT NULL DEFAULT now(),
                recorded_by text NOT NULL,
                previous_data jsonb NULL,
                new_data jsonb NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_crm_agent_directory_history_agent
                ON crm_agent_directory_history (agent_id, recorded_at DESC, id DESC);
            CREATE TABLE IF NOT EXISTS crm_agent_directory_imports (
                import_key text PRIMARY KEY,
                source_fingerprint text NOT NULL,
                people_count integer NOT NULL,
                imported_at timestamptz NOT NULL DEFAULT now()
            );
            """),
        new(OperationalImportSchema.MigrationId, OperationalImportSchema.Sql),
    ];
}
