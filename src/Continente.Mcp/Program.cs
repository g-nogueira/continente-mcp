using Continente.Mcp;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ContinenteOptions>(
    builder.Configuration.GetSection(ContinenteOptions.SectionName));

builder.Services.AddSingleton<ContinenteClient>();

builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.SessionMode = HttpServerSessionMode.Stateless;
    })
    .WithToolsFromAssembly();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "continente-mcp",
    status = "ok",
    mcp = "/mcp"
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapMcp("/mcp");

app.Run();
