using Ddm.Api.Common;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// --- services (one line per module, in task order)
builder.Services.AddDdmProblemDetails();

var app = builder.Build();

// --- pipeline
app.UseExceptionHandler();
app.UseStatusCodePages();

// --- endpoints
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program;
