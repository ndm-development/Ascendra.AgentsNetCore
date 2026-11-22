using Npgsql;

namespace Ascendra.Agents.Web.Infrastructure;

/// <summary>
/// Punto único de acceso a PostgreSQL. Prepara el esquema antes de que los
/// servicios operativos lean o escriban datos.
/// </summary>
public sealed class PostgresDatabase(
    IConfiguration configuration,
    ILogger<PostgresDatabase> logger) : IHostedService, IAsyncDisposable
{
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private NpgsqlDataSource? _dataSource;

    public async Task StartAsync(CancellationToken cancellationToken) =>
        await EnsureInitializedAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        return await _dataSource!.OpenConnectionAsync(cancellationToken);
    }

    public async Task<bool> HasCompletedImportAsync(string importKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1 FROM application_imports WHERE import_key = @key", connection);
        command.Parameters.AddWithValue("key", importKey);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task MarkImportCompletedAsync(string importKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO application_imports (import_key, completed_at)
            VALUES (@key, now())
            ON CONFLICT (import_key) DO NOTHING;
            """, connection);
        command.Parameters.AddWithValue("key", importKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_dataSource is not null) return;

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_dataSource is not null) return;

            var rawConnectionString = configuration.GetConnectionString("Ascendra")
                ?? configuration["Postgres:ConnectionString"];
            if (string.IsNullOrWhiteSpace(rawConnectionString))
            {
                throw new InvalidOperationException("Falta ConnectionStrings__Ascendra para conectar PostgreSQL.");
            }

            var builder = BuildConnectionString(rawConnectionString, configuration.GetValue<int?>("Postgres:MaxPoolSize") ?? 30);
            var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                await using var command = new NpgsqlCommand(SchemaSql, connection);
                await command.ExecuteNonQueryAsync(cancellationToken);
                _dataSource = dataSource;
                logger.LogInformation("PostgreSQL de Ascendra está disponible con un máximo de {MaxPoolSize} conexiones agrupadas.", builder.MaxPoolSize);
            }
            catch
            {
                await dataSource.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private static NpgsqlConnectionStringBuilder BuildConnectionString(string rawConnectionString, int requestedPoolSize)
    {
        NpgsqlConnectionStringBuilder builder;
        if (Uri.TryCreate(rawConnectionString, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals("postgres", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("postgresql", StringComparison.OrdinalIgnoreCase)))
        {
            var separator = uri.UserInfo.IndexOf(':');
            var username = separator < 0 ? uri.UserInfo : uri.UserInfo[..separator];
            var password = separator < 0 ? string.Empty : uri.UserInfo[(separator + 1)..];
            builder = new NpgsqlConnectionStringBuilder
            {
                Host = uri.Host,
                Port = uri.IsDefaultPort ? 5432 : uri.Port,
                Database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')),
                Username = Uri.UnescapeDataString(username),
                Password = Uri.UnescapeDataString(password),
            };

            foreach (var segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = segment.Split('=', 2);
                if (pair.Length != 2 || !pair[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase)) continue;
                if (Enum.TryParse<SslMode>(Uri.UnescapeDataString(pair[1]), ignoreCase: true, out var sslMode)) builder.SslMode = sslMode;
            }
        }
        else
        {
            builder = new NpgsqlConnectionStringBuilder(rawConnectionString);
        }

        builder.ApplicationName = "ascendra-agents";
        builder.MaxPoolSize = Math.Clamp(requestedPoolSize, 30, 50);
        builder.MinPoolSize = 0;
        builder.Timeout = 15;
        builder.CommandTimeout = 30;
        return builder;
    }

    public async ValueTask DisposeAsync()
    {
        _initializationGate.Dispose();
        if (_dataSource is not null) await _dataSource.DisposeAsync();
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS application_imports (
            import_key text PRIMARY KEY,
            completed_at timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS chat_messages (
            id uuid PRIMARY KEY,
            sent_at timestamptz NOT NULL,
            sender_username text NOT NULL,
            sender_display_name text NOT NULL,
            sender_role text NOT NULL,
            recipient_username text NULL,
            recipient_display_name text NULL,
            recipient_role text NULL,
            content text NOT NULL,
            attachment_id uuid NULL,
            attachment_file_name text NULL,
            attachment_content_type text NULL,
            attachment_length bigint NULL
        );
        CREATE INDEX IF NOT EXISTS ix_chat_messages_sent_at ON chat_messages (sent_at DESC);
        CREATE INDEX IF NOT EXISTS ix_chat_messages_private ON chat_messages (sender_username, recipient_username, sent_at DESC)
            WHERE recipient_username IS NOT NULL;

        CREATE TABLE IF NOT EXISTS call_dispositions (
            id uuid PRIMARY KEY,
            recorded_at timestamptz NOT NULL,
            client_id text NOT NULL,
            phone text NOT NULL,
            agent_id text NOT NULL,
            agent_name text NOT NULL,
            category text NOT NULL,
            result text NOT NULL,
            channel text NOT NULL,
            code text NOT NULL,
            weight integer NOT NULL,
            comment text NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS ix_call_dispositions_client_recorded ON call_dispositions (client_id, recorded_at DESC);
        CREATE INDEX IF NOT EXISTS ix_call_dispositions_client_weight ON call_dispositions (client_id, weight DESC, recorded_at DESC);
        CREATE INDEX IF NOT EXISTS ix_call_dispositions_monitoring ON call_dispositions (recorded_at DESC, agent_name, category, channel);

        CREATE TABLE IF NOT EXISTS promise_registrations (
            id uuid PRIMARY KEY,
            registered_at timestamptz NOT NULL,
            client_id text NOT NULL,
            agent_id text NOT NULL,
            agent_name text NOT NULL,
            promise_date date NULL,
            amount numeric(18,2) NULL,
            status text NOT NULL DEFAULT 'Vigente',
            notes text NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS ix_promise_registrations_client ON promise_registrations (client_id, registered_at DESC);
        CREATE INDEX IF NOT EXISTS ix_promise_registrations_agent ON promise_registrations (agent_id, registered_at DESC);

        CREATE TABLE IF NOT EXISTS promesas (
            id_promesa integer GENERATED BY DEFAULT AS IDENTITY (START WITH 1) PRIMARY KEY,
            cliente_unico varchar(100) NOT NULL,
            agente varchar(100) NULL,
            numero_cliente varchar(100) NOT NULL,
            fecha_promesa date NOT NULL,
            monto_inicial numeric(18,2) NULL,
            monto_semanal numeric(18,2) NULL,
            tipo_promesa varchar(100) NULL,
            numero_semanas smallint NOT NULL,
            fecha_creacion timestamptz NOT NULL,
            usu_alta varchar(20) NULL,
            estatus varchar(100) NOT NULL DEFAULT 'Pendiente de pago',
            origen varchar(20) NOT NULL DEFAULT 'Ascendra',
            id_promesa_origen varchar(100) NULL,
            modificado_por varchar(100) NULL,
            fecha_modificacion timestamptz NULL,
            CONSTRAINT ck_promesas_numero_semanas CHECK (numero_semanas BETWEEN 0 AND 255),
            CONSTRAINT ck_promesas_montos CHECK (
                (monto_inicial IS NULL OR monto_inicial >= 0)
                AND (monto_semanal IS NULL OR monto_semanal >= 0))
        );
        ALTER TABLE promesas ADD COLUMN IF NOT EXISTS origen varchar(20) NOT NULL DEFAULT 'Ascendra';
        ALTER TABLE promesas ADD COLUMN IF NOT EXISTS id_promesa_origen varchar(100) NULL;
        ALTER TABLE promesas ADD COLUMN IF NOT EXISTS estatus varchar(100) NOT NULL DEFAULT 'Pendiente de pago';
        ALTER TABLE promesas ADD COLUMN IF NOT EXISTS modificado_por varchar(100) NULL;
        ALTER TABLE promesas ADD COLUMN IF NOT EXISTS fecha_modificacion timestamptz NULL;
        ALTER TABLE promesas ALTER COLUMN agente DROP NOT NULL;
        ALTER TABLE promesas ALTER COLUMN tipo_promesa DROP NOT NULL;
        ALTER TABLE promesas ALTER COLUMN usu_alta DROP NOT NULL;
        ALTER TABLE promesas DROP CONSTRAINT IF EXISTS ck_promesas_numero_semanas;
        ALTER TABLE promesas ADD CONSTRAINT ck_promesas_numero_semanas
            CHECK (numero_semanas BETWEEN 0 AND 255);
        CREATE INDEX IF NOT EXISTS ix_promesas_cliente_fecha ON promesas (cliente_unico, fecha_promesa DESC);
        CREATE INDEX IF NOT EXISTS ix_promesas_agente_creacion ON promesas (usu_alta, fecha_creacion DESC);
        CREATE INDEX IF NOT EXISTS ix_promesas_monitoring ON promesas (fecha_creacion DESC, agente, tipo_promesa, estatus);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_promesas_origen_externo
            ON promesas (origen, id_promesa_origen)
            WHERE id_promesa_origen IS NOT NULL;
        """;
}
