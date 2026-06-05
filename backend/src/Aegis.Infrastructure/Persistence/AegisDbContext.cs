using Aegis.Application.Common;
using Aegis.Domain;
using Aegis.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Persistence;

public sealed class AegisDbContext(DbContextOptions<AegisDbContext> options) : DbContext(options), IAegisDbContext
{
    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    public DbSet<MessageFeedback> MessageFeedback => Set<MessageFeedback>();

    IQueryable<Conversation> IAegisDbContext.Conversations => Conversations;

    IQueryable<ChatMessage> IAegisDbContext.ChatMessages => ChatMessages;

    IQueryable<MessageFeedback> IAegisDbContext.MessageFeedback => MessageFeedback;

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

    public async Task<ChatMessage?> GetChatMessageAsync(
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        return await ChatMessages
            .FirstOrDefaultAsync(message => message.Id == messageId, cancellationToken);
    }

    public async Task<ChatMessage?> GetPreviousUserMessageAsync(
        Guid conversationId,
        DateTimeOffset before,
        CancellationToken cancellationToken = default)
    {
        return await ChatMessages
            .Where(message =>
                message.ConversationId == conversationId &&
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
            .FirstOrDefaultAsync(feedback => feedback.Id == feedbackId, cancellationToken);
    }

    public async Task<IReadOnlyList<MessageFeedback>> GetRecentMessageFeedbackAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Max(1, limit);
        return await MessageFeedback
            .Include(feedback => feedback.Message)
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
            .FirstOrDefaultAsync(conversation => conversation.Id == conversationId, cancellationToken);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetRecentMessagesAsync(
        Guid conversationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Max(1, limit);
        var messages = await ChatMessages
            .Where(message => message.ConversationId == conversationId)
            .OrderByDescending(message => message.CreatedAt)
            .ThenByDescending(message => message.Id)
            .Take(normalizedLimit)
            .ToListAsync(cancellationToken);

        return messages
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .ToList();
    }

    public async Task<IReadOnlyList<Conversation>> GetRecentConversationsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedLimit = Math.Max(1, limit);
        return await Conversations
            .Include(conversation => conversation.Messages)
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .ThenByDescending(conversation => conversation.Id)
            .Take(normalizedLimit)
            .ToListAsync(cancellationToken);
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
    }
}
