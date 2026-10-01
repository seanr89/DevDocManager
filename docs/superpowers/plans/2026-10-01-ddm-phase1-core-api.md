# DDM Phase 1 Core API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Developer Documentation Manager's phase 1 core API: projects, roles, API tokens, and versioned markdown documents with ETag concurrency, on .NET minimal API and PostgreSQL.

**Architecture:** One ASP.NET Core minimal-API service (`Ddm.Api`) organised as a modular monolith (folders `Common`, `Domain`, `Data`, `Identity`, `Projects`, `Tokens`, `Storage`, `Documents`). Metadata lives in PostgreSQL via EF Core; document bodies live in S3-compatible object storage behind `IBlobStore`, content-addressed by SHA-256. Every route sits under `/api/v1`, goes through one authorization module, and returns RFC 9457 problem details.

**Tech Stack:** .NET 10 (`net10.0`), ASP.NET Core minimal APIs, EF Core + Npgsql (PostgreSQL 17), AWSSDK.S3 (MinIO locally), Markdig, HtmlSanitizer, YamlDotNet, xUnit v2, Testcontainers, OpenTelemetry. The React/TypeScript web client is a later plan.

**Spec:** `Developer Documentation Manager Design Document.md` (repo root)

## Scope

In this plan: F1 (projects), F2 (markdown documents with a path tree, no editor UI), F9 (roles), F7 minus diff (version list, read, restore), F8 minus bulk publish (API tokens), audit log, health/metrics/OpenAPI.

Deferred to later plans: web client, assets (F3), OpenAPI spec upload/rendering (F4), tags (F5), search (F6), version diff, bulk publish endpoint, webhooks (F10), outbox/workers, rendered-HTML cache, public visibility, releases, blob garbage collection.

**Decisions this plan makes where the spec is silent:**
- Any signed-in user may create a project and becomes its admin. API tokens may not create projects.
- Member ids are the opaque OIDC `sub` string; there is no users table.
- A token's effective role is `Reader` (scope `read`) or `Editor` (scope `write`); tokens never administer.
- `PUT` to an existing document requires `If-Match` (428 otherwise); `If-None-Match: *` means create-only. Re-sending identical content is a no-op (no new version).
- Non-readers get `404 project_not_found`, identical to a missing project, never 403.
- Document paths: `/`-separated segments of `[A-Za-z0-9_][A-Za-z0-9._-]*`, must end `.md`, max 255 chars.

## Global Constraints

- All routes live under `/api/v1` and every content route is nested under a project.
- `Authorization: Bearer` with a user session token or an API token. API tokens are scoped to one project and to read or write.
- Errors are RFC 9457 problem details with a stable `code` field.
- Pagination is cursor-based: `limit` and `cursor` parameters, with a `next` cursor in the response.
- Writes send `If-Match` with the current version's ETag; a stale write returns `412` rather than overwriting.
- Roles per project: reader (read), editor (read + create/edit content), admin (everything incl. members, tokens, settings). Visibility is `private` (members only) or `internal` (any signed-in user can read).
- API tokens are stored only as a hash, shown once, and revocable.
- Content is never lost on a failed write; documents remain plain markdown with front matter; untrusted markdown is sanitized before render.
- Every write and permission change records who, what and when; the audit log is admin-readable per project.
- Structured logs, metrics and health checks from the first release.
- Tests need a running Docker daemon (Testcontainers). Use xUnit v2 (`xunit` 2.9.x), not `xunit.v3`.
- Work on a feature branch or worktree, not on `speccing`.

## Review Focus

1. Concurrent writers: N parallel `PUT`s with the same `If-Match` must give exactly one 200, the rest 412, never a 500 (Task 10).
2. Re-publishing identical content (CI re-runs on every merge) must not create a new version (Task 10).
3. Front matter edge cases: an unclosed leading `---` is body text, not front matter; a YAML alias bomb or deeply nested YAML is rejected with 400, not a hang or crash (Task 9).
4. The last admin can never be removed or demoted, even when two admins race (Task 6).
5. A non-member sees the same 404 for a private project as for a missing one, and a token for project A sees nothing of project B (Tasks 5, 7).

## File Structure

```
Ddm.sln  docker-compose.yml
src/Ddm.Api/
  Program.cs                      thin: one registration line per module
  appsettings.json / appsettings.Development.json
  Common/    ApiException, ProblemCodes, ApiExceptionHandler, ProblemDetailsSetup, Paging, Wire
  Domain/    Project, Member, Document, ContentVersion, ApiToken, AuditEntry (+ enums)
  Data/      DdmDbContext, DdmDbContextFactory, DataSetup, DbExceptions, AuditExtensions, ProjectLocks, Migrations/
  Identity/  Caller, AuthSetup, ApiTokenSecrets, ApiTokenHandler, ProjectAuthorizer, MeEndpoints
  Projects/  ProjectDtos, ProjectValidation, ProjectEndpoints, MemberEndpoints, AuditEndpoints
  Tokens/    TokenEndpoints
  Storage/   IBlobStore, S3Options, S3BlobStore, StorageSetup
  Documents/ DocumentPath, DocRoute, FrontMatter, ContentNegotiation, MarkdownRenderer, Preconditions, DocumentDtos, DocumentService, DocumentEndpoints
tests/Ddm.Api.Tests/
  GlobalUsings.cs
  Infrastructure/  PostgresFixture, DdmApiFactory, ApiTestBase, TestAuth, InMemoryBlobStore
  (one test file per feature)
```

---

### Task 1: Solution scaffold and liveness endpoint

**Files:**
- Create: `Ddm.sln`, `.gitignore`, `docker-compose.yml`, `src/Ddm.Api/*`, `tests/Ddm.Api.Tests/*`
- Modify: `README.md`
- Test: `tests/Ddm.Api.Tests/HealthTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Program.cs` with the anchors `// --- services`, `// --- pipeline`, `// --- endpoints` that later tasks add lines under; public `Program` class; `GET /healthz`.

- [ ] **Step 1: Scaffold projects**

```bash
cd /Users/seanrafferty/Documents/development/repos/DevDocManager
dotnet new sln -n Ddm
dotnet new gitignore
dotnet new web -n Ddm.Api -o src/Ddm.Api --framework net10.0
dotnet new xunit -n Ddm.Api.Tests -o tests/Ddm.Api.Tests --framework net10.0
rm tests/Ddm.Api.Tests/UnitTest1.cs
dotnet sln add src/Ddm.Api tests/Ddm.Api.Tests
dotnet add tests/Ddm.Api.Tests reference src/Ddm.Api
dotnet add tests/Ddm.Api.Tests package Microsoft.AspNetCore.Mvc.Testing
```

- [ ] **Step 2: Write the failing test**

`tests/Ddm.Api.Tests/GlobalUsings.cs`:
```csharp
global using System.Net;
global using System.Net.Http.Json;
global using System.Text;
global using System.Text.Json;
global using Microsoft.AspNetCore.Mvc.Testing;
```

`tests/Ddm.Api.Tests/HealthTests.cs`:
```csharp
namespace Ddm.Api.Tests;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Healthz_returns_ok_without_credentials()
    {
        var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Testing")).CreateClient();
        var response = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test`
Expected: build FAILS with CS0122 (`Program` is inaccessible) or CS0246 for `UseEnvironment` (add `using Microsoft.AspNetCore.Hosting;` to the test file if the compiler asks).

- [ ] **Step 4: Write the implementation**

`src/Ddm.Api/Program.cs`:
```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();

// --- services (one line per module, in task order)

var app = builder.Build();

// --- pipeline

// --- endpoints
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program;
```

`src/Ddm.Api/appsettings.json`:
```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*"
}
```

`src/Ddm.Api/appsettings.Development.json`:
```json
{
  "ConnectionStrings": { "Ddm": "Host=localhost;Port=5432;Database=ddm;Username=ddm;Password=ddm" },
  "Database": { "MigrateOnStart": true },
  "Auth": { "DevSigningKey": "dev-only-signing-key-change-me-0123456789", "Issuer": "ddm-dev", "Audience": "ddm-api" },
  "Storage": { "ServiceUrl": "http://localhost:9000", "AccessKey": "minioadmin", "SecretKey": "minioadmin", "Bucket": "ddm", "ForcePathStyle": true, "CreateBucket": true }
}
```

`docker-compose.yml`:
```yaml
services:
  postgres:
    image: postgres:17-alpine
    environment: { POSTGRES_USER: ddm, POSTGRES_PASSWORD: ddm, POSTGRES_DB: ddm }
    ports: ["5432:5432"]
    volumes: [pgdata:/var/lib/postgresql/data]
  minio:
    image: minio/minio:latest
    command: server /data --console-address ":9001"
    environment: { MINIO_ROOT_USER: minioadmin, MINIO_ROOT_PASSWORD: minioadmin }
    ports: ["9000:9000", "9001:9001"]
    volumes: [miniodata:/data]
volumes:
  pgdata: {}
  miniodata: {}
```

Append to `README.md`:
```markdown

## Running locally

    docker compose up -d
    dotnet run --project src/Ddm.Api      # Development env migrates the DB on start
    dotnet test                           # needs Docker for Testcontainers
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test`
Expected: PASS (1 test).

- [ ] **Step 6: Commit**

```bash
git add Ddm.sln .gitignore docker-compose.yml src tests README.md
git commit -m "feat: scaffold solution with liveness endpoint"
```

---

### Task 2: Problem details and stable error codes

**Files:**
- Create: `src/Ddm.Api/Common/ApiException.cs`, `ProblemCodes.cs`, `ApiExceptionHandler.cs`, `ProblemDetailsSetup.cs`
- Modify: `src/Ddm.Api/Program.cs`
- Test: `tests/Ddm.Api.Tests/ApiExceptionHandlerTests.cs`, `tests/Ddm.Api.Tests/ProblemDetailsTests.cs`

**Interfaces:**
- Consumes: Task 1 `Program.cs` anchors.
- Produces: `ApiException(int status, string code, string title, string? detail = null)` with factories `BadRequest(code,title,detail?)`, `Forbidden(code,title)`, `NotFound(code,title)`, `Conflict(code,title)`, `PreconditionFailed(title)`, `PreconditionRequired(title)`, `PayloadTooLarge(title)`; `ProblemCodes.ForStatus(int)`; `services.AddDdmProblemDetails()`.

- [ ] **Step 1: Write the failing tests**

`tests/Ddm.Api.Tests/ApiExceptionHandlerTests.cs`:
```csharp
using Ddm.Api.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ddm.Api.Tests;

public class ApiExceptionHandlerTests
{
    private static async Task<(bool Handled, int Status, string? ContentType, JsonElement Body)> RunAsync(Exception ex)
    {
        var services = new ServiceCollection().AddLogging().AddDdmProblemDetails().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Response.Body = new MemoryStream();
        var handler = new ApiExceptionHandler(services.GetRequiredService<IProblemDetailsService>());
        var handled = await handler.TryHandleAsync(ctx, ex, CancellationToken.None);
        ctx.Response.Body.Position = 0;
        var body = ctx.Response.Body.Length == 0 ? default : await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Response.Body);
        return (handled, ctx.Response.StatusCode, ctx.Response.ContentType, body);
    }

    [Fact]
    public async Task ApiException_becomes_problem_json_with_code()
    {
        var r = await RunAsync(ApiException.Conflict("slug_taken", "Slug taken"));
        Assert.True(r.Handled);
        Assert.Equal(409, r.Status);
        Assert.StartsWith("application/problem+json", r.ContentType);
        Assert.Equal("slug_taken", r.Body.GetProperty("code").GetString());
        Assert.Equal("Slug taken", r.Body.GetProperty("title").GetString());
        Assert.Equal(409, r.Body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Detail_is_included_when_given()
    {
        var r = await RunAsync(ApiException.BadRequest("validation_failed", "Invalid", "name is required"));
        Assert.Equal("name is required", r.Body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Malformed_request_bodies_map_to_400_not_500()
    {
        var r = await RunAsync(new BadHttpRequestException("bad json", 400));
        Assert.True(r.Handled);
        Assert.Equal(400, r.Status);
        Assert.Equal("bad_request", r.Body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_exceptions_are_left_to_the_default_500_path()
    {
        var r = await RunAsync(new InvalidOperationException("boom"));
        Assert.False(r.Handled);
    }

    [Theory]
    [InlineData(401, "unauthenticated")]
    [InlineData(404, "not_found")]
    [InlineData(412, "precondition_failed")]
    [InlineData(428, "precondition_required")]
    [InlineData(500, "internal_error")]
    [InlineData(418, "http_418")]
    public void ForStatus_maps_statuses_to_stable_codes(int status, string code) =>
        Assert.Equal(code, ProblemCodes.ForStatus(status));
}
```

`tests/Ddm.Api.Tests/ProblemDetailsTests.cs`:
```csharp
using Microsoft.AspNetCore.Hosting;

namespace Ddm.Api.Tests;

public class ProblemDetailsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Unknown_routes_return_problem_json_with_a_stable_code()
    {
        var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Testing")).CreateClient();
        var response = await client.GetAsync("/api/v1/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.Equal(404, body.GetProperty("status").GetInt32());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ApiExceptionHandlerTests|FullyQualifiedName~ProblemDetailsTests"`
Expected: build FAILS (`ApiException`, `ApiExceptionHandler` not defined).

- [ ] **Step 3: Write the implementation**

`src/Ddm.Api/Common/ApiException.cs`:
```csharp
namespace Ddm.Api.Common;

public sealed class ApiException(int status, string code, string title, string? detail = null)
    : Exception(detail ?? title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;

    public static ApiException BadRequest(string code, string title, string? detail = null) => new(400, code, title, detail);
    public static ApiException Forbidden(string code, string title) => new(403, code, title);
    public static ApiException NotFound(string code, string title) => new(404, code, title);
    public static ApiException Conflict(string code, string title) => new(409, code, title);
    public static ApiException PreconditionFailed(string title) => new(412, "precondition_failed", title);
    public static ApiException PreconditionRequired(string title) => new(428, "precondition_required", title);
    public static ApiException PayloadTooLarge(string title) => new(413, "payload_too_large", title);
}
```

`src/Ddm.Api/Common/ProblemCodes.cs`:
```csharp
namespace Ddm.Api.Common;

public static class ProblemCodes
{
    public static string ForStatus(int status) => status switch
    {
        400 => "bad_request",
        401 => "unauthenticated",
        403 => "forbidden",
        404 => "not_found",
        405 => "method_not_allowed",
        406 => "not_acceptable",
        409 => "conflict",
        412 => "precondition_failed",
        413 => "payload_too_large",
        415 => "unsupported_media_type",
        428 => "precondition_required",
        >= 500 => "internal_error",
        _ => $"http_{status}",
    };
}
```

`src/Ddm.Api/Common/ApiExceptionHandler.cs`:
```csharp
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Ddm.Api.Common;

public sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct) => ex switch
    {
        ApiException api => WriteAsync(ctx, ex, api.Status, api.Code, api.Title, api.Detail),
        BadHttpRequestException bad => WriteAsync(ctx, ex, bad.StatusCode, "bad_request", "The request could not be read", bad.Message),
        _ => ValueTask.FromResult(false),
    };

    private async ValueTask<bool> WriteAsync(HttpContext ctx, Exception ex, int status, string code, string title, string? detail)
    {
        ctx.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = ctx,
            Exception = ex,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = detail,
                Type = $"urn:ddm:problem:{code}",
                Extensions = { ["code"] = code },
            },
        });
    }
}
```

`src/Ddm.Api/Common/ProblemDetailsSetup.cs`:
```csharp
namespace Ddm.Api.Common;

public static class ProblemDetailsSetup
{
    public static IServiceCollection AddDdmProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            var status = ctx.ProblemDetails.Status ?? ctx.HttpContext.Response.StatusCode;
            ctx.ProblemDetails.Extensions.TryAdd("code", ProblemCodes.ForStatus(status));
        });
        services.AddExceptionHandler<ApiExceptionHandler>();
        return services;
    }
}
```

`Program.cs` edits: add `using Ddm.Api.Common;` as the first line; under `// --- services` add `builder.Services.AddDdmProblemDetails();`; under `// --- pipeline` add:
```csharp
app.UseExceptionHandler();
app.UseStatusCodePages();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: RFC 9457 problem details with stable error codes"
```

---

### Task 3: Data layer, migrations and integration-test infrastructure

**Files:**
- Create: `src/Ddm.Api/Domain/{Enums,Project,Member,Document,ContentVersion,ApiToken,AuditEntry}.cs`, `src/Ddm.Api/Data/{DdmDbContext,DdmDbContextFactory,DataSetup,DbExceptions}.cs`, `src/Ddm.Api/Data/Migrations/*` (generated), `tests/Ddm.Api.Tests/Infrastructure/{PostgresFixture,DdmApiFactory,ApiTestBase}.cs`
- Modify: `src/Ddm.Api/Program.cs`, `tests/Ddm.Api.Tests/HealthTests.cs`
- Test: `tests/Ddm.Api.Tests/DataLayerTests.cs`, `tests/Ddm.Api.Tests/HealthTests.cs`

**Interfaces:**
- Consumes: Task 2 `AddDdmProblemDetails`.
- Produces: entities in `Ddm.Api.Domain` (see Step 3), `DdmDbContext` with `DbSet`s `Projects, Members, Documents, Versions, ApiTokens, AuditEntries`; `ex.IsUniqueViolation()` on `DbUpdateException`; `services.AddDdmData()`; `app.MigrateIfConfigured()`; `GET /readyz`; test types `PostgresFixture.CreateDatabaseAsync()`, `DdmApiFactory(string connectionString, bool migrate = true, string environment = "Testing")`, `ApiTestBase(PostgresFixture)` with `Factory`, `Anonymous()`.

- [ ] **Step 1: Install packages and tooling**

```bash
dotnet add src/Ddm.Api package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add src/Ddm.Api package Microsoft.EntityFrameworkCore.Design
dotnet add src/Ddm.Api package Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore
dotnet add tests/Ddm.Api.Tests package Testcontainers.PostgreSql
dotnet new tool-manifest
dotnet tool install dotnet-ef
```

- [ ] **Step 2: Write the failing tests and test infrastructure**

`tests/Ddm.Api.Tests/Infrastructure/PostgresFixture.cs`:
```csharp
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ddm.Api.Tests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"ddm_{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<PostgresFixture>;
```
(If the compiler warns that the parameterless `PostgreSqlBuilder` is obsolete, pass the image to the constructor instead.)

`tests/Ddm.Api.Tests/Infrastructure/DdmApiFactory.cs`:
```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Ddm.Api.Tests.Infrastructure;

public sealed class DdmApiFactory(string connectionString, bool migrate = true, string environment = "Testing")
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ddm"] = connectionString,
            ["Database:MigrateOnStart"] = migrate ? "true" : "false",
        }));
    }
}
```

`tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs`:
```csharp
namespace Ddm.Api.Tests.Infrastructure;

/// <summary>Each test gets its own database and its own app instance.</summary>
[Collection("db")]
public abstract class ApiTestBase(PostgresFixture pg) : IAsyncLifetime
{
    protected DdmApiFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync() => Factory = new DdmApiFactory(await pg.CreateDatabaseAsync());
    public async Task DisposeAsync() => await Factory.DisposeAsync();

    protected HttpClient Anonymous() => Factory.CreateClient();
}
```

Replace `tests/Ddm.Api.Tests/HealthTests.cs`:
```csharp
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class HealthTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Healthz_returns_ok_without_credentials()
    {
        var response = await Anonymous().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_is_healthy_when_the_database_is_reachable()
    {
        var response = await Anonymous().GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_is_unhealthy_when_the_database_is_unreachable()
    {
        await using var factory = new DdmApiFactory("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=2", migrate: false);
        var response = await factory.CreateClient().GetAsync("/readyz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
```

`tests/Ddm.Api.Tests/DataLayerTests.cs`:
```csharp
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class DataLayerTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private DdmDbContext NewDb()
    {
        _ = Anonymous(); // ensure the host (and migrations) have started
        return Factory.Services.CreateScope().ServiceProvider.GetRequiredService<DdmDbContext>();
    }

    [Fact]
    public async Task Migrations_create_the_schema()
    {
        using var db = NewDb();
        Assert.Equal(0, await db.Projects.CountAsync());
        Assert.Equal(0, await db.Versions.CountAsync());
    }

    [Fact]
    public async Task Project_slug_is_unique()
    {
        using var db = NewDb();
        db.Projects.Add(new Project { Slug = "payments", Name = "A" });
        await db.SaveChangesAsync();
        db.Projects.Add(new Project { Slug = "payments", Name = "B" });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.IsUniqueViolation());
    }

    [Fact]
    public async Task Document_path_is_unique_per_project_but_not_across_projects()
    {
        using var db = NewDb();
        var a = new Project { Slug = "a", Name = "A" };
        var b = new Project { Slug = "b", Name = "B" };
        db.Projects.AddRange(a, b);
        db.Documents.AddRange(
            new Document { ProjectId = a.Id, Path = "x.md" },
            new Document { ProjectId = b.Id, Path = "x.md" });
        await db.SaveChangesAsync();
        db.Documents.Add(new Document { ProjectId = a.Id, Path = "x.md" });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.IsUniqueViolation());
    }

    [Fact]
    public async Task Deleting_a_project_cascades_to_members_documents_and_tokens()
    {
        using var db = NewDb();
        var p = new Project { Slug = "p", Name = "P" };
        db.Projects.Add(p);
        db.Members.Add(new Member { ProjectId = p.Id, UserId = "u", Role = Role.Admin });
        db.Documents.Add(new Document { ProjectId = p.Id, Path = "x.md" });
        db.ApiTokens.Add(new ApiToken { ProjectId = p.Id, Name = "t", Scope = TokenScope.Read, HashedSecret = "h", CreatedBy = "u" });
        await db.SaveChangesAsync();

        db.Projects.Remove(p);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.Members.CountAsync());
        Assert.Equal(0, await db.Documents.CountAsync());
        Assert.Equal(0, await db.ApiTokens.CountAsync());
    }

    [Fact]
    public async Task Version_numbers_are_unique_per_item()
    {
        using var db = NewDb();
        var item = Guid.NewGuid();
        ContentVersion V(int n) => new() { ItemType = ItemType.Document, ItemId = item, Number = n, ContentRef = "k", ContentSha256 = "s", Author = "u" };
        db.Versions.Add(V(1));
        await db.SaveChangesAsync();
        db.Versions.Add(V(1));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(ex.IsUniqueViolation());
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test`
Expected: build FAILS (`Ddm.Api.Domain`, `DdmDbContext`, `IsUniqueViolation` not defined).

- [ ] **Step 4: Write the domain entities**

`src/Ddm.Api/Domain/Enums.cs`:
```csharp
namespace Ddm.Api.Domain;

public enum Visibility { Private = 1, Internal = 2 }
public enum Role { Reader = 1, Editor = 2, Admin = 3 }
public enum ItemType { Document = 1, Spec = 2 }
public enum TokenScope { Read = 1, Write = 2 }
```

`src/Ddm.Api/Domain/Project.cs`:
```csharp
namespace Ddm.Api.Domain;

public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public Visibility Visibility { get; set; } = Visibility.Private;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

`src/Ddm.Api/Domain/Member.cs`:
```csharp
namespace Ddm.Api.Domain;

public class Member
{
    public Guid ProjectId { get; set; }
    public required string UserId { get; set; }
    public Role Role { get; set; }
}
```

`src/Ddm.Api/Domain/Document.cs`:
```csharp
namespace Ddm.Api.Domain;

public class Document
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public required string Path { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Front matter as a JSON object string (jsonb column).</summary>
    public string FrontMatter { get; set; } = "{}";
    public Guid? CurrentVersionId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

`src/Ddm.Api/Domain/ContentVersion.cs`:
```csharp
namespace Ddm.Api.Domain;

/// <summary>Immutable snapshot of a Document (or, later, a Spec). Table name: versions.</summary>
public class ContentVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ItemType ItemType { get; set; }
    public Guid ItemId { get; set; }
    public int Number { get; set; }
    public required string ContentRef { get; set; }
    public required string ContentSha256 { get; set; }
    public required string Author { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

`src/Ddm.Api/Domain/ApiToken.cs`:
```csharp
namespace Ddm.Api.Domain;

public class ApiToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public TokenScope Scope { get; set; }
    public required string HashedSecret { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
```

`src/Ddm.Api/Domain/AuditEntry.cs`:
```csharp
namespace Ddm.Api.Domain;

public class AuditEntry
{
    public long Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Target { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 5: Write the data layer**

`src/Ddm.Api/Data/DdmDbContext.cs`:
```csharp
using Ddm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Data;

public sealed class DdmDbContext(DbContextOptions<DdmDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Member> Members => Set<Member>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<ContentVersion> Versions => Set<ContentVersion>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Project>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Slug).HasMaxLength(64).UseCollation("C");
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Visibility).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<Member>(e =>
        {
            e.HasKey(x => new { x.ProjectId, x.UserId });
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.UserId).HasMaxLength(256).UseCollation("C");
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
        });

        b.Entity<Document>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Path).HasMaxLength(255).UseCollation("C");
            e.HasIndex(x => new { x.ProjectId, x.Path }).IsUnique();
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.FrontMatter).HasColumnType("jsonb");
        });

        b.Entity<ContentVersion>(e =>
        {
            e.ToTable("versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.ItemType).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.ItemType, x.ItemId, x.Number }).IsUnique();
            e.Property(x => x.ContentRef).HasMaxLength(512);
            e.Property(x => x.ContentSha256).HasMaxLength(64);
            e.Property(x => x.Author).HasMaxLength(300);
            e.Property(x => x.Message).HasMaxLength(500);
        });

        b.Entity<ApiToken>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.HashedSecret).HasMaxLength(64);
            e.Property(x => x.CreatedBy).HasMaxLength(300);
        });

        b.Entity<AuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.ProjectId, x.Id });
            e.Property(x => x.Actor).HasMaxLength(300);
            e.Property(x => x.Action).HasMaxLength(64);
            e.Property(x => x.Target).HasMaxLength(512);
        });
    }
}
```

`src/Ddm.Api/Data/DdmDbContextFactory.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ddm.Api.Data;

/// <summary>Used only by `dotnet ef`; override the target with DDM_CONNECTION.</summary>
public sealed class DdmDbContextFactory : IDesignTimeDbContextFactory<DdmDbContext>
{
    public DdmDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<DdmDbContext>()
        .UseNpgsql(Environment.GetEnvironmentVariable("DDM_CONNECTION")
                   ?? "Host=localhost;Port=5432;Database=ddm;Username=ddm;Password=ddm")
        .Options);
}
```

`src/Ddm.Api/Data/DbExceptions.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ddm.Api.Data;

public static class DbExceptions
{
    public static bool IsUniqueViolation(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
```

`src/Ddm.Api/Data/DataSetup.cs`:
```csharp
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Data;

public static class DataSetup
{
    public static IServiceCollection AddDdmData(this IServiceCollection services)
    {
        services.AddDbContext<DdmDbContext>((sp, o) =>
            o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("Ddm")));
        services.AddHealthChecks().AddDbContextCheck<DdmDbContext>("database", tags: ["ready"]);
        return services;
    }

    public static void MigrateIfConfigured(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Database:MigrateOnStart")) return;
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<DdmDbContext>().Database.Migrate();
    }

    public static void MapReadiness(this WebApplication app) =>
        app.MapHealthChecks("/readyz", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
}
```

`Program.cs` edits: add `using Ddm.Api.Data;` at top; under `// --- services` add `builder.Services.AddDdmData();`; after `var app = builder.Build();` add `app.MigrateIfConfigured();`; under `// --- endpoints` add `app.MapReadiness();`.

- [ ] **Step 6: Generate the initial migration**

```bash
dotnet ef migrations add InitialCreate --project src/Ddm.Api --output-dir Data/Migrations
```
Expected: creates `src/Ddm.Api/Data/Migrations/*_InitialCreate.cs`. Open it and confirm `COLLATE "C"` appears on `Slug`, `Path`, `UserId` and `jsonb` on `FrontMatter`.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS (first run pulls the postgres image).

- [ ] **Step 8: Commit**

```bash
git add .config src tests
git commit -m "feat: EF Core data model, migrations, readiness check and test infrastructure"
```

---

### Task 4: Authentication (JWT bearer) and `GET /api/v1/me`

**Files:**
- Create: `src/Ddm.Api/Identity/{Caller,AuthSetup,MeEndpoints}.cs`, `tests/Ddm.Api.Tests/Infrastructure/TestAuth.cs`
- Modify: `src/Ddm.Api/Program.cs`, `tests/Ddm.Api.Tests/Infrastructure/{DdmApiFactory,ApiTestBase}.cs`, `tests/Ddm.Api.Tests/ProblemDetailsTests.cs`
- Test: `tests/Ddm.Api.Tests/AuthTests.cs`

**Interfaces:**
- Consumes: `ApiException`, `DdmApiFactory`, `ApiTestBase`.
- Produces: `enum CallerKind { User, Token }`; `record Caller(CallerKind Kind, string Actor, string? UserId, Guid? TokenId, Guid? TokenProjectId, TokenScope? TokenScope)` with `static Caller From(ClaimsPrincipal)` and claim-name constants `TokenIdClaim`, `TokenProjectClaim`, `TokenScopeClaim`; `services.AddDdmAuthentication()`; `app.EnsureAuthConfigured()`; the `v1` route group (`RequireAuthorization()`); test helpers `TestAuth.TokenFor(userId, key?, audience?)`, `TestAuth.ExpiredTokenFor(userId)`, `ApiTestBase.ClientFor(userId)`.

- [ ] **Step 1: Install package**

```bash
dotnet add src/Ddm.Api package Microsoft.AspNetCore.Authentication.JwtBearer
```

- [ ] **Step 2: Write the failing tests and helpers**

`tests/Ddm.Api.Tests/Infrastructure/TestAuth.cs`:
```csharp
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ddm.Api.Tests.Infrastructure;

public static class TestAuth
{
    public const string SigningKey = "test-signing-key-test-signing-key-0123456789";
    public const string Issuer = "ddm-tests";
    public const string Audience = "ddm-api";

    public static string TokenFor(string userId, string? key = null, string? audience = null) =>
        Mint(userId, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), key, audience);

    public static string ExpiredTokenFor(string userId) =>
        Mint(userId, DateTime.UtcNow.AddMinutes(-20), DateTime.UtcNow.AddMinutes(-10), null, null);

    private static string Mint(string userId, DateTime notBefore, DateTime expires, string? key, string? audience) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience ?? Audience,
            Subject = new ClaimsIdentity([new Claim("sub", userId)]),
            NotBefore = notBefore,
            Expires = expires,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? SigningKey)), SecurityAlgorithms.HmacSha256),
        });
}
```

In `DdmApiFactory.ConfigureWebHost`, add to the dictionary:
```csharp
            ["Auth:DevSigningKey"] = TestAuth.SigningKey,
            ["Auth:Issuer"] = TestAuth.Issuer,
            ["Auth:Audience"] = TestAuth.Audience,
```

In `ApiTestBase` add:
```csharp
    protected HttpClient ClientFor(string userId)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestAuth.TokenFor(userId));
        return client;
    }
```

Replace `tests/Ddm.Api.Tests/ProblemDetailsTests.cs`:
```csharp
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class ProblemDetailsTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Unknown_routes_return_problem_json_with_a_stable_code()
    {
        var response = await Anonymous().GetAsync("/api/v1/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.Equal(404, body.GetProperty("status").GetInt32());
    }
}
```

`tests/Ddm.Api.Tests/AuthTests.cs`:
```csharp
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class AuthTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private static async Task<string> CodeAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    [Fact]
    public async Task Missing_credentials_return_401_problem()
    {
        var response = await Anonymous().GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await CodeAsync(response));
    }

    [Fact]
    public async Task Valid_user_token_identifies_the_caller()
    {
        var response = await ClientFor("alice").GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("user", me.GetProperty("kind").GetString());
        Assert.Equal("alice", me.GetProperty("userId").GetString());
        Assert.Equal("user:alice", me.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestAuth.ExpiredTokenFor("alice"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestAuth.TokenFor("mallory", key: "some-other-signing-key-0123456789abcdef"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Token_for_another_audience_is_rejected()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", TestAuth.TokenFor("alice", audience: "someone-else"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Healthz_stays_anonymous()
    {
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Production_refuses_to_start_with_only_the_dev_signing_key()
    {
        await using var factory = new DdmApiFactory("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p", migrate: false, environment: "Production");
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Auth:Authority", ex.ToString());
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AuthTests|FullyQualifiedName~ProblemDetailsTests"`
Expected: FAIL (`/api/v1/me` returns 404; Production test does not throw).

- [ ] **Step 4: Write the implementation**

`src/Ddm.Api/Identity/Caller.cs`:
```csharp
using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Domain;

namespace Ddm.Api.Identity;

public enum CallerKind { User, Token }

public sealed record Caller(
    CallerKind Kind, string Actor, string? UserId, Guid? TokenId, Guid? TokenProjectId, TokenScope? TokenScope)
{
    public const string TokenIdClaim = "ddm:token_id";
    public const string TokenProjectClaim = "ddm:project_id";
    public const string TokenScopeClaim = "ddm:scope";

    public static Caller From(ClaimsPrincipal p)
    {
        if (p.Identity?.IsAuthenticated != true)
            throw new ApiException(401, "unauthenticated", "Authentication required");

        if (p.FindFirstValue(TokenIdClaim) is { } tokenId)
            return new(CallerKind.Token, $"token:{tokenId}", null, Guid.Parse(tokenId),
                Guid.Parse(p.FindFirstValue(TokenProjectClaim)!),
                Enum.Parse<TokenScope>(p.FindFirstValue(TokenScopeClaim)!));

        var sub = p.FindFirstValue("sub") ?? throw new ApiException(401, "unauthenticated", "The token has no subject");
        return new(CallerKind.User, $"user:{sub}", sub, null, null, null);
    }
}
```

`src/Ddm.Api/Identity/AuthSetup.cs`:
```csharp
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Ddm.Api.Identity;

public static class AuthSetup
{
    public static IServiceCollection AddDdmAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IConfiguration, IHostEnvironment>((o, config, env) =>
            {
                o.MapInboundClaims = false;
                var audience = config["Auth:Audience"];
                var authority = config["Auth:Authority"];
                if (!string.IsNullOrEmpty(authority))
                {
                    o.Authority = authority;
                    o.Audience = audience;
                    return;
                }

                var key = config["Auth:DevSigningKey"];
                if (string.IsNullOrEmpty(key) || env.IsProduction())
                    throw new InvalidOperationException(
                        "Configure Auth:Authority (OIDC). Auth:DevSigningKey is only allowed outside Production.");

                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = config["Auth:Issuer"],
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(5),
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                };
            });
        services.AddAuthorization();
        return services;
    }

    /// <summary>Fail at startup, not on the first request, if authentication is misconfigured.</summary>
    public static void EnsureAuthConfigured(this WebApplication app) =>
        app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
}
```

`src/Ddm.Api/Identity/MeEndpoints.cs`:
```csharp
using System.Security.Claims;

namespace Ddm.Api.Identity;

public sealed record MeDto(string Kind, string Actor, string? UserId, Guid? ProjectId, string? Scope);

public static class MeEndpoints
{
    public static void MapMe(this RouteGroupBuilder v1) => v1.MapGet("/me", (ClaimsPrincipal user) =>
    {
        var c = Caller.From(user);
        return Results.Ok(new MeDto(c.Kind.ToString().ToLowerInvariant(), c.Actor, c.UserId,
            c.TokenProjectId, c.TokenScope?.ToString().ToLowerInvariant()));
    });
}
```

`Program.cs` edits: add `using Ddm.Api.Identity;`; under services `builder.Services.AddDdmAuthentication();`; after `app.UseStatusCodePages();` add
```csharp
app.UseAuthentication();
app.UseAuthorization();
app.EnsureAuthConfigured();
```
and under `// --- endpoints` (after readiness) add
```csharp
var v1 = app.MapGroup("/api/v1").RequireAuthorization();
v1.MapMe();
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: JWT bearer authentication, caller model and /me endpoint"
```

---

### Task 5: Authorization module, projects CRUD, paging and audit helper

**Files:**
- Create: `src/Ddm.Api/Common/{Paging,Wire}.cs`, `src/Ddm.Api/Data/AuditExtensions.cs`, `src/Ddm.Api/Identity/ProjectAuthorizer.cs`, `src/Ddm.Api/Projects/{ProjectDtos,ProjectValidation,ProjectEndpoints}.cs`
- Modify: `src/Ddm.Api/Program.cs`, `tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs`
- Test: `tests/Ddm.Api.Tests/PagingTests.cs`, `tests/Ddm.Api.Tests/ProjectTests.cs`

**Interfaces:**
- Consumes: `Caller`, `ApiException`, `DdmDbContext`, `IsUniqueViolation`, `v1` group.
- Produces: `Page<T>(IReadOnlyList<T> Items, string? Next)`; `Paging.ParseLimit(int?)`, `Paging.DecodeCursor(string?) : string?`, `Paging.DecodeLongCursor(string?) : long?`, `Paging.EncodeCursor(string)`, `Paging.ToPage<T>(List<T> rowsFetchedWithLimitPlusOne, int limit, Func<T,string> keyOf)`; `Wire.TryParse<T>(string?, out T) where T : struct, Enum`, `Wire.Lower(Enum)`; `db.Audit(Caller, Guid projectId, string action, string target)` (adds an entry, caller saves); `ProjectAuthorizer` (scoped) with `Task<ProjectAccess> RequireAsync(Caller, string slug, Role needed, CancellationToken)`, `Task<Dictionary<Guid, Role>> RolesAsync(Caller, IReadOnlyCollection<Project>, CancellationToken)`, `IQueryable<Project> VisibleTo(Caller)`; `record ProjectAccess(Project Project, Role Role)`; `ProjectDto(string Slug, string Name, string Description, string Visibility, string Role, DateTimeOffset CreatedAt)`; `v1.MapProjects()`; test helpers `ReadAsync<T>`, `ProblemCodeAsync`, `CreateProjectAsync(client, slug, visibility = "private")`.

- [ ] **Step 1: Write the failing tests**

Add to `ApiTestBase`:
```csharp
    protected static async Task<T> ReadAsync<T>(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<T>())!;

    protected static async Task<string> ProblemCodeAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;

    protected static async Task<ProjectDto> CreateProjectAsync(HttpClient client, string slug, string visibility = "private")
    {
        var r = await client.PostAsJsonAsync("/api/v1/projects", new { slug, name = slug, visibility });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await ReadAsync<ProjectDto>(r);
    }
```
with `using Ddm.Api.Projects;` at the top of that file.

`tests/Ddm.Api.Tests/PagingTests.cs`:
```csharp
using Ddm.Api.Common;

namespace Ddm.Api.Tests;

public class PagingTests
{
    [Fact] public void Limit_defaults_to_50() => Assert.Equal(50, Paging.ParseLimit(null));
    [Fact] public void Limit_is_clamped_to_200() => Assert.Equal(200, Paging.ParseLimit(10_000));
    [Theory] [InlineData(0)] [InlineData(-5)]
    public void Non_positive_limits_are_rejected(int limit)
    {
        var ex = Assert.Throws<ApiException>(() => Paging.ParseLimit(limit));
        Assert.Equal("invalid_limit", ex.Code);
    }

    [Fact] public void Cursor_round_trips() => Assert.Equal("my-slug", Paging.DecodeCursor(Paging.EncodeCursor("my-slug")));
    [Fact] public void Missing_cursor_means_first_page() => Assert.Null(Paging.DecodeCursor(null));

    [Theory] [InlineData("!!!not-base64!!!")] [InlineData("a")]
    public void Garbage_cursors_are_rejected(string cursor)
    {
        var ex = Assert.Throws<ApiException>(() => Paging.DecodeCursor(cursor));
        Assert.Equal("invalid_cursor", ex.Code);
    }

    [Fact]
    public void Non_numeric_long_cursor_is_rejected() =>
        Assert.Equal("invalid_cursor", Assert.Throws<ApiException>(() => Paging.DecodeLongCursor(Paging.EncodeCursor("abc"))).Code);

    [Fact]
    public void ToPage_trims_the_extra_row_and_sets_next()
    {
        var page = Paging.ToPage(["a", "b", "c"], 2, s => s);
        Assert.Equal(["a", "b"], page.Items);
        Assert.Equal("b", Paging.DecodeCursor(page.Next));
    }

    [Fact]
    public void ToPage_has_no_next_on_the_last_page() =>
        Assert.Null(Paging.ToPage(["a", "b"], 2, s => s).Next);
}
```

`tests/Ddm.Api.Tests/ProjectTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Projects;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class ProjectTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Create_returns_201_and_makes_the_creator_an_admin()
    {
        var r = await ClientFor("alice").PostAsJsonAsync("/api/v1/projects", new { slug = "payments", name = "Payments", description = "Pay docs" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("/api/v1/projects/payments", r.Headers.Location!.ToString());
        var p = await ReadAsync<ProjectDto>(r);
        Assert.Equal("admin", p.Role);
        Assert.Equal("private", p.Visibility);
        Assert.Equal("Pay docs", p.Description);
    }

    [Theory]
    [InlineData("")] [InlineData("Payments")] [InlineData("has space")] [InlineData("under_score")]
    [InlineData("-lead")] [InlineData("trail-")] [InlineData("a/b")]
    public async Task Create_rejects_invalid_slugs(string slug)
    {
        var r = await ClientFor("alice").PostAsJsonAsync("/api/v1/projects", new { slug, name = "X" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("validation_failed", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Create_rejects_a_slug_longer_than_64_characters()
    {
        var r = await ClientFor("alice").PostAsJsonAsync("/api/v1/projects", new { slug = new string('a', 65), name = "X" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_a_blank_name()
    {
        var r = await ClientFor("alice").PostAsJsonAsync("/api/v1/projects", new { slug = "ok", name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Theory] [InlineData("public")] [InlineData("7")] [InlineData("1")] [InlineData("")]
    public async Task Create_rejects_unknown_visibility(string visibility)
    {
        var r = await ClientFor("alice").PostAsJsonAsync("/api/v1/projects", new { slug = "ok", name = "X", visibility });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Create_without_a_body_returns_400()
    {
        var r = await ClientFor("alice").PostAsync("/api/v1/projects", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Create_with_malformed_json_returns_400_not_500()
    {
        var r = await ClientFor("alice").PostAsync("/api/v1/projects", new StringContent("{nope", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Duplicate_slug_returns_409()
    {
        await CreateProjectAsync(ClientFor("alice"), "payments");
        var r = await ClientFor("bob").PostAsJsonAsync("/api/v1/projects", new { slug = "payments", name = "Mine" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("slug_taken", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Unauthenticated_requests_get_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Anonymous().GetAsync("/api/v1/projects")).StatusCode);
    }

    [Fact]
    public async Task List_returns_only_projects_the_caller_can_see()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "secret", "private");
        await CreateProjectAsync(alice, "shared", "internal");

        var bob = await ReadAsync<Page<ProjectDto>>(await ClientFor("bob").GetAsync("/api/v1/projects"));
        Assert.Equal(["shared"], bob.Items.Select(p => p.Slug));
        Assert.Equal("reader", bob.Items[0].Role);

        var aliceList = await ReadAsync<Page<ProjectDto>>(await alice.GetAsync("/api/v1/projects"));
        Assert.Equal(["secret", "shared"], aliceList.Items.Select(p => p.Slug));
    }

    [Fact]
    public async Task List_pages_through_every_project_exactly_once()
    {
        var alice = ClientFor("alice");
        foreach (var s in new[] { "p1", "p2", "p3", "p4", "p5" }) await CreateProjectAsync(alice, s);

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var url = "/api/v1/projects?limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await ReadAsync<Page<ProjectDto>>(await alice.GetAsync(url));
            Assert.True(page.Items.Count <= 2);
            seen.AddRange(page.Items.Select(p => p.Slug));
            cursor = page.Next;
        } while (cursor is not null);

        Assert.Equal(["p1", "p2", "p3", "p4", "p5"], seen);
    }

    [Theory] [InlineData("0")] [InlineData("-1")]
    public async Task List_rejects_non_positive_limit(string limit)
    {
        var r = await ClientFor("alice").GetAsync($"/api/v1/projects?limit={limit}");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_limit", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task List_clamps_an_oversized_limit()
    {
        Assert.Equal(HttpStatusCode.OK, (await ClientFor("alice").GetAsync("/api/v1/projects?limit=100000")).StatusCode);
    }

    [Fact]
    public async Task List_rejects_a_malformed_cursor()
    {
        var r = await ClientFor("alice").GetAsync("/api/v1/projects?cursor=%21%21%21");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_cursor", await ProblemCodeAsync(r));
    }

    // Review Focus 5: no existence leak.
    [Fact]
    public async Task A_private_project_looks_exactly_like_a_missing_one_to_non_members()
    {
        await CreateProjectAsync(ClientFor("alice"), "secret", "private");
        var bob = ClientFor("bob");
        var hidden = await bob.GetAsync("/api/v1/projects/secret");
        var missing = await bob.GetAsync("/api/v1/projects/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(missing.StatusCode, hidden.StatusCode);
        Assert.Equal("project_not_found", await ProblemCodeAsync(hidden));
        Assert.Equal(await ProblemCodeAsync(missing), "project_not_found");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync("/api/v1/projects/secret")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PatchAsJsonAsync("/api/v1/projects/secret", new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Internal_project_is_readable_by_any_signed_in_user_but_not_writable()
    {
        await CreateProjectAsync(ClientFor("alice"), "shared", "internal");
        var bob = ClientFor("bob");
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/v1/projects/shared")).StatusCode);
        var patch = await bob.PatchAsJsonAsync("/api/v1/projects/shared", new { name = "Mine" });
        Assert.Equal(HttpStatusCode.Forbidden, patch.StatusCode);
        Assert.Equal("insufficient_role", await ProblemCodeAsync(patch));
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync("/api/v1/projects/shared")).StatusCode);
    }

    [Fact]
    public async Task Admin_can_patch_name_description_and_visibility_and_absent_fields_are_untouched()
    {
        var alice = ClientFor("alice");
        await alice.PostAsJsonAsync("/api/v1/projects", new { slug = "p", name = "Old", description = "keep me" });
        var r = await alice.PatchAsJsonAsync("/api/v1/projects/p", new { name = "New", visibility = "internal" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var p = await ReadAsync<ProjectDto>(r);
        Assert.Equal("p", p.Slug);
        Assert.Equal("New", p.Name);
        Assert.Equal("keep me", p.Description);
        Assert.Equal("internal", p.Visibility);
    }

    [Fact]
    public async Task Patch_rejects_an_invalid_visibility()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PatchAsJsonAsync("/api/v1/projects/p", new { visibility = "everyone" })).StatusCode);
    }

    [Fact]
    public async Task Delete_removes_the_project_and_its_memberships_so_the_slug_can_be_reused()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/projects/p")).StatusCode);

        await CreateProjectAsync(ClientFor("bob"), "p");
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/projects/p")).StatusCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PagingTests|FullyQualifiedName~ProjectTests"`
Expected: build FAILS (`Paging`, `ProjectDto` not defined).

- [ ] **Step 3: Write Paging and Wire**

`src/Ddm.Api/Common/Paging.cs`:
```csharp
using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Ddm.Api.Common;

public sealed record Page<T>(IReadOnlyList<T> Items, string? Next);

public static class Paging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    private static readonly UTF8Encoding Strict = new(false, true);

    public static int ParseLimit(int? limit)
    {
        if (limit is null) return DefaultLimit;
        if (limit < 1) throw ApiException.BadRequest("invalid_limit", "limit must be at least 1");
        return Math.Min(limit.Value, MaxLimit);
    }

    public static string EncodeCursor(string key) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(key));

    public static string? DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor)) return null;
        try { return Strict.GetString(Base64Url.DecodeFromChars(cursor)); }
        catch (Exception ex) when (ex is FormatException or ArgumentException or DecoderFallbackException)
        {
            throw ApiException.BadRequest("invalid_cursor", "The cursor is not valid");
        }
    }

    public static long? DecodeLongCursor(string? cursor)
    {
        var raw = DecodeCursor(cursor);
        if (raw is null) return null;
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw ApiException.BadRequest("invalid_cursor", "The cursor is not valid");
    }

    /// <summary>Rows must have been fetched with <c>limit + 1</c>.</summary>
    public static Page<T> ToPage<T>(List<T> rows, int limit, Func<T, string> keyOf)
    {
        if (rows.Count <= limit) return new(rows, null);
        var items = rows.GetRange(0, limit);
        return new(items, EncodeCursor(keyOf(items[^1])));
    }
}
```

`src/Ddm.Api/Common/Wire.cs`:
```csharp
namespace Ddm.Api.Common;

/// <summary>JSON wire format for enums: lowercase names only, never numbers.</summary>
public static class Wire
{
    public static string Lower(Enum value) => value.ToString().ToLowerInvariant();

    public static bool TryParse<T>(string? raw, out T value) where T : struct, Enum
    {
        value = default;
        var s = raw?.Trim();
        return !string.IsNullOrEmpty(s)
            && s.All(char.IsAsciiLetter)
            && Enum.TryParse(s, ignoreCase: true, out value)
            && Enum.IsDefined(value);
    }
}
```

- [ ] **Step 4: Write audit helper and the authorization module**

`src/Ddm.Api/Data/AuditExtensions.cs`:
```csharp
using Ddm.Api.Domain;
using Ddm.Api.Identity;

namespace Ddm.Api.Data;

public static class AuditExtensions
{
    /// <summary>Stages an audit entry; it is committed by the caller's SaveChanges, atomically with the change.</summary>
    public static void Audit(this DdmDbContext db, Caller caller, Guid projectId, string action, string target) =>
        db.AuditEntries.Add(new AuditEntry { ProjectId = projectId, Actor = caller.Actor, Action = action, Target = target });
}
```

`src/Ddm.Api/Identity/ProjectAuthorizer.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Identity;

public sealed record ProjectAccess(Project Project, Role Role);

/// <summary>The single answer to "can this caller do this on this project". Every handler calls it.</summary>
public sealed class ProjectAuthorizer(DdmDbContext db)
{
    /// <summary>404 if the caller cannot even read the project, 403 if they can read but lack <paramref name="needed"/>.</summary>
    public async Task<ProjectAccess> RequireAsync(Caller caller, string slug, Role needed, CancellationToken ct)
    {
        var project = await db.Projects.SingleOrDefaultAsync(p => p.Slug == slug, ct);
        Role? role = null;
        if (project is not null && (await RolesAsync(caller, [project], ct)).TryGetValue(project.Id, out var r)) role = r;

        if (project is null || role is null)
            throw ApiException.NotFound("project_not_found", "Project not found");
        if (role < needed)
            throw ApiException.Forbidden("insufficient_role", $"This action needs the {Wire.Lower(needed)} role");
        return new(project, role.Value);
    }

    public async Task<Dictionary<Guid, Role>> RolesAsync(Caller caller, IReadOnlyCollection<Project> projects, CancellationToken ct)
    {
        var result = new Dictionary<Guid, Role>();
        if (caller.Kind == CallerKind.Token)
        {
            foreach (var p in projects.Where(p => p.Id == caller.TokenProjectId))
                result[p.Id] = caller.TokenScope == TokenScope.Write ? Role.Editor : Role.Reader;
            return result;
        }

        var ids = projects.Select(p => p.Id).ToList();
        var memberRoles = await db.Members
            .Where(m => m.UserId == caller.UserId && ids.Contains(m.ProjectId))
            .ToDictionaryAsync(m => m.ProjectId, m => m.Role, ct);
        foreach (var p in projects)
        {
            if (memberRoles.TryGetValue(p.Id, out var role)) result[p.Id] = role;
            else if (p.Visibility == Visibility.Internal) result[p.Id] = Role.Reader;
        }
        return result;
    }

    public IQueryable<Project> VisibleTo(Caller caller) => caller.Kind == CallerKind.Token
        ? db.Projects.Where(p => p.Id == caller.TokenProjectId)
        : db.Projects.Where(p => p.Visibility == Visibility.Internal
                                 || db.Members.Any(m => m.ProjectId == p.Id && m.UserId == caller.UserId));
}
```

- [ ] **Step 5: Write the project DTOs, validation and endpoints**

`src/Ddm.Api/Projects/ProjectDtos.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Domain;

namespace Ddm.Api.Projects;

public sealed record ProjectDto(string Slug, string Name, string Description, string Visibility, string Role, DateTimeOffset CreatedAt)
{
    public static ProjectDto From(Project p, Role role) =>
        new(p.Slug, p.Name, p.Description, Wire.Lower(p.Visibility), Wire.Lower(role), p.CreatedAt);
}

public sealed record CreateProjectRequest(string? Slug, string? Name, string? Description, string? Visibility);
public sealed record UpdateProjectRequest(string? Name, string? Description, string? Visibility);
```

`src/Ddm.Api/Projects/ProjectValidation.cs`:
```csharp
using System.Text.RegularExpressions;
using Ddm.Api.Common;
using Ddm.Api.Domain;

namespace Ddm.Api.Projects;

public static partial class ProjectValidation
{
    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?\z")]
    private static partial Regex SlugRegex();

    public static ApiException Invalid(string detail) =>
        ApiException.BadRequest("validation_failed", "The request is not valid", detail);

    public static (string Slug, string Name, string Description, Visibility Visibility) ForCreate(CreateProjectRequest? r)
    {
        if (r is null) throw Invalid("A JSON body is required");
        var slug = r.Slug ?? "";
        if (!SlugRegex().IsMatch(slug))
            throw Invalid("slug must be 1-64 characters: lowercase letters, digits and hyphens, not starting or ending with a hyphen");
        var visibility = r.Visibility is null ? Visibility.Private : ParseVisibility(r.Visibility);
        return (slug, Name(r.Name), Description(r.Description), visibility);
    }

    public static string Name(string? name)
    {
        var n = name?.Trim() ?? "";
        return n.Length is >= 1 and <= 200 ? n : throw Invalid("name must be 1-200 characters");
    }

    public static string Description(string? description)
    {
        var d = description?.Trim() ?? "";
        return d.Length <= 2000 ? d : throw Invalid("description must be at most 2000 characters");
    }

    public static Visibility ParseVisibility(string raw) =>
        Wire.TryParse<Visibility>(raw, out var v) ? v : throw Invalid("visibility must be 'private' or 'internal'");
}
```

`src/Ddm.Api/Projects/ProjectEndpoints.cs`:
```csharp
using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Projects;

public static class ProjectEndpoints
{
    public static void MapProjects(this RouteGroupBuilder v1)
    {
        v1.MapPost("/projects", CreateAsync);
        v1.MapGet("/projects", ListAsync);
        v1.MapGet("/projects/{slug}", GetAsync);
        v1.MapPatch("/projects/{slug}", UpdateAsync);
        v1.MapDelete("/projects/{slug}", DeleteAsync);
    }

    private static async Task<IResult> CreateAsync(ClaimsPrincipal user, CreateProjectRequest? body, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        if (caller.Kind != CallerKind.User)
            throw ApiException.Forbidden("user_required", "API tokens cannot create projects");
        var v = ProjectValidation.ForCreate(body);

        var project = new Project { Slug = v.Slug, Name = v.Name, Description = v.Description, Visibility = v.Visibility };
        db.Projects.Add(project);
        db.Members.Add(new Member { ProjectId = project.Id, UserId = caller.UserId!, Role = Role.Admin });
        db.Audit(caller, project.Id, "project.create", project.Slug);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.Conflict("slug_taken", $"The project slug '{project.Slug}' is already taken");
        }
        return Results.Created($"/api/v1/projects/{project.Slug}", ProjectDto.From(project, Role.Admin));
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user, string? cursor, int? limit, ProjectAuthorizer authz, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);

        var query = authz.VisibleTo(caller);
        if (after is not null) query = query.Where(p => string.Compare(p.Slug, after) > 0);
        var rows = await query.OrderBy(p => p.Slug).Take(take + 1).ToListAsync(ct);

        var page = Paging.ToPage(rows, take, p => p.Slug);
        var roles = await authz.RolesAsync(caller, [.. page.Items], ct);
        var dtos = page.Items.Select(p => ProjectDto.From(p, roles[p.Id])).ToList();
        return Results.Ok(new Page<ProjectDto>(dtos, page.Next));
    }

    private static async Task<IResult> GetAsync(string slug, ClaimsPrincipal user, ProjectAuthorizer authz, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        return Results.Ok(ProjectDto.From(access.Project, access.Role));
    }

    private static async Task<IResult> UpdateAsync(
        string slug, UpdateProjectRequest? body, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        if (body is null) throw ProjectValidation.Invalid("A JSON body is required");

        var p = access.Project;
        if (body.Name is not null) p.Name = ProjectValidation.Name(body.Name);
        if (body.Description is not null) p.Description = ProjectValidation.Description(body.Description);
        if (body.Visibility is not null) p.Visibility = ProjectValidation.ParseVisibility(body.Visibility);
        db.Audit(caller, p.Id, "project.update", p.Slug);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ProjectDto.From(p, access.Role));
    }

    private static async Task<IResult> DeleteAsync(
        string slug, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        var project = access.Project;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Versions have no FK (they are polymorphic), so remove them explicitly.
        await db.Versions
            .Where(v => v.ItemType == ItemType.Document
                        && db.Documents.Any(d => d.Id == v.ItemId && d.ProjectId == project.Id))
            .ExecuteDeleteAsync(ct);
        db.Projects.Remove(project); // cascades to members, documents, tokens
        db.Audit(caller, project.Id, "project.delete", project.Slug);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
```

`Program.cs` edits: add `using Ddm.Api.Projects;`; under services `builder.Services.AddScoped<ProjectAuthorizer>();`; under endpoints after `v1.MapMe();` add `v1.MapProjects();`.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS. If the keyset test fails with a translation error for `string.Compare`, replace it with `p.Slug.CompareTo(after) > 0`.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat: project authorization module, projects CRUD, cursor paging, audit staging"
```

---

### Task 6: Members, last-admin protection and the audit log endpoint

**Files:**
- Create: `src/Ddm.Api/Data/ProjectLocks.cs`, `src/Ddm.Api/Projects/{MemberEndpoints,AuditEndpoints}.cs`
- Modify: `src/Ddm.Api/Projects/ProjectDtos.cs`, `src/Ddm.Api/Program.cs`, `tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs`
- Test: `tests/Ddm.Api.Tests/MemberTests.cs`, `tests/Ddm.Api.Tests/AuditTests.cs`

**Interfaces:**
- Consumes: `ProjectAuthorizer.RequireAsync`, `db.Audit`, `Paging`, `Wire`.
- Produces: `MemberDto(string UserId, string Role)`, `SetMemberRequest(string? Role)`, `AuditEntryDto(long Id, string Actor, string Action, string Target, DateTimeOffset At)`; `ProjectLocks.LockAsync(DdmDbContext, Guid, CancellationToken)`; `v1.MapMembers()`, `v1.MapAudit()`; routes `GET /projects/{slug}/members`, `PUT|DELETE /projects/{slug}/members/{userId}`, `GET /projects/{slug}/audit`; test helper `AddMemberAsync(adminClient, slug, userId, role)`.

- [ ] **Step 1: Write the failing tests**

Add to `ApiTestBase`:
```csharp
    protected static async Task AddMemberAsync(HttpClient admin, string slug, string userId, string role)
    {
        var r = await admin.PutAsJsonAsync($"/api/v1/projects/{slug}/members/{Uri.EscapeDataString(userId)}", new { role });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
```

`tests/Ddm.Api.Tests/MemberTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Projects;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class MemberTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Added_member_gains_access_with_the_given_role()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "editor");

        var p = await ReadAsync<ProjectDto>(await ClientFor("bob").GetAsync("/api/v1/projects/p"));
        Assert.Equal("editor", p.Role);
    }

    [Fact]
    public async Task Member_ids_with_special_characters_work()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "auth0|abc@example.com", "reader");
        var list = await ReadAsync<Page<MemberDto>>(await alice.GetAsync("/api/v1/projects/p/members"));
        Assert.Contains(list.Items, m => m.UserId == "auth0|abc@example.com" && m.Role == "reader");
    }

    [Fact]
    public async Task Put_on_an_existing_member_changes_their_role()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "editor");
        await AddMemberAsync(alice, "p", "bob", "reader");
        var list = await ReadAsync<Page<MemberDto>>(await alice.GetAsync("/api/v1/projects/p/members"));
        Assert.Equal("reader", list.Items.Single(m => m.UserId == "bob").Role);
    }

    [Theory] [InlineData("owner")] [InlineData("4")] [InlineData("")]
    public async Task Invalid_roles_are_rejected(string role)
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await alice.PutAsJsonAsync("/api/v1/projects/p/members/bob", new { role });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Only_admins_manage_members()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "ed", "editor");
        var ed = ClientFor("ed");
        Assert.Equal(HttpStatusCode.Forbidden, (await ed.GetAsync("/api/v1/projects/p/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ed.PutAsJsonAsync("/api/v1/projects/p/members/x", new { role = "reader" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ClientFor("stranger").GetAsync("/api/v1/projects/p/members")).StatusCode);
    }

    [Fact]
    public async Task Removed_member_loses_access_to_a_private_project()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "reader");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/members/bob")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ClientFor("bob").GetAsync("/api/v1/projects/p")).StatusCode);
    }

    [Fact]
    public async Task Removing_a_non_member_returns_404()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await alice.DeleteAsync("/api/v1/projects/p/members/ghost");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("member_not_found", await ProblemCodeAsync(r));
    }

    // Review Focus 4
    [Fact]
    public async Task The_last_admin_cannot_be_removed_or_demoted()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");

        var remove = await alice.DeleteAsync("/api/v1/projects/p/members/alice");
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
        Assert.Equal("last_admin", await ProblemCodeAsync(remove));

        var demote = await alice.PutAsJsonAsync("/api/v1/projects/p/members/alice", new { role = "editor" });
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        Assert.Equal("last_admin", await ProblemCodeAsync(demote));
    }

    [Fact]
    public async Task An_admin_can_step_down_once_another_admin_exists()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "admin");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/members/alice")).StatusCode);
    }

    // Review Focus 4: two admins removing each other at the same time must leave one.
    [Fact]
    public async Task Two_admins_removing_each_other_concurrently_leave_one_admin()
    {
        var alice = ClientFor("alice");
        var bob = ClientFor("bob");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "admin");

        var results = await Task.WhenAll(
            alice.DeleteAsync("/api/v1/projects/p/members/bob"),
            bob.DeleteAsync("/api/v1/projects/p/members/alice"));

        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.NoContent);
        Assert.All(results, r => Assert.Contains(r.StatusCode,
            new[] { HttpStatusCode.NoContent, HttpStatusCode.Conflict, HttpStatusCode.NotFound }));
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DdmDbContext>();
        Assert.Equal(1, await db.Members.CountAsync(m => m.Role == Role.Admin));
    }
}
```

`tests/Ddm.Api.Tests/AuditTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Projects;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class AuditTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Admin_sees_who_did_what_newest_first()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "editor");
        await alice.DeleteAsync("/api/v1/projects/p/members/bob");

        var page = await ReadAsync<Page<AuditEntryDto>>(await alice.GetAsync("/api/v1/projects/p/audit"));
        Assert.Equal(["member.remove", "member.set", "project.create"], page.Items.Select(e => e.Action));
        Assert.All(page.Items, e => Assert.Equal("user:alice", e.Actor));
        Assert.Equal("bob", page.Items[0].Target);
    }

    [Fact]
    public async Task Audit_log_pages_with_a_cursor()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "bob", "reader");
        await AddMemberAsync(alice, "p", "carol", "reader");

        var first = await ReadAsync<Page<AuditEntryDto>>(await alice.GetAsync("/api/v1/projects/p/audit?limit=2"));
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.Next);
        var second = await ReadAsync<Page<AuditEntryDto>>(
            await alice.GetAsync($"/api/v1/projects/p/audit?limit=2&cursor={Uri.EscapeDataString(first.Next!)}"));
        Assert.Single(second.Items);
        Assert.Null(second.Next);
    }

    [Fact]
    public async Task Audit_log_is_admin_only()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "ed", "editor");
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientFor("ed").GetAsync("/api/v1/projects/p/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ClientFor("stranger").GetAsync("/api/v1/projects/p/audit")).StatusCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MemberTests|FullyQualifiedName~AuditTests"`
Expected: build FAILS (`MemberDto`, `AuditEntryDto` not defined).

- [ ] **Step 3: Write the implementation**

Append to `src/Ddm.Api/Projects/ProjectDtos.cs`:
```csharp
public sealed record MemberDto(string UserId, string Role);
public sealed record SetMemberRequest(string? Role);
public sealed record AuditEntryDto(long Id, string Actor, string Action, string Target, DateTimeOffset At);
```

`src/Ddm.Api/Data/ProjectLocks.cs`:
```csharp
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Data;

public static class ProjectLocks
{
    /// <summary>Serialises writers on one project's membership. Call inside a transaction.</summary>
    public static Task<int> LockAsync(this DdmDbContext db, Guid projectId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Projects\" WHERE \"Id\" = {projectId} FOR UPDATE", ct);
}
```

`src/Ddm.Api/Projects/MemberEndpoints.cs`:
```csharp
using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Projects;

public static class MemberEndpoints
{
    public static void MapMembers(this RouteGroupBuilder v1)
    {
        v1.MapGet("/projects/{slug}/members", ListAsync);
        v1.MapPut("/projects/{slug}/members/{userId}", SetAsync);
        v1.MapDelete("/projects/{slug}/members/{userId}", RemoveAsync);
    }

    private static async Task<IResult> ListAsync(
        string slug, string? cursor, int? limit, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Admin, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);

        var query = db.Members.Where(m => m.ProjectId == access.Project.Id);
        if (after is not null) query = query.Where(m => string.Compare(m.UserId, after) > 0);
        var rows = await query.OrderBy(m => m.UserId).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, m => m.UserId);
        return Results.Ok(new Page<MemberDto>(page.Items.Select(m => new MemberDto(m.UserId, Wire.Lower(m.Role))).ToList(), page.Next));
    }

    private static async Task<IResult> SetAsync(
        string slug, string userId, SetMemberRequest? body, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        ValidateUserId(userId);
        if (!Wire.TryParse<Role>(body?.Role, out var role))
            throw ProjectValidation.Invalid("role must be 'reader', 'editor' or 'admin'");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockAsync(access.Project.Id, ct);
        var member = await db.Members.SingleOrDefaultAsync(m => m.ProjectId == access.Project.Id && m.UserId == userId, ct);
        if (member is null)
            db.Members.Add(member = new Member { ProjectId = access.Project.Id, UserId = userId, Role = role });
        else
        {
            if (member.Role == Role.Admin && role != Role.Admin) await EnsureAnotherAdminAsync(db, access.Project.Id, userId, ct);
            member.Role = role;
        }
        db.Audit(caller, access.Project.Id, "member.set", userId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new MemberDto(userId, Wire.Lower(role)));
    }

    private static async Task<IResult> RemoveAsync(
        string slug, string userId, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockAsync(access.Project.Id, ct);
        var member = await db.Members.SingleOrDefaultAsync(m => m.ProjectId == access.Project.Id && m.UserId == userId, ct)
            ?? throw ApiException.NotFound("member_not_found", "Member not found");
        if (member.Role == Role.Admin) await EnsureAnotherAdminAsync(db, access.Project.Id, userId, ct);
        db.Members.Remove(member);
        db.Audit(caller, access.Project.Id, "member.remove", userId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task EnsureAnotherAdminAsync(DdmDbContext db, Guid projectId, string excludingUserId, CancellationToken ct)
    {
        var others = await db.Members.CountAsync(m => m.ProjectId == projectId && m.Role == Role.Admin && m.UserId != excludingUserId, ct);
        if (others == 0) throw ApiException.Conflict("last_admin", "A project must keep at least one admin");
    }

    private static void ValidateUserId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 256)
            throw ProjectValidation.Invalid("userId must be 1-256 characters");
    }
}
```

`src/Ddm.Api/Projects/AuditEndpoints.cs`:
```csharp
using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Projects;

public static class AuditEndpoints
{
    public static void MapAudit(this RouteGroupBuilder v1) => v1.MapGet("/projects/{slug}/audit", ListAsync);

    private static async Task<IResult> ListAsync(
        string slug, string? cursor, int? limit, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Admin, ct);
        var take = Paging.ParseLimit(limit);
        var before = Paging.DecodeLongCursor(cursor);

        var query = db.AuditEntries.Where(e => e.ProjectId == access.Project.Id);
        if (before is not null) query = query.Where(e => e.Id < before);
        var rows = await query.OrderByDescending(e => e.Id).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, e => e.Id.ToString());
        var dtos = page.Items.Select(e => new AuditEntryDto(e.Id, e.Actor, e.Action, e.Target, e.At)).ToList();
        return Results.Ok(new Page<AuditEntryDto>(dtos, page.Next));
    }
}
```

`Program.cs` edit: after `v1.MapProjects();` add `v1.MapMembers();` and `v1.MapAudit();`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: project members with last-admin protection and audit log endpoint"
```

---

### Task 7: API tokens (create, list, revoke, authenticate)

**Files:**
- Create: `src/Ddm.Api/Identity/{ApiTokenSecrets,ApiTokenHandler}.cs`, `src/Ddm.Api/Tokens/TokenEndpoints.cs`
- Modify: `src/Ddm.Api/Identity/AuthSetup.cs`, `src/Ddm.Api/Program.cs`, `tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs`
- Test: `tests/Ddm.Api.Tests/ApiTokenSecretsTests.cs`, `tests/Ddm.Api.Tests/TokenTests.cs`

**Interfaces:**
- Consumes: `Caller` claim constants, `ProjectAuthorizer`, `db.Audit`, `Wire`, `ProjectValidation.Invalid`.
- Produces: `ApiTokenSecrets.Prefix = "ddm_tok_"`, `Generate(Guid id) : (string Token, string Hash)`, `Hash(string secret) : string`, `TryParse(string token, out Guid id, out string secret) : bool`; `TokenDto(Guid Id, string Name, string Scope, string CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt)`; `CreatedTokenDto(Guid Id, string Name, string Scope, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string Secret)`; `CreateTokenRequest(string? Name, string? Scope, DateTimeOffset? ExpiresAt)`; routes `GET|POST /projects/{slug}/tokens`, `DELETE /projects/{slug}/tokens/{id:guid}`; authentication scheme `"ApiToken"` selected automatically for `Bearer ddm_tok_…`; test helpers `CreateTokenAsync(admin, slug, scope, name = "ci")` returning the secret string and `TokenClient(secret)`.

- [ ] **Step 1: Write the failing tests**

Add to `ApiTestBase` (with `using Ddm.Api.Tokens;`):
```csharp
    protected static async Task<string> CreateTokenAsync(HttpClient admin, string slug, string scope, string name = "ci")
    {
        var r = await admin.PostAsJsonAsync($"/api/v1/projects/{slug}/tokens", new { name, scope });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
    }

    protected HttpClient TokenClient(string secret)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", secret);
        return client;
    }
```

`tests/Ddm.Api.Tests/ApiTokenSecretsTests.cs`:
```csharp
using Ddm.Api.Identity;

namespace Ddm.Api.Tests;

public class ApiTokenSecretsTests
{
    [Fact]
    public void Generated_tokens_round_trip_through_parse()
    {
        var id = Guid.NewGuid();
        var (token, hash) = ApiTokenSecrets.Generate(id);
        Assert.StartsWith("ddm_tok_", token);
        Assert.True(ApiTokenSecrets.TryParse(token, out var parsedId, out var secret));
        Assert.Equal(id, parsedId);
        Assert.Equal(hash, ApiTokenSecrets.Hash(secret));
    }

    [Fact]
    public void The_hash_is_64_hex_chars_and_does_not_contain_the_secret()
    {
        var (token, hash) = ApiTokenSecrets.Generate(Guid.NewGuid());
        ApiTokenSecrets.TryParse(token, out _, out var secret);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.DoesNotContain(secret, hash);
    }

    [Fact]
    public void Two_tokens_differ() =>
        Assert.NotEqual(ApiTokenSecrets.Generate(Guid.NewGuid()).Token, ApiTokenSecrets.Generate(Guid.NewGuid()).Token);

    [Theory]
    [InlineData("")]
    [InlineData("ddm_tok_")]
    [InlineData("ddm_tok_notaguid_secret")]
    [InlineData("ddm_tok_00000000000000000000000000000000")]
    [InlineData("other_00000000000000000000000000000000_secret")]
    public void Malformed_tokens_do_not_parse(string token) =>
        Assert.False(ApiTokenSecrets.TryParse(token, out _, out _));
}
```

`tests/Ddm.Api.Tests/TokenTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Identity;
using Ddm.Api.Projects;
using Ddm.Api.Tests.Infrastructure;
using Ddm.Api.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class TokenTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Created_token_shows_its_secret_once_and_is_stored_hashed()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await alice.PostAsJsonAsync("/api/v1/projects/p/tokens", new { name = "ci", scope = "write" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var created = await r.Content.ReadFromJsonAsync<JsonElement>();
        var secret = created.GetProperty("secret").GetString()!;
        Assert.StartsWith("ddm_tok_", secret);

        var list = await alice.GetStringAsync("/api/v1/projects/p/tokens");
        Assert.DoesNotContain(secret, list);
        Assert.DoesNotContain("secret", list, StringComparison.OrdinalIgnoreCase);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DdmDbContext>();
        var row = await db.ApiTokens.SingleAsync();
        ApiTokenSecrets.TryParse(secret, out _, out var raw);
        Assert.Equal(ApiTokenSecrets.Hash(raw), row.HashedSecret);
        Assert.DoesNotContain(raw, row.HashedSecret);
    }

    [Fact]
    public async Task Token_authenticates_as_a_token_caller_bound_to_its_project()
    {
        var alice = ClientFor("alice");
        var p = await CreateProjectAsync(alice, "p");
        var me = await TokenClient(await CreateTokenAsync(alice, "p", "read")).GetFromJsonAsync<MeDto>("/api/v1/me");
        Assert.Equal("token", me!.Kind);
        Assert.Equal("read", me.Scope);
        Assert.Null(me.UserId);
        Assert.StartsWith("token:", me.Actor);
        Assert.NotNull(me.ProjectId);
    }

    // Review Focus 5: a token for project A sees nothing of project B.
    [Fact]
    public async Task Token_cannot_see_other_projects_even_internal_ones()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "a");
        await CreateProjectAsync(alice, "b", "internal");
        var token = TokenClient(await CreateTokenAsync(alice, "a", "write"));

        Assert.Equal(HttpStatusCode.OK, (await token.GetAsync("/api/v1/projects/a")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await token.GetAsync("/api/v1/projects/b")).StatusCode);
        var list = await ReadAsync<Page<ProjectDto>>(await token.GetAsync("/api/v1/projects"));
        Assert.Equal(["a"], list.Items.Select(x => x.Slug));
    }

    [Fact]
    public async Task Tokens_never_administer_and_cannot_create_projects()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var token = TokenClient(await CreateTokenAsync(alice, "p", "write"));

        Assert.Equal(HttpStatusCode.Forbidden, (await token.GetAsync("/api/v1/projects/p/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.PutAsJsonAsync("/api/v1/projects/p/members/x", new { role = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await token.DeleteAsync("/api/v1/projects/p")).StatusCode);
        var create = await token.PostAsJsonAsync("/api/v1/projects", new { slug = "new", name = "New" });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Equal("user_required", await ProblemCodeAsync(create));
    }

    [Fact]
    public async Task Revoked_token_stops_working()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var secret = await CreateTokenAsync(alice, "p", "read");
        var id = (await ReadAsync<Page<TokenDto>>(await alice.GetAsync("/api/v1/projects/p/tokens"))).Items.Single().Id;

        Assert.Equal(HttpStatusCode.OK, (await TokenClient(secret).GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/v1/projects/p/tokens/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync($"/api/v1/projects/p/tokens/{id}")).StatusCode); // idempotent
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenClient(secret).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var secret = await CreateTokenAsync(alice, "p", "read");
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DdmDbContext>();
            await db.ApiTokens.ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenClient(secret).GetAsync("/api/v1/me")).StatusCode);
    }

    [Theory]
    [InlineData("ddm_tok_garbage")]
    [InlineData("ddm_tok_00000000000000000000000000000000_wrong")]
    public async Task Malformed_or_unknown_tokens_get_401_not_500(string secret)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenClient(secret).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Right_id_wrong_secret_is_rejected()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var secret = await CreateTokenAsync(alice, "p", "read");
        ApiTokenSecrets.TryParse(secret, out var id, out _);
        var forged = $"ddm_tok_{id:N}_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        Assert.Equal(HttpStatusCode.Unauthorized, (await TokenClient(forged).GetAsync("/api/v1/me")).StatusCode);
    }

    [Theory]
    [InlineData("", "read")] [InlineData("ci", "admin")] [InlineData("ci", "")] [InlineData("ci", "3")]
    public async Task Create_validates_name_and_scope(string name, string scope)
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await alice.PostAsJsonAsync("/api/v1/projects/p/tokens", new { name, scope });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Create_rejects_an_expiry_in_the_past()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await alice.PostAsJsonAsync("/api/v1/projects/p/tokens",
            new { name = "ci", scope = "read", expiresAt = DateTimeOffset.UtcNow.AddDays(-1) });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Revoking_an_unknown_token_returns_404()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await alice.DeleteAsync($"/api/v1/projects/p/tokens/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("token_not_found", await ProblemCodeAsync(r));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ApiTokenSecretsTests|FullyQualifiedName~TokenTests"`
Expected: build FAILS (`ApiTokenSecrets`, `TokenDto` not defined).

- [ ] **Step 3: Write the implementation**

`src/Ddm.Api/Identity/ApiTokenSecrets.cs`:
```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Ddm.Api.Identity;

/// <summary>Token format: ddm_tok_{tokenId as 32 hex}_{base64url secret}. Only the SHA-256 of the secret is stored.</summary>
public static class ApiTokenSecrets
{
    public const string Prefix = "ddm_tok_";
    private const int IdLength = 32;

    public static (string Token, string Hash) Generate(Guid id)
    {
        var secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return ($"{Prefix}{id:N}_{secret}", Hash(secret));
    }

    public static string Hash(string secret) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    public static bool TryParse(string token, out Guid id, out string secret)
    {
        id = default;
        secret = "";
        if (!token.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = token.AsSpan(Prefix.Length);
        if (rest.Length <= IdLength + 1 || rest[IdLength] != '_') return false;
        if (!Guid.TryParseExact(rest[..IdLength], "N", out id)) return false;
        secret = rest[(IdLength + 1)..].ToString();
        return true;
    }
}
```

`src/Ddm.Api/Identity/ApiTokenHandler.cs`:
```csharp
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Ddm.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Identity;

public sealed class ApiTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, DdmDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";
    public const string HeaderPrefix = "Bearer " + ApiTokenSecrets.Prefix;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        if (!ApiTokenSecrets.TryParse(header["Bearer ".Length..].Trim(), out var id, out var secret))
            return AuthenticateResult.Fail("Malformed API token");

        var row = await db.ApiTokens.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, Context.RequestAborted);
        var hashOk = row is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(ApiTokenSecrets.Hash(secret)), Encoding.ASCII.GetBytes(row.HashedSecret));
        if (!hashOk || row!.RevokedAt is not null || (row.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow))
            return AuthenticateResult.Fail("Invalid API token");

        var identity = new ClaimsIdentity(
        [
            new Claim(Caller.TokenIdClaim, row.Id.ToString()),
            new Claim(Caller.TokenProjectClaim, row.ProjectId.ToString()),
            new Claim(Caller.TokenScopeClaim, row.Scope.ToString()),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
```

In `AuthSetup.AddDdmAuthentication`, replace
`services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();` with:
```csharp
        const string Smart = "Smart";
        services.AddAuthentication(o =>
            {
                o.DefaultScheme = Smart;
                o.DefaultChallengeScheme = Smart;
            })
            .AddPolicyScheme(Smart, "Bearer JWT or API token", o => o.ForwardDefaultSelector = ctx =>
                ctx.Request.Headers.Authorization.ToString().StartsWith(ApiTokenHandler.HeaderPrefix, StringComparison.OrdinalIgnoreCase)
                    ? ApiTokenHandler.SchemeName
                    : JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer()
            .AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(ApiTokenHandler.SchemeName, _ => { });
```
and add `using Microsoft.AspNetCore.Authentication;` at the top.

`src/Ddm.Api/Tokens/TokenEndpoints.cs`:
```csharp
using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Projects;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Tokens;

public sealed record CreateTokenRequest(string? Name, string? Scope, DateTimeOffset? ExpiresAt);

public sealed record TokenDto(Guid Id, string Name, string Scope, string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt)
{
    public static TokenDto From(ApiToken t) =>
        new(t.Id, t.Name, Wire.Lower(t.Scope), t.CreatedBy, t.CreatedAt, t.ExpiresAt, t.RevokedAt);
}

/// <summary>Returned once, at creation. The secret is never retrievable again.</summary>
public sealed record CreatedTokenDto(Guid Id, string Name, string Scope, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string Secret);

public static class TokenEndpoints
{
    public static void MapTokens(this RouteGroupBuilder v1)
    {
        v1.MapGet("/projects/{slug}/tokens", ListAsync);
        v1.MapPost("/projects/{slug}/tokens", CreateAsync);
        v1.MapDelete("/projects/{slug}/tokens/{id:guid}", RevokeAsync);
    }

    private static async Task<IResult> ListAsync(
        string slug, string? cursor, int? limit, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Admin, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);

        var query = db.ApiTokens.Where(t => t.ProjectId == access.Project.Id);
        if (after is not null && Guid.TryParse(after, out var afterId)) query = query.Where(t => t.Id.CompareTo(afterId) > 0);
        else if (after is not null) throw ApiException.BadRequest("invalid_cursor", "The cursor is not valid");
        var rows = await query.OrderBy(t => t.Id).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, t => t.Id.ToString());
        return Results.Ok(new Page<TokenDto>(page.Items.Select(TokenDto.From).ToList(), page.Next));
    }

    private static async Task<IResult> CreateAsync(
        string slug, CreateTokenRequest? body, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        var name = body?.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 100) throw ProjectValidation.Invalid("name must be 1-100 characters");
        if (!Wire.TryParse<TokenScope>(body?.Scope, out var scope)) throw ProjectValidation.Invalid("scope must be 'read' or 'write'");
        if (body!.ExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow) throw ProjectValidation.Invalid("expiresAt must be in the future");

        var id = Guid.NewGuid();
        var (token, hash) = ApiTokenSecrets.Generate(id);
        var row = new ApiToken
        {
            Id = id, ProjectId = access.Project.Id, Name = name, Scope = scope, HashedSecret = hash,
            CreatedBy = caller.Actor, ExpiresAt = body.ExpiresAt,
        };
        db.ApiTokens.Add(row);
        db.Audit(caller, access.Project.Id, "token.create", $"{id} ({name})");
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/projects/{slug}/tokens", new CreatedTokenDto(id, name, Wire.Lower(scope), row.CreatedAt, row.ExpiresAt, token));
    }

    private static async Task<IResult> RevokeAsync(
        string slug, Guid id, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        var row = await db.ApiTokens.SingleOrDefaultAsync(t => t.Id == id && t.ProjectId == access.Project.Id, ct)
            ?? throw ApiException.NotFound("token_not_found", "Token not found");
        if (row.RevokedAt is null)
        {
            row.RevokedAt = DateTimeOffset.UtcNow;
            db.Audit(caller, access.Project.Id, "token.revoke", id.ToString());
            await db.SaveChangesAsync(ct);
        }
        return Results.NoContent();
    }
}
```

`Program.cs` edits: add `using Ddm.Api.Tokens;`; after `v1.MapAudit();` add `v1.MapTokens();`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS. If the `Guid.CompareTo` keyset filter in `TokenEndpoints.ListAsync` fails to translate, order tokens by `CreatedAt` then `Id` instead and use `CreatedAt` ticks as the cursor key.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: API tokens with hashed secrets, revocation and bearer authentication"
```

---

### Task 8: Blob storage (S3-compatible)

**Files:**
- Create: `src/Ddm.Api/Storage/{IBlobStore,S3Options,S3BlobStore,StorageSetup}.cs`, `tests/Ddm.Api.Tests/Infrastructure/InMemoryBlobStore.cs`
- Modify: `src/Ddm.Api/Program.cs`, `tests/Ddm.Api.Tests/Infrastructure/DdmApiFactory.cs`
- Test: `tests/Ddm.Api.Tests/S3BlobStoreTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `interface IBlobStore { Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct); Task<byte[]?> GetAsync(string key, CancellationToken ct); }`; `S3BlobStore(IAmazonS3 s3, string bucket)` plus `static Task EnsureBucketAsync(IAmazonS3, string bucket, CancellationToken)`; `services.AddDdmStorage(IConfiguration)`; test fake `InMemoryBlobStore` with `FailNextPut`, `Count`; `DdmApiFactory.Blobs`.

- [ ] **Step 1: Install packages**

```bash
dotnet add src/Ddm.Api package AWSSDK.S3
```

- [ ] **Step 2: Write the failing test**

`tests/Ddm.Api.Tests/S3BlobStoreTests.cs`:
```csharp
using Amazon.Runtime;
using Amazon.S3;
using Ddm.Api.Storage;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Ddm.Api.Tests;

public class S3BlobStoreTests : IAsyncLifetime
{
    private readonly IContainer _minio = new ContainerBuilder()
        .WithImage("minio/minio:latest")
        .WithPortBinding(9000, true)
        .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
        .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
        .WithCommand("server", "/data")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPath("/minio/health/live").ForPort(9000)))
        .Build();

    private IAmazonS3 _s3 = null!;
    private S3BlobStore _store = null!;

    public async Task InitializeAsync()
    {
        await _minio.StartAsync();
        _s3 = new AmazonS3Client(new BasicAWSCredentials("minioadmin", "minioadmin"), new AmazonS3Config
        {
            ServiceURL = $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}",
            ForcePathStyle = true,
            AuthenticationRegion = "us-east-1",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });
        await S3BlobStore.EnsureBucketAsync(_s3, "ddm-test", default);
        _store = new S3BlobStore(_s3, "ddm-test");
    }

    public async Task DisposeAsync()
    {
        _s3.Dispose();
        await _minio.DisposeAsync();
    }

    [Fact]
    public async Task Put_then_get_round_trips_bytes()
    {
        var bytes = Encoding.UTF8.GetBytes("# Hello\n\nunicode: héllo ✓");
        await _store.PutAsync("projects/p/docs/abc.md", bytes, "text/markdown", default);
        Assert.Equal(bytes, await _store.GetAsync("projects/p/docs/abc.md", default));
    }

    [Fact]
    public async Task Missing_keys_return_null() => Assert.Null(await _store.GetAsync("nope/nothing.md", default));

    [Fact]
    public async Task Putting_the_same_key_twice_is_harmless()
    {
        var bytes = new byte[] { 1, 2, 3 };
        await _store.PutAsync("k", bytes, "application/octet-stream", default);
        await _store.PutAsync("k", bytes, "application/octet-stream", default);
        Assert.Equal(bytes, await _store.GetAsync("k", default));
    }

    [Fact]
    public async Task A_one_mebibyte_payload_round_trips()
    {
        var bytes = new byte[1024 * 1024];
        Random.Shared.NextBytes(bytes);
        await _store.PutAsync("big", bytes, "application/octet-stream", default);
        Assert.Equal(bytes, await _store.GetAsync("big", default));
    }

    [Fact]
    public async Task Ensuring_the_bucket_twice_does_not_throw() =>
        await S3BlobStore.EnsureBucketAsync(_s3, "ddm-test", default);
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~S3BlobStoreTests"`
Expected: build FAILS (`Ddm.Api.Storage` not defined).

- [ ] **Step 4: Write the implementation**

`src/Ddm.Api/Storage/IBlobStore.cs`:
```csharp
namespace Ddm.Api.Storage;

public interface IBlobStore
{
    /// <summary>Stores the object. Keys are content-addressed, so re-putting the same key is harmless.</summary>
    Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct);

    /// <summary>Returns null when the key does not exist.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken ct);
}
```

`src/Ddm.Api/Storage/S3Options.cs`:
```csharp
namespace Ddm.Api.Storage;

public sealed class S3Options
{
    public string ServiceUrl { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "ddm";
    public string Region { get; set; } = "us-east-1";
    public bool ForcePathStyle { get; set; } = true;
    public bool CreateBucket { get; set; }
}
```

`src/Ddm.Api/Storage/S3BlobStore.cs`:
```csharp
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;

namespace Ddm.Api.Storage;

public sealed class S3BlobStore(IAmazonS3 s3, string bucket) : IBlobStore
{
    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        using var body = new MemoryStream(content, writable: false);
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = body, ContentType = contentType }, ct);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct)
    {
        try
        {
            using var response = await s3.GetObjectAsync(bucket, key, ct);
            using var ms = new MemoryStream();
            await response.ResponseStream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public static async Task EnsureBucketAsync(IAmazonS3 s3, string bucket, CancellationToken ct)
    {
        try { await s3.PutBucketAsync(bucket, ct); }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists") { }
    }
}
```

`src/Ddm.Api/Storage/StorageSetup.cs`:
```csharp
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Storage;

public static class StorageSetup
{
    public static IServiceCollection AddDdmStorage(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<S3Options>(config.GetSection("Storage"));
        services.AddSingleton<IAmazonS3>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<S3Options>>().Value;
            return new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), new AmazonS3Config
            {
                ServiceURL = o.ServiceUrl,
                ForcePathStyle = o.ForcePathStyle,
                AuthenticationRegion = o.Region,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
        });
        services.AddSingleton<IBlobStore>(sp =>
            new S3BlobStore(sp.GetRequiredService<IAmazonS3>(), sp.GetRequiredService<IOptions<S3Options>>().Value.Bucket));
        if (config.GetValue<bool>("Storage:CreateBucket")) services.AddHostedService<BucketInitializer>();
        return services;
    }

    private sealed class BucketInitializer(IAmazonS3 s3, IOptions<S3Options> options) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => S3BlobStore.EnsureBucketAsync(s3, options.Value.Bucket, ct);
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
```

`Program.cs` edits: add `using Ddm.Api.Storage;`; under services `builder.Services.AddDdmStorage(builder.Configuration);`.

`tests/Ddm.Api.Tests/Infrastructure/InMemoryBlobStore.cs`:
```csharp
using System.Collections.Concurrent;
using Ddm.Api.Storage;

namespace Ddm.Api.Tests.Infrastructure;

public sealed class InMemoryBlobStore : IBlobStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new();

    /// <summary>When set, the next PutAsync throws once, simulating a storage outage.</summary>
    public bool FailNextPut { get; set; }
    public int Count => _blobs.Count;

    public Task PutAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        if (FailNextPut)
        {
            FailNextPut = false;
            throw new IOException("simulated storage outage");
        }
        _blobs[key] = content;
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken ct) =>
        Task.FromResult<byte[]?>(_blobs.TryGetValue(key, out var b) ? b : null);
}
```

In `DdmApiFactory`, add `using Ddm.Api.Storage; using Microsoft.AspNetCore.TestHost; using Microsoft.Extensions.DependencyInjection; using Microsoft.Extensions.DependencyInjection.Extensions;`, the property `public InMemoryBlobStore Blobs { get; } = new();` and at the end of `ConfigureWebHost`:
```csharp
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IBlobStore>();
            s.AddSingleton<IBlobStore>(Blobs);
        });
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS (first run pulls the MinIO image). If the MinIO wait strategy times out, confirm Docker can reach `/minio/health/live` on the mapped port.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: S3-compatible blob store behind IBlobStore"
```

---

### Task 9: Document building blocks (path, route, front matter, negotiation, rendering)

Pure functions with no database or HTTP, so every edge case is a fast unit test.

**Files:**
- Create: `src/Ddm.Api/Documents/{DocumentPath,DocRoute,FrontMatter,ContentNegotiation,MarkdownRenderer}.cs`
- Test: `tests/Ddm.Api.Tests/{DocumentPathTests,DocRouteTests,FrontMatterTests,ContentNegotiationTests,MarkdownRendererTests}.cs`

**Interfaces:**
- Consumes: `ApiException`.
- Produces:
  - `DocumentPath.Validate(string?) : string?` (error message or null), `DocumentPath.Require(string?) : string` (throws `ApiException` 400 `invalid_path`).
  - `abstract record DocRoute` with `Current(string Path)`, `History(string Path)`, `Snapshot(string Path, int Number)`, `Restore(string Path, int Number)` and `static DocRoute Parse(string rest)`.
  - `record ParsedMarkdown(string Title, string FrontMatterJson, string Body)`; `FrontMatter.Parse(string markdown, string path) : ParsedMarkdown` (throws 400 `invalid_front_matter`).
  - `enum DocFormat { Markdown, Html, Json }`; `ContentNegotiation.Choose(IList<MediaTypeHeaderValue>) : DocFormat?` (null means 406).
  - `MarkdownRenderer.ToHtml(string markdown) : string` (sanitized).

- [ ] **Step 1: Install packages**

```bash
dotnet add src/Ddm.Api package Markdig
dotnet add src/Ddm.Api package HtmlSanitizer
dotnet add src/Ddm.Api package YamlDotNet
```

- [ ] **Step 2: Write the failing tests**

`tests/Ddm.Api.Tests/DocumentPathTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Documents;

namespace Ddm.Api.Tests;

public class DocumentPathTests
{
    [Theory]
    [InlineData("setup.md")] [InlineData("guides/setup.md")] [InlineData("a/b/c/d.md")]
    [InlineData("_drafts/x.md")] [InlineData("v1.2/notes-final_2.md")] [InlineData("a.md/b.md")]
    public void Valid_paths_are_accepted(string path) => Assert.Null(DocumentPath.Validate(path));

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("setup")] [InlineData("setup.txt")] [InlineData("SETUP.MD")]
    [InlineData("/setup.md")] [InlineData("setup.md/")] [InlineData("a//b.md")] [InlineData("../x.md")]
    [InlineData("a/../x.md")] [InlineData("./x.md")] [InlineData(".hidden.md")] [InlineData("a/.md")]
    [InlineData("a\\b.md")] [InlineData("a b.md")] [InlineData("a%2fb.md")] [InlineData("é.md")]
    [InlineData("a\n/b.md")] [InlineData("a/b\n.md")] [InlineData("-x.md")]
    public void Invalid_paths_are_rejected(string? path) => Assert.NotNull(DocumentPath.Validate(path));

    [Fact] public void Overlong_paths_are_rejected() => Assert.NotNull(DocumentPath.Validate(new string('a', 253) + ".md"));
    [Fact] public void Overlong_segments_are_rejected() => Assert.NotNull(DocumentPath.Validate(new string('a', 101) + ".md"));
    [Fact] public void Too_many_segments_are_rejected() => Assert.NotNull(DocumentPath.Validate(string.Join('/', Enumerable.Repeat("a", 11)) + ".md"));

    [Fact]
    public void Require_throws_a_400_with_a_stable_code()
    {
        var ex = Assert.Throws<ApiException>(() => DocumentPath.Require("x.txt"));
        Assert.Equal(400, ex.Status);
        Assert.Equal("invalid_path", ex.Code);
    }
}
```

`tests/Ddm.Api.Tests/DocRouteTests.cs`:
```csharp
using Ddm.Api.Documents;

namespace Ddm.Api.Tests;

public class DocRouteTests
{
    [Fact] public void Plain_document() => Assert.Equal(new DocRoute.Current("guides/setup.md"), DocRoute.Parse("guides/setup.md"));
    [Fact] public void History() => Assert.Equal(new DocRoute.History("guides/setup.md"), DocRoute.Parse("guides/setup.md/versions"));
    [Fact] public void Snapshot() => Assert.Equal(new DocRoute.Snapshot("a.md", 3), DocRoute.Parse("a.md/versions/3"));
    [Fact] public void Restore() => Assert.Equal(new DocRoute.Restore("a.md", 3), DocRoute.Parse("a.md/versions/3/restore"));

    [Fact] public void A_document_named_versions_is_still_a_document() =>
        Assert.Equal(new DocRoute.Current("a/versions.md"), DocRoute.Parse("a/versions.md"));

    [Fact] public void A_directory_named_versions_is_still_a_document() =>
        Assert.Equal(new DocRoute.Current("versions/3.md"), DocRoute.Parse("versions/3.md"));

    [Fact] public void A_path_that_looks_like_a_suffix_but_ends_in_md_is_a_document() =>
        Assert.Equal(new DocRoute.Current("x.md/versions/3.md"), DocRoute.Parse("x.md/versions/3.md"));

    [Fact] public void Overflowing_version_numbers_become_0_and_so_404_later() =>
        Assert.Equal(new DocRoute.Snapshot("a.md", 0), DocRoute.Parse("a.md/versions/99999999999"));

    [Fact] public void Anything_else_falls_through_to_path_validation() =>
        Assert.Equal(new DocRoute.Current("a.md/versions/x"), DocRoute.Parse("a.md/versions/x"));
}
```

`tests/Ddm.Api.Tests/FrontMatterTests.cs`:
```csharp
using System.Diagnostics;
using Ddm.Api.Common;
using Ddm.Api.Documents;

namespace Ddm.Api.Tests;

public class FrontMatterTests
{
    private static JsonElement Json(ParsedMarkdown p) => JsonDocument.Parse(p.FrontMatterJson).RootElement;

    [Fact]
    public void Title_and_tags_come_from_front_matter()
    {
        var p = FrontMatter.Parse("---\ntitle: Setup\ntags: [onboarding, guide]\ndraft: true\nweight: 3\n---\n# Heading\nbody", "guides/setup.md");
        Assert.Equal("Setup", p.Title);
        Assert.Equal("# Heading\nbody", p.Body);
        Assert.Equal(["onboarding", "guide"], Json(p).GetProperty("tags").EnumerateArray().Select(e => e.GetString()));
        Assert.True(Json(p).GetProperty("draft").GetBoolean());
        Assert.Equal(3, Json(p).GetProperty("weight").GetInt32());
    }

    [Fact]
    public void Without_a_title_the_first_heading_is_used()
    {
        Assert.Equal("Hello World", FrontMatter.Parse("intro\n\n# Hello World\n\ntext", "a.md").Title);
    }

    [Fact]
    public void Headings_inside_code_fences_are_ignored()
    {
        Assert.Equal("setup", FrontMatter.Parse("```\n# not a title\n```\n", "guides/setup.md").Title);
    }

    [Fact]
    public void Without_anything_the_file_name_is_the_title()
    {
        Assert.Equal("setup", FrontMatter.Parse("no heading here", "guides/setup.md").Title);
        Assert.Equal("empty", FrontMatter.Parse("", "empty.md").Title);
    }

    [Fact]
    public void Crlf_and_a_byte_order_mark_are_handled()
    {
        var p = FrontMatter.Parse("﻿---\r\ntitle: Win\r\n---\r\n# X\r\n", "a.md");
        Assert.Equal("Win", p.Title);
    }

    [Fact]
    public void Empty_front_matter_is_an_empty_object()
    {
        var p = FrontMatter.Parse("---\n---\nbody", "a.md");
        Assert.Equal("{}", p.FrontMatterJson);
        Assert.Equal("body", p.Body);
    }

    [Fact]
    public void A_long_title_is_truncated_to_300_characters()
    {
        Assert.Equal(300, FrontMatter.Parse($"---\ntitle: {new string('x', 500)}\n---\n", "a.md").Title.Length);
    }

    // Review Focus 3
    [Theory]
    [InlineData("---\nthis is just text with a rule above and no closing")]
    [InlineData("---\n# Looks like a heading, never closed\n")]
    public void An_unclosed_leading_rule_is_body_text_not_front_matter(string markdown)
    {
        var p = FrontMatter.Parse(markdown, "a.md");
        Assert.Equal(markdown, p.Body);
        Assert.Equal("{}", p.FrontMatterJson);
    }

    [Fact]
    public void Four_dashes_is_a_horizontal_rule_not_front_matter()
    {
        var p = FrontMatter.Parse("----\ntitle: no\n----\n", "a.md");
        Assert.Equal("{}", p.FrontMatterJson);
    }

    [Theory]
    [InlineData("---\ntitle: [unclosed\n---\n")]
    [InlineData("---\n- just\n- a list\n---\n")]
    [InlineData("---\nplain scalar\n---\n")]
    public void Malformed_or_non_mapping_front_matter_is_a_400(string markdown)
    {
        var ex = Assert.Throws<ApiException>(() => FrontMatter.Parse(markdown, "a.md"));
        Assert.Equal(400, ex.Status);
        Assert.Equal("invalid_front_matter", ex.Code);
    }

    // Review Focus 3
    [Fact]
    public void A_yaml_alias_bomb_is_rejected_quickly()
    {
        var yaml = new StringBuilder("---\na: &a [x,x,x,x,x,x,x,x,x]\n");
        var prev = "a";
        for (var i = 0; i < 9; i++)
        {
            var name = $"l{i}";
            yaml.Append($"{name}: &{name} [{string.Join(',', Enumerable.Repeat('*' + prev, 9))}]\n");
            prev = name;
        }
        yaml.Append("---\n");

        var sw = Stopwatch.StartNew();
        var ex = Assert.Throws<ApiException>(() => FrontMatter.Parse(yaml.ToString(), "a.md"));
        Assert.Equal("invalid_front_matter", ex.Code);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));
    }

    // Review Focus 3
    [Fact]
    public void Deeply_nested_yaml_is_rejected_without_crashing()
    {
        var nested = new string('[', 3000) + new string(']', 3000);
        var ex = Assert.Throws<ApiException>(() => FrontMatter.Parse($"---\na: {nested}\n---\n", "a.md"));
        Assert.Equal("invalid_front_matter", ex.Code);
    }

    [Fact]
    public void Oversized_front_matter_is_rejected()
    {
        var big = "---\n" + string.Concat(Enumerable.Range(0, 3000).Select(i => $"key{i}: value{i}\n")) + "---\n";
        Assert.Equal("invalid_front_matter", Assert.Throws<ApiException>(() => FrontMatter.Parse(big, "a.md")).Code);
    }
}
```

`tests/Ddm.Api.Tests/ContentNegotiationTests.cs`:
```csharp
using Ddm.Api.Documents;
using Microsoft.Net.Http.Headers;

namespace Ddm.Api.Tests;

public class ContentNegotiationTests
{
    private static DocFormat? Choose(params string[] accept) =>
        ContentNegotiation.Choose(accept.Length == 0 ? [] : MediaTypeHeaderValue.ParseList(accept));

    [Fact] public void No_accept_header_means_markdown() => Assert.Equal(DocFormat.Markdown, Choose());
    [Fact] public void Wildcard_means_markdown() => Assert.Equal(DocFormat.Markdown, Choose("*/*"));
    [Fact] public void Html_is_chosen_when_asked_for() => Assert.Equal(DocFormat.Html, Choose("text/html"));
    [Fact] public void Json_is_chosen_when_asked_for() => Assert.Equal(DocFormat.Json, Choose("application/json"));
    [Fact] public void Quality_values_decide() => Assert.Equal(DocFormat.Html, Choose("text/markdown;q=0.5, text/html"));
    [Fact] public void Listed_order_breaks_ties() => Assert.Equal(DocFormat.Markdown, Choose("text/markdown, text/html"));
    [Fact] public void Unsupported_types_are_not_acceptable() => Assert.Null(Choose("application/xml"));
    [Fact] public void Zero_quality_excludes_a_type() => Assert.Null(Choose("text/html;q=0"));
    [Fact] public void Falls_through_to_a_supported_type() => Assert.Equal(DocFormat.Html, Choose("application/xml, text/html;q=0.1"));
}
```

`tests/Ddm.Api.Tests/MarkdownRendererTests.cs`:
```csharp
using Ddm.Api.Documents;

namespace Ddm.Api.Tests;

public class MarkdownRendererTests
{
    private static readonly MarkdownRenderer Renderer = new();

    [Fact]
    public void Renders_headings_with_anchor_ids_tables_and_code_fences()
    {
        var html = Renderer.ToHtml("# Title\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```csharp\nvar x = 1;\n```\n");
        Assert.Contains("<h1", html);
        Assert.Contains("id=\"title\"", html);
        Assert.Contains("<table", html);
        Assert.Contains("<code", html);
    }

    [Fact]
    public void Renders_admonition_style_alerts()
    {
        Assert.Contains("markdown-alert", Renderer.ToHtml("> [!NOTE]\n> Remember this\n"));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "<script")]
    [InlineData("<img src=x onerror=alert(1)>", "onerror")]
    [InlineData("[click](javascript:alert(1))", "javascript:")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "javascript:")]
    [InlineData("<iframe src=\"https://evil.example\"></iframe>", "<iframe")]
    [InlineData("<div onclick=\"x()\">hi</div>", "onclick")]
    [InlineData("![x](data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSk+)", "data:")]
    [InlineData("<svg onload=alert(1)></svg>", "onload")]
    public void Dangerous_content_is_removed(string markdown, string forbidden) =>
        Assert.DoesNotContain(forbidden, Renderer.ToHtml(markdown), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Safe_links_survive() =>
        Assert.Contains("href=\"https://example.com\"", Renderer.ToHtml("[ok](https://example.com)"));
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DocumentPathTests|FullyQualifiedName~DocRouteTests|FullyQualifiedName~FrontMatterTests|FullyQualifiedName~ContentNegotiationTests|FullyQualifiedName~MarkdownRendererTests"`
Expected: build FAILS (`Ddm.Api.Documents` types not defined).

- [ ] **Step 4: Write the implementation**

`src/Ddm.Api/Documents/DocumentPath.cs`:
```csharp
using System.Text.RegularExpressions;
using Ddm.Api.Common;

namespace Ddm.Api.Documents;

public static partial class DocumentPath
{
    public const int MaxLength = 255;
    public const int MaxSegments = 10;
    public const int MaxSegmentLength = 100;

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._-]*\z")]
    private static partial Regex SegmentRegex();

    /// <summary>Returns an error message, or null when the path is valid.</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "Path is required";
        if (path.Length > MaxLength) return $"Path is limited to {MaxLength} characters";
        if (!path.EndsWith(".md", StringComparison.Ordinal)) return "Path must end with .md";
        var segments = path.Split('/');
        if (segments.Length > MaxSegments) return $"Path is limited to {MaxSegments} segments";
        foreach (var s in segments)
            if (s.Length > MaxSegmentLength || !SegmentRegex().IsMatch(s))
                return $"Invalid path segment '{s}': use letters, digits, '.', '_' and '-', starting with a letter, digit or '_'";
        return null;
    }

    public static string Require(string? path) =>
        Validate(path) is { } error
            ? throw ApiException.BadRequest("invalid_path", "Invalid document path", error)
            : path!;
}
```

`src/Ddm.Api/Documents/DocRoute.cs`:
```csharp
using System.Text.RegularExpressions;

namespace Ddm.Api.Documents;

/// <summary>
/// Splits the catch-all part of /docs/{**rest}. Document paths always end in ".md", which is
/// what makes "…/versions" suffixes unambiguous.
/// </summary>
public abstract partial record DocRoute
{
    public sealed record Current(string Path) : DocRoute;
    public sealed record History(string Path) : DocRoute;
    public sealed record Snapshot(string Path, int Number) : DocRoute;
    public sealed record Restore(string Path, int Number) : DocRoute;

    [GeneratedRegex(@"^(?<path>.+\.md)(?:/versions(?:/(?<n>[0-9]+)(?<restore>/restore)?)?)?\z")]
    private static partial Regex RouteRegex();

    public static DocRoute Parse(string rest)
    {
        var m = RouteRegex().Match(rest);
        if (!m.Success) return new Current(rest);
        var path = m.Groups["path"].Value;
        if (!m.Groups["n"].Success) return m.Length == path.Length ? new Current(path) : new History(path);
        if (!int.TryParse(m.Groups["n"].Value, out var n)) n = 0;
        return m.Groups["restore"].Success ? new Restore(path, n) : new Snapshot(path, n);
    }
}
```

`src/Ddm.Api/Documents/FrontMatter.cs`:
```csharp
using System.Text;
using System.Text.Json;
using Ddm.Api.Common;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Ddm.Api.Documents;

public sealed record ParsedMarkdown(string Title, string FrontMatterJson, string Body);

public static class FrontMatter
{
    public const int MaxBytes = 16 * 1024;
    private const int MaxDepth = 20;
    private const int MaxTitle = 300;

    public static ParsedMarkdown Parse(string markdown, string path)
    {
        var (yaml, body) = Split(markdown.TrimStart('﻿'));
        var json = yaml is null ? "{}" : ToJson(yaml);
        var title = TitleFrom(json) ?? FirstHeading(body) ?? System.IO.Path.GetFileNameWithoutExtension(path);
        return new(title.Length <= MaxTitle ? title : title[..MaxTitle], json, body);
    }

    /// <summary>A leading "---" line opens front matter only if a closing "---" line follows.</summary>
    private static (string? Yaml, string Body) Split(string text)
    {
        using var reader = new StringReader(text);
        if (reader.ReadLine()?.TrimEnd() != "---") return (null, text);
        var yaml = new StringBuilder();
        while (reader.ReadLine() is { } line)
        {
            if (line.TrimEnd() == "---") return (yaml.ToString(), reader.ReadToEnd());
            yaml.AppendLine(line);
        }
        return (null, text);
    }

    private static string ToJson(string yaml)
    {
        if (Encoding.UTF8.GetByteCount(yaml) > MaxBytes) throw Invalid($"Front matter is limited to {MaxBytes} bytes");
        if (string.IsNullOrWhiteSpace(yaml)) return "{}";
        try
        {
            // Reject aliases (billion-laughs) and deep nesting before anything recurses over the document.
            var parser = new Parser(new StringReader(yaml));
            var depth = 0;
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case AnchorAlias: throw Invalid("YAML aliases are not supported in front matter");
                    case SequenceStart or MappingStart when ++depth > MaxDepth: throw Invalid($"Front matter is nested deeper than {MaxDepth} levels");
                    case SequenceEnd or MappingEnd: depth--; break;
                }
            }

            var value = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build().Deserialize<object?>(yaml);
            if (value is null) return "{}";
            if (value is not System.Collections.IDictionary) throw Invalid("Front matter must be a YAML mapping (key: value pairs)");

            var json = new SerializerBuilder().JsonCompatible().Build().Serialize(value).Trim();
            using (JsonDocument.Parse(json)) { }
            return json;
        }
        catch (YamlException ex) { throw Invalid(ex.Message); }
        catch (JsonException) { throw Invalid("Front matter could not be represented as JSON"); }
    }

    private static string? TitleFrom(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
               && !string.IsNullOrWhiteSpace(t.GetString())
            ? t.GetString()!.Trim()
            : null;
    }

    private static string? FirstHeading(string body)
    {
        var inFence = false;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("```") || line.StartsWith("~~~")) inFence = !inFence;
            else if (!inFence && line.StartsWith("# ") && line[2..].Trim() is { Length: > 0 } heading) return heading;
        }
        return null;
    }

    private static ApiException Invalid(string detail) =>
        ApiException.BadRequest("invalid_front_matter", "Invalid front matter", detail);
}
```

`src/Ddm.Api/Documents/ContentNegotiation.cs`:
```csharp
using Microsoft.Net.Http.Headers;

namespace Ddm.Api.Documents;

public enum DocFormat { Markdown, Html, Json }

public static class ContentNegotiation
{
    /// <summary>Returns null when nothing the client accepts is available (a 406).</summary>
    public static DocFormat? Choose(IList<MediaTypeHeaderValue> accept)
    {
        if (accept.Count == 0) return DocFormat.Markdown;
        var ordered = accept.Select((m, i) => (Media: m, Index: i))
            .OrderByDescending(x => x.Media.Quality ?? 1.0).ThenBy(x => x.Index);
        foreach (var (media, _) in ordered)
        {
            if ((media.Quality ?? 1.0) <= 0) continue;
            switch (media.MediaType.Value?.ToLowerInvariant())
            {
                case "text/markdown": return DocFormat.Markdown;
                case "text/html": return DocFormat.Html;
                case "application/json": return DocFormat.Json;
                case "text/*" or "*/*": return DocFormat.Markdown;
            }
        }
        return null;
    }
}
```

`src/Ddm.Api/Documents/MarkdownRenderer.cs`:
```csharp
using Ganss.Xss;
using Markdig;

namespace Ddm.Api.Documents;

public sealed class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAlertBlocks()
        .Build();

    /// <summary>Renders markdown to HTML, then sanitizes against an allow-list. Raw HTML in the source is untrusted.</summary>
    public string ToHtml(string markdown)
    {
        var sanitizer = new HtmlSanitizer(); // a fresh instance per call: sanitizers are not safe to share once configured
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("id");
        return sanitizer.Sanitize(Markdown.ToHtml(markdown, Pipeline));
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DocumentPathTests|FullyQualifiedName~DocRouteTests|FullyQualifiedName~FrontMatterTests|FullyQualifiedName~ContentNegotiationTests|FullyQualifiedName~MarkdownRendererTests"`
Expected: PASS. If a sanitizer case fails (for example `<svg onload>` leaving `onload`), check the HtmlSanitizer default `AllowedTags`; `svg` is not allowed by default, so the whole element should vanish.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: document path, route, front matter, negotiation and sanitized rendering"
```

---

### Task 10: Document write and read path (versions, ETags, content negotiation)

**Files:**
- Create: `src/Ddm.Api/Documents/{Preconditions,DocumentDtos,DocumentService,DocumentEndpoints}.cs`
- Modify: `src/Ddm.Api/Program.cs`, `tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs`
- Test: `tests/Ddm.Api.Tests/PreconditionsTests.cs`, `tests/Ddm.Api.Tests/DocumentWriteReadTests.cs`

**Interfaces:**
- Consumes: Task 8 `IBlobStore`; Task 9 `DocumentPath.Require`, `DocRoute.Parse`, `FrontMatter.Parse`, `ContentNegotiation.Choose`, `MarkdownRenderer`; Task 5 `ProjectAuthorizer`, `db.Audit`; Task 3 `IsUniqueViolation`.
- Produces:
  - `readonly record struct WritePrecondition(int? IfMatchVersion, bool IfMatchAny, bool IfNoneMatchAny)`; `Preconditions.Parse(IHeaderDictionary) : WritePrecondition` (400 `invalid_etag`); `Preconditions.ETag(int version) : string` (returns `"v7"` including the quotes).
  - `DocumentDto(string Path, string Title, int Version, DateTimeOffset UpdatedAt, JsonElement FrontMatter)` with `static From(string path, ParsedMarkdown p, ContentVersion v)`; `CreateDocumentRequest(string? Path, string? Content, string? Message)`.
  - `sealed record WriteResult(string Path, ParsedMarkdown Parsed, ContentVersion Version, bool Created, bool Changed)`.
  - `DocumentService` (scoped): `const int MaxBytes = 1_048_576`; `static readonly UTF8Encoding StrictUtf8`; `Task<WriteResult> WriteAsync(Caller caller, Project project, string path, byte[] bytes, string? message, WritePrecondition pre, bool requireIfMatch, CancellationToken ct)`; `Task<(Document Doc, ContentVersion Version)> GetCurrentAsync(Project project, string path, CancellationToken ct)` (404 `document_not_found`); `Task<byte[]> ReadContentAsync(ContentVersion v, CancellationToken ct)`; `Task<bool> ExistsAsync(Project project, string path, CancellationToken ct)`.
  - `v1.MapDocuments()` with `POST /projects/{slug}/docs`, `GET|PUT /projects/{slug}/docs/{**rest}`; test helpers `PutDocAsync(client, slug, path, markdown, ifMatch = null, message = null)` and `GetDocAsync(client, slug, path, accept = null)`.
- Blob key scheme: `projects/{projectId:N}/docs/{sha256}.md`.

- [ ] **Step 1: Write the failing tests**

Add to `ApiTestBase`:
```csharp
    protected static Task<HttpResponseMessage> PutDocAsync(
        HttpClient client, string slug, string path, string markdown, string? ifMatch = null, string? message = null, string? ifNoneMatch = null)
    {
        var url = $"/api/v1/projects/{slug}/docs/{path}" + (message is null ? "" : $"?message={Uri.EscapeDataString(message)}");
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(markdown, Encoding.UTF8, "text/markdown") };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return client.SendAsync(request);
    }

    protected static Task<HttpResponseMessage> GetDocAsync(HttpClient client, string slug, string path, string? accept = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/projects/{slug}/docs/{path}");
        if (accept is not null) request.Headers.TryAddWithoutValidation("Accept", accept);
        return client.SendAsync(request);
    }
```

`tests/Ddm.Api.Tests/PreconditionsTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Documents;
using Microsoft.AspNetCore.Http;

namespace Ddm.Api.Tests;

public class PreconditionsTests
{
    private static WritePrecondition Parse(string? ifMatch = null, string? ifNoneMatch = null)
    {
        var h = new HeaderDictionary();
        if (ifMatch is not null) h.IfMatch = ifMatch;
        if (ifNoneMatch is not null) h.IfNoneMatch = ifNoneMatch;
        return Preconditions.Parse(h);
    }

    [Fact] public void No_headers_means_no_preconditions() => Assert.Equal(new WritePrecondition(null, false, false), Parse());
    [Fact] public void Version_etag() => Assert.Equal(7, Parse("\"v7\"").IfMatchVersion);
    [Fact] public void Star_matches_any() => Assert.True(Parse("*").IfMatchAny);
    [Fact] public void If_none_match_star() => Assert.True(Parse(ifNoneMatch: "*").IfNoneMatchAny);
    [Fact] public void Etag_formatting() => Assert.Equal("\"v12\"", Preconditions.ETag(12));

    [Theory]
    [InlineData("v7")] [InlineData("\"7\"")] [InlineData("\"v\"")] [InlineData("\"v0\"")] [InlineData("\"v-1\"")]
    [InlineData("\"vx\"")] [InlineData("W/\"v7\"")] [InlineData("\"v1\", \"v2\"")] [InlineData("\"v99999999999\"")]
    public void Malformed_if_match_is_a_400(string value)
    {
        var ex = Assert.Throws<ApiException>(() => Parse(value));
        Assert.Equal("invalid_etag", ex.Code);
    }

    [Fact]
    public void If_none_match_only_supports_star() =>
        Assert.Equal("invalid_etag", Assert.Throws<ApiException>(() => Parse(ifNoneMatch: "\"v1\"")).Code);
}
```

`tests/Ddm.Api.Tests/DocumentWriteReadTests.cs`:
```csharp
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class DocumentWriteReadTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private const string Page = "---\ntitle: Setup\ntags: [onboarding, guide]\n---\n# Setup\n\nHello.\n";

    private async Task<HttpClient> ProjectWithAliceAsync(string slug = "p", string visibility = "private")
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, slug, visibility);
        return alice;
    }

    private async Task<T> WithDbAsync<T>(Func<DdmDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<DdmDbContext>());
    }

    [Fact]
    public async Task Put_creates_a_document_at_version_1()
    {
        var alice = await ProjectWithAliceAsync();
        var r = await PutDocAsync(alice, "p", "guides/setup.md", Page);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v1\"", r.Headers.ETag!.Tag);
        var dto = await ReadAsync<DocumentDto>(r);
        Assert.Equal("guides/setup.md", dto.Path);
        Assert.Equal("Setup", dto.Title);
        Assert.Equal(1, dto.Version);
        Assert.Equal("onboarding", dto.FrontMatter.GetProperty("tags")[0].GetString());
    }

    [Fact]
    public async Task Get_returns_the_original_markdown_by_default_and_by_accept()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "setup.md", Page);
        foreach (var accept in new string?[] { null, "text/markdown", "*/*" })
        {
            var r = await GetDocAsync(alice, "p", "setup.md", accept);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.StartsWith("text/markdown", r.Content.Headers.ContentType!.MediaType);
            Assert.Equal(Page, await r.Content.ReadAsStringAsync());
            Assert.Equal("\"v1\"", r.Headers.ETag!.Tag);
        }
    }

    [Fact]
    public async Task Get_with_accept_html_returns_sanitized_html_without_the_front_matter()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "x.md", "---\ntitle: T\n---\n# Hi\n\n<script>alert(1)</script>\n");
        var r = await GetDocAsync(alice, "p", "x.md", "text/html");
        Assert.StartsWith("text/html", r.Content.Headers.ContentType!.MediaType);
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("<h1", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("title: T", html);
    }

    [Fact]
    public async Task Get_with_accept_json_returns_metadata()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "setup.md", Page);
        var dto = await ReadAsync<DocumentDto>(await GetDocAsync(alice, "p", "setup.md", "application/json"));
        Assert.Equal("Setup", dto.Title);
        Assert.Equal(1, dto.Version);
    }

    [Fact]
    public async Task Get_with_an_unsupported_accept_is_406()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "setup.md", Page);
        var r = await GetDocAsync(alice, "p", "setup.md", "application/xml");
        Assert.Equal(HttpStatusCode.NotAcceptable, r.StatusCode);
        Assert.Equal("not_acceptable", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Get_of_a_missing_document_is_404()
    {
        var alice = await ProjectWithAliceAsync();
        var r = await GetDocAsync(alice, "p", "nope.md");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("document_not_found", await ProblemCodeAsync(r));
    }

    [Theory] [InlineData("x.txt")] [InlineData("a//b.md")] [InlineData(".hidden.md")]
    public async Task Invalid_paths_are_a_400_and_nothing_is_stored(string path)
    {
        var alice = await ProjectWithAliceAsync();
        var r = await PutDocAsync(alice, "p", path, "# x");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_path", await ProblemCodeAsync(r));
        Assert.Equal(0, Factory.Blobs.Count);
    }

    [Fact]
    public async Task Updating_an_existing_document_requires_if_match()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# one");
        var r = await PutDocAsync(alice, "p", "a.md", "# two");
        Assert.Equal((HttpStatusCode)428, r.StatusCode);
        Assert.Equal("precondition_required", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Update_with_the_current_etag_creates_the_next_version()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# one");
        var r = await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"", message: "second draft");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("\"v2\"", r.Headers.ETag!.Tag);
        Assert.Equal("# two", await (await GetDocAsync(alice, "p", "a.md")).Content.ReadAsStringAsync());
        var v2 = await WithDbAsync(db => db.Versions.SingleAsync(v => v.Number == 2));
        Assert.Equal("second draft", v2.Message);
        Assert.Equal("user:alice", v2.Author);
    }

    [Fact]
    public async Task A_stale_etag_returns_412_and_leaves_the_content_alone()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# one");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"");
        var r = await PutDocAsync(alice, "p", "a.md", "# stale overwrite", ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, r.StatusCode);
        Assert.Equal("precondition_failed", await ProblemCodeAsync(r));
        Assert.Equal("# two", await (await GetDocAsync(alice, "p", "a.md")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task If_match_on_a_document_that_does_not_exist_is_412()
    {
        var alice = await ProjectWithAliceAsync();
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutDocAsync(alice, "p", "new.md", "# x", ifMatch: "\"v1\"")).StatusCode);
    }

    [Fact]
    public async Task If_none_match_star_means_create_only()
    {
        var alice = await ProjectWithAliceAsync();
        Assert.Equal(HttpStatusCode.Created, (await PutDocAsync(alice, "p", "a.md", "# x", ifNoneMatch: "*")).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutDocAsync(alice, "p", "a.md", "# y", ifNoneMatch: "*")).StatusCode);
    }

    [Fact]
    public async Task A_malformed_if_match_is_a_400()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# x");
        var r = await PutDocAsync(alice, "p", "a.md", "# y", ifMatch: "v1");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_etag", await ProblemCodeAsync(r));
    }

    // Review Focus 2
    [Fact]
    public async Task Republishing_identical_content_creates_no_new_version()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", Page);
        var again = await PutDocAsync(alice, "p", "a.md", Page, ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("\"v1\"", again.Headers.ETag!.Tag);
        Assert.Equal(1, await WithDbAsync(db => db.Versions.CountAsync()));
        Assert.Equal(1, await WithDbAsync(db => db.AuditEntries.CountAsync(e => e.Action.StartsWith("doc."))));
    }

    // Review Focus 1
    [Fact]
    public async Task Concurrent_writers_with_the_same_etag_produce_one_winner_and_no_500s()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# v1");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => PutDocAsync(alice, "p", "a.md", $"# change {i}", ifMatch: "\"v1\"")));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed));
        Assert.Equal(2, await WithDbAsync(db => db.Versions.CountAsync()));
    }

    [Fact]
    public async Task Concurrent_creates_of_the_same_path_produce_one_winner_and_no_500s()
    {
        var alice = await ProjectWithAliceAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(i => PutDocAsync(alice, "p", "same.md", $"# create {i}")));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Equal(HttpStatusCode.PreconditionFailed, r.StatusCode));
        Assert.Equal(1, await WithDbAsync(db => db.Documents.CountAsync()));
    }

    [Fact]
    public async Task A_storage_outage_fails_the_write_and_stores_nothing()
    {
        var alice = await ProjectWithAliceAsync();
        Factory.Blobs.FailNextPut = true;
        var r = await PutDocAsync(alice, "p", "a.md", "# x");
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal("internal_error", await ProblemCodeAsync(r));
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
        Assert.Equal(0, await WithDbAsync(db => db.Documents.CountAsync()));
        Assert.Equal(0, await WithDbAsync(db => db.Versions.CountAsync()));
    }

    [Fact]
    public async Task Malformed_front_matter_is_a_400_and_nothing_is_stored()
    {
        var alice = await ProjectWithAliceAsync();
        var r = await PutDocAsync(alice, "p", "a.md", "---\ntitle: [oops\n---\n# x");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_front_matter", await ProblemCodeAsync(r));
        Assert.Equal(0, Factory.Blobs.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
    }

    [Fact]
    public async Task An_empty_body_creates_an_empty_document_titled_by_its_file_name()
    {
        var alice = await ProjectWithAliceAsync();
        var r = await PutDocAsync(alice, "p", "empty.md", "");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("empty", (await ReadAsync<DocumentDto>(r)).Title);
    }

    [Fact]
    public async Task A_body_over_one_mebibyte_is_413()
    {
        var alice = await ProjectWithAliceAsync();
        var r = await PutDocAsync(alice, "p", "big.md", new string('a', DocumentService.MaxBytes + 1));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal("payload_too_large", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task A_body_that_is_not_utf8_is_a_400()
    {
        var alice = await ProjectWithAliceAsync();
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/projects/p/docs/bad.md")
        {
            Content = new ByteArrayContent([0xFF, 0xFE, 0xFD]),
        };
        request.Content.Headers.ContentType = new("text/markdown");
        var r = await alice.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_encoding", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task A_non_markdown_content_type_is_415()
    {
        var alice = await ProjectWithAliceAsync();
        var r = await alice.PutAsync("/api/v1/projects/p/docs/a.md", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, r.StatusCode);
    }

    [Fact]
    public async Task Readers_cannot_write_and_strangers_cannot_even_see_the_project()
    {
        var alice = await ProjectWithAliceAsync("shared", "internal");
        await CreateProjectAsync(alice, "secret", "private");
        var bob = ClientFor("bob");

        var forbidden = await PutDocAsync(bob, "shared", "a.md", "# x");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("insufficient_role", await ProblemCodeAsync(forbidden));

        Assert.Equal(HttpStatusCode.NotFound, (await PutDocAsync(bob, "secret", "a.md", "# x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(bob, "secret", "a.md")).StatusCode);
        // and an invalid path must not turn the 404 into a 400 (that would confirm the project exists)
        Assert.Equal(HttpStatusCode.NotFound, (await PutDocAsync(bob, "secret", "x.txt", "# x")).StatusCode);
    }

    [Fact]
    public async Task Readers_can_read_internal_documents()
    {
        var alice = await ProjectWithAliceAsync("shared", "internal");
        await PutDocAsync(alice, "shared", "a.md", "# x");
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(ClientFor("bob"), "shared", "a.md")).StatusCode);
    }

    [Fact]
    public async Task The_same_path_in_two_projects_is_independent()
    {
        var alice = await ProjectWithAliceAsync("one");
        await CreateProjectAsync(alice, "two");
        await PutDocAsync(alice, "one", "a.md", "# one");
        await PutDocAsync(alice, "two", "a.md", "# two");
        Assert.Equal("# one", await (await GetDocAsync(alice, "one", "a.md")).Content.ReadAsStringAsync());
        Assert.Equal("# two", await (await GetDocAsync(alice, "two", "a.md")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Write_tokens_can_publish_and_read_tokens_cannot()
    {
        var alice = await ProjectWithAliceAsync();
        var writer = TokenClient(await CreateTokenAsync(alice, "p", "write"));
        var reader = TokenClient(await CreateTokenAsync(alice, "p", "read", "ro"));

        Assert.Equal(HttpStatusCode.Created, (await PutDocAsync(writer, "p", "ci.md", "# from ci")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutDocAsync(reader, "p", "ci2.md", "# nope")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(reader, "p", "ci.md")).StatusCode);
        var version = await WithDbAsync(db => db.Versions.SingleAsync());
        Assert.StartsWith("token:", version.Author);
    }

    [Fact]
    public async Task Post_creates_a_document_and_a_duplicate_is_409()
    {
        var alice = await ProjectWithAliceAsync();
        var created = await alice.PostAsJsonAsync("/api/v1/projects/p/docs", new { path = "guides/new.md", content = "# New", message = "init" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/v1/projects/p/docs/guides/new.md", created.Headers.Location!.ToString());

        var dup = await alice.PostAsJsonAsync("/api/v1/projects/p/docs", new { path = "guides/new.md", content = "# Again" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal("document_exists", await ProblemCodeAsync(dup));
    }

    [Fact]
    public async Task Post_validates_path_and_content()
    {
        var alice = await ProjectWithAliceAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/projects/p/docs", new { path = "x.txt", content = "# x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.PostAsJsonAsync("/api/v1/projects/p/docs", new { path = "x.md" })).StatusCode);
    }

    [Fact]
    public async Task Writes_are_audited()
    {
        var alice = await ProjectWithAliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# one");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"");
        var actions = await WithDbAsync(db => db.AuditEntries.Where(e => e.Action.StartsWith("doc.")).OrderBy(e => e.Id).Select(e => e.Action).ToListAsync());
        Assert.Equal(["doc.create", "doc.update"], actions);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PreconditionsTests|FullyQualifiedName~DocumentWriteReadTests"`
Expected: build FAILS (`Preconditions`, `DocumentDto`, `DocumentService` not defined).

- [ ] **Step 3: Write preconditions and DTOs**

`src/Ddm.Api/Documents/Preconditions.cs`:
```csharp
using System.Globalization;
using Ddm.Api.Common;

namespace Ddm.Api.Documents;

public readonly record struct WritePrecondition(int? IfMatchVersion, bool IfMatchAny, bool IfNoneMatchAny)
{
    public bool HasIfMatch => IfMatchVersion is not null || IfMatchAny;
}

public static class Preconditions
{
    public static string ETag(int version) => $"\"v{version}\"";

    public static WritePrecondition Parse(IHeaderDictionary headers)
    {
        int? version = null;
        var any = false;
        var ifMatch = headers.IfMatch;
        if (ifMatch.Count > 1) throw Invalid("Send a single If-Match value");
        if (ifMatch.Count == 1)
        {
            var raw = ifMatch[0]!.Trim();
            if (raw == "*") any = true;
            else if (TryParseVersion(raw, out var n)) version = n;
            else throw Invalid("If-Match must be a quoted version ETag such as \"v7\", or *");
        }

        var ifNoneMatch = headers.IfNoneMatch;
        var noneAny = ifNoneMatch.Count == 1 && ifNoneMatch[0]!.Trim() == "*";
        if (ifNoneMatch.Count > 0 && !noneAny) throw Invalid("If-None-Match only supports *");
        return new(version, any, noneAny);
    }

    private static bool TryParseVersion(string raw, out int n)
    {
        n = 0;
        return raw.Length >= 4 && raw.StartsWith("\"v", StringComparison.Ordinal) && raw.EndsWith('"')
               && int.TryParse(raw.AsSpan(2, raw.Length - 3), NumberStyles.None, CultureInfo.InvariantCulture, out n)
               && n > 0;
    }

    private static ApiException Invalid(string detail) => ApiException.BadRequest("invalid_etag", "Invalid ETag precondition", detail);
}
```

`src/Ddm.Api/Documents/DocumentDtos.cs`:
```csharp
using System.Text.Json;
using Ddm.Api.Domain;

namespace Ddm.Api.Documents;

public sealed record DocumentDto(string Path, string Title, int Version, DateTimeOffset UpdatedAt, JsonElement FrontMatter)
{
    public static DocumentDto From(string path, ParsedMarkdown parsed, ContentVersion version)
    {
        using var doc = JsonDocument.Parse(parsed.FrontMatterJson);
        return new(path, parsed.Title, version.Number, version.CreatedAt, doc.RootElement.Clone());
    }
}

public sealed record CreateDocumentRequest(string? Path, string? Content, string? Message);
```

- [ ] **Step 4: Write the document service**

`src/Ddm.Api/Documents/DocumentService.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Documents;

public sealed record WriteResult(string Path, ParsedMarkdown Parsed, ContentVersion Version, bool Created, bool Changed);

public sealed class DocumentService(DdmDbContext db, IBlobStore blobs)
{
    public const int MaxBytes = 1_048_576;
    public static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Validates, stores the body (content-addressed, so a failed DB commit only leaves a harmless orphan),
    /// then creates the version and moves the current pointer in one transaction.
    /// </summary>
    public async Task<WriteResult> WriteAsync(
        Caller caller, Project project, string path, byte[] bytes, string? message,
        WritePrecondition pre, bool requireIfMatch, CancellationToken ct)
    {
        if (bytes.Length > MaxBytes) throw ApiException.PayloadTooLarge($"Documents are limited to {MaxBytes} bytes");
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw ApiException.BadRequest("invalid_encoding", "Document content must be valid UTF-8"); }
        var parsed = FrontMatter.Parse(text, path);

        var existing = await db.Documents.SingleOrDefaultAsync(d => d.ProjectId == project.Id && d.Path == path, ct);
        var current = existing?.CurrentVersionId is { } cid ? await db.Versions.SingleAsync(v => v.Id == cid, ct) : null;
        CheckPreconditions(existing, current, pre, requireIfMatch);

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (current is not null && current.ContentSha256 == sha)
            return new(path, parsed, current, Created: false, Changed: false);

        var key = $"projects/{project.Id:N}/docs/{sha}.md";
        await blobs.PutAsync(key, bytes, "text/markdown; charset=utf-8", ct);

        var created = existing is null;
        var doc = existing ?? new Document { ProjectId = project.Id, Path = path };
        var version = new ContentVersion
        {
            ItemType = ItemType.Document, ItemId = doc.Id, Number = (current?.Number ?? 0) + 1,
            ContentRef = key, ContentSha256 = sha, Author = caller.Actor, Message = message,
        };
        doc.Title = parsed.Title;
        doc.FrontMatter = parsed.FrontMatterJson;
        doc.CurrentVersionId = version.Id;
        doc.UpdatedAt = version.CreatedAt;
        if (created) db.Documents.Add(doc);
        db.Versions.Add(version);
        db.Audit(caller, project.Id, created ? "doc.create" : "doc.update", path);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Another writer took this version number (or created this path) first.
            throw ApiException.PreconditionFailed("The document was changed by another writer; fetch the latest version and retry");
        }
        return new(path, parsed, version, created, Changed: true);
    }

    private static void CheckPreconditions(Document? existing, ContentVersion? current, WritePrecondition pre, bool requireIfMatch)
    {
        if (existing is null)
        {
            if (pre.HasIfMatch) throw ApiException.PreconditionFailed("The document does not exist");
            return;
        }
        if (pre.IfNoneMatchAny) throw ApiException.PreconditionFailed("The document already exists");
        if (!pre.HasIfMatch)
        {
            if (requireIfMatch)
                throw ApiException.PreconditionRequired("Updating an existing document requires an If-Match header with its current ETag");
            return;
        }
        if (pre.IfMatchVersion is { } v && v != current!.Number)
            throw ApiException.PreconditionFailed($"The document is at version {current.Number}, not {v}");
    }

    public async Task<(Document Doc, ContentVersion Version)> GetCurrentAsync(Project project, string path, CancellationToken ct)
    {
        var doc = await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.ProjectId == project.Id && d.Path == path, ct)
            ?? throw ApiException.NotFound("document_not_found", "Document not found");
        var version = await db.Versions.AsNoTracking().SingleAsync(v => v.Id == doc.CurrentVersionId, ct);
        return (doc, version);
    }

    public Task<bool> ExistsAsync(Project project, string path, CancellationToken ct) =>
        db.Documents.AnyAsync(d => d.ProjectId == project.Id && d.Path == path, ct);

    public async Task<byte[]> ReadContentAsync(ContentVersion version, CancellationToken ct) =>
        await blobs.GetAsync(version.ContentRef, ct)
        ?? throw new ApiException(500, "content_missing", "Stored content is missing", $"No object for {version.ContentRef}");
}
```

- [ ] **Step 5: Write the endpoints**

`src/Ddm.Api/Documents/DocumentEndpoints.cs`:
```csharp
using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Domain;
using Ddm.Api.Identity;

namespace Ddm.Api.Documents;

public static class DocumentEndpoints
{
    public static void MapDocuments(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/projects/{slug}/docs");
        g.MapPost("", CreateAsync);
        g.MapGet("{**rest}", GetAsync);
        g.MapPut("{**rest}", PutAsync);
    }

    private static async Task<IResult> CreateAsync(
        string slug, CreateDocumentRequest? body, HttpContext http, ClaimsPrincipal user,
        ProjectAuthorizer authz, DocumentService docs, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        var path = DocumentPath.Require(body?.Path);
        if (body!.Content is null) throw ApiException.BadRequest("validation_failed", "The request is not valid", "content is required");
        if (body.Message is { Length: > 500 }) throw ApiException.BadRequest("validation_failed", "The request is not valid", "message is limited to 500 characters");
        if (await docs.ExistsAsync(access.Project, path, ct))
            throw ApiException.Conflict("document_exists", $"A document already exists at {path}");

        var result = await docs.WriteAsync(caller, access.Project, path, Encoding.UTF8.GetBytes(body.Content), body.Message,
            new WritePrecondition(null, false, IfNoneMatchAny: true), requireIfMatch: false, ct);
        return Created(http, slug, result);
    }

    private static async Task<IResult> GetAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user,
        ProjectAuthorizer authz, DocumentService docs, MarkdownRenderer renderer, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        if (DocRoute.Parse(rest) is not DocRoute.Current route) throw ApiException.NotFound("not_found", "No such route");
        var path = DocumentPath.Require(route.Path);
        var (_, version) = await docs.GetCurrentAsync(access.Project, path, ct);
        return await RespondAsync(http, docs, renderer, path, version, ct);
    }

    private static async Task<IResult> PutAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, DocumentService docs, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        if (DocRoute.Parse(rest) is not DocRoute.Current route) throw ApiException.NotFound("not_found", "No such route");
        var path = DocumentPath.Require(route.Path);
        var pre = Preconditions.Parse(http.Request.Headers);
        var message = MessageFrom(http.Request);
        var bytes = await ReadBodyAsync(http.Request, ct);

        var result = await docs.WriteAsync(caller, access.Project, path, bytes, message, pre, requireIfMatch: true, ct);
        return result.Created ? Created(http, slug, result) : Ok(http, result);
    }

    // --- shared helpers ---

    internal static string? MessageFrom(HttpRequest request)
    {
        string? message = request.Query["message"];
        if (message is { Length: > 500 })
            throw ApiException.BadRequest("validation_failed", "The request is not valid", "message is limited to 500 characters");
        return string.IsNullOrWhiteSpace(message) ? null : message;
    }

    internal static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        var type = request.GetTypedHeaders().ContentType?.MediaType.Value?.ToLowerInvariant();
        if (type is not ("text/markdown" or "text/plain"))
            throw new ApiException(415, "unsupported_media_type", "Send documents as text/markdown");
        if (request.ContentLength > DocumentService.MaxBytes) throw TooLarge();

        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        int n;
        while ((n = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > DocumentService.MaxBytes) throw TooLarge();
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    private static ApiException TooLarge() => ApiException.PayloadTooLarge($"Documents are limited to {DocumentService.MaxBytes} bytes");

    internal static IResult Created(HttpContext http, string slug, WriteResult r)
    {
        http.Response.Headers.ETag = Preconditions.ETag(r.Version.Number);
        return Results.Created($"/api/v1/projects/{slug}/docs/{r.Path}", DocumentDto.From(r.Path, r.Parsed, r.Version));
    }

    internal static IResult Ok(HttpContext http, WriteResult r)
    {
        http.Response.Headers.ETag = Preconditions.ETag(r.Version.Number);
        return Results.Ok(DocumentDto.From(r.Path, r.Parsed, r.Version));
    }

    internal static async Task<IResult> RespondAsync(
        HttpContext http, DocumentService docs, MarkdownRenderer renderer, string path, ContentVersion version, CancellationToken ct)
    {
        var format = ContentNegotiation.Choose(http.Request.GetTypedHeaders().Accept)
            ?? throw new ApiException(406, "not_acceptable", "Not acceptable", "Supported: text/markdown, text/html, application/json");
        http.Response.Headers.ETag = Preconditions.ETag(version.Number);
        http.Response.Headers.Vary = "Accept";
        http.Response.Headers.CacheControl = "private, no-cache";

        var text = DocumentService.StrictUtf8.GetString(await docs.ReadContentAsync(version, ct));
        var parsed = FrontMatter.Parse(text, path);
        return format switch
        {
            DocFormat.Html => Results.Text(renderer.ToHtml(parsed.Body), "text/html; charset=utf-8"),
            DocFormat.Json => Results.Ok(DocumentDto.From(path, parsed, version)),
            _ => Results.Text(text, "text/markdown; charset=utf-8"),
        };
    }
}
```

`Program.cs` edits: add `using Ddm.Api.Documents;`; under services add
```csharp
builder.Services.AddScoped<DocumentService>();
builder.Services.AddSingleton<MarkdownRenderer>();
```
and after `v1.MapTokens();` add `v1.MapDocuments();`.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS. If the storage-outage test returns a thrown exception instead of a 500 response, confirm `UseExceptionHandler()` is registered before routing in `Program.cs`.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat: versioned markdown documents with ETag concurrency and content negotiation"
```

---

### Task 11: Document listing, deletion, version history and restore

**Files:**
- Modify: `src/Ddm.Api/Documents/DocumentDtos.cs`, `DocumentService.cs`, `DocumentEndpoints.cs`
- Test: `tests/Ddm.Api.Tests/DocumentListDeleteTests.cs`, `tests/Ddm.Api.Tests/DocumentVersionTests.cs`

**Interfaces:**
- Consumes: Task 10 `DocumentService`, `DocumentEndpoints` helpers (`Ok`, `RespondAsync`, `MessageFrom`), `DocRoute`.
- Produces: `DocumentSummaryDto(string Path, string Title, int Version, DateTimeOffset UpdatedAt)`; `VersionDto(int Number, string Author, string? Message, DateTimeOffset CreatedAt)`; service methods `RequireDocumentAsync`, `GetVersionAsync`, `ListVersionsAsync`, `RestoreAsync`, `DeleteAsync`; routes `GET /projects/{slug}/docs?prefix=&limit=&cursor=`, `DELETE /docs/{path}`, `GET /docs/{path}/versions`, `GET /docs/{path}/versions/{n}`, `POST /docs/{path}/versions/{n}/restore`.

- [ ] **Step 1: Write the failing tests**

`tests/Ddm.Api.Tests/DocumentListDeleteTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class DocumentListDeleteTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private async Task<HttpClient> SeedAsync(params string[] paths)
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        foreach (var path in paths) await PutDocAsync(alice, "p", path, $"# {path}");
        return alice;
    }

    [Fact]
    public async Task List_returns_paths_titles_and_versions_in_path_order()
    {
        var alice = await SeedAsync("b.md", "a/z.md", "a/y.md");
        var page = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs"));
        Assert.Equal(["a/y.md", "a/z.md", "b.md"], page.Items.Select(d => d.Path));
        Assert.All(page.Items, d => Assert.Equal(1, d.Version));
        Assert.Equal("# a/y.md".TrimStart('#', ' '), page.Items[0].Title);
    }

    [Fact]
    public async Task List_of_an_empty_project_is_empty()
    {
        var alice = await SeedAsync();
        var page = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs"));
        Assert.Empty(page.Items);
        Assert.Null(page.Next);
    }

    [Fact]
    public async Task List_pages_through_every_document_once()
    {
        var alice = await SeedAsync("d1.md", "d2.md", "d3.md", "d4.md", "d5.md");
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var url = "/api/v1/projects/p/docs?limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync(url));
            seen.AddRange(page.Items.Select(d => d.Path));
            cursor = page.Next;
        } while (cursor is not null);
        Assert.Equal(["d1.md", "d2.md", "d3.md", "d4.md", "d5.md"], seen);
    }

    [Fact]
    public async Task Prefix_filters_by_folder_and_treats_underscore_literally()
    {
        var alice = await SeedAsync("a_b/x.md", "axb/y.md", "a_b/sub/z.md");
        var page = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs?prefix=a_b/"));
        Assert.Equal(["a_b/sub/z.md", "a_b/x.md"], page.Items.Select(d => d.Path));
    }

    [Fact]
    public async Task List_only_shows_this_projects_documents()
    {
        var alice = await SeedAsync("mine.md");
        await CreateProjectAsync(alice, "other");
        await PutDocAsync(alice, "other", "theirs.md", "# t");
        var page = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs"));
        Assert.Equal(["mine.md"], page.Items.Select(d => d.Path));
    }

    [Fact]
    public async Task List_hides_a_private_project_from_strangers()
    {
        await SeedAsync("a.md");
        Assert.Equal(HttpStatusCode.NotFound, (await ClientFor("bob").GetAsync("/api/v1/projects/p/docs")).StatusCode);
    }

    [Fact]
    public async Task Delete_removes_the_document_and_its_versions()
    {
        var alice = await SeedAsync("a.md");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/docs/a.md")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "a.md")).StatusCode);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DdmDbContext>();
        Assert.Equal(0, await db.Versions.CountAsync());
        Assert.Contains(await db.AuditEntries.Select(e => e.Action).ToListAsync(), a => a == "doc.delete");
    }

    [Fact]
    public async Task A_deleted_path_can_be_created_again_from_version_1()
    {
        var alice = await SeedAsync("a.md");
        await alice.DeleteAsync("/api/v1/projects/p/docs/a.md");
        var r = await PutDocAsync(alice, "p", "a.md", "# reborn");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v1\"", r.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Delete_of_a_missing_document_is_404()
    {
        var alice = await SeedAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync("/api/v1/projects/p/docs/nope.md")).StatusCode);
    }

    [Fact]
    public async Task Delete_honours_a_stale_if_match()
    {
        var alice = await SeedAsync("a.md");
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/projects/p/docs/a.md");
        request.Headers.TryAddWithoutValidation("If-Match", "\"v9\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await alice.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
    }

    [Fact]
    public async Task Readers_cannot_delete()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "shared", "internal");
        await PutDocAsync(alice, "shared", "a.md", "# x");
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientFor("bob").DeleteAsync("/api/v1/projects/shared/docs/a.md")).StatusCode);
    }
}
```

`tests/Ddm.Api.Tests/DocumentVersionTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Documents;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class DocumentVersionTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private async Task<HttpClient> ThreeVersionsAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await PutDocAsync(alice, "p", "a.md", "# one", message: "first");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"", message: "second");
        await PutDocAsync(alice, "p", "a.md", "# three", ifMatch: "\"v2\"");
        return alice;
    }

    private static Task<HttpResponseMessage> Restore(HttpClient c, int n, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/projects/p/docs/a.md/versions/{n}/restore");
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return c.SendAsync(request);
    }

    [Fact]
    public async Task History_lists_versions_newest_first_with_author_and_message()
    {
        var alice = await ThreeVersionsAsync();
        var page = await ReadAsync<Page<VersionDto>>(await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions"));
        Assert.Equal([3, 2, 1], page.Items.Select(v => v.Number));
        Assert.Equal("second", page.Items[1].Message);
        Assert.Equal("first", page.Items[2].Message);
        Assert.Null(page.Items[0].Message);
        Assert.All(page.Items, v => Assert.Equal("user:alice", v.Author));
    }

    [Fact]
    public async Task History_pages_with_a_cursor()
    {
        var alice = await ThreeVersionsAsync();
        var first = await ReadAsync<Page<VersionDto>>(await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions?limit=2"));
        Assert.Equal([3, 2], first.Items.Select(v => v.Number));
        var second = await ReadAsync<Page<VersionDto>>(
            await alice.GetAsync($"/api/v1/projects/p/docs/a.md/versions?limit=2&cursor={Uri.EscapeDataString(first.Next!)}"));
        Assert.Equal([1], second.Items.Select(v => v.Number));
        Assert.Null(second.Next);
    }

    [Fact]
    public async Task An_old_version_can_be_read_with_its_own_etag()
    {
        var alice = await ThreeVersionsAsync();
        var r = await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions/1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("# one", await r.Content.ReadAsStringAsync());
        Assert.Equal("\"v1\"", r.Headers.ETag!.Tag);
    }

    [Theory] [InlineData("99")] [InlineData("0")] [InlineData("99999999999")]
    public async Task Unknown_versions_are_404(string n)
    {
        var alice = await ThreeVersionsAsync();
        var r = await alice.GetAsync($"/api/v1/projects/p/docs/a.md/versions/{n}");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("version_not_found", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task History_of_a_missing_document_is_404()
    {
        var alice = await ThreeVersionsAsync();
        var r = await alice.GetAsync("/api/v1/projects/p/docs/nope.md/versions");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("document_not_found", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Restore_creates_a_new_version_and_keeps_history_intact()
    {
        var alice = await ThreeVersionsAsync();
        var r = await Restore(alice, 1);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("\"v4\"", r.Headers.ETag!.Tag);

        Assert.Equal("# one", await (await GetDocAsync(alice, "p", "a.md")).Content.ReadAsStringAsync());
        Assert.Equal("# two", await alice.GetStringAsync("/api/v1/projects/p/docs/a.md/versions/2"));
        Assert.Equal("# three", await alice.GetStringAsync("/api/v1/projects/p/docs/a.md/versions/3"));
        var history = await ReadAsync<Page<VersionDto>>(await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions"));
        Assert.Equal([4, 3, 2, 1], history.Items.Select(v => v.Number));
        Assert.Equal("Restore version 1", history.Items[0].Message);
    }

    [Fact]
    public async Task Restoring_content_that_is_already_current_is_a_no_op()
    {
        var alice = await ThreeVersionsAsync();
        await Restore(alice, 1);
        var again = await Restore(alice, 1);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("\"v4\"", again.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Restore_honours_a_stale_if_match()
    {
        var alice = await ThreeVersionsAsync();
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Restore(alice, 1, ifMatch: "\"v1\"")).StatusCode);
        Assert.Equal("# three", await (await GetDocAsync(alice, "p", "a.md")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Restore_of_an_unknown_version_is_404()
    {
        var alice = await ThreeVersionsAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await Restore(alice, 99)).StatusCode);
    }

    [Fact]
    public async Task Readers_can_read_history_but_not_restore()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p", "internal");
        await PutDocAsync(alice, "p", "a.md", "# one");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"");
        var bob = ClientFor("bob");
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/v1/projects/p/docs/a.md/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Restore(bob, 1)).StatusCode);
    }

    [Fact]
    public async Task A_non_numeric_version_is_a_path_error()
    {
        var alice = await ThreeVersionsAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions/x")).StatusCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DocumentListDeleteTests|FullyQualifiedName~DocumentVersionTests"`
Expected: build FAILS (`DocumentSummaryDto`, `VersionDto` not defined).

- [ ] **Step 3: Extend DTOs and service**

Append to `DocumentDtos.cs`:
```csharp
public sealed record DocumentSummaryDto(string Path, string Title, int Version, DateTimeOffset UpdatedAt);
public sealed record VersionDto(int Number, string Author, string? Message, DateTimeOffset CreatedAt);
```

Add these members inside `DocumentService` (also `using Ddm.Api.Documents;` is not needed; keep existing usings):
```csharp
    public async Task<Document> RequireDocumentAsync(Project project, string path, CancellationToken ct) =>
        await db.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.ProjectId == project.Id && d.Path == path, ct)
        ?? throw ApiException.NotFound("document_not_found", "Document not found");

    public async Task<ContentVersion> GetVersionAsync(Project project, string path, int number, CancellationToken ct)
    {
        var doc = await RequireDocumentAsync(project, path, ct);
        return await db.Versions.AsNoTracking().SingleOrDefaultAsync(
                   v => v.ItemType == ItemType.Document && v.ItemId == doc.Id && v.Number == number, ct)
               ?? throw ApiException.NotFound("version_not_found", "Version not found");
    }

    public async Task<Page<VersionDto>> ListVersionsAsync(Project project, string path, int? limit, string? cursor, CancellationToken ct)
    {
        var doc = await RequireDocumentAsync(project, path, ct);
        var take = Paging.ParseLimit(limit);
        var before = Paging.DecodeLongCursor(cursor);

        var query = db.Versions.AsNoTracking().Where(v => v.ItemType == ItemType.Document && v.ItemId == doc.Id);
        if (before is not null) query = query.Where(v => v.Number < before);
        var rows = await query.OrderByDescending(v => v.Number).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, v => v.Number.ToString());
        return new(page.Items.Select(v => new VersionDto(v.Number, v.Author, v.Message, v.CreatedAt)).ToList(), page.Next);
    }

    /// <summary>Restoring writes the old content as a new version; history is never rewritten.</summary>
    public async Task<WriteResult> RestoreAsync(
        Caller caller, Project project, string path, int number, WritePrecondition pre, CancellationToken ct)
    {
        var version = await GetVersionAsync(project, path, number, ct);
        var bytes = await ReadContentAsync(version, ct);
        return await WriteAsync(caller, project, path, bytes, $"Restore version {number}", pre, requireIfMatch: false, ct);
    }

    public async Task DeleteAsync(Caller caller, Project project, string path, WritePrecondition pre, CancellationToken ct)
    {
        var doc = await db.Documents.SingleOrDefaultAsync(d => d.ProjectId == project.Id && d.Path == path, ct)
                  ?? throw ApiException.NotFound("document_not_found", "Document not found");
        if (pre.IfMatchVersion is { } v)
        {
            var current = await db.Versions.AsNoTracking().SingleAsync(x => x.Id == doc.CurrentVersionId, ct);
            if (current.Number != v) throw ApiException.PreconditionFailed($"The document is at version {current.Number}, not {v}");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Versions.Where(x => x.ItemType == ItemType.Document && x.ItemId == doc.Id).ExecuteDeleteAsync(ct);
        db.Documents.Remove(doc);
        db.Audit(caller, project.Id, "doc.delete", path);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
```

- [ ] **Step 4: Extend the endpoints**

In `DocumentEndpoints.MapDocuments`, replace the body with:
```csharp
        var g = v1.MapGroup("/projects/{slug}/docs");
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapGet("{**rest}", GetAsync);
        g.MapPut("{**rest}", PutAsync);
        g.MapDelete("{**rest}", DeleteAsync);
        g.MapPost("{**rest}", RestoreAsync);
```

Replace the existing `GetAsync` method with:
```csharp
    private static async Task<IResult> GetAsync(
        string slug, string rest, string? cursor, int? limit, HttpContext http, ClaimsPrincipal user,
        ProjectAuthorizer authz, DocumentService docs, MarkdownRenderer renderer, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        switch (DocRoute.Parse(rest))
        {
            case DocRoute.Current c:
            {
                var path = DocumentPath.Require(c.Path);
                var (_, version) = await docs.GetCurrentAsync(access.Project, path, ct);
                return await RespondAsync(http, docs, renderer, path, version, ct);
            }
            case DocRoute.History h:
                return Results.Ok(await docs.ListVersionsAsync(access.Project, DocumentPath.Require(h.Path), limit, cursor, ct));
            case DocRoute.Snapshot s:
            {
                var path = DocumentPath.Require(s.Path);
                var version = await docs.GetVersionAsync(access.Project, path, s.Number, ct);
                return await RespondAsync(http, docs, renderer, path, version, ct);
            }
            default:
                throw ApiException.NotFound("not_found", "No such route");
        }
    }
```

Add these methods and the `using Ddm.Api.Data; using Microsoft.EntityFrameworkCore;` lines to the file:
```csharp
    private static async Task<IResult> ListAsync(
        string slug, string? prefix, string? cursor, int? limit, ClaimsPrincipal user,
        ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);
        if (prefix is { Length: > DocumentPath.MaxLength })
            throw ApiException.BadRequest("validation_failed", "The request is not valid", "prefix is too long");

        var docsQuery = db.Documents.Where(d => d.ProjectId == access.Project.Id);
        if (!string.IsNullOrEmpty(prefix)) docsQuery = docsQuery.Where(d => d.Path.StartsWith(prefix));
        if (after is not null) docsQuery = docsQuery.Where(d => string.Compare(d.Path, after) > 0);

        var rows = await (from d in docsQuery
                          join v in db.Versions on d.CurrentVersionId equals (Guid?)v.Id
                          orderby d.Path
                          select new DocumentSummaryDto(d.Path, d.Title, v.Number, d.UpdatedAt))
            .Take(take + 1).ToListAsync(ct);
        return Results.Ok(Paging.ToPage(rows, take, d => d.Path));
    }

    private static async Task<IResult> DeleteAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, DocumentService docs, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        if (DocRoute.Parse(rest) is not DocRoute.Current route) throw ApiException.NotFound("not_found", "No such route");
        await docs.DeleteAsync(caller, access.Project, DocumentPath.Require(route.Path), Preconditions.Parse(http.Request.Headers), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RestoreAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, DocumentService docs, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        if (DocRoute.Parse(rest) is not DocRoute.Restore route) throw ApiException.NotFound("not_found", "No such route");
        var result = await docs.RestoreAsync(caller, access.Project, DocumentPath.Require(route.Path), route.Number,
            Preconditions.Parse(http.Request.Headers), ct);
        return Ok(http, result);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS. If the list keyset test fails to translate `string.Compare`, use `d.Path.CompareTo(after) > 0`.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: document listing, deletion, version history and restore"
```

---

### Task 12: Self-describing OpenAPI document

**Files:**
- Modify: `src/Ddm.Api/Program.cs`
- Test: `tests/Ddm.Api.Tests/OpenApiTests.cs`

**Interfaces:**
- Consumes: all mapped routes.
- Produces: anonymous `GET /api/v1/openapi.json` (the spec's "the DDM API publishes its own OpenAPI spec").

- [ ] **Step 1: Install package**

```bash
dotnet add src/Ddm.Api package Microsoft.AspNetCore.OpenApi
```

- [ ] **Step 2: Write the failing test**

`tests/Ddm.Api.Tests/OpenApiTests.cs`:
```csharp
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class OpenApiTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task The_api_publishes_its_own_openapi_document_anonymously()
    {
        var response = await Anonymous().GetAsync("/api/v1/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("3.", doc.GetProperty("openapi").GetString());
        var paths = doc.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/v1/projects", out _));
        Assert.True(paths.TryGetProperty("/api/v1/projects/{slug}", out _));
        Assert.True(paths.TryGetProperty("/api/v1/me", out _));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~OpenApiTests"`
Expected: FAIL (404).

- [ ] **Step 4: Write the implementation**

`Program.cs`: under services add `builder.Services.AddOpenApi();`; under endpoints, before the `v1` group, add `app.MapOpenApi("/api/v1/openapi.json");`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS. If document generation throws for an endpoint signature, add `.ExcludeFromDescription()` to that route rather than changing its handler.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: publish the API's own OpenAPI document"
```

---

### Task 13: Metrics endpoint and configuration docs

**Files:**
- Modify: `src/Ddm.Api/Program.cs`, `README.md`
- Test: `tests/Ddm.Api.Tests/MetricsTests.cs`

**Interfaces:**
- Consumes: the full pipeline.
- Produces: `GET /metrics` in Prometheus text format (ASP.NET Core request metrics). It is anonymous by design: deploy it on an internal port or behind network policy, never the public ingress.

- [ ] **Step 1: Install packages**

```bash
dotnet add src/Ddm.Api package OpenTelemetry.Extensions.Hosting
dotnet add src/Ddm.Api package OpenTelemetry.Instrumentation.AspNetCore
dotnet add src/Ddm.Api package OpenTelemetry.Exporter.Prometheus.AspNetCore --prerelease
```

- [ ] **Step 2: Write the failing test**

`tests/Ddm.Api.Tests/MetricsTests.cs`:
```csharp
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class MetricsTests(PostgresFixture pg) : ApiTestBase(pg)
{
    [Fact]
    public async Task Metrics_expose_http_request_durations_in_prometheus_format()
    {
        var client = Anonymous();
        await client.GetAsync("/healthz");
        var response = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("http_server_request_duration", await response.Content.ReadAsStringAsync());
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~MetricsTests"`
Expected: FAIL (404).

- [ ] **Step 4: Write the implementation**

`Program.cs`: add `using OpenTelemetry.Metrics;`; under services add
```csharp
builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddAspNetCoreInstrumentation().AddPrometheusExporter());
```
and under endpoints add `app.MapPrometheusScrapingEndpoint(); // /metrics: keep off the public ingress`.

Append to `README.md`:
```markdown

## Configuration

| Key | Purpose |
| --- | --- |
| `ConnectionStrings:Ddm` | PostgreSQL connection string |
| `Database:MigrateOnStart` | `true` applies EF migrations at startup (Development default) |
| `Auth:Authority`, `Auth:Audience` | OIDC provider for user bearer tokens (required in Production) |
| `Auth:DevSigningKey`, `Auth:Issuer` | Symmetric dev/test signing key; the app refuses to start with it in Production |
| `Storage:ServiceUrl`, `AccessKey`, `SecretKey`, `Bucket`, `ForcePathStyle` | S3-compatible object storage |
| `Storage:CreateBucket` | `true` creates the bucket at startup (local MinIO) |

Endpoints: `/healthz` (liveness), `/readyz` (database), `/metrics` (Prometheus, internal only), `/api/v1/openapi.json`.
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS for every test. Then `docker compose up -d && dotnet run --project src/Ddm.Api` and `curl -s localhost:5000/healthz` (use the port the run command prints) to confirm the Development profile boots against Postgres and MinIO.

- [ ] **Step 6: Commit**

```bash
git add src tests README.md
git commit -m "feat: Prometheus metrics endpoint and configuration docs"
```

---

## Self-Review

**Spec coverage**

| Spec item | Task |
| --- | --- |
| F1 projects CRUD, list only visible | 5 |
| F2 markdown documents, path tree, front matter title | 9, 10, 11 |
| F7 version history and restore (diff deferred) | 10, 11 |
| F8 API tokens, scopes, hashed, shown once, revocable (bulk publish deferred) | 7 |
| F9 roles and visibility, one authorization module, 404-not-403 | 5, 6 |
| `If-Match`/412, problem details with `code`, cursor pagination, Bearer auth | 2, 4, 5, 10 |
| Atomic writes, content never lost on failure | 10 (storage-outage test) |
| Sanitized render, strict path/size/encoding checks, YAML hardening | 9, 10 |
| Audit log, admin-readable | 5, 6, 7, 10, 11 |
| Structured logs, health, metrics, self-hosted OpenAPI | 1, 3, 12, 13 |
| F3, F4, F5, F6, F10, web client | Deferred (see Scope) |

**Placeholder scan:** no TBD/TODO steps; every code step has complete code.

**Type consistency:** `Caller`, `ProjectAuthorizer.RequireAsync(caller, slug, Role, ct)`, `Paging.*`, `WritePrecondition`, `DocumentService.WriteAsync(... requireIfMatch ...)`, `DocRoute.Current/History/Snapshot/Restore`, `DocumentDto`, `DocumentSummaryDto`, `VersionDto` are named identically in every task that uses them.

**Review Focus coverage:** (1) Task 10 concurrent PUT test; (2) Task 10 identical re-publish test; (3) Task 9 front-matter tests; (4) Task 6 last-admin tests incl. race; (5) Tasks 5 and 7 existence-leak and token-isolation tests.
