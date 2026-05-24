using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MSAgentFrameworkRAG;
using MSAgentFrameworkRAG.Interfaces;
using MSAgentFrameworkRAG.Services;
using Quartz;
using System.IO;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File("logs/rag_agent_log.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

AppDomain.CurrentDomain.ProcessExit += (s, e) => Log.CloseAndFlush();

var builder = WebApplication.CreateBuilder(args);

// Enable Serilog as the logging provider
builder.Host.UseSerilog();

// Configure SQL Server Database Context with standard isolated lifetimes to prevent pooling concurrency issues
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Configure Options Pattern for Secrets
builder.Services.Configure<OpenAISettings>(builder.Configuration.GetSection(OpenAISettings.Position));
builder.Services.Configure<PineconeSettings>(builder.Configuration.GetSection(PineconeSettings.Position));

// Register custom services with correct lifetimes
builder.Services.AddSingleton<SessionCache>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();
builder.Services.AddScoped<IRetrievalService, RetrievalService>();
builder.Services.AddScoped<IChatAgentService, ChatAgentService>();
builder.Services.AddScoped<IMetadataExtractionService, MetadataExtractionService>();
builder.Services.AddScoped<IRerankService, RerankService>();

// Configure Controllers
builder.Services.AddControllers();

// Register Quartz.NET
builder.Services.AddQuartz(q =>
{
    q.UseMicrosoftDependencyInjectionJobFactory();
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors("AllowAll");

// Ensure SQL Server database and tables are created and migrate schema
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.EnsureCreated();

    // Recover from any stuck ingestion states due to unexpected system crashes/restarts
    try
    {
        var stuckDocs = dbContext.UploadedDocuments
            .Where(d => d.Status == "Pending" || d.Status == "Processing")
            .ToList();

        if (stuckDocs.Any())
        {
            foreach (var doc in stuckDocs)
            {
                doc.Status = "Failed";
                doc.ErrorMessage = "Ingestion interrupted due to system restart.";
            }
            dbContext.SaveChanges();
            Console.WriteLine($"[Program] Cleaned up {stuckDocs.Count} stuck documents on startup.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Program] Failed to clean up stuck documents: {ex.Message}");
    }

    // Cold-start dynamic ADO.NET column and table migrations to update existing databases
    var sql = @"
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('UploadedDocuments') AND name = 'DocumentType')
        ALTER TABLE UploadedDocuments ADD DocumentType NVARCHAR(MAX) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('UploadedDocuments') AND name = 'Company')
        ALTER TABLE UploadedDocuments ADD Company NVARCHAR(MAX) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('UploadedDocuments') AND name = 'FiscalQuarter')
        ALTER TABLE UploadedDocuments ADD FiscalQuarter NVARCHAR(MAX) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('UploadedDocuments') AND name = 'FiscalYear')
        ALTER TABLE UploadedDocuments ADD FiscalYear INT NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('UploadedDocuments') AND name = 'PublicationDate')
        ALTER TABLE UploadedDocuments ADD PublicationDate NVARCHAR(MAX) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('UploadedDocuments') AND name = 'Version')
        ALTER TABLE UploadedDocuments ADD Version NVARCHAR(MAX) NULL;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('UploadedDocuments') AND name = 'IsLatest')
        ALTER TABLE UploadedDocuments ADD IsLatest BIT NOT NULL DEFAULT 0;
    IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('UploadedDocuments') AND name = 'ChunkCount')
        ALTER TABLE UploadedDocuments ADD ChunkCount INT NOT NULL DEFAULT 0;

    IF OBJECT_ID('ParentChunks', 'U') IS NULL
    BEGIN
        CREATE TABLE ParentChunks (
            Id NVARCHAR(450) PRIMARY KEY,
            DocumentId NVARCHAR(450) NOT NULL FOREIGN KEY REFERENCES UploadedDocuments(Id) ON DELETE CASCADE,
            Content NVARCHAR(MAX) NOT NULL,
            CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE()
        );
    END
    ";
    
    try
    {
        dbContext.Database.ExecuteSqlRaw(sql);
        Console.WriteLine("[Program] Dynamic database schema migration completed successfully.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Program] Dynamic database schema migration bypassed or failed: {ex.Message}");
    }
}

// Create uploads folder inside wwwroot
var uploadsDir = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"), "uploads");
if (!Directory.Exists(uploadsDir))
{
    Directory.CreateDirectory(uploadsDir);
}

app.UseRouting();

app.MapControllers();

app.Run();
