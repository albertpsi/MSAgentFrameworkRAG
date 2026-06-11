using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MSAgentFrameworkRAG;
using MSAgentFrameworkRAG.Interfaces;
using MSAgentFrameworkRAG.Services;
using MSAgentFrameworkRAG.Helpers;
using MSAgentFrameworkRAG.Jobs;
using Quartz;
using System;
using System.IO;
using System.Linq;
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

// Configure Options Pattern for Secrets and Custom Settings
builder.Services.Configure<OpenAISettings>(builder.Configuration.GetSection(OpenAISettings.Position));
builder.Services.Configure<PineconeSettings>(builder.Configuration.GetSection(PineconeSettings.Position));
builder.Services.Configure<ParserSettings>(builder.Configuration.GetSection(ParserSettings.Position));
builder.Services.Configure<StorageSettings>(builder.Configuration.GetSection(StorageSettings.Position));

// Register HttpClient
builder.Services.AddHttpClient();

// Register custom services with correct lifetimes
builder.Services.AddSingleton<SessionCache>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<IDocumentIngestionService, DocumentIngestionService>();
builder.Services.AddScoped<IRetrievalService, RetrievalService>();
builder.Services.AddScoped<IChatAgentService, ChatAgentService>();
builder.Services.AddScoped<IMetadataExtractionService, MetadataExtractionService>();
builder.Services.AddScoped<IRerankService, RerankService>();

// Register Parser components
builder.Services.AddScoped<DoclingParser>();
builder.Services.AddScoped<IParserFactory, ParserFactory>();

// Configure Controllers
builder.Services.AddControllers();

// Register Quartz.NET with periodic background IngestionPollingJob
builder.Services.AddQuartz(q =>
{
    q.UseMicrosoftDependencyInjectionJobFactory();

    var jobKey = new JobKey("IngestionPollingJob");
    q.AddJob<IngestionPollingJob>(opts => opts.WithIdentity(jobKey));
    q.AddTrigger(opts => opts
        .ForJob(jobKey)
        .WithIdentity("IngestionPollingTrigger")
        .StartNow()
        .WithSimpleSchedule(x => x.WithIntervalInSeconds(10).RepeatForever())
    );
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
    await dbContext.Database.EnsureCreatedAsync();

    // Recover from any stuck ingestion states due to unexpected system crashes/restarts
    try
    {
        var stuckDocs = await dbContext.UploadedDocuments
            .Where(d => d.Status == "Processing")
            .ToListAsync();

        if (stuckDocs.Any())
        {
            foreach (var doc in stuckDocs)
            {
                doc.Status = "Failed";
                doc.ErrorMessage = "Ingestion interrupted due to system restart.";
            }
            dbContext.SaveChanges();
            Console.WriteLine($"[Program] Cleaned up {stuckDocs.Count} stuck processing documents on startup.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Program] Failed to clean up stuck documents: {ex.Message}");
    }
}

// Ensure storage folders are created on startup
using (var scope = app.Services.CreateScope())
{
    var storageSettings = scope.ServiceProvider.GetRequiredService<IOptions<StorageSettings>>().Value;
    if (!Directory.Exists(storageSettings.DocumentsDirectory))
    {
        Directory.CreateDirectory(storageSettings.DocumentsDirectory);
    }
    if (!Directory.Exists(storageSettings.ParsedDirectory))
    {
        Directory.CreateDirectory(storageSettings.ParsedDirectory);
    }
}

app.UseRouting();

app.MapControllers();

app.Run();
