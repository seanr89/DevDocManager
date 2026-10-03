using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Identity;
using Ddm.Api.Projects;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// --- services (one line per module, in task order)
builder.Services.AddDdmProblemDetails();
builder.Services.AddDdmData();
builder.Services.AddDdmAuthentication();
builder.Services.AddScoped<ProjectAuthorizer>();

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

var v1 = app.MapGroup("/api/v1").RequireAuthorization();
v1.MapMe();
v1.MapProjects();

app.Run();

public partial class Program;
