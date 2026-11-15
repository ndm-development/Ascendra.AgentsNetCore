using Ascendra.Agents.Web.Domain;

namespace Ascendra.Agents.Web.Services;

/// <summary>
/// Chat operativo de corta vida. Todo el estado vive únicamente en memoria del
/// proceso: no se escriben mensajes en Excel, archivos ni bases de datos.
/// </summary>
public sealed class EphemeralChatService : IHostedService, IDisposable
{
    private static readonly TimeSpan MessageLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private const int MaximumMessageLength = 500;

    private readonly object _gate = new();
    private readonly Dictionary<string, ChatParticipant> _connections = new(StringComparer.Ordinal);
    private readonly List<ChatMessage> _messages = [];
    private Timer? _cleanupTimer;

    public event Action? Changed;

    public static ChatParticipant ParticipantFor(AgentUser user) => new(
        user.Username,
        string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName.Trim());

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
                .Where(message => message.Recipient is null
                    || string.Equals(message.Sender.Username, viewer.Username, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(message.Recipient.Username, viewer.Username, StringComparison.OrdinalIgnoreCase))
                .OrderBy(message => message.SentAt)
                .ToArray();

            return new ChatPanelState(participants, messages);
        }
    }

    public void SendToEveryone(ChatParticipant sender, string? content) =>
        AddMessage(sender, null, content);

    public void SendDirect(ChatParticipant sender, string? recipientUsername, string? content)
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
            throw new InvalidOperationException("Elige a otro usuario para enviar un mensaje directo.");
        }

        AddMessage(sender, recipient, content);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
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

            _messages.Add(new ChatMessage(Guid.NewGuid(), DateTimeOffset.UtcNow, sender, recipient, message));
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
        }

        if (changed) NotifyChanged();
    }

    private bool PruneExpiredLocked()
    {
        var cutoff = DateTimeOffset.UtcNow - MessageLifetime;
        return _messages.RemoveAll(message => message.SentAt < cutoff) > 0;
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

public sealed record ChatParticipant(string Username, string DisplayName);

public sealed record ChatMessage(
    Guid Id,
    DateTimeOffset SentAt,
    ChatParticipant Sender,
    ChatParticipant? Recipient,
    string Content);

public sealed record ChatPanelState(
    IReadOnlyList<ChatParticipant> ConnectedUsers,
    IReadOnlyList<ChatMessage> Messages)
{
    public static ChatPanelState Empty { get; } = new(Array.Empty<ChatParticipant>(), Array.Empty<ChatMessage>());
}
