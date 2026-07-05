using Aegis.Application.Chat;
using Aegis.Application.Common;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace Aegis.Infrastructure.Persistence;

public sealed class AegisDbContext(DbContextOptions<AegisDbContext> options) : DbContext(options), IAegisDbContext
{
    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    public DbSet<MessageFeedback> MessageFeedback => Set<MessageFeedback>();

    public DbSet<LlmRequestAudit> LlmRequestAudits => Set<LlmRequestAudit>();

    IQueryable<Conversation> IAegisDbContext.Conversations => Conversations;

    IQueryable<ChatMessage> IAegisDbContext.ChatMessages => ChatMessages;

    IQueryable<MessageFeedback> IAegisDbContext.MessageFeedback => MessageFeedback;

    IQueryable<LlmRequestAudit> IAegisDbContext.LlmRequestAudits => LlmRequestAudits;

    public void AddConversation(Conversation conversation)
    {
        Conversations.Add(conversation);
    }

    public void AddChatMessage(ChatMessage message)
    {
        ChatMessages.Add(message);
    }

    public void AddMessageFeedback(MessageFeedback feedback)
    {
        MessageFeedback.Add(feedback);
    }

    public void AddLlmRequestAudit(LlmRequestAudit audit)
    {
        LlmRequestAudits.Add(audit);
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
                    ) AS "LastMessageContent"
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
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
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

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("conversations");
            entity.HasKey(conversation => conversation.Id);

            entity.Property(conversation => conversation.Title)
                .HasMaxLength(200)
                .IsRequired(false);

            entity.Property(conversation => conversation.TitleSource)
                .HasMaxLength(40)
                .IsRequired(false);

            entity.Property(conversation => conversation.TitleGeneratedAt)
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
    }
}
