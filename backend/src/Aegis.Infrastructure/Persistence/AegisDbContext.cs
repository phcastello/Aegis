using Aegis.Application.Common;
using Aegis.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aegis.Infrastructure.Persistence;

public sealed class AegisDbContext(DbContextOptions<AegisDbContext> options) : DbContext(options), IAegisDbContext
{
    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    IQueryable<Conversation> IAegisDbContext.Conversations => Conversations;

    IQueryable<ChatMessage> IAegisDbContext.ChatMessages => ChatMessages;

    public void AddConversation(Conversation conversation)
    {
        Conversations.Add(conversation);
    }

    public void AddChatMessage(ChatMessage message)
    {
        ChatMessages.Add(message);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.ToTable("conversations");
            entity.HasKey(conversation => conversation.Id);

            entity.Property(conversation => conversation.Title)
                .HasMaxLength(200)
                .IsRequired();

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
    }
}
