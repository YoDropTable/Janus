var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { service = "janus", status = "ok" }));

// TODO v0.1: register the official MCP C# SDK, Janus tools, and SQLite repository.
// MCP is the product interface; /health is operational only.

app.Run();
