using Ddm.Api.Common;
using Ddm.Api.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// --- services (one line per module, in task order)
builder.Services.AddDdmProblemDetails();
builder.Services.AddDdmData();

var app = builder.Build();
app.MigrateIfConfigured();

// --- pipeline
app.UseExceptionHandler();
app.UseStatusCodePages();

// --- endpoints
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapReadiness();

app.Run();

public partial class Program;
