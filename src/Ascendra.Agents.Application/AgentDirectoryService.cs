using System.Security.Cryptography;
using System.Text.Json;
using Ascendra.Agents.Domain;

namespace Ascendra.Agents.Application;

public sealed record AgentDirectorySnapshot(
    string Version, IReadOnlyList<AgentDirectoryRecord> Directory, IReadOnlyList<AgentUser> Users);

public sealed record AgentDirectoryImportResult(bool Imported, int People, int ActiveUsers);

public interface IAgentDirectoryRepository
{
    Task<IReadOnlyList<AgentDirectoryRecord>> ReadAsync(CancellationToken cancellationToken = default);
    Task<bool> SeedIfEmptyAsync(IReadOnlyList<AgentDirectoryRecord> directory, string fingerprint,
        CancellationToken cancellationToken = default);
    // Implementations must check actor permissions and expected revision in the write transaction.
    Task<AgentDirectoryRecord> SaveAsync(AgentDirectoryRecord agent, string editorId,
        long expectedRevision, CancellationToken cancellationToken = default);
}

public sealed class AgentDirectoryService(IAgentDirectoryRepository repository)
{
    public async Task<AgentDirectorySnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var rows = await repository.ReadAsync(cancellationToken);
        if (rows.Count == 0) throw new InvalidOperationException("El padrón de PostgreSQL aún no está inicializado.");
        var directory = AgentDirectoryRules.Validate(rows);
        var version = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(directory))).ToLowerInvariant();
        var users = directory.Where(agent => agent.IsActive).Select(agent => agent.ToUser()).ToArray();
        return new AgentDirectorySnapshot(version, directory, Array.AsReadOnly(users));
    }

    public async Task<AgentDirectoryImportResult> SeedIfEmptyAsync(
        IEnumerable<AgentDirectoryRecord> source, string fingerprint, CancellationToken cancellationToken = default)
    {
        var directory = AgentDirectoryRules.Validate(source, requireActiveUsers: true);
        var imported = await repository.SeedIfEmptyAsync(directory, fingerprint, cancellationToken);
        var current = await GetAsync(cancellationToken);
        return new AgentDirectoryImportResult(imported, current.Directory.Count, current.Users.Count);
    }

    public async Task<AgentUser> RequireActiveAsync(string? id, CancellationToken cancellationToken = default)
    {
        var normalized = IdentityNormalization.AgentId(id);
        return (await GetAsync(cancellationToken)).Users.FirstOrDefault(agent =>
            agent.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("La persona ya no está activa en el padrón.");
    }

    public async Task<AgentDirectorySnapshot> GetForManagementAsync(string editorId, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(cancellationToken);
        var actor = snapshot.Directory.FirstOrDefault(agent => agent.Id.Equals(IdentityNormalization.AgentId(editorId), StringComparison.OrdinalIgnoreCase));
        if (!AgentDirectoryRules.CanManage(actor)) throw new InvalidOperationException("No tienes permiso para consultar el padrón administrativo.");
        return snapshot;
    }

    public Task<AgentDirectoryRecord> SaveAsync(AgentDirectoryRecord agent, string editorId,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        if (expectedRevision < 0) throw new InvalidOperationException("Recarga el padrón antes de editar.");
        var validated = AgentDirectoryRules.Validate([agent]).Single();
        return repository.SaveAsync(validated, IdentityNormalization.AgentId(editorId), expectedRevision, cancellationToken);
    }
}
