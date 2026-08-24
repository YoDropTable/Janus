using Janus.Core.Abstractions;
using Janus.Server;
using Janus.Storage;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

var databasePath = builder.Configuration["JANUS_DB_PATH"];
if (string.IsNullOrWhiteSpace(databasePath))
{
    databasePath = Path.Combine(builder.Environment.ContentRootPath, "data", "janus.db");
}

builder.Services.AddSingleton<IJanusRepository>(_ => new SqliteJanusRepository(databasePath));
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<JanusTools>();

var app = builder.Build();

await app.Services.GetRequiredService<IJanusRepository>().InitializeAsync();

app.MapGet("/health", () => Results.Ok(new { service = "janus", status = "ok" }));
app.MapMcp("/mcp");

app.Run();
