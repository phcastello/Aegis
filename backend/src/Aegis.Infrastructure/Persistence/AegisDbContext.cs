using Aegis.Application.Chat;
using Aegis.Application.Common;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace Aegis.Infrastructure.Persistence;

public sealed class AegisDbContext(
    DbContextOptions<AegisDbContext> options,
    IConversationChangePublisher? conversationChanges = null) : DbContext(options), IAegisDbContext
{
    private Guid? conversationChangeTransaction;
    private readonly HashSet<Guid> transactionConversationChanges = [];

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        var changed = CaptureConversationChanges();
        var count = base.SaveChanges(acceptAllChangesOnSuccess);
        if (count > 0) RecordConversationChanges(changed);
        return count;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var changed = CaptureConversationChanges();
        int count;
        try
        {
            count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateConcurrencyException error) when (error.Entries.Any(e => e.Entity is UploadedChatFile))
        {
            throw new ArgumentException("Este envio já foi utilizado ou expirou. Confira a conversa antes de enviar novamente.", error);
        }
        if (count > 0) RecordConversationChanges(changed);
        return count;
    }

    private Guid[] CaptureConversationChanges()
    {
        if (conversationChanges is null) return [];
        return ChangeTracker.Entries()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity switch
            {
                Conversation conversation => conversation.Id,
                ChatMessage message => message.ConversationId,
                ResearchJob research => research.ConversationId,
                _ => Guid.Empty
            })
            .Where(id => id != Guid.Empty).Distinct().ToArray();
    }

    // Also used by direct SQL/ExecuteUpdate paths which bypass tracked changes.
    internal void RecordConversationChanges(params Guid[] ids)
    {
        if (conversationChanges is null || ids.Length == 0) return;
        if (Database.IsRelational() && Database.CurrentTransaction is { } transaction)
        {
            if (conversationChangeTransaction != transaction.TransactionId)
            {
                transactionConversationChanges.Clear();
                conversationChangeTransaction = transaction.TransactionId;
            }
            transactionConversationChanges.UnionWith(ids);
            return;
        }

        foreach (var id in ids) conversationChanges.Publish(id);
    }

    internal void FinishConversationChanges(Guid transactionId, bool committed)
    {
        if (conversationChangeTransaction != transactionId) return;
        if (committed && conversationChanges is not null)
            foreach (var id in transactionConversationChanges)
                conversationChanges.Publish(id);
        transactionConversationChanges.Clear();
        conversationChangeTransaction = null;
    }

    public DbSet<NodePushRegistration> NodePushRegistrations => Set<NodePushRegistration>();
    public DbSet<NodeCapabilitySnapshot> NodeCapabilities => Set<NodeCapabilitySnapshot>();
    public DbSet<AegisNode> Nodes => Set<AegisNode>();
    public DbSet<NodeCredential> NodeCredentials => Set<NodeCredential>();
    public DbSet<NodePairingCode> NodePairingCodes => Set<NodePairingCode>();
    public DbSet<NodePairingAttempt> NodePairingAttempts => Set<NodePairingAttempt>();

    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<MemoryRecord> MemoryRecords => Set<MemoryRecord>();
    public DbSet<OwnerPreferences> OwnerPreferences => Set<OwnerPreferences>();
    public DbSet<MemoryEvidence> MemoryEvidences => Set<MemoryEvidence>();
    public DbSet<MemoryEntity> MemoryEntities => Set<MemoryEntity>();
    public DbSet<MemoryEntityAlias> MemoryEntityAliases => Set<MemoryEntityAlias>();
    public DbSet<MemoryRelation> MemoryRelations => Set<MemoryRelation>();
    public DbSet<MemoryRelationEvidence> MemoryRelationEvidences => Set<MemoryRelationEvidence>();
    public DbSet<MemoryProjectionJob> MemoryProjectionJobs => Set<MemoryProjectionJob>();
    public DbSet<MemoryExtractionJob> MemoryExtractionJobs => Set<MemoryExtractionJob>();
    public DbSet<MemoryActivityEvent> MemoryActivityEvents => Set<MemoryActivityEvent>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<ReminderDeliveryAttempt> ReminderDeliveryAttempts => Set<ReminderDeliveryAttempt>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatAttachment> ChatAttachments => Set<ChatAttachment>();
    public DbSet<UploadedChatFile> ChatUploads => Set<UploadedChatFile>();
    public DbSet<ChatUploadChunk> ChatUploadChunks => Set<ChatUploadChunk>();

    public void AddChatAttachments(IReadOnlyList<ChatAttachment> attachments) => ChatAttachments.AddRange(attachments);

    private IQueryable<ChatAttachment> VisibleAttachments =>
        ChatAttachments.AsNoTracking().Where(a => a.Message.Conversation!.DeletedAt == null);

    public async Task<ILookup<Guid, ChatAttachmentResponse>> GetAttachmentIndexAsync(Guid conversationId, CancellationToken ct)
    {
        var rows = await VisibleAttachments.Where(a => a.Message.ConversationId == conversationId)
            .OrderBy(a => a.Position)
            .Select(a => new { a.MessageId, a.Id, a.FileName, a.ContentType, a.SizeBytes,
                LinkData = a.ContentType == ChatLinkAttachment.ContentType ? a.Data : null })
            .ToListAsync(ct);
        return rows.ToLookup(a => a.MessageId, a => new ChatAttachmentResponse(a.Id, a.FileName, a.ContentType, a.SizeBytes,
            ChatLinkAttachment.ReadUrl(a.LinkData)));
    }

    public Task<List<ChatAttachment>> GetMessageAttachmentsAsync(Guid messageId, CancellationToken ct) =>
        VisibleAttachments.Include(a => a.Upload).Where(a => a.MessageId == messageId).OrderBy(a => a.Position).ToListAsync(ct);

    public Task<ChatAttachment?> GetAttachmentAsync(Guid messageId, Guid attachmentId, CancellationToken ct) =>
        VisibleAttachments.Include(a => a.Upload).SingleOrDefaultAsync(a => a.Id == attachmentId && a.MessageId == messageId, ct);

    public async Task<List<ChatAttachment>> ResolveChatUploadsAsync(Guid messageId, IReadOnlyList<Guid> uploadIds, CancellationToken ct)
    {
        if (uploadIds.Count > ChatUploadLimits.MaxFiles || uploadIds.Distinct().Count() != uploadIds.Count)
            throw new ArgumentException($"Selecione até {ChatUploadLimits.MaxFiles} arquivos diferentes por mensagem.");
        var ids = uploadIds.ToArray();
        var now = DateTimeOffset.UtcNow;
        // Track the original null AttachmentId. The normal message SaveChanges then
        // claims every upload atomically, and competing claims roll back in full.
        var uploads = await ChatUploads.Where(u => ids.Contains(u.Id) && u.AttachmentId == null && u.ExpiresAt > now)
            .ToDictionaryAsync(u => u.Id, ct);
        if (uploads.Values.Sum(upload => (long)upload.SizeBytes) > ChatUploadLimits.MaxMessageBytes)
            throw new ArgumentException("Os anexos excedem 10 GiB por mensagem.");
        var received = await ChatUploadChunks.Where(c => ids.Contains(c.UploadId)).GroupBy(c => c.UploadId)
            .Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Id, g => g.Count, ct);
        var files = new List<ChatAttachment>(ids.Length);
        foreach (var id in ids)
        {
            if (!uploads.TryGetValue(id, out var upload))
                throw new ArgumentException("Um envio já foi utilizado ou expirou. Confira a conversa antes de enviar novamente.");
            if (received.GetValueOrDefault(id) != ChatUploadLimits.ChunkCount(upload.SizeBytes))
                throw new ArgumentException("O envio do arquivo ainda está incompleto.");
            files.Add(new ChatAttachment(messageId, files.Count, upload));
        }
        return files;
    }

    public Task<Stream> OpenAttachmentContentAsync(ChatAttachment attachment, CancellationToken ct) =>
        Task.FromResult<Stream>(attachment.Upload is { } upload
            ? new ChatUploadStream(this, upload.Id, upload.SizeBytes, ct)
            : new MemoryStream(attachment.Data, writable: false));

    public DbSet<ResearchJob> ResearchJobs => Set<ResearchJob>();

    public DbSet<MessageFeedback> MessageFeedback => Set<MessageFeedback>();

    public DbSet<LlmRequestAudit> LlmRequestAudits => Set<LlmRequestAudit>();

    public DbSet<WebSearchUsageEntry> WebSearchUsage => Set<WebSearchUsageEntry>();

    public DbSet<EmailAccountConnection> EmailAccountConnections => Set<EmailAccountConnection>();

    public DbSet<PendingCalendarAction> PendingCalendarActions => Set<PendingCalendarAction>();

    public DbSet<CalendarActionAudit> CalendarActionAudits => Set<CalendarActionAudit>();

    public DbSet<PendingEmailAction> PendingEmailActions => Set<PendingEmailAction>();

    public DbSet<EmailActionAudit> EmailActionAudits => Set<EmailActionAudit>();

    public DbSet<ToolContextEntry> ToolContextEntries => Set<ToolContextEntry>();

    IQueryable<Conversation> IAegisDbContext.Conversations => Conversations;

    IQueryable<ChatMessage> IAegisDbContext.ChatMessages => ChatMessages;

    IQueryable<MessageFeedback> IAegisDbContext.MessageFeedback => MessageFeedback;

    IQueryable<LlmRequestAudit> IAegisDbContext.LlmRequestAudits => LlmRequestAudits;

    IQueryable<EmailAccountConnection> IAegisDbContext.EmailAccountConnections => EmailAccountConnections;

    IQueryable<PendingCalendarAction> IAegisDbContext.PendingCalendarActions => PendingCalendarActions;

    IQueryable<CalendarActionAudit> IAegisDbContext.CalendarActionAudits => CalendarActionAudits;

    IQueryable<PendingEmailAction> IAegisDbContext.PendingEmailActions => PendingEmailActions;

    IQueryable<EmailActionAudit> IAegisDbContext.EmailActionAudits => EmailActionAudits;

    IQueryable<ToolContextEntry> IAegisDbContext.ToolContextEntries => ToolContextEntries;

    public async Task<bool> SetGeneratedTitleIfEligibleAsync(Guid id, string title, string? raw, CancellationToken ct) {
        var now = DateTimeOffset.UtcNow;
        if (!Database.IsRelational()) {
            var conversation = await GetConversationWithMessagesAsync(id, ct);
            if (conversation is null || !conversation.CanGenerateAutomaticTitle) return false;
            conversation.SetGeneratedTitle(title, raw, now); await SaveChangesAsync(ct); return true;
        }
        // A manual rename that commits during model generation always wins.
        var changed = await Conversations.Where(c => c.Id == id && c.DeletedAt == null && c.TitleGeneratedAt == null && c.TitleSource != Conversation.ManualTitleSource)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, title).SetProperty(c => c.TitleSource, Conversation.GeneratedTitleSource)
                .SetProperty(c => c.TitleGeneratedAt, now).SetProperty(c => c.TitleGenerationRawResponse, raw).SetProperty(c => c.UpdatedAt, now), ct) > 0;
        if (changed) RecordConversationChanges(id);
        return changed;
    }

    public void AddConversation(Conversation conversation)
    {
        Conversations.Add(conversation);
    }

    public void AddChatMessage(ChatMessage message)
    {
        ChatMessages.Add(message);
    }

    public void AddMemoryExtractionJob(MemoryExtractionJob job) => MemoryExtractionJobs.Add(job);

    public void AddMessageFeedback(MessageFeedback feedback)
    {
        MessageFeedback.Add(feedback);
    }

    public void AddLlmRequestAudit(LlmRequestAudit audit)
    {
        LlmRequestAudits.Add(audit);
    }

    public void AddEmailAccountConnection(EmailAccountConnection connection)
    {
        EmailAccountConnections.Add(connection);
    }

    public void AddPendingCalendarAction(PendingCalendarAction action) => PendingCalendarActions.Add(action);

    public void AddCalendarActionAudit(CalendarActionAudit audit) => CalendarActionAudits.Add(audit);

    public void AddPendingEmailAction(PendingEmailAction action)
    {
        PendingEmailActions.Add(action);
    }

    public void AddEmailActionAudit(EmailActionAudit audit)
    {
        EmailActionAudits.Add(audit);
    }

    public void AddToolContextEntry(ToolContextEntry entry)
    {
        ToolContextEntries.Add(entry);
    }

    public async Task<ChatMessage?> GetChatMessageAsync(
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        return await ChatMessages
            .Include(message => message.Conversation)
            .FirstOrDefaultAsync(
                message => message.Id == messageId && message.Conversation!.DeletedAt == null,
                cancellationToken);
    }

    public async Task<ChatMessage?> GetPreviousUserMessageAsync(
        Guid conversationId,
        DateTimeOffset before,
        CancellationToken cancellationToken = default)
    {
        return await ChatMessages
            .Include(message => message.Conversation)
            .Where(message =>
                message.ConversationId == conversationId &&
                message.Conversation!.DeletedAt == null &&
                message.Role == ChatRoles.User &&
                message.CreatedAt <= before)
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<MessageFeedback?> GetMessageFeedbackWithMessageAsync(
        Guid feedbackId,
        CancellationToken cancellationToken = default)
    {
        return await MessageFeedback
            .Include(feedback => feedback.Message)
            .Include(feedback => feedback.Conversation)
            .FirstOrDefaultAsync(
                feedback => feedback.Id == feedbackId && feedback.Conversation!.DeletedAt == null,
                cancellationToken);
    }

    public async Task<IReadOnlyList<MessageFeedback>> GetRecentMessageFeedbackAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Max(1, limit);
        return await MessageFeedback
            .Include(feedback => feedback.Message)
            .Include(feedback => feedback.Conversation)
            .Where(feedback => feedback.Conversation!.DeletedAt == null)
            .OrderByDescending(feedback => feedback.CreatedAt)
            .ThenByDescending(feedback => feedback.Id)
            .Take(normalizedLimit)
            .ToListAsync(cancellationToken);
    }

    public async Task<Conversation?> GetConversationWithMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        return await Conversations
            .Include(conversation => conversation.Messages)
            .FirstOrDefaultAsync(
                conversation => conversation.Id == conversationId && conversation.DeletedAt == null,
                cancellationToken);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetRecentMessagesAsync(
        Guid conversationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Max(1, limit);
        var messages = await ChatMessages
            .Include(message => message.Conversation)
            .Where(message => message.ConversationId == conversationId && message.Conversation!.DeletedAt == null)
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Take(normalizedLimit)
            .ToListAsync(cancellationToken);

        return messages
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .ToList();
    }

    public async Task<IReadOnlyList<ConversationSummaryData>> GetRecentConversationSummariesAsync(
        int limit,
        ConversationCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Max(1, limit);
        var connection = Database.GetDbConnection();
        var shouldCloseConnection = connection.State == ConnectionState.Closed;
        if (shouldCloseConnection)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    c."Id",
                    c."Title",
                    c."CreatedAt",
                    c."UpdatedAt",
                    c."TitleSource",
                    (
                        SELECT COUNT(*)::integer
                        FROM chat_messages m
                        WHERE m."ConversationId" = c."Id"
                    ) AS "MessageCount",
                    (
                        SELECT lm."Content"
                        FROM chat_messages lm
                        WHERE lm."ConversationId" = c."Id"
                        ORDER BY lm."CreatedAt" DESC, lm."Id" DESC
                        LIMIT 1
                    ) AS "LastMessageContent",
                    c."LastProactiveSequence" > c."ReadProactiveSequence" AS "HasUnreadProactive"
                FROM conversations c
                WHERE
                    c."DeletedAt" IS NULL
                    AND (
                        CAST(@CursorUpdatedAt AS timestamp with time zone) IS NULL
                        OR c."UpdatedAt" < CAST(@CursorUpdatedAt AS timestamp with time zone)
                        OR (
                            c."UpdatedAt" = CAST(@CursorUpdatedAt AS timestamp with time zone)
                            AND c."Id" < CAST(@CursorId AS uuid)
                        )
                    )
                ORDER BY c."UpdatedAt" DESC, c."Id" DESC
                LIMIT @Limit;
                """;

            AddParameter(command, "Limit", normalizedLimit);
            AddParameter(command, "CursorUpdatedAt", cursor?.UpdatedAt.ToUniversalTime() ?? (object)DBNull.Value);
            AddParameter(command, "CursorId", cursor?.Id ?? (object)DBNull.Value);

            var summaries = new List<ConversationSummaryData>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                summaries.Add(new ConversationSummaryData(
                    reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero),
                    new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetBoolean(7)));
            }

            return summaries;
        }
        finally
        {
            if (shouldCloseConnection)
            {
                await connection.CloseAsync();
            }
        }
    }

    public async Task<PendingEmailAction?> GetLatestOpenPendingEmailActionAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await PendingEmailActions
            .Where(action =>
                action.ConversationId == conversationId &&
                action.ConfirmedAt == null &&
                action.CancelledAt == null &&
                action.ExecutedAt == null &&
                action.SupersededAt == null &&
                action.ExpiresAt > now)
            .OrderByDescending(action => action.CreatedAt)
            .ThenByDescending(action => action.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<PendingCalendarAction?> GetLatestOpenPendingCalendarActionAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await PendingCalendarActions
            .Where(action =>
                action.ConversationId == conversationId &&
                action.ConfirmedAt == null &&
                action.CancelledAt == null &&
                action.ExecutedAt == null &&
                action.SupersededAt == null &&
                action.ExpiresAt > now)
            .OrderByDescending(action => action.CreatedAt)
            .ThenByDescending(action => action.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Expiration does not erase a possibly sent request. Preparation must inspect these actions too.
    public async Task<IReadOnlyList<PendingCalendarAction>> GetUnresolvedPendingCalendarActionsAsync(
        Guid conversationId, CancellationToken cancellationToken = default) =>
        await PendingCalendarActions.Where(action => action.ConversationId == conversationId && action.ConfirmedAt == null &&
            action.CancelledAt == null && action.ExecutedAt == null && action.SupersededAt == null)
            .OrderByDescending(action => action.CreatedAt).ThenByDescending(action => action.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PendingEmailAction>> GetUnresolvedPendingEmailActionsAsync(
        Guid conversationId, CancellationToken cancellationToken = default) =>
        await PendingEmailActions.Where(action => action.ConversationId == conversationId && action.ConfirmedAt == null &&
            action.CancelledAt == null && action.ExecutedAt == null && action.SupersededAt == null)
            .OrderByDescending(action => action.CreatedAt).ThenByDescending(action => action.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ToolContextEntry>> GetActiveToolContextEntriesAsync(
        Guid conversationId,
        string scope,
        string? entryType = null,
        string? key = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var query = ToolContextEntries
            .Where(entry =>
                entry.ConversationId == conversationId &&
                entry.Scope == scope &&
                entry.ReplacedAt == null &&
                entry.ExpiresAt > now);

        if (!string.IsNullOrWhiteSpace(entryType))
        {
            query = query.Where(entry => entry.EntryType == entryType);
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            query = query.Where(entry => entry.Key == key);
        }

        return await query
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> HasRecentToolContextEntriesAsync(
        Guid conversationId,
        string scope,
        DateTimeOffset since,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await ToolContextEntries.AnyAsync(
            entry =>
                entry.ConversationId == conversationId &&
                entry.Scope == scope &&
                entry.ReplacedAt == null &&
                entry.ExpiresAt > now &&
                entry.CreatedAt >= since,
            cancellationToken);
    }

    public async Task ReplaceActiveToolContextEntriesAsync(
        Guid conversationId,
        string scope,
        string entryType,
        string key,
        CancellationToken cancellationToken = default)
    {
        var entries = await GetActiveToolContextEntriesAsync(
            conversationId,
            scope,
            entryType,
            key,
            cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in entries)
        {
            entry.Replace(now);
        }
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OwnerPreferences>(entity =>
        {
            entity.ToTable("owner_preferences");
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).ValueGeneratedNever();
            entity.Property(p => p.CustomInstructions).HasMaxLength(Aegis.Domain.Entities.OwnerPreferences.MaximumInstructionsLength);
            entity.Property(p => p.Revision).IsConcurrencyToken();
            entity.HasData(new OwnerPreferences());
        });
        modelBuilder.ConfigureMemory();
        modelBuilder.ConfigureNodes();
        Aegis.Infrastructure.Reminders.ReminderCompletionModelConfiguration.ConfigureReminderCompletion(modelBuilder);
        Aegis.Infrastructure.Places.PlaceModelConfiguration.ConfigurePlaces(modelBuilder);
        modelBuilder.Entity<Reminder>(e =>
        {
            e.ToTable("reminders");
            e.HasKey(r => r.Id);
            e.Property(r => r.Text).HasMaxLength(600).IsRequired();
            e.Property(r => r.TimeZoneId).HasMaxLength(100).IsRequired();
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(30);
            e.HasOne<Conversation>().WithMany().HasForeignKey(r => r.SourceConversationId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(r => new { r.Status, r.NextAttemptAt });
            e.HasIndex(r => new { r.Status, r.LeaseExpiresAt });
            e.HasIndex(r => r.DueAtUtc);
        });
        modelBuilder.Entity<PushSubscription>(e =>
        {
            e.ToTable("push_subscriptions");
            e.HasKey(s => s.Id);
            e.Property(s => s.Endpoint).HasMaxLength(2048).IsRequired();
            e.Property(s => s.P256dh).HasMaxLength(100).IsRequired();
            e.Property(s => s.Auth).HasMaxLength(40).IsRequired();
            e.Property(s => s.UserAgent).HasMaxLength(500);
            e.HasIndex(s => s.Endpoint).IsUnique();
            e.HasIndex(s => s.DeviceId);
            e.HasIndex(s => s.DisabledAt);
        });
        modelBuilder.Entity<ReminderDeliveryAttempt>(e =>
        {
            e.ToTable("reminder_node_delivery_attempts");
            e.HasKey(a => a.Id);
            e.Property(a => a.Result).HasMaxLength(40);
            e.Property(a => a.Transport).HasMaxLength(30);
            e.HasOne<Reminder>().WithMany().HasForeignKey(a => a.ReminderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AegisNode>().WithMany().HasForeignKey(a => a.NodeId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(a => new { a.ReminderId, a.Attempt }).IsUnique(false);
            e.HasIndex(a => a.CommandId);
        });
        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("conversations");
            entity.Ignore(c => c.HasUnreadProactive);
            entity.HasKey(conversation => conversation.Id);

            entity.Property(conversation => conversation.Title)
                .HasMaxLength(200)
                .IsRequired(false);

            entity.Property(conversation => conversation.TitleSource)
                .HasMaxLength(40)
                .IsRequired(false);

            entity.Property(conversation => conversation.TitleGeneratedAt)
                .IsRequired(false);

            entity.Property(conversation => conversation.TitleGenerationRawResponse)
                .IsRequired(false);

            entity.Property(conversation => conversation.DeletedAt)
                .IsRequired(false);

            entity.Property(conversation => conversation.CreatedAt)
                .IsRequired();

            entity.Property(conversation => conversation.UpdatedAt)
                .IsRequired();

            entity.HasMany(conversation => conversation.Messages)
                .WithOne(message => message.Conversation)
                .HasForeignKey(message => message.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Navigation(conversation => conversation.Messages)
                .UsePropertyAccessMode(PropertyAccessMode.Field);

            entity.HasIndex(conversation => new { conversation.DeletedAt, conversation.UpdatedAt, conversation.Id });
        });

        modelBuilder.Entity<ReminderIntervention>(e => {
            e.ToTable("reminder_interventions"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.RuleId, x.ActivationEpoch }).IsUnique();
            e.HasIndex(x => new { x.State, x.NextAttemptAt });
            e.Property(x => x.RecoveryResultsJson).HasColumnType("jsonb");
            e.HasOne<GeographicReminderRule>().WithMany().HasForeignKey(x => x.RuleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<SharedReminderIncident>(e => {
            e.ToTable("shared_reminder_incidents"); e.HasKey(x => x.Id);
            e.Property(x => x.GroupKey).HasMaxLength(200);
            e.HasIndex(x => x.GroupKey).IsUnique().HasFilter("\"ResolvedAt\" IS NULL");
            e.HasIndex(x => new { x.State, x.NextAttemptAt });
            e.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<ReminderIntervention>().HasOne<SharedReminderIncident>().WithMany()
            .HasForeignKey(x => x.SharedIncidentId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<ProactiveNotification>(e => {
            e.ToTable("proactive_notifications"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.MessageId, x.NodeId }).IsUnique().AreNullsDistinct(false);
            e.HasIndex(x => new { x.State, x.NextAttemptAt });
            e.HasOne<ChatMessage>().WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ChatMessage>().HasIndex(x => new { x.ConversationId, x.ProactiveSequence }).IsUnique();

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.ToTable("chat_messages");
            entity.HasKey(message => message.Id);

            entity.Property(message => message.Role)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(message => message.Content)
                .IsRequired();

            entity.Property(message => message.Model)
                .HasMaxLength(200);

            entity.Property(message => message.PromptSnapshot);

            entity.Property(message => message.RuntimeContextSnapshot);

            entity.Property(message => message.MetadataJson)
                .HasColumnType("jsonb");

            entity.Property(message => message.CreatedAt)
                .IsRequired();

            entity.Property(message => message.UpdatedAt)
                .IsRequired();
        });

        modelBuilder.Entity<ChatAttachment>(entity =>
        {
            entity.ToTable("chat_attachments");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.FileName).HasMaxLength(255);
            entity.Property(a => a.ContentType).HasMaxLength(128);
            entity.HasIndex(a => new { a.MessageId, a.Position }).IsUnique();
            entity.HasOne(a => a.Message).WithMany().HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UploadedChatFile>(entity =>
        {
            entity.ToTable("chat_uploads");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.FileName).HasMaxLength(255);
            entity.Property(u => u.ContentType).HasMaxLength(128);
            entity.Property(u => u.AttachmentId).IsConcurrencyToken();
            entity.HasIndex(u => u.ExpiresAt).HasFilter("\"AttachmentId\" IS NULL");
            entity.HasOne<ChatAttachment>().WithOne(a => a.Upload)
                .HasForeignKey<UploadedChatFile>(u => u.AttachmentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatUploadChunk>(entity =>
        {
            entity.ToTable("chat_upload_chunks");
            entity.HasKey(c => new { c.UploadId, c.Position });
            entity.HasOne<UploadedChatFile>().WithMany().HasForeignKey(c => c.UploadId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageFeedback>(entity =>
        {
            entity.ToTable("message_feedback");
            entity.HasKey(feedback => feedback.Id);

            entity.Property(feedback => feedback.Rating)
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(feedback => feedback.Reason)
                .HasMaxLength(80);

            entity.Property(feedback => feedback.Comment);

            entity.Property(feedback => feedback.CorrectedAnswer);

            entity.Property(feedback => feedback.MetadataJson)
                .HasColumnType("jsonb");

            entity.Property(feedback => feedback.CreatedAt)
                .IsRequired();

            entity.Property(feedback => feedback.UpdatedAt)
                .IsRequired();

            entity.HasOne(feedback => feedback.Conversation)
                .WithMany()
                .HasForeignKey(feedback => feedback.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(feedback => feedback.Message)
                .WithMany()
                .HasForeignKey(feedback => feedback.MessageId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(feedback => feedback.MessageId);
            entity.HasIndex(feedback => feedback.ConversationId);
            entity.HasIndex(feedback => feedback.Rating);
            entity.HasIndex(feedback => feedback.CreatedAt);
        });

        modelBuilder.Entity<ResearchJob>(entity =>
        {
            entity.ToTable("research_jobs");
            entity.HasKey(job => job.Id);
            entity.Property(job => job.Id).ValueGeneratedNever();
            entity.Property(job => job.State).HasMaxLength(20).IsRequired();
            entity.Property(job => job.Phase).HasMaxLength(20);
            entity.Property(job => job.ErrorCode).HasMaxLength(40);
            entity.Property(job => job.SourcesJson).HasColumnType("jsonb").IsRequired();
            entity.Property(job => job.Revision).IsConcurrencyToken();
            entity.HasIndex(job => new { job.State, job.CreatedAt });
            entity.HasIndex(job => new { job.State, job.DeadlineAt });
            entity.HasIndex(job => new { job.ConversationId, job.CreatedAt });
            entity.HasOne<Conversation>().WithMany().HasForeignKey(job => job.ConversationId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ChatMessage>().WithMany().HasForeignKey(job => job.UserMessageId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ChatMessage>().WithMany().HasForeignKey(job => job.ResultMessageId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WebSearchUsageEntry>(entity =>
        {
            entity.ToTable("web_search_usage");
            entity.HasKey(item => item.CallId);
            entity.Property(item => item.CallId).HasMaxLength(200);
            entity.Property(item => item.ResponseId).HasMaxLength(200);
            entity.Property(item => item.ActionType).HasMaxLength(30).IsRequired();
            entity.Property(item => item.Status).HasMaxLength(30).IsRequired();
            entity.Property(item => item.FirstObservedAt).IsRequired();
            entity.Property(item => item.LastObservedAt).IsRequired();
            entity.HasIndex(item => item.FirstObservedAt);
        });

        modelBuilder.Entity<LlmRequestAudit>(entity =>
        {
            entity.ToTable("llm_request_audits");
            entity.HasKey(audit => audit.Id);

            entity.Property(audit => audit.Provider)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(audit => audit.Model)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(audit => audit.Success)
                .IsRequired();

            entity.Property(audit => audit.DurationMilliseconds)
                .IsRequired();

            entity.Property(audit => audit.RequestPayloadJson)
                .HasColumnType("jsonb")
                .IsRequired();

            entity.Property(audit => audit.ResponseBody);
            entity.Property(audit => audit.FailureReason);

            entity.Property(audit => audit.ErrorType)
                .HasMaxLength(500);

            entity.Property(audit => audit.CreatedAt)
                .IsRequired();

            entity.Property(audit => audit.UpdatedAt)
                .IsRequired();

            entity.HasOne(audit => audit.Conversation)
                .WithMany()
                .HasForeignKey(audit => audit.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(audit => audit.UserMessage)
                .WithMany()
                .HasForeignKey(audit => audit.UserMessageId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne(audit => audit.AssistantMessage)
                .WithMany()
                .HasForeignKey(audit => audit.AssistantMessageId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasIndex(audit => audit.ConversationId);
            entity.HasIndex(audit => audit.UserMessageId);
            entity.HasIndex(audit => audit.AssistantMessageId);
            entity.HasIndex(audit => audit.Success);
            entity.HasIndex(audit => audit.CreatedAt);
        });

        modelBuilder.Entity<EmailAccountConnection>(entity =>
        {
            entity.ToTable("email_account_connections");
            entity.HasKey(connection => connection.Id);

            entity.Property(connection => connection.Provider)
                .HasMaxLength(40)
                .IsRequired();

            entity.Property(connection => connection.EmailAddress)
                .HasMaxLength(320)
                .IsRequired(false);

            entity.Property(connection => connection.AccessTokenEncrypted)
                .IsRequired();

            entity.Property(connection => connection.RefreshTokenEncrypted)
                .IsRequired(false);

            entity.Property(connection => connection.AccessTokenExpiresAt)
                .IsRequired();

            entity.Property(connection => connection.Scopes)
                .IsRequired();

            entity.Property(connection => connection.DisconnectedAt)
                .IsRequired(false);

            entity.Property(connection => connection.CreatedAt)
                .IsRequired();

            entity.Property(connection => connection.UpdatedAt)
                .IsRequired();

            entity.HasIndex(connection => new { connection.Provider, connection.DisconnectedAt });
        });

        modelBuilder.Entity<PendingEmailAction>(entity =>
        {
            entity.ToTable("pending_email_actions");
            entity.HasKey(action => action.Id);

            entity.Property(action => action.ActionType)
                .HasMaxLength(80)
                .IsRequired();

            entity.Property(action => action.EmailIdsJson)
                .HasColumnType("jsonb")
                .IsRequired();

            entity.Property(action => action.HumanSummary)
                .HasMaxLength(500)
                .IsRequired();

            entity.Property(action => action.ExpiresAt)
                .IsRequired();

            entity.Property(action => action.ConfirmedAt)
                .IsRequired(false);

            entity.Property(action => action.CancelledAt)
                .IsRequired(false);

            entity.Property(action => action.ExecutedAt)
                .IsRequired(false);

            entity.Property(action => action.MayHaveAppliedChanges)
                .HasDefaultValue(false)
                .IsRequired();

            entity.Property(action => action.CreatedAt)
                .IsRequired();

            entity.Property(action => action.UpdatedAt)
                .IsRequired();

            entity.HasOne(action => action.Conversation)
                .WithMany()
                .HasForeignKey(action => action.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(action => new { action.ConversationId, action.ExpiresAt });
            entity.HasIndex(action => action.ActionType);
        });

        modelBuilder.Entity<EmailActionAudit>(entity =>
        {
            entity.ToTable("email_action_audits");
            entity.HasKey(audit => audit.Id);

            entity.Property(audit => audit.ActionType)
                .HasMaxLength(80)
                .IsRequired();

            entity.Property(audit => audit.EmailIdsJson)
                .HasColumnType("jsonb")
                .IsRequired();

            entity.Property(audit => audit.Success)
                .IsRequired();

            entity.Property(audit => audit.FailureReason);

            entity.Property(audit => audit.CreatedAt)
                .IsRequired();

            entity.Property(audit => audit.UpdatedAt)
                .IsRequired();

            entity.HasOne(audit => audit.Conversation)
                .WithMany()
                .HasForeignKey(audit => audit.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(audit => audit.UserConfirmationMessage)
                .WithMany()
                .HasForeignKey(audit => audit.UserConfirmationMessageId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasIndex(audit => audit.ConversationId);
            entity.HasIndex(audit => audit.UserConfirmationMessageId);
            entity.HasIndex(audit => audit.ActionType);
            entity.HasIndex(audit => audit.CreatedAt);
            entity.HasIndex(audit => audit.Success);
        });

        modelBuilder.Entity<PendingCalendarAction>(entity =>
        {
            entity.ToTable("pending_calendar_actions");
            entity.HasKey(action => action.Id);

            entity.Property(action => action.ActionType)
                .HasMaxLength(80)
                .IsRequired();

            entity.Property(action => action.PayloadJson)
                .HasColumnType("jsonb")
                .IsRequired();

            entity.Property(action => action.CalendarId).HasMaxLength(1024).IsRequired();
            entity.Property(action => action.EventId).HasMaxLength(1024).IsRequired();

            entity.Property(action => action.HumanSummary)
                .HasMaxLength(500)
                .IsRequired();

            entity.Property(action => action.ExpiresAt)
                .IsRequired();

            entity.Property(action => action.ConfirmedAt)
                .IsRequired(false);

            entity.Property(action => action.CancelledAt)
                .IsRequired(false);

            entity.Property(action => action.ExecutedAt)
                .IsRequired(false);

            entity.Property(action => action.MayHaveAppliedChanges)
                .HasDefaultValue(false)
                .IsRequired();

            entity.Property(action => action.CreatedAt)
                .IsRequired();

            entity.Property(action => action.UpdatedAt)
                .IsRequired();

            entity.HasOne(action => action.Conversation)
                .WithMany()
                .HasForeignKey(action => action.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(action => new { action.ConversationId, action.ExpiresAt });
            entity.HasIndex(action => action.ActionType);
        });

        modelBuilder.Entity<CalendarActionAudit>(entity =>
        {
            entity.ToTable("calendar_action_audits");
            entity.HasKey(audit => audit.Id);

            entity.Property(audit => audit.ActionType)
                .HasMaxLength(80)
                .IsRequired();

            entity.Property(audit => audit.CalendarId).HasMaxLength(1024).IsRequired();

            entity.Property(audit => audit.EventId)
                .HasMaxLength(1024)
                .IsRequired();

            entity.Property(audit => audit.Success)
                .IsRequired();

            entity.Property(audit => audit.FailureReason);

            entity.Property(audit => audit.CreatedAt)
                .IsRequired();

            entity.Property(audit => audit.UpdatedAt)
                .IsRequired();

            entity.HasOne(audit => audit.Conversation)
                .WithMany()
                .HasForeignKey(audit => audit.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(audit => audit.UserConfirmationMessage)
                .WithMany()
                .HasForeignKey(audit => audit.UserConfirmationMessageId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasIndex(audit => audit.PendingActionId);
            entity.HasIndex(audit => audit.ConversationId);
            entity.HasIndex(audit => audit.UserConfirmationMessageId);
            entity.HasIndex(audit => audit.ActionType);
            entity.HasIndex(audit => audit.CreatedAt);
            entity.HasIndex(audit => audit.Success);
        });

        modelBuilder.Entity<ToolContextEntry>(entity =>
        {
            entity.ToTable("tool_context_entries");
            entity.HasKey(entry => entry.Id);

            entity.Property(entry => entry.Scope)
                .HasMaxLength(80)
                .IsRequired();

            entity.Property(entry => entry.EntryType)
                .HasMaxLength(80)
                .IsRequired();

            entity.Property(entry => entry.Key)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(entry => entry.DataJson)
                .HasColumnType("jsonb")
                .IsRequired();

            entity.Property(entry => entry.SourceToolName)
                .HasMaxLength(120)
                .IsRequired();

            entity.Property(entry => entry.ExpiresAt)
                .IsRequired();

            entity.Property(entry => entry.ReplacedAt)
                .IsRequired(false);

            entity.Property(entry => entry.CreatedAt)
                .IsRequired();

            entity.Property(entry => entry.UpdatedAt)
                .IsRequired();

            entity.HasOne(entry => entry.Conversation)
                .WithMany()
                .HasForeignKey(entry => entry.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(entry => new { entry.ConversationId, entry.Scope, entry.EntryType, entry.Key });
            entity.HasIndex(entry => new { entry.ConversationId, entry.Scope, entry.ExpiresAt });
            entity.HasIndex(entry => entry.ReplacedAt);
        });
    }
}
