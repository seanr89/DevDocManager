using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Identity;
using Ddm.Api.Projects;
using Ddm.Api.Storage;
using Ddm.Api.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// --- services (one line per module, in task order)
builder.Services.AddDdmProblemDetails();
builder.Services.AddDdmData();
builder.Services.AddDdmAuthentication();
builder.Services.AddScoped<ProjectAuthorizer>();
builder.Services.AddDdmStorage(builder.Configuration);
builder.Services.AddScoped<DocumentService>();
builder.Services.AddSingleton<MarkdownRenderer>();
builder.Services.AddOpenApi();

var app = builder.Build();
app.MigrateIfConfigured();

// --- pipeline
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();
app.EnsureAuthConfigured();

// --- endpoints
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapReadiness();
app.MapOpenApi("/api/v1/openapi.json");

var v1 = app.MapGroup("/api/v1").RequireAuthorization();
v1.MapMe();
v1.MapProjects();
v1.MapMembers();
v1.MapAudit();
v1.MapTokens();
v1.MapDocuments();

app.Run();

public partial class Program;
