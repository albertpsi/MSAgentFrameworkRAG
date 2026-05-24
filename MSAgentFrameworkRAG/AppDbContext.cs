using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;

namespace MSAgentFrameworkRAG
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<UploadedDocument> UploadedDocuments { get; set; }
        public DbSet<DbConversation> Conversations { get; set; }
        public DbSet<DbChatMessage> ChatMessages { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DbConversation>()
                .HasMany(c => c.Messages)
                .WithOne()
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

    public class DbConversation
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public List<DbChatMessage> Messages { get; set; } = new();
    }

    public class DbChatMessage
    {
        public int Id { get; set; }
        public string ConversationId { get; set; } = string.Empty;
        public string Sender { get; set; } = "user"; // user, assistant
        public string Text { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string? CitationsJson { get; set; } // Serialized List<SourceCitation>
    }
}
