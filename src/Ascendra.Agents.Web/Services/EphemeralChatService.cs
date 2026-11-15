using System.Text.Json;
using Ascendra.Agents.Web.Domain;
using Microsoft.Extensions.Options;

namespace Ascendra.Agents.Web.Services;

/// <summary>
/// Chat operativo de corta vida. Conserva mensajes e imágenes en el disco
/// persistente durante el periodo configurado, sin depender de Excel o de una
/// base de datos.
/// </summary>
public sealed class EphemeralChatService : IHostedService, IDisposable
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private const int MaximumMessageLength = 500;
    private const int MaximumStoredMessages = 8_000;
    public const long MaximumImageBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JournalSerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly Dictionary<string, ChatParticipant> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> _stagedAttachmentOwners = [];
    private readonly List<ChatMessage> _messages = [];
    private readonly ILogger<EphemeralChatService> _logger;
    private readonly string _storagePath;
    private readonly string _attachmentDirectory;
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
        _attachmentDirectory = string.IsNullOrWhiteSpace(configuration.AttachmentStoragePath)
            ? Path.Combine(Path.GetDirectoryName(_storagePath)!, "ascendra-chat-attachments")
            : ResolveStoragePath(configuration.AttachmentStoragePath, environment);
    }

    public event Action? Changed;

    public static ChatParticipant ParticipantFor(AgentUser user) => new(
        user.Username,
        string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName.Trim(),
        RoleFor(user.Role));

    public static ChatParticipant ParticipantFor(string username, string displayName, string role) => new(
        username,
        string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(),
        RoleFor(role));

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

    public void SendToEveryone(ChatParticipant sender, string? content, ChatAttachment? attachment = null) =>
        AddMessage(sender, null, content, attachment);

    public void SendPrivate(ChatParticipant sender, string? recipientUsername, string? content, ChatAttachment? attachment = null)
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

        AddMessage(sender, recipient, content, attachment);
    }

    public async Task<ChatAttachment> StoreImageAsync(
        ChatParticipant sender,
        Stream source,
        string? originalFileName,
        CancellationToken cancellationToken = default)
    {
        if (source is null) throw new InvalidOperationException("No se recibió una imagen.");
        var attachment = new ChatAttachment(Guid.NewGuid(), SafeFileName(originalFileName), "image/unknown", 0);
        var temporaryPath = $"{Path.Combine(_attachmentDirectory, attachment.Id.ToString("N"))}.{Guid.NewGuid():N}.tmp";

        try
        {
            Directory.CreateDirectory(_attachmentDirectory);
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                await source.CopyToAsync(target, cancellationToken);
                if (target.Length > MaximumImageBytes)
                {
                    throw new InvalidOperationException("La imagen no puede superar 4 MB.");
                }
            }

            var contentType = DetectImageContentType(temporaryPath)
                ?? throw new InvalidOperationException("Adjunta una imagen PNG, JPG, GIF o WEBP válida.");
            var finalAttachment = attachment with
            {
                ContentType = contentType,
                Length = new FileInfo(temporaryPath).Length,
            };
            var finalPath = AttachmentPath(finalAttachment);
            File.Move(temporaryPath, finalPath, true);

            lock (_gate)
            {
                _stagedAttachmentOwners[finalAttachment.Id] = sender.Username;
            }
            return finalAttachment;
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            DeleteAttachmentFile(attachment);
            throw;
        }
    }

    public void DiscardStagedAttachment(ChatParticipant sender, ChatAttachment? attachment)
    {
        if (attachment is null) return;

        lock (_gate)
        {
            if (!_stagedAttachmentOwners.Remove(attachment.Id, out var owner)
                || !string.Equals(owner, sender.Username, StringComparison.OrdinalIgnoreCase)) return;
            DeleteAttachmentFile(attachment);
        }
    }

    public ChatAttachmentFile? GetAttachment(ChatParticipant viewer, Guid attachmentId)
    {
        lock (_gate)
        {
            PruneExpiredLocked();
            var message = _messages.LastOrDefault(item => item.Attachment?.Id == attachmentId);
            if (message is not null && CanViewMessage(viewer, message))
            {
                var path = AttachmentPath(message.Attachment!);
                return File.Exists(path) ? new ChatAttachmentFile(path, message.Attachment!.ContentType, message.Attachment.FileName) : null;
            }

            if (_stagedAttachmentOwners.TryGetValue(attachmentId, out var owner)
                && string.Equals(owner, viewer.Username, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = Directory.Exists(_attachmentDirectory)
                    ? Directory.EnumerateFiles(_attachmentDirectory, $"{attachmentId:N}.*").FirstOrDefault()
                    : null;
                if (candidate is not null)
                {
                    var contentType = DetectImageContentType(candidate) ?? "application/octet-stream";
                    return new ChatAttachmentFile(candidate, contentType, "imagen adjunta");
                }
            }
        }

        return null;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            LoadJournalLocked();
            if (PruneExpiredLocked()) PersistAfterCleanupLocked();
            RemoveOrphanedAttachmentFilesLocked();
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

    private void AddMessage(ChatParticipant sender, ChatParticipant? recipient, string? content, ChatAttachment? attachment)
    {
        var message = CleanMessage(content, attachment is not null);
        lock (_gate)
        {
            PruneExpiredLocked();
            if (!_connections.Values.Any(user => string.Equals(user.Username, sender.Username, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Tu sesión de chat ya no está activa. Vuelve a abrir el panel.");
            }
            if (attachment is not null && (!_stagedAttachmentOwners.Remove(attachment.Id, out var owner)
                || !string.Equals(owner, sender.Username, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("La imagen ya no está disponible. Vuelve a adjuntarla.");
            }

            var newMessage = new ChatMessage(Guid.NewGuid(), DateTimeOffset.UtcNow, sender, recipient, message, attachment);
            _messages.Add(newMessage);
            TrimMessageCountLocked();

            try
            {
                PersistJournalLocked();
            }
            catch (Exception exception)
            {
                _messages.Remove(newMessage);
                if (attachment is not null) DeleteAttachmentFile(attachment);
                _logger.LogError(exception, "No se pudo conservar un mensaje del chat en {StoragePath}", _storagePath);
                throw new InvalidOperationException("No se pudo guardar el mensaje temporalmente. Intenta de nuevo.");
            }
        }

        NotifyChanged();
    }

    private static string CleanMessage(string? content, bool hasAttachment)
    {
        var value = content?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) && !hasAttachment) throw new InvalidOperationException("Escribe un mensaje antes de enviarlo.");
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
        var expired = _messages.Where(message => message.SentAt < cutoff).ToArray();
        if (expired.Length == 0) return false;

        _messages.RemoveAll(message => message.SentAt < cutoff);
        foreach (var attachment in expired.Select(message => message.Attachment).Where(attachment => attachment is not null))
        {
            DeleteAttachmentFile(attachment!);
        }
        return true;
    }

    private static ChatUserRole RoleFor(string role) => role.Equals("Gerente", StringComparison.OrdinalIgnoreCase)
        ? ChatUserRole.Manager
        : role.Equals("Supervisor", StringComparison.OrdinalIgnoreCase)
            ? ChatUserRole.Supervisor
            : ChatUserRole.Agent;

    private static bool CanViewMessage(ChatParticipant viewer, ChatMessage message)
    {
        if (message.Recipient is null || IsParticipant(viewer, message.Sender) || IsParticipant(viewer, message.Recipient)) return true;

        return viewer.Role switch
        {
            ChatUserRole.Manager => message.Sender.Role != ChatUserRole.Manager,
            ChatUserRole.Supervisor => message.Sender.Role == ChatUserRole.Agent && message.Recipient?.Role == ChatUserRole.Agent,
            _ => false,
        };
    }

    private static bool IsParticipant(ChatParticipant viewer, ChatParticipant? participant) => participant is not null
        && string.Equals(viewer.Username, participant.Username, StringComparison.OrdinalIgnoreCase);

    private void TrimMessageCountLocked()
    {
        while (_messages.Count > MaximumStoredMessages)
        {
            var message = _messages[0];
            _messages.RemoveAt(0);
            if (message.Attachment is not null) DeleteAttachmentFile(message.Attachment);
        }
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
        try { PersistJournalLocked(); }
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
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new ChatJournal(_messages.ToArray()), JournalSerializerOptions));
            File.Move(temporaryPath, _storagePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void RemoveOrphanedAttachmentFilesLocked()
    {
        if (!Directory.Exists(_attachmentDirectory)) return;
        var retained = _messages.Where(message => message.Attachment is not null)
            .Select(message => AttachmentPath(message.Attachment!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(_attachmentDirectory))
        {
            if (!retained.Contains(path)) TryDeleteFile(path);
        }
    }

    private string AttachmentPath(ChatAttachment attachment) => Path.Combine(_attachmentDirectory, $"{attachment.Id:N}{ImageExtension(attachment.ContentType)}");
    private void DeleteAttachmentFile(ChatAttachment attachment) => TryDeleteFile(AttachmentPath(attachment));
    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* La limpieza se reintentará en el siguiente ciclo. */ }
    }

    private static string SafeFileName(string? originalFileName)
    {
        var name = Path.GetFileName(originalFileName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(name) ? "imagen" : name.Length <= 120 ? name : name[..120];
    }

    private static string? DetectImageContentType(string path)
    {
        var bytes = new byte[16];
        using var input = File.OpenRead(path);
        var length = input.Read(bytes, 0, bytes.Length);
        if (length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255) return "image/jpeg";
        if (length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8.ToArray()) || bytes[..6].SequenceEqual("GIF89a"u8.ToArray()))) return "image/gif";
        if (length >= 12 && bytes[..4].SequenceEqual("RIFF"u8.ToArray()) && bytes[8..12].SequenceEqual("WEBP"u8.ToArray())) return "image/webp";
        return null;
    }

    private static string ImageExtension(string contentType) => contentType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".img",
    };

    private static string ResolveStoragePath(string? configuredPath, IWebHostEnvironment environment)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? "/var/data/ascendra-chat.json" : configuredPath.Trim();
        if (!Path.IsPathRooted(path)) return Path.GetFullPath(path, environment.ContentRootPath);
        if (environment.IsDevelopment() && path.StartsWith("/var/data/", StringComparison.Ordinal) && !Directory.Exists("/var/data"))
        {
            var fileName = Path.GetFileName(path);
            return Path.Combine(environment.ContentRootPath, "data", "generated", fileName);
        }
        return path;
    }

    private void NotifyChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch { /* Un circuito terminado no bloquea la notificación al resto. */ }
        }
    }
}

public sealed class ChatOptions
{
    public const string SectionName = "Chat";
    public string StoragePath { get; init; } = "/var/data/ascendra-chat.json";
    public string AttachmentStoragePath { get; init; } = "/var/data/ascendra-chat-attachments";
    public int RetentionDays { get; init; } = 7;
}

public enum ChatUserRole { Agent, Supervisor, Manager }

public sealed record ChatParticipant(string Username, string DisplayName, ChatUserRole Role)
{
    public bool CanReviewPrivateMessages => Role is ChatUserRole.Supervisor or ChatUserRole.Manager;
}

public sealed record ChatAttachment(Guid Id, string FileName, string ContentType, long Length);
public sealed record ChatAttachmentFile(string Path, string ContentType, string DownloadName);

public sealed record ChatMessage(
    Guid Id,
    DateTimeOffset SentAt,
    ChatParticipant Sender,
    ChatParticipant? Recipient,
    string Content,
    ChatAttachment? Attachment = null);

public sealed record ChatJournal(IReadOnlyList<ChatMessage> Messages);

public sealed record ChatPanelState(IReadOnlyList<ChatParticipant> ConnectedUsers, IReadOnlyList<ChatMessage> Messages)
{
    public static ChatPanelState Empty { get; } = new(Array.Empty<ChatParticipant>(), Array.Empty<ChatMessage>());
}
