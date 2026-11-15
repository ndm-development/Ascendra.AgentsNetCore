using System.Text.Json;
using Ascendra.Agents.Web.Domain;
using Microsoft.Extensions.Options;

namespace Ascendra.Agents.Web.Services;

/// <summary>
/// Chat operativo de corta vida. Los mensajes se guardan en un archivo JSON
/// temporal para sobrevivir reinicios y despliegues, y se depuran después del
/// periodo configurado. No utiliza Excel ni una base de datos.
/// </summary>
public sealed class EphemeralChatService : IHostedService, IDisposable
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private const int MaximumMessageLength = 500;
    private const int MaximumStoredMessages = 8_000;
    private static readonly JsonSerializerOptions JournalSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, ChatParticipant> _connections = new(StringComparer.Ordinal);
    private readonly List<ChatMessage> _messages = [];
    private readonly ILogger<EphemeralChatService> _logger;
    private readonly string _storagePath;
    private readonly TimeSpan _messageLifetime;
    private Timer? _cleanupTimer;

    public EphemeralChatService(
        IOptions<ChatOptions> options,
        IWebHostEnvironment environment,
        ILogger<EphemeralChatService> logger)
    {
        _logger = logger;
        var configuration = options.Value;
        _messageLifetime = TimeSpan.FromDays(Math.Clamp(configuration.RetentionDays, 1, 14));
        _storagePath = ResolveStoragePath(configuration.StoragePath, environment);
    }

    public event Action? Changed;

    public static ChatParticipant ParticipantFor(AgentUser user) => new(
        user.Username,
        string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName.Trim(),
        RoleFor(user));

    public void Register(string connectionId, ChatParticipant participant)
    {
        lock (_gate)
        {
            PruneExpiredLocked();
            _connections[connectionId] = participant;
        }

        NotifyChanged();
    }

    public void Unregister(string connectionId)
    {
        var changed = false;
        lock (_gate)
        {
            changed = _connections.Remove(connectionId);
        }

        if (changed) NotifyChanged();
    }

    public ChatPanelState GetState(ChatParticipant viewer)
    {
        lock (_gate)
        {
            PruneExpiredLocked();
            var participants = _connections.Values
                .GroupBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(user => user.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            var messages = _messages
                .Where(message => CanViewMessage(viewer, message))
                .OrderBy(message => message.SentAt)
                .ToArray();

            return new ChatPanelState(participants, messages);
        }
    }

    public void SendToEveryone(ChatParticipant sender, string? content) =>
        AddMessage(sender, null, content);

    public void SendPrivate(ChatParticipant sender, string? recipientUsername, string? content)
    {
        ChatParticipant recipient;
        lock (_gate)
        {
            PruneExpiredLocked();
            recipient = _connections.Values.FirstOrDefault(user =>
                string.Equals(user.Username, recipientUsername, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Ese usuario ya no está conectado.");
        }

        if (string.Equals(sender.Username, recipient.Username, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Elige a otro usuario para enviar un mensaje privado.");
        }

        AddMessage(sender, recipient, content);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            LoadJournalLocked();
            if (PruneExpiredLocked()) PersistAfterCleanupLocked();
        }

        _cleanupTimer = new Timer(_ => RemoveExpiredMessages(), null, CleanupInterval, CleanupInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cleanupTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    public void Dispose() => _cleanupTimer?.Dispose();

    private void AddMessage(ChatParticipant sender, ChatParticipant? recipient, string? content)
    {
        var message = CleanMessage(content);
        lock (_gate)
        {
            PruneExpiredLocked();
            if (!_connections.Values.Any(user => string.Equals(user.Username, sender.Username, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Tu sesión de chat ya no está activa. Vuelve a abrir el panel.");
            }

            var newMessage = new ChatMessage(Guid.NewGuid(), DateTimeOffset.UtcNow, sender, recipient, message);
            _messages.Add(newMessage);
            TrimMessageCountLocked();

            try
            {
                PersistJournalLocked();
            }
            catch (Exception exception)
            {
                _messages.Remove(newMessage);
                _logger.LogError(exception, "No se pudo conservar un mensaje del chat en {StoragePath}", _storagePath);
                throw new InvalidOperationException("No se pudo guardar el mensaje temporalmente. Intenta de nuevo.");
            }
        }

        NotifyChanged();
    }

    private static string CleanMessage(string? content)
    {
        var value = content?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Escribe un mensaje antes de enviarlo.");
        if (value.Length > MaximumMessageLength) throw new InvalidOperationException($"El mensaje no puede exceder {MaximumMessageLength} caracteres.");
        return value;
    }

    private void RemoveExpiredMessages()
    {
        var changed = false;
        lock (_gate)
        {
            changed = PruneExpiredLocked();
            if (changed) PersistAfterCleanupLocked();
        }

        if (changed) NotifyChanged();
    }

    private bool PruneExpiredLocked()
    {
        var cutoff = DateTimeOffset.UtcNow - _messageLifetime;
        return _messages.RemoveAll(message => message.SentAt < cutoff) > 0;
    }

    private static ChatUserRole RoleFor(AgentUser user) => user.Role.Equals("Gerente", StringComparison.OrdinalIgnoreCase)
        ? ChatUserRole.Manager
        : user.Role.Equals("Supervisor", StringComparison.OrdinalIgnoreCase)
            ? ChatUserRole.Supervisor
            : ChatUserRole.Agent;

    private static bool CanViewMessage(ChatParticipant viewer, ChatMessage message)
    {
        if (message.Recipient is null || IsParticipant(viewer, message.Sender) || IsParticipant(viewer, message.Recipient))
        {
            return true;
        }

        return viewer.Role switch
        {
            // Los gerentes auditan la operación, pero no los mensajes escritos
            // por otros gerentes; cada uno conserva visibilidad de los propios.
            ChatUserRole.Manager => message.Sender.Role != ChatUserRole.Manager,

            // Supervisión revisa la comunicación operativa entre agentes,
            // sin exponer conversaciones de otros supervisores o gerentes.
            ChatUserRole.Supervisor => message.Sender.Role == ChatUserRole.Agent
                && message.Recipient?.Role == ChatUserRole.Agent,

            _ => false,
        };
    }

    private static bool IsParticipant(ChatParticipant viewer, ChatParticipant? participant) => participant is not null
        && string.Equals(viewer.Username, participant.Username, StringComparison.OrdinalIgnoreCase);

    private void TrimMessageCountLocked()
    {
        var excess = _messages.Count - MaximumStoredMessages;
        if (excess > 0) _messages.RemoveRange(0, excess);
    }

    private void LoadJournalLocked()
    {
        try
        {
            if (!File.Exists(_storagePath)) return;

            var journal = JsonSerializer.Deserialize<ChatJournal>(File.ReadAllText(_storagePath), JournalSerializerOptions);
            if (journal?.Messages is null) return;

            _messages.Clear();
            _messages.AddRange(journal.Messages.OrderBy(message => message.SentAt));
            TrimMessageCountLocked();
            _logger.LogInformation("Se recuperaron {MessageCount} mensajes temporales del chat.", _messages.Count);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No se pudo leer el historial temporal del chat en {StoragePath}", _storagePath);
            _messages.Clear();
        }
    }

    private void PersistAfterCleanupLocked()
    {
        try
        {
            PersistJournalLocked();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "No se pudo actualizar la limpieza del historial temporal en {StoragePath}", _storagePath);
        }
    }

    private void PersistJournalLocked()
    {
        var directory = Path.GetDirectoryName(_storagePath);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("La ubicación del historial de chat no es válida.");

        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_storagePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var journal = new ChatJournal(_messages.ToArray());
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(journal, JournalSerializerOptions));
            File.Move(temporaryPath, _storagePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string ResolveStoragePath(string? configuredPath, IWebHostEnvironment environment)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? "/var/data/ascendra-chat.json" : configuredPath.Trim();
        if (!Path.IsPathRooted(path)) return Path.GetFullPath(path, environment.ContentRootPath);

        // Facilita correr la aplicación localmente antes de que exista un disco
        // montado; en Render /var/data prevalece y conserva los mensajes.
        if (environment.IsDevelopment() && path.StartsWith("/var/data/", StringComparison.Ordinal) && !Directory.Exists("/var/data"))
        {
            return Path.Combine(environment.ContentRootPath, "data", "generated", "ascendra-chat.json");
        }

        return path;
    }

    private void NotifyChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch
            {
                // Un circuito que terminó no debe impedir que los demás reciban
                // las novedades. Al liberarse se desuscribe en DisposeAsync.
            }
        }
    }
}

public sealed class ChatOptions
{
    public const string SectionName = "Chat";
    public string StoragePath { get; init; } = "/var/data/ascendra-chat.json";
    public int RetentionDays { get; init; } = 7;
}

public enum ChatUserRole { Agent, Supervisor, Manager }

public sealed record ChatParticipant(string Username, string DisplayName, ChatUserRole Role)
{
    public bool CanReviewPrivateMessages => Role is ChatUserRole.Supervisor or ChatUserRole.Manager;
}

public sealed record ChatMessage(
    Guid Id,
    DateTimeOffset SentAt,
    ChatParticipant Sender,
    ChatParticipant? Recipient,
    string Content);

public sealed record ChatJournal(IReadOnlyList<ChatMessage> Messages);

public sealed record ChatPanelState(
    IReadOnlyList<ChatParticipant> ConnectedUsers,
    IReadOnlyList<ChatMessage> Messages)
{
    public static ChatPanelState Empty { get; } = new(Array.Empty<ChatParticipant>(), Array.Empty<ChatMessage>());
}
