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
        new("2026-11-27-03-operational-processing", """
            CREATE TABLE IF NOT EXISTS crm_proceso_ejecuciones (
                id_ejecucion uuid PRIMARY KEY,
                huella_fuentes text NOT NULL UNIQUE,
                version_reglas text NOT NULL,
                terminado_en timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS crm_proceso_actual (
                id boolean PRIMARY KEY DEFAULT true CHECK (id),
                id_ejecucion uuid NOT NULL REFERENCES crm_proceso_ejecuciones(id_ejecucion),
                actualizado_en timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS crm_proceso_contactos (
                id_ejecucion uuid NOT NULL REFERENCES crm_proceso_ejecuciones(id_ejecucion),
                posicion integer NOT NULL,
                cliente_unico text NULL,
                telefono text NULL,
                nombre_referencia text NULL,
                parentesco text NULL,
                nombre_cliente text NULL,
                producto text NULL,
                saldo_total numeric NULL,
                estatus text NULL,
                origen text NULL,
                PRIMARY KEY (id_ejecucion, posicion)
            );
            CREATE TABLE IF NOT EXISTS crm_proceso_cartera (
                id_ejecucion uuid NOT NULL REFERENCES crm_proceso_ejecuciones(id_ejecucion),
                posicion integer NOT NULL,
                cliente_unico text NULL,
                nombre_cliente text NULL,
                producto text NULL,
                saldo_total numeric NULL,
                estatus text NULL,
                dia_pago text NULL,
                zona_geografica text NULL,
                telefonos jsonb NULL,
                PRIMARY KEY (id_ejecucion, posicion)
            );
            CREATE TABLE IF NOT EXISTS crm_proceso_pagos (
                id_ejecucion uuid NOT NULL REFERENCES crm_proceso_ejecuciones(id_ejecucion),
                id_pago_origen bigint NOT NULL,
                cliente_unico text NULL,
                fecha_recepcion date NULL,
                recuperacion_por_gestion numeric NULL,
                semana integer NULL,
                producto text NULL,
                concepto_calculo text NULL,
                plan_de_pago text NULL,
                zona_geografica text NULL,
                agente text NULL,
                agente_id text NULL,
                nombre_cliente text NULL,
                dia_semana text NULL,
                id_promesa_asignada text NULL,
                id_promesa_base integer NULL,
                tipo_asignacion text NULL,
                PRIMARY KEY (id_ejecucion, id_pago_origen)
            );
            CREATE TABLE IF NOT EXISTS crm_proceso_promesas (
                id_ejecucion uuid NOT NULL REFERENCES crm_proceso_ejecuciones(id_ejecucion),
                id_promesa_base integer NOT NULL,
                id_promesa text NULL,
                id_promesa_ascendra integer NULL,
                cliente_unico text NULL,
                nombre_cliente text NULL,
                numero_cliente text NULL,
                agente text NULL,
                agente_id text NULL,
                fecha_promesa date NULL,
                monto_inicial numeric NULL,
                monto_semanal numeric NULL,
                tipo_promesa text NULL,
                numero_semanas integer NULL,
                fecha_creacion timestamp without time zone NULL,
                estatus text NULL,
                PRIMARY KEY (id_ejecucion, id_promesa_base)
            );
            -- Estado actual por cliente. El corte diario es la única fuente
            -- de actividad; una ausencia desactiva, pero jamás elimina.
            CREATE TABLE IF NOT EXISTS crm_clientes_operativos (
                cliente_unico text PRIMARY KEY,
                activo boolean NOT NULL,
                id_ejecucion_actual uuid NULL REFERENCES crm_proceso_ejecuciones(id_ejecucion),
                primera_visto_en timestamptz NOT NULL DEFAULT now(),
                actualizado_en timestamptz NOT NULL DEFAULT now()
            );
            -- Libreta acumulativa: referencias cargadas y teléfonos que en
            -- algún corte vinieron en cartera. No depende de seguir activo.
            CREATE TABLE IF NOT EXISTS crm_contactos_operativos (
                id_contacto bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                clave_contacto text NOT NULL UNIQUE,
                cliente_unico text NOT NULL,
                telefono text NOT NULL,
                nombre_referencia text NOT NULL DEFAULT '',
                parentesco text NOT NULL DEFAULT '',
                origen text NOT NULL,
                primera_ejecucion uuid NOT NULL REFERENCES crm_proceso_ejecuciones(id_ejecucion),
                ultima_ejecucion uuid NOT NULL REFERENCES crm_proceso_ejecuciones(id_ejecucion),
                primera_visto_en timestamptz NOT NULL DEFAULT now(),
                ultimo_visto_en timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS ix_crm_proceso_contactos_cliente
                ON crm_proceso_contactos (id_ejecucion, cliente_unico);
            CREATE INDEX IF NOT EXISTS ix_crm_proceso_cartera_cliente
                ON crm_proceso_cartera (id_ejecucion, cliente_unico);
            CREATE INDEX IF NOT EXISTS ix_crm_proceso_pagos_agente
                ON crm_proceso_pagos (id_ejecucion, agente_id, fecha_recepcion);
            CREATE INDEX IF NOT EXISTS ix_crm_proceso_promesas_agente
                ON crm_proceso_promesas (id_ejecucion, agente_id, fecha_promesa);
            CREATE INDEX IF NOT EXISTS ix_crm_clientes_operativos_activo
                ON crm_clientes_operativos (activo, cliente_unico);
            CREATE INDEX IF NOT EXISTS ix_crm_contactos_operativos_cliente_telefono
                ON crm_contactos_operativos (cliente_unico, telefono);
            CREATE OR REPLACE VIEW crm_proceso_contactos_vigentes AS
                SELECT fila.* FROM crm_proceso_contactos fila
                JOIN crm_proceso_actual actual ON actual.id_ejecucion = fila.id_ejecucion;
            CREATE OR REPLACE VIEW crm_proceso_cartera_vigente AS
                SELECT fila.* FROM crm_proceso_cartera fila
                JOIN crm_proceso_actual actual ON actual.id_ejecucion = fila.id_ejecucion;
            CREATE OR REPLACE VIEW crm_proceso_pagos_vigentes AS
                SELECT fila.* FROM crm_proceso_pagos fila
                JOIN crm_proceso_actual actual ON actual.id_ejecucion = fila.id_ejecucion;
            CREATE OR REPLACE VIEW crm_proceso_promesas_vigentes AS
                SELECT fila.* FROM crm_proceso_promesas fila
                JOIN crm_proceso_actual actual ON actual.id_ejecucion = fila.id_ejecucion;
            """),
    ];
}
