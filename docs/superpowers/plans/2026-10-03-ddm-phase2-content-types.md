# DDM Phase 2: Assets, Specs, Tags and Bulk Publish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the DDM API so a project can hold images and other assets, validated OpenAPI specs and tags, and so CI can mirror a whole docs folder into a project with one atomic publish call.

**Architecture:** Same modular monolith as phase 1 (`src/Ddm.Api`). New modules `Assets/`, `Specs/`, `Tags/` and `Publishing/` sit beside `Documents/`. Each content service splits a write into a pure *prepare* step and a *stage* step that adds entities to the `DbContext` without saving, so single-item endpoints and bulk publish share one write path per type. Deletes become tombstones (`DeletedAt`); Postgres `xmin` row versions stop a write and a delete of the same row from both succeeding. Images in rendered markdown point at HMAC-signed `/content` URLs that browsers can load without a bearer token.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core + Npgsql (PostgreSQL 17), AWSSDK.S3, Markdig, HtmlSanitizer, YamlDotNet, **Microsoft.OpenApi 2.12.0 + Microsoft.OpenApi.YamlReader 2.12.0** (new), `System.Formats.Tar` / `System.IO.Compression` (built in), xUnit v2, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-03-ddm-phase2-content-types-design.md` (and the parent `Developer Documentation Manager Design Document.md`). Read the spec before starting; this plan argues from it.

## Plan decisions that refine the spec

The spec is the source of truth; these are the places where the plan had to choose or deviate, with the reason.

1. **Row versions.** `Document`, `Asset` and `Spec` get a `uint RowVersion` mapped to Postgres `xmin` as an EF concurrency token. Phase 1's delete removed the row, which is what made a racing `PUT` fail; a tombstone keeps the row, so without a row version a `PUT` and a `DELETE` conditional on the same version could both succeed.
2. **No streaming put.** `IBlobStore` gains `OpenReadAsync` (reads stream) but keeps the `byte[]` `PutAsync`: uploads are capped at 10 MB and must be buffered for sniffing and hashing anyway (spec 3.3 listed a streaming put; it would add code with no benefit).
3. **Asset preconditions** get their own `Preconditions.ParseSha` / `ShaPrecondition`, so the strict `"v{n}"` parsing that existing tests pin stays unchanged.
4. **`Spec.OperationCount`** column, so listing does not have to count a jsonb array.
5. **Archive problems:** a corrupt archive is `400 invalid_archive`; hostile entries are reported inside `422 publish_invalid` with entry codes `unsafe_path`, `unsupported_entry`, `duplicate_entry`. Tag writes racing on the same item return `409 tags_conflict`.
6. **`Content:SigningKey`** (≥ 32 chars) is required in every environment; Development and the test factory supply one. **`Content:BaseUrl`** is required outside Development/Testing; when empty, content URLs are relative (`/content/...`, same origin).
7. **Publish memory:** extracted entries are held in memory, bounded by `Publish:MaxExpandedBytes` (250 MB). The upload itself is spooled to a temp file.
8. **Spec detection in archives:** YAML (`.yaml`/`.yml`) is a spec when it has an `openapi:` key at column 0; JSON is a spec when its root object has an `openapi` property.
9. **Tag rows** are created with `INSERT … ON CONFLICT DO NOTHING` as soon as a write stages tags. Outside a publish transaction, a write that then fails can leave an unused tag (count 0) behind; it is harmless and an admin can delete it.
10. **Existing documents** written in phase 1 with `tags:` in their front matter get tag assignments on their next write. There is no production data yet, so there is no backfill.

## Global Constraints

- Every API route lives under `/api/v1` and every content route is nested under `/projects/{slug}`. The only route outside is the anonymous `GET /content/{projectId}/{sha}`.
- Errors are RFC 9457 problem details with a stable `code`. Errors that list several problems carry an `errors` array (`invalid_spec`, `publish_invalid`).
- Pagination is cursor-based: `limit` and `cursor` parameters, `next` in the response (`Page<T>`).
- Documents and specs use version ETags `"v{n}"`; assets use the quoted lowercase hex SHA-256. A stale `If-Match` is `412`; updating an existing item without `If-Match` is `428`; `If-None-Match: *` is create-only.
- A caller who cannot read a project gets `404 project_not_found`, never `403`.
- Deletes are tombstones; versions are kept; writing to a tombstoned path revives the row and continues version numbering; history and restore work on tombstones; listings take `?deleted=true`.
- Tag names match `^[a-z0-9][a-z0-9-]{0,49}$` after normalisation (trim, lowercase, runs of whitespace or `_` → `-`); at most 20 per item; repeated `?tag=` filters combine with AND.
- Size limits: documents 1 MB, assets `Assets:MaxBytes` = 10 MB, specs `Specs:MaxBytes` = 5 MB, publish archive `Publish:MaxArchiveBytes` = 100 MB, `Publish:MaxExpandedBytes` = 250 MB, `Publish:MaxEntries` = 5,000.
- Every asset response sets `X-Content-Type-Options: nosniff` and `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; sandbox`; non-images also get `Content-Disposition: attachment`.
- Specs: OpenAPI 3.0.x or 3.1.x only; every `$ref` must start with `#`; YAML aliases are refused; nesting is capped at 64 levels.
- Every write and permission change records who, what and when in the audit log, in the same transaction as the change.
- `Microsoft.OpenApi` and `Microsoft.OpenApi.YamlReader` are pinned to **2.12.0**: `Microsoft.AspNetCore.OpenApi` 10.0.12 already depends on 2.12.0, and 3.x would break the API's own OpenAPI document.
- `Program.cs` stays thin: one registration line per module under the existing `// --- services` and `// --- endpoints` anchors.
- Tests need a running Docker daemon (Testcontainers). Use xUnit v2 (`xunit` 2.9.x), not `xunit.v3`.
- Work on a feature branch or worktree, not on `speccing`. Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

Failure modes the spec implies that are most likely to bite real users. Each has a pinned test in the owning task.

1. **A publish racing single-item edits** to the same file must end with either the whole publish applied or `409 publish_conflict`, never a 500 and never a lost version (Task 14).
2. **A `PUT` racing a `DELETE`, both conditional on the same version**, must never both succeed now that delete keeps the row; the phase 1 race tests must still pass (Tasks 2 and 3).
3. **A real `tar czf docs.tgz .` from macOS** (`./` prefixes, `._*` AppleDouble files, `.DS_Store`, an empty file) must publish cleanly, with hidden files reported as ignored (Task 12).
4. **Tags across deletes and front matter edits:** a revived document keeps its tags; dropping the `tags:` key keeps them; `tags: []` clears them (Task 4).
5. **Images referenced with `../` from nested folders** must resolve to the right asset and load through the signed link without credentials; a replaced image must get a new URL (Task 9).

## File Structure

```
src/Ddm.Api/
  Common/      + SafeYaml (alias/depth guard), ContentPath (shared path rules, folder prefixes, relative resolution),
                 RequestBody (bounded body reads, per-request size limit)
  Domain/      + Asset, Spec, Tag, TagAssignment, ItemRef; Document gains DeletedAt + RowVersion;
                 ContentVersion gains NormalizedRef; ItemType gains Asset, Project
  Data/        DdmDbContext (+ new sets and mappings), Migrations/<timestamp>_Phase2Content
  Documents/   DocumentService (prepare/stage split, tombstones), DocumentEndpoints (tags route, filters),
               DocRoute (+ /tags), Preconditions (+ ShaPrecondition), MarkdownRenderer (+ image resolution), FrontMatter
  Tags/        TagName (normalise, front matter), TagService (stage sets, lookups, AND filter), TagEndpoints, TagDtos
  Projects/    ProjectDtos (+ tags), ProjectEndpoints (+ tags on PATCH, ?tag= filter, spec versions on delete)
  Assets/      AssetTypes (allow-list + sniffing), AssetPath, AssetRoute, AssetService, AssetEndpoints, AssetDtos,
               AssetResponses (safe serving), AssetOptions, AssetSetup, ContentOptions, ContentUrlSigner,
               ContentEndpoints (/content), ContentSetup, AssetResolver
  Specs/       SpecName, PointerLocator, SpecValidator, SpecService, SpecEndpoints, SpecDtos, SpecOptions, SpecSetup
  Publishing/  ArchiveReader, PublishPlanner, PublishService, PublishEndpoints, PublishDtos, PublishOptions, PublishSetup
  Storage/     IBlobStore (+ OpenReadAsync), S3BlobStore
tests/Ddm.Api.Tests/
  Infrastructure/ InMemoryBlobStore (+ OpenReadAsync), DdmApiFactory (+ content key, overrides), ApiTestBase (+ helpers), Archives
  (one test file per feature, named in each task)
```

---

### Task 1: Shared foundations

**Files:**
- Create: `src/Ddm.Api/Common/SafeYaml.cs`, `src/Ddm.Api/Common/ContentPath.cs`
- Modify: `src/Ddm.Api/Documents/FrontMatter.cs`, `src/Ddm.Api/Documents/DocumentPath.cs`, `src/Ddm.Api/Documents/Preconditions.cs`, `src/Ddm.Api/Common/ApiException.cs`, `src/Ddm.Api/Common/ApiExceptionHandler.cs`, `src/Ddm.Api/Common/ProblemCodes.cs`
- Test: `tests/Ddm.Api.Tests/SafeYamlTests.cs`, `tests/Ddm.Api.Tests/ContentPathTests.cs`, `tests/Ddm.Api.Tests/PreconditionsTests.cs`, `tests/Ddm.Api.Tests/ApiExceptionHandlerTests.cs`

**Interfaces:**
- Consumes: phase 1 `ApiException`, `ApiExceptionHandler`, `FrontMatter`, `DocumentPath`, `Preconditions`.
- Produces:
  - `record YamlProblem(string Message, int? Line, int? Column)`; `SafeYaml.Check(string yaml, int maxDepth) → YamlProblem?` (line and column are 1-based).
  - `ContentPath.MaxLength = 255`; `ContentPath.ValidateSegments(string? path) → string?` (error or null); `ContentPath.ValidateFolder(string? prefix) → string?` (empty is valid; otherwise segments ending `/`).
  - `record struct ShaPrecondition(string? IfMatchSha, bool IfMatchAny, bool IfNoneMatchAny)` with `HasIfMatch`; `Preconditions.ParseSha(IHeaderDictionary) → ShaPrecondition`; `Preconditions.ShaETag(string sha256) → string`.
  - `ApiException.Extensions` (`IReadOnlyDictionary<string, object?>?`, init-only), written into the problem body; `ApiException.Unprocessable(string code, string title, IReadOnlyList<object> errors, string? detail = null)` → 422 with an `errors` member.
  - `ProblemCodes.ForStatus(422) == "unprocessable_content"`.

- [ ] **Step 1: Write the failing tests**

`tests/Ddm.Api.Tests/SafeYamlTests.cs`:
```csharp
using Ddm.Api.Common;

namespace Ddm.Api.Tests;

public class SafeYamlTests
{
    [Fact] public void Plain_yaml_passes() => Assert.Null(SafeYaml.Check("a: 1\nb: [x, y]\n", 10));

    [Fact] public void Json_is_yaml_too() => Assert.Null(SafeYaml.Check("{\"a\": [1, {\"b\": 2}]}", 10));

    [Fact]
    public void Aliases_are_rejected_with_their_line()
    {
        var p = SafeYaml.Check("a: &x [1, 2]\nb: *x\n", 10);
        Assert.NotNull(p);
        Assert.Contains("aliases", p!.Message);
        Assert.Equal(2, p.Line);
    }

    [Fact]
    public void Nesting_beyond_the_limit_is_rejected()
    {
        var p = SafeYaml.Check("a: {b: {c: {d: 1}}}\n", 3);
        Assert.NotNull(p);
        Assert.Contains("nested deeper than 3", p!.Message);
    }

    [Fact]
    public void Syntax_errors_report_a_line()
    {
        var p = SafeYaml.Check("a: 1\nb: [1,\n", 10);
        Assert.NotNull(p);
        Assert.NotNull(p!.Line);
    }
}
```

`tests/Ddm.Api.Tests/ContentPathTests.cs`:
```csharp
using Ddm.Api.Common;

namespace Ddm.Api.Tests;

public class ContentPathTests
{
    [Theory] [InlineData("a.png")] [InlineData("images/a.png")] [InlineData("_x/y-z.v2.txt")]
    public void Valid_paths(string path) => Assert.Null(ContentPath.ValidateSegments(path));

    [Theory]
    [InlineData("")] [InlineData("/a.png")] [InlineData("a//b.png")] [InlineData(".git/config")]
    [InlineData("a/../b.png")] [InlineData("a b.png")]
    public void Invalid_paths(string path) => Assert.NotNull(ContentPath.ValidateSegments(path));

    [Theory] [InlineData(null)] [InlineData("")] [InlineData("guides/")] [InlineData("guides/v2/")]
    public void Valid_folders(string? prefix) => Assert.Null(ContentPath.ValidateFolder(prefix));

    [Theory] [InlineData("guides")] [InlineData("/")] [InlineData("../")] [InlineData("a//")]
    public void Invalid_folders(string prefix) => Assert.NotNull(ContentPath.ValidateFolder(prefix));
}
```

Append to `tests/Ddm.Api.Tests/PreconditionsTests.cs` (inside the class):
```csharp
    private static readonly string Sha = new('a', 64);

    private static ShaPrecondition ParseSha(string? ifMatch = null, string? ifNoneMatch = null)
    {
        IHeaderDictionary h = new HeaderDictionary();
        if (ifMatch is not null) h.IfMatch = ifMatch;
        if (ifNoneMatch is not null) h.IfNoneMatch = ifNoneMatch;
        return Preconditions.ParseSha(h);
    }

    [Fact] public void Sha_etag() => Assert.Equal(Sha, ParseSha($"\"{Sha}\"").IfMatchSha);
    [Fact] public void Sha_star_matches_any() => Assert.True(ParseSha("*").IfMatchAny);
    [Fact] public void Sha_if_none_match_star() => Assert.True(ParseSha(ifNoneMatch: "*").IfNoneMatchAny);
    [Fact] public void Sha_etag_formatting() => Assert.Equal($"\"{Sha}\"", Preconditions.ShaETag(Sha));

    [Theory] [InlineData("\"v7\"")] [InlineData("\"abc\"")] [InlineData("abc")]
    public void A_version_or_short_etag_is_not_a_sha_etag(string value) =>
        Assert.Equal("invalid_etag", Assert.Throws<ApiException>(() => ParseSha(value)).Code);

    [Fact]
    public void Uppercase_sha_etags_are_rejected() =>
        Assert.Equal("invalid_etag", Assert.Throws<ApiException>(() => ParseSha($"\"{new string('A', 64)}\"")).Code);
```

Append to `tests/Ddm.Api.Tests/ApiExceptionHandlerTests.cs` (inside the class), and add `[InlineData(422, "unprocessable_content")]` to the `ForStatus_maps_statuses_to_stable_codes` theory:
```csharp
    [Fact]
    public async Task Extensions_such_as_an_errors_list_are_written()
    {
        var r = await RunAsync(ApiException.Unprocessable("invalid_spec", "Invalid", [new { pointer = "/a", line = 3 }]));
        Assert.Equal(422, r.Status);
        Assert.Equal("invalid_spec", r.Body.GetProperty("code").GetString());
        var e = r.Body.GetProperty("errors")[0];
        Assert.Equal("/a", e.GetProperty("pointer").GetString());
        Assert.Equal(3, e.GetProperty("line").GetInt32());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SafeYamlTests|FullyQualifiedName~ContentPathTests|FullyQualifiedName~PreconditionsTests|FullyQualifiedName~ApiExceptionHandlerTests"`
Expected: build FAILS (`SafeYaml`, `ContentPath`, `ShaPrecondition`, `ApiException.Unprocessable` do not exist).

- [ ] **Step 3: Implement**

`src/Ddm.Api/Common/SafeYaml.cs`:
```csharp
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Ddm.Api.Common;

public sealed record YamlProblem(string Message, int? Line, int? Column);

/// <summary>
/// Checks YAML (and JSON, which YAML parses) before anything materialises it: aliases enable billion-laughs
/// expansion and deep nesting makes recursive consumers overflow, so both are refused up front.
/// </summary>
public static class SafeYaml
{
    public static YamlProblem? Check(string yaml, int maxDepth)
    {
        try
        {
            var parser = new Parser(new StringReader(yaml));
            var depth = 0;
            while (parser.MoveNext())
            {
                var e = parser.Current!;
                switch (e)
                {
                    case AnchorAlias:
                        return At(e.Start, "YAML aliases are not supported");
                    case SequenceStart or MappingStart when ++depth > maxDepth:
                        return At(e.Start, $"YAML is nested deeper than {maxDepth} levels");
                    case SequenceEnd or MappingEnd:
                        depth--;
                        break;
                }
            }
            return null;
        }
        catch (YamlException ex) { return At(ex.Start, ex.Message); }
    }

    private static YamlProblem At(Mark mark, string message) =>
        mark.Line > 0 ? new(message, (int)mark.Line, (int)mark.Column) : new(message, null, null);
}
```

In `src/Ddm.Api/Documents/FrontMatter.cs`, replace the scan inside `ToJson`:
```csharp
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

            var value =
```
with:
```csharp
        // Reject aliases (billion-laughs) and deep nesting before anything recurses over the document.
        if (SafeYaml.Check(yaml, MaxDepth) is { } problem) throw Invalid(problem.Message);
        try
        {
            var value =
```
and delete the now-unused `using YamlDotNet.Core.Events;` (keep `using YamlDotNet.Core;` for `YamlException`).

`src/Ddm.Api/Common/ContentPath.cs`:
```csharp
using System.Text.RegularExpressions;

namespace Ddm.Api.Common;

/// <summary>Path rules shared by documents and assets: '/'-separated segments, no dot-files, bounded size.</summary>
public static partial class ContentPath
{
    public const int MaxLength = 255;
    public const int MaxSegments = 10;
    public const int MaxSegmentLength = 100;

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9._-]*\z")]
    private static partial Regex SegmentRegex();

    /// <summary>Returns an error message, or null when the path and every segment are valid.</summary>
    public static string? ValidateSegments(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "Path is required";
        if (path.Length > MaxLength) return $"Path is limited to {MaxLength} characters";
        var segments = path.Split('/');
        if (segments.Length > MaxSegments) return $"Path is limited to {MaxSegments} segments";
        foreach (var s in segments)
            if (s.Length > MaxSegmentLength || !SegmentRegex().IsMatch(s))
                return $"Invalid path segment '{s}': use letters, digits, '.', '_' and '-', starting with a letter, digit or '_'";
        return null;
    }

    /// <summary>A folder prefix is empty (the project root) or valid segments followed by '/'. Returns an error or null.</summary>
    public static string? ValidateFolder(string? prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return null;
        if (!prefix.EndsWith('/')) return "A folder prefix must end with '/'";
        return ValidateSegments(prefix[..^1]);
    }
}
```

Replace the body of `src/Ddm.Api/Documents/DocumentPath.cs` with:
```csharp
using Ddm.Api.Common;

namespace Ddm.Api.Documents;

public static class DocumentPath
{
    public const int MaxLength = ContentPath.MaxLength;

    /// <summary>Returns an error message, or null when the path is valid.</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "Path is required";
        if (!path.EndsWith(".md", StringComparison.Ordinal)) return "Path must end with .md";
        return ContentPath.ValidateSegments(path);
    }

    public static string Require(string? path) =>
        Validate(path) is { } error
            ? throw ApiException.BadRequest("invalid_path", "Invalid document path", error)
            : path!;
}
```
(If `DocumentPathTests` references `DocumentPath.MaxSegments` or `MaxSegmentLength`, add `public const int MaxSegments = ContentPath.MaxSegments;` and `public const int MaxSegmentLength = ContentPath.MaxSegmentLength;`.)

Replace `src/Ddm.Api/Documents/Preconditions.cs` with:
```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using Ddm.Api.Common;

namespace Ddm.Api.Documents;

public readonly record struct WritePrecondition(int? IfMatchVersion, bool IfMatchAny, bool IfNoneMatchAny)
{
    public bool HasIfMatch => IfMatchVersion is not null || IfMatchAny;
}

/// <summary>Preconditions for content addressed by checksum (assets): the ETag is the quoted lowercase SHA-256.</summary>
public readonly record struct ShaPrecondition(string? IfMatchSha, bool IfMatchAny, bool IfNoneMatchAny)
{
    public bool HasIfMatch => IfMatchSha is not null || IfMatchAny;
}

public static partial class Preconditions
{
    public static string ETag(int version) => $"\"v{version}\"";
    public static string ShaETag(string sha256) => $"\"{sha256}\"";

    [GeneratedRegex("^\"[0-9a-f]{64}\"\\z")]
    private static partial Regex ShaETagRegex();

    public static WritePrecondition Parse(IHeaderDictionary headers)
    {
        var (ifMatch, noneAny) = Read(headers);
        if (ifMatch is null) return new(null, false, noneAny);
        if (ifMatch == "*") return new(null, true, noneAny);
        if (TryParseVersion(ifMatch, out var n)) return new(n, false, noneAny);
        throw Invalid("If-Match must be a quoted version ETag such as \"v7\", or *");
    }

    public static ShaPrecondition ParseSha(IHeaderDictionary headers)
    {
        var (ifMatch, noneAny) = Read(headers);
        if (ifMatch is null) return new(null, false, noneAny);
        if (ifMatch == "*") return new(null, true, noneAny);
        if (ShaETagRegex().IsMatch(ifMatch)) return new(ifMatch[1..^1], false, noneAny);
        throw Invalid("If-Match must be the asset's quoted SHA-256 ETag, or *");
    }

    private static (string? IfMatch, bool IfNoneMatchAny) Read(IHeaderDictionary headers)
    {
        var ifMatch = headers.IfMatch;
        if (ifMatch.Count > 1) throw Invalid("Send a single If-Match value");
        var ifNoneMatch = headers.IfNoneMatch;
        var noneAny = ifNoneMatch.Count == 1 && ifNoneMatch[0]!.Trim() == "*";
        if (ifNoneMatch.Count > 0 && !noneAny) throw Invalid("If-None-Match only supports *");
        return (ifMatch.Count == 1 ? ifMatch[0]!.Trim() : null, noneAny);
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

In `src/Ddm.Api/Common/ApiException.cs`, add inside the class:
```csharp
    /// <summary>Extra problem-details members, such as an <c>errors</c> list.</summary>
    public IReadOnlyDictionary<string, object?>? Extensions { get; init; }

    /// <summary>422 with every problem listed under <c>errors</c>, so a client can fix them all at once.</summary>
    public static ApiException Unprocessable(string code, string title, IReadOnlyList<object> errors, string? detail = null) =>
        new(422, code, title, detail) { Extensions = new Dictionary<string, object?> { ["errors"] = errors } };
```

In `src/Ddm.Api/Common/ApiExceptionHandler.cs`, pass the extensions through:
```csharp
    public ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct) => ex switch
    {
        ApiException api => WriteAsync(ctx, ex, api.Status, api.Code, api.Title, api.Detail, api.Extensions),
        BadHttpRequestException bad => WriteAsync(ctx, ex, bad.StatusCode, "bad_request", "The request could not be read", bad.Message, null),
        _ => ValueTask.FromResult(false),
    };

    private async ValueTask<bool> WriteAsync(HttpContext ctx, Exception ex, int status, string code, string title, string? detail,
        IReadOnlyDictionary<string, object?>? extensions)
    {
        ctx.Response.StatusCode = status;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = $"urn:ddm:problem:{code}",
            Extensions = { ["code"] = code },
        };
        if (extensions is not null)
            foreach (var (key, value) in extensions) problem.Extensions[key] = value;
        if (await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = ctx, Exception = ex, ProblemDetails = problem }))
            return true;

        // The default writer honours Accept and declines (e.g. Accept: application/xml), which would send an
        // empty body. Errors are always problem+json with a stable code, so write it regardless.
        await Results.Problem(problem).ExecuteAsync(ctx);
        return true;
    }
```

In `src/Ddm.Api/Common/ProblemCodes.cs`, add `422 => "unprocessable_content",` after the `415` arm.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SafeYamlTests|FullyQualifiedName~ContentPathTests|FullyQualifiedName~PreconditionsTests|FullyQualifiedName~ApiExceptionHandlerTests|FullyQualifiedName~FrontMatterTests|FullyQualifiedName~DocumentPathTests"`
Expected: PASS (front matter and document path tests prove the refactors kept behaviour).

- [ ] **Step 5: Commit**

```bash
git add src/Ddm.Api/Common src/Ddm.Api/Documents tests/Ddm.Api.Tests
git commit -m "feat: shared YAML guard, content paths, SHA preconditions and problem extensions" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Phase 2 schema

**Files:**
- Create: `src/Ddm.Api/Domain/Asset.cs`, `src/Ddm.Api/Domain/Spec.cs`, `src/Ddm.Api/Domain/Tag.cs`, `src/Ddm.Api/Domain/TagAssignment.cs`, `src/Ddm.Api/Domain/ItemRef.cs`, `src/Ddm.Api/Data/Migrations/<timestamp>_Phase2Content.cs` (generated)
- Modify: `src/Ddm.Api/Domain/Enums.cs`, `src/Ddm.Api/Domain/Document.cs`, `src/Ddm.Api/Domain/ContentVersion.cs`, `src/Ddm.Api/Data/DdmDbContext.cs`, `src/Ddm.Api/Projects/ProjectEndpoints.cs`, `src/Ddm.Api/Documents/DocumentService.cs` (one query)
- Test: `tests/Ddm.Api.Tests/DataLayerTests.cs`

**Interfaces:**
- Consumes: phase 1 domain and `DdmDbContext`.
- Produces:
  - `enum ItemType { Document = 1, Spec = 2, Asset = 3, Project = 4 }`; `readonly record struct ItemRef(ItemType Type, Guid Id)`.
  - `Document.DeletedAt` (`DateTimeOffset?`), `Document.RowVersion` (`uint`, maps to `xmin`).
  - `Asset { Guid Id; Guid ProjectId; string Path; string ContentType; long Size; string Sha256; string StorageKey; DateTimeOffset UpdatedAt; string UpdatedBy; DateTimeOffset? DeletedAt; uint RowVersion }`.
  - `Spec { Guid Id; Guid ProjectId; string Name; string Title; string ApiVersion; string OpenApiVersion; string Format; string Operations (jsonb); int OperationCount; Guid? CurrentVersionId; DateTimeOffset UpdatedAt; DateTimeOffset? DeletedAt; uint RowVersion }`.
  - `Tag { Guid Id; Guid ProjectId; string Name }`, table `Tags`, unique `(ProjectId, Name)`; `TagAssignment { Guid TagId; ItemType ItemType; Guid ItemId }`.
  - `ContentVersion.NormalizedRef` (`string?`).
  - `DdmDbContext.Assets`, `.Specs`, `.Tags`, `.TagAssignments`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Ddm.Api.Tests/DataLayerTests.cs` (inside the class):
```csharp
    private static Asset NewAsset(Guid projectId, string path) => new()
    {
        ProjectId = projectId, Path = path, ContentType = "image/png", Size = 1, Sha256 = new string('a', 64),
        StorageKey = "k", UpdatedBy = "u",
    };

    [Fact]
    public async Task Asset_path_spec_name_and_tag_name_are_unique_per_project()
    {
        using var db = NewDb();
        var p = new Project { Slug = "p", Name = "P" };
        db.Projects.Add(p);
        db.Assets.Add(NewAsset(p.Id, "a.png"));
        db.Specs.Add(new Spec { ProjectId = p.Id, Name = "api" });
        db.Tags.Add(new Tag { ProjectId = p.Id, Name = "guide" });
        await db.SaveChangesAsync();

        foreach (var add in new Action[]
                 {
                     () => db.Assets.Add(NewAsset(p.Id, "a.png")),
                     () => db.Specs.Add(new Spec { ProjectId = p.Id, Name = "api" }),
                     () => db.Tags.Add(new Tag { ProjectId = p.Id, Name = "guide" }),
                 })
        {
            db.ChangeTracker.Clear();
            add();
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(ex.IsUniqueViolation());
        }
    }

    [Fact]
    public async Task Deleting_a_project_cascades_to_assets_specs_tags_and_assignments()
    {
        using var db = NewDb();
        var p = new Project { Slug = "p", Name = "P" };
        var tag = new Tag { ProjectId = p.Id, Name = "guide" };
        db.Projects.Add(p);
        db.Assets.Add(NewAsset(p.Id, "a.png"));
        db.Specs.Add(new Spec { ProjectId = p.Id, Name = "api" });
        db.Tags.Add(tag);
        db.TagAssignments.Add(new TagAssignment { TagId = tag.Id, ItemType = ItemType.Project, ItemId = p.Id });
        await db.SaveChangesAsync();

        db.Projects.Remove(p);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.Assets.CountAsync());
        Assert.Equal(0, await db.Specs.CountAsync());
        Assert.Equal(0, await db.Tags.CountAsync());
        Assert.Equal(0, await db.TagAssignments.CountAsync());
    }

    [Fact]
    public async Task A_stale_document_row_cannot_be_saved()
    {
        using var db1 = NewDb();
        using var db2 = NewDb();
        var p = new Project { Slug = "p", Name = "P" };
        var doc = new Document { ProjectId = p.Id, Path = "a.md" };
        db1.Projects.Add(p);
        db1.Documents.Add(doc);
        await db1.SaveChangesAsync();

        var stale = await db2.Documents.SingleAsync();
        doc.Title = "first";
        await db1.SaveChangesAsync();
        stale.Title = "second";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DataLayerTests"`
Expected: build FAILS (`Asset`, `Spec`, `Tag`, `TagAssignment`, `db.Assets` etc. do not exist).

- [ ] **Step 3: Implement the domain changes**

`src/Ddm.Api/Domain/Enums.cs`: change `ItemType` to
```csharp
public enum ItemType { Document = 1, Spec = 2, Asset = 3, Project = 4 }
```
(`ItemType` is stored as a string, so existing rows are unaffected.)

`src/Ddm.Api/Domain/ItemRef.cs`:
```csharp
namespace Ddm.Api.Domain;

/// <summary>Any taggable thing: a document, asset, spec or project.</summary>
public readonly record struct ItemRef(ItemType Type, Guid Id);
```

`src/Ddm.Api/Domain/Document.cs`: add
```csharp
    /// <summary>Set when the document is deleted. The row and its versions stay so it can be restored.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>Postgres xmin: a concurrency token, so a write and a delete of the same row cannot both win.</summary>
    public uint RowVersion { get; set; }
```

`src/Ddm.Api/Domain/ContentVersion.cs`: add
```csharp
    /// <summary>Specs only: the normalised JSON form of this version.</summary>
    public string? NormalizedRef { get; set; }
```
and change the summary to `Immutable snapshot of a Document or Spec. Table name: versions.`

`src/Ddm.Api/Domain/Asset.cs`:
```csharp
namespace Ddm.Api.Domain;

/// <summary>An image or other binary. Not versioned: replacing it points the row at new content-addressed bytes.</summary>
public class Asset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public required string Path { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required string Sha256 { get; set; }
    public required string StorageKey { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public required string UpdatedBy { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public uint RowVersion { get; set; }
}
```

`src/Ddm.Api/Domain/Spec.cs`:
```csharp
namespace Ddm.Api.Domain;

/// <summary>An OpenAPI definition. Its bodies live in versions; this row holds the parsed summary of the current one.</summary>
public class Spec
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public string Title { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string OpenApiVersion { get; set; } = "";
    /// <summary>"yaml" or "json": the format of the original upload.</summary>
    public string Format { get; set; } = "yaml";
    /// <summary>JSON array of {method, path, operationId, summary, tags} (jsonb).</summary>
    public string Operations { get; set; } = "[]";
    public int OperationCount { get; set; }
    public Guid? CurrentVersionId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeletedAt { get; set; }
    public uint RowVersion { get; set; }
}
```

`src/Ddm.Api/Domain/Tag.cs`:
```csharp
namespace Ddm.Api.Domain;

/// <summary>A project-scoped label, created on first use.</summary>
public class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
}
```

`src/Ddm.Api/Domain/TagAssignment.cs`:
```csharp
namespace Ddm.Api.Domain;

/// <summary>Attaches a tag to any item. Polymorphic, so there is no foreign key to the item itself.</summary>
public class TagAssignment
{
    public Guid TagId { get; set; }
    public ItemType ItemType { get; set; }
    public Guid ItemId { get; set; }
}
```

- [ ] **Step 4: Map the new entities**

In `src/Ddm.Api/Data/DdmDbContext.cs`, add the sets:
```csharp
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<Spec> Specs => Set<Spec>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TagAssignment> TagAssignments => Set<TagAssignment>();
```
add `e.Property(x => x.RowVersion).IsRowVersion();` as the last line of the `Document` mapping, add `e.Property(x => x.NormalizedRef).HasMaxLength(512);` to the `ContentVersion` mapping, and add before the `ApiToken` mapping:
```csharp
        b.Entity<Asset>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Path).HasMaxLength(255).UseCollation("C");
            e.HasIndex(x => new { x.ProjectId, x.Path }).IsUnique();
            e.HasIndex(x => new { x.ProjectId, x.Sha256 });
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.Sha256).HasMaxLength(64);
            e.Property(x => x.StorageKey).HasMaxLength(512);
            e.Property(x => x.UpdatedBy).HasMaxLength(300);
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        b.Entity<Spec>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(100).UseCollation("C");
            e.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.ApiVersion).HasMaxLength(100);
            e.Property(x => x.OpenApiVersion).HasMaxLength(16);
            e.Property(x => x.Format).HasMaxLength(8);
            e.Property(x => x.Operations).HasColumnType("jsonb");
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        b.Entity<Tag>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(50).UseCollation("C");
            e.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
        });

        b.Entity<TagAssignment>(e =>
        {
            e.HasKey(x => new { x.TagId, x.ItemType, x.ItemId });
            e.HasOne<Tag>().WithMany().HasForeignKey(x => x.TagId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.ItemType).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.ItemType, x.ItemId });
        });
```

`uint` + `IsRowVersion()` is Npgsql's mapping to the `xmin` system column. If the generated migration contains an `AddColumn` for `xmin`, leave it: Npgsql skips system columns when it generates SQL.

Because `Document` now maps `xmin`, every raw query that materialises a `Document` must select it: system columns are not part of `SELECT *`. In `src/Ddm.Api/Documents/DocumentService.cs` `DeleteAsync`, change the query text to:
```csharp
                      .FromSqlInterpolated($"SELECT *, xmin FROM \"Documents\" WHERE \"ProjectId\" = {project.Id} AND \"Path\" = {path} FOR UPDATE")
```
(Task 3 rewrites this method; this keeps the suite green in between.)

In `src/Ddm.Api/Projects/ProjectEndpoints.cs` `DeleteAsync`, replace the version delete so spec versions go too (versions have no foreign key):
```csharp
        // Versions have no FK (they are polymorphic), so remove them explicitly.
        await db.Versions
            .Where(v => (v.ItemType == ItemType.Document && db.Documents.Any(d => d.Id == v.ItemId && d.ProjectId == project.Id))
                        || (v.ItemType == ItemType.Spec && db.Specs.Any(s => s.Id == v.ItemId && s.ProjectId == project.Id)))
            .ExecuteDeleteAsync(ct);
        db.Projects.Remove(project); // cascades to members, documents, assets, specs, tags (and their assignments), tokens
```

- [ ] **Step 5: Generate the migration**

Run:
```bash
dotnet tool restore
dotnet ef migrations add Phase2Content --project src/Ddm.Api --output-dir Data/Migrations
```
Expected: `Data/Migrations/<timestamp>_Phase2Content.cs` creating `Assets`, `Specs`, `Tags`, `TagAssignments`, adding `Documents.DeletedAt` and `versions.NormalizedRef`, with the indexes above. Read it and confirm there are no other changes.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DataLayerTests|FullyQualifiedName~ProjectTests|FullyQualifiedName~DocumentListDeleteTests"`
Expected: PASS. The existing delete race tests still pass (delete still removes the row until Task 3).

- [ ] **Step 7: Commit**

```bash
git add src/Ddm.Api/Domain src/Ddm.Api/Data src/Ddm.Api/Projects tests/Ddm.Api.Tests/DataLayerTests.cs
git commit -m "feat: phase 2 schema for assets, specs, tags and tombstones" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Document tombstones and the prepare/stage split

**Files:**
- Modify: `src/Ddm.Api/Documents/DocumentService.cs` (rewrite), `src/Ddm.Api/Documents/DocumentEndpoints.cs`
- Test: `tests/Ddm.Api.Tests/DocumentTombstoneTests.cs` (new), `tests/Ddm.Api.Tests/DocumentListDeleteTests.cs` (two tests change meaning)

**Interfaces:**
- Consumes: `Document.DeletedAt`, `Document.RowVersion` (Task 2).
- Produces (in `Ddm.Api.Documents`):
  - `record WriteResult(Guid DocumentId, string Path, ParsedMarkdown Parsed, ContentVersion Version, bool Created, bool Changed)`.
  - `record PreparedDocument(string Path, byte[] Bytes, string Sha256, ParsedMarkdown Parsed)` with `string BlobKey(Guid projectId)`.
  - `record DocumentState(Document Doc, ContentVersion? Current)` with `bool IsLive`.
  - `DocumentService`:
    - `static PreparedDocument Prepare(string path, byte[] bytes)`: validates path, size, UTF-8 and front matter; pure.
    - `Task UploadAsync(Project, PreparedDocument, CancellationToken)`.
    - `Task<WriteResult> WriteAsync(Caller, Project, PreparedDocument, string? message, WritePrecondition, bool requireIfMatch, CancellationToken)`.
    - `Task<WriteResult> StageAsync(Caller, Project, PreparedDocument, DocumentState? state, string? message, CancellationToken)`: creates, updates or revives; does not save or upload.
    - `void StageDelete(Caller, Project, Document)`: tombstones; does not save.
    - `Task<DocumentState?> FindAsync(Project, string path, CancellationToken)`: includes tombstones, tracked.
    - `Task<Document> RequireDocumentAsync(Project, string path, bool includeDeleted, CancellationToken)`.
    - `GetCurrentAsync`, `ExistsAsync` and `ReadContentAsync` keep their signatures and now ignore tombstones; `GetVersionAsync`, `ListVersionsAsync` and `RestoreAsync` work on tombstones; `DeleteAsync` tombstones.
  - `GET /projects/{slug}/docs?deleted=true` lists tombstones only.

- [ ] **Step 1: Write the failing tests**

In `tests/Ddm.Api.Tests/DocumentListDeleteTests.cs`, replace `Delete_removes_the_document_and_its_versions` and `A_deleted_path_can_be_created_again_from_version_1` with:
```csharp
    [Fact]
    public async Task Delete_tombstones_the_document_and_keeps_its_versions()
    {
        var alice = await SeedAsync("a.md");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/docs/a.md")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "a.md")).StatusCode);

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DdmDbContext>();
        Assert.Equal(2, await db.Versions.CountAsync());
        Assert.NotNull((await db.Documents.SingleAsync()).DeletedAt);
        Assert.Contains(await db.AuditEntries.Select(e => e.Action).ToListAsync(), a => a == "doc.delete");
    }

    [Fact]
    public async Task Recreating_a_deleted_path_continues_its_version_numbers()
    {
        var alice = await SeedAsync("a.md");
        await alice.DeleteAsync("/api/v1/projects/p/docs/a.md");
        var r = await PutDocAsync(alice, "p", "a.md", "# reborn");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v2\"", r.Headers.ETag!.Tag);
    }
```

`tests/Ddm.Api.Tests/DocumentTombstoneTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Documents;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class DocumentTombstoneTests(PostgresFixture pg) : ApiTestBase(pg)
{
    /// <summary>a.md at v2, then deleted.</summary>
    private async Task<HttpClient> DeletedAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await PutDocAsync(alice, "p", "a.md", "# one");
        await PutDocAsync(alice, "p", "a.md", "# two", ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/docs/a.md")).StatusCode);
        return alice;
    }

    [Fact]
    public async Task Tombstones_are_hidden_from_the_list_but_shown_with_deleted_true()
    {
        var alice = await DeletedAsync();
        await PutDocAsync(alice, "p", "b.md", "# b");
        var live = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs"));
        Assert.Equal(["b.md"], live.Items.Select(d => d.Path));
        var deleted = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs?deleted=true"));
        Assert.Equal(["a.md"], deleted.Items.Select(d => d.Path));
        Assert.Equal(2, deleted.Items[0].Version);
    }

    [Fact]
    public async Task History_and_old_versions_stay_readable_after_delete()
    {
        var alice = await DeletedAsync();
        var page = await ReadAsync<Page<VersionDto>>(await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions"));
        Assert.Equal([2, 1], page.Items.Select(v => v.Number));
        var v1 = await alice.GetAsync("/api/v1/projects/p/docs/a.md/versions/1");
        Assert.Equal("# one", await v1.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Restoring_a_tombstoned_document_revives_it_as_the_next_version()
    {
        var alice = await DeletedAsync();
        var r = await alice.PostAsync("/api/v1/projects/p/docs/a.md/versions/1/restore", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("\"v3\"", r.Headers.ETag!.Tag);
        Assert.Equal("# one", await (await GetDocAsync(alice, "p", "a.md")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task If_match_against_a_tombstone_fails_and_if_none_match_star_revives()
    {
        var alice = await DeletedAsync();
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutDocAsync(alice, "p", "a.md", "# x", ifMatch: "\"v2\"")).StatusCode);
        var r = await PutDocAsync(alice, "p", "a.md", "# x", ifNoneMatch: "*");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v3\"", r.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Reviving_with_the_same_content_as_the_last_version_still_revives()
    {
        var alice = await DeletedAsync();
        var r = await PutDocAsync(alice, "p", "a.md", "# two");
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_tombstone_again_is_404()
    {
        var alice = await DeletedAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync("/api/v1/projects/p/docs/a.md")).StatusCode);
    }

    [Fact]
    public async Task Post_create_over_a_tombstone_succeeds()
    {
        var alice = await DeletedAsync();
        var r = await alice.PostAsJsonAsync("/api/v1/projects/p/docs", new { path = "a.md", content = "# again" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v3\"", r.Headers.ETag!.Tag);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DocumentTombstoneTests|FullyQualifiedName~DocumentListDeleteTests"`
Expected: FAIL (delete still removes the row; `?deleted=true` is ignored).

- [ ] **Step 3: Rewrite the document service**

Replace `src/Ddm.Api/Documents/DocumentService.cs` with:
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

public sealed record WriteResult(Guid DocumentId, string Path, ParsedMarkdown Parsed, ContentVersion Version, bool Created, bool Changed);

/// <summary>A validated document body, ready to store. Preparing touches neither the database nor blob storage.</summary>
public sealed record PreparedDocument(string Path, byte[] Bytes, string Sha256, ParsedMarkdown Parsed)
{
    public string BlobKey(Guid projectId) => $"projects/{projectId:N}/docs/{Sha256}.md";
}

/// <summary>A document row, live or tombstoned, and the version its pointer names.</summary>
public sealed record DocumentState(Document Doc, ContentVersion? Current)
{
    public bool IsLive => Doc.DeletedAt is null;
}

public sealed class DocumentService(DdmDbContext db, IBlobStore blobs)
{
    public const int MaxBytes = 1_048_576;
    public static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static PreparedDocument Prepare(string path, byte[] bytes)
    {
        DocumentPath.Require(path);
        if (bytes.Length > MaxBytes) throw ApiException.PayloadTooLarge($"Documents are limited to {MaxBytes} bytes");
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw ApiException.BadRequest("invalid_encoding", "Document content must be valid UTF-8"); }
        var parsed = FrontMatter.Parse(text, path);
        return new(path, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), parsed);
    }

    public Task UploadAsync(Project project, PreparedDocument prepared, CancellationToken ct) =>
        blobs.PutAsync(prepared.BlobKey(project.Id), prepared.Bytes, "text/markdown; charset=utf-8", ct);

    /// <summary>
    /// Single-item write: checks preconditions, stores the body (content-addressed, so a failed DB commit only
    /// leaves a harmless orphan), then stages and commits the version. A tombstone counts as missing.
    /// </summary>
    public async Task<WriteResult> WriteAsync(
        Caller caller, Project project, PreparedDocument prepared, string? message,
        WritePrecondition pre, bool requireIfMatch, CancellationToken ct)
    {
        var state = await FindAsync(project, prepared.Path, ct);
        var live = state is { IsLive: true } ? state : null;
        CheckPreconditions(live, pre, requireIfMatch);
        if (live?.Current is { } current && current.ContentSha256 == prepared.Sha256)
            return new(live.Doc.Id, prepared.Path, prepared.Parsed, current, Created: false, Changed: false);

        await UploadAsync(project, prepared, ct);
        var result = await StageAsync(caller, project, prepared, state, message, ct);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Another writer took this version number (or created this path) first.
            throw ApiException.PreconditionFailed("The document was changed by another writer; fetch the latest version and retry");
        }
        catch (DbUpdateConcurrencyException)
        {
            // The row we read was updated or deleted before we saved (its xmin moved on).
            throw ApiException.PreconditionFailed("The document was changed or deleted by another writer");
        }
        return result;
    }

    /// <summary>
    /// Stages the next version, creating, updating or reviving the row. Does not save or upload: the caller
    /// has checked preconditions and stored the blob. Reviving continues the version numbering.
    /// </summary>
    public Task<WriteResult> StageAsync(
        Caller caller, Project project, PreparedDocument prepared, DocumentState? state, string? message, CancellationToken ct)
    {
        var doc = state?.Doc ?? new Document { ProjectId = project.Id, Path = prepared.Path };
        var created = state is not { IsLive: true };
        var version = new ContentVersion
        {
            ItemType = ItemType.Document, ItemId = doc.Id, Number = (state?.Current?.Number ?? 0) + 1,
            ContentRef = prepared.BlobKey(project.Id), ContentSha256 = prepared.Sha256, Author = caller.Actor, Message = message,
        };
        doc.Title = prepared.Parsed.Title;
        doc.FrontMatter = prepared.Parsed.FrontMatterJson;
        doc.CurrentVersionId = version.Id;
        doc.UpdatedAt = version.CreatedAt;
        doc.DeletedAt = null;
        if (state is null) db.Documents.Add(doc);
        db.Versions.Add(version);
        db.Audit(caller, project.Id, created ? "doc.create" : "doc.update", prepared.Path);
        return Task.FromResult(new WriteResult(doc.Id, prepared.Path, prepared.Parsed, version, created, Changed: true));
    }

    /// <summary>Tombstones a tracked, live document. Does not save.</summary>
    public void StageDelete(Caller caller, Project project, Document doc)
    {
        doc.DeletedAt = DateTimeOffset.UtcNow;
        doc.UpdatedAt = doc.DeletedAt.Value;
        db.Audit(caller, project.Id, "doc.delete", doc.Path);
    }

    /// <summary>The row at this path, including a tombstone, tracked for update.</summary>
    public async Task<DocumentState?> FindAsync(Project project, string path, CancellationToken ct)
    {
        var doc = await db.Documents.SingleOrDefaultAsync(d => d.ProjectId == project.Id && d.Path == path, ct);
        if (doc is null) return null;
        var current = doc.CurrentVersionId is { } cid ? await db.Versions.SingleAsync(v => v.Id == cid, ct) : null;
        return new(doc, current);
    }

    private static void CheckPreconditions(DocumentState? live, WritePrecondition pre, bool requireIfMatch)
    {
        if (live is null)
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
        if (pre.IfMatchVersion is { } v && v != live.Current!.Number)
            throw ApiException.PreconditionFailed($"The document is at version {live.Current.Number}, not {v}");
    }

    public async Task<(Document Doc, ContentVersion Version)> GetCurrentAsync(Project project, string path, CancellationToken ct)
    {
        var doc = await RequireDocumentAsync(project, path, includeDeleted: false, ct);
        var version = await db.Versions.AsNoTracking().SingleAsync(v => v.Id == doc.CurrentVersionId, ct);
        return (doc, version);
    }

    public Task<bool> ExistsAsync(Project project, string path, CancellationToken ct) =>
        db.Documents.AnyAsync(d => d.ProjectId == project.Id && d.Path == path && d.DeletedAt == null, ct);

    public async Task<byte[]> ReadContentAsync(ContentVersion version, CancellationToken ct) =>
        await blobs.GetAsync(version.ContentRef, ct)
        ?? throw new ApiException(500, "content_missing", "Stored content is missing", $"No object for {version.ContentRef}");

    public async Task<Document> RequireDocumentAsync(Project project, string path, bool includeDeleted, CancellationToken ct) =>
        await db.Documents.AsNoTracking()
            .SingleOrDefaultAsync(d => d.ProjectId == project.Id && d.Path == path && (includeDeleted || d.DeletedAt == null), ct)
        ?? throw ApiException.NotFound("document_not_found", "Document not found");

    /// <summary>Versions stay readable after a delete, so a tombstoned document can be inspected and restored.</summary>
    public async Task<ContentVersion> GetVersionAsync(Project project, string path, int number, CancellationToken ct)
    {
        var doc = await RequireDocumentAsync(project, path, includeDeleted: true, ct);
        return await db.Versions.AsNoTracking().SingleOrDefaultAsync(
                   v => v.ItemType == ItemType.Document && v.ItemId == doc.Id && v.Number == number, ct)
               ?? throw ApiException.NotFound("version_not_found", "Version not found");
    }

    public async Task<Page<VersionDto>> ListVersionsAsync(Project project, string path, int? limit, string? cursor, CancellationToken ct)
    {
        var doc = await RequireDocumentAsync(project, path, includeDeleted: true, ct);
        var take = Paging.ParseLimit(limit);
        var before = Paging.DecodeLongCursor(cursor);

        var query = db.Versions.AsNoTracking().Where(v => v.ItemType == ItemType.Document && v.ItemId == doc.Id);
        if (before is not null) query = query.Where(v => v.Number < before);
        var rows = await query.OrderByDescending(v => v.Number).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, v => v.Number.ToString());
        return new(page.Items.Select(v => new VersionDto(v.Number, v.Author, v.Message, v.CreatedAt)).ToList(), page.Next);
    }

    /// <summary>Restoring writes the old content as a new version; history is never rewritten. Revives a tombstone.</summary>
    public async Task<WriteResult> RestoreAsync(
        Caller caller, Project project, string path, int number, WritePrecondition pre, CancellationToken ct)
    {
        var version = await GetVersionAsync(project, path, number, ct);
        var bytes = await ReadContentAsync(version, ct);
        return await WriteAsync(caller, project, Prepare(path, bytes), $"Restore version {number}", pre, requireIfMatch: false, ct);
    }

    public async Task DeleteAsync(Caller caller, Project project, string path, WritePrecondition pre, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Lock the row so a concurrent write either commits before our If-Match check or fails on its stale xmin.
        // xmin is a system column, so SELECT * does not include it: name it explicitly for the RowVersion mapping.
        var doc = await db.Documents
                      .FromSqlInterpolated($"SELECT *, xmin FROM \"Documents\" WHERE \"ProjectId\" = {project.Id} AND \"Path\" = {path} AND \"DeletedAt\" IS NULL FOR UPDATE")
                      .SingleOrDefaultAsync(ct)
                  ?? throw ApiException.NotFound("document_not_found", "Document not found");
        if (pre.IfMatchVersion is { } v)
        {
            var current = await db.Versions.AsNoTracking().SingleAsync(x => x.Id == doc.CurrentVersionId, ct);
            if (current.Number != v) throw ApiException.PreconditionFailed($"The document is at version {current.Number}, not {v}");
        }

        StageDelete(caller, project, doc);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
```

- [ ] **Step 4: Update the endpoints**

In `src/Ddm.Api/Documents/DocumentEndpoints.cs`:

`CreateAsync`: replace the `WriteAsync` call with
```csharp
        var result = await docs.WriteAsync(caller, access.Project, DocumentService.Prepare(path, Encoding.UTF8.GetBytes(body.Content)),
            body.Message, new WritePrecondition(null, false, IfNoneMatchAny: true), requireIfMatch: false, ct);
```

`PutAsync`: replace the `WriteAsync` call with
```csharp
        var result = await docs.WriteAsync(caller, access.Project, DocumentService.Prepare(path, bytes), message, pre, requireIfMatch: true, ct);
```

`ListAsync`: add a `bool? deleted` parameter after `prefix`, and after `var docsQuery = db.Documents.Where(d => d.ProjectId == access.Project.Id);` add
```csharp
        docsQuery = deleted == true ? docsQuery.Where(d => d.DeletedAt != null) : docsQuery.Where(d => d.DeletedAt == null);
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~Document"`
Expected: PASS, including `An_update_racing_a_delete_never_returns_500` and `A_conditional_delete_never_removes_a_newer_version` (Review Focus 2: the row version now makes the losing writer fail with 412).

- [ ] **Step 6: Commit**

```bash
git add src/Ddm.Api/Documents tests/Ddm.Api.Tests
git commit -m "feat: tombstone document deletes and split writes into prepare and stage" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Tags for documents

**Files:**
- Create: `src/Ddm.Api/Tags/TagName.cs`, `src/Ddm.Api/Tags/TagService.cs`, `src/Ddm.Api/Tags/TagEndpoints.cs`, `src/Ddm.Api/Tags/TagDtos.cs`
- Modify: `src/Ddm.Api/Documents/DocRoute.cs`, `src/Ddm.Api/Documents/DocumentService.cs`, `src/Ddm.Api/Documents/DocumentEndpoints.cs` (rewrite), `src/Ddm.Api/Documents/DocumentDtos.cs`, `src/Ddm.Api/Program.cs`
- Test: `tests/Ddm.Api.Tests/TagNameTests.cs`, `tests/Ddm.Api.Tests/DocumentTagTests.cs`, `tests/Ddm.Api.Tests/DocRouteTests.cs`

**Interfaces:**
- Consumes: `Tag`, `TagAssignment`, `ItemRef` (Task 2); `DocumentService.StageAsync`, `PreparedDocument`, `DocumentState` (Task 3).
- Produces:
  - `TagName.Normalize(string?) → string`, `TagName.NormalizeSet(IEnumerable<string?>) → IReadOnlyList<string>` (ordered, distinct, ≤ 20), `TagName.Filter(string[]?) → IReadOnlyList<string>`, `TagName.FromFrontMatter(string json) → IReadOnlyList<string>?` (null when there is no `tags` key), `TagName.DeclaredInFrontMatter(string json) → bool`.
  - `TagService` (scoped):
    - `Task StageSetAsync(Guid projectId, ItemRef item, IReadOnlyList<string> names, CancellationToken)`.
    - `Task<IReadOnlyList<string>> SetFromRequestAsync(Caller, Guid projectId, ItemRef item, string target, SetTagsRequest?, CancellationToken)`: validates, stages, audits `tags.set`, saves.
    - `Task<Dictionary<Guid, IReadOnlyList<string>>> TagsForAsync(ItemType, IReadOnlyCollection<Guid>, CancellationToken)`.
    - `Task<IReadOnlyList<string>> TagsForAsync(ItemRef, CancellationToken)`.
    - `IQueryable<Guid> ItemsWithAll(ItemType, IReadOnlyList<string> names)`.
  - `TagEndpoints.ReadBodyAsync(HttpRequest, CancellationToken) → Task<SetTagsRequest?>`; routes `GET /projects/{slug}/tags`, `DELETE /projects/{slug}/tags/{name}`.
  - DTOs: `TagCountDto(string Name, int Count)`, `SetTagsRequest(string[]? Tags)`, `TagsDto(IReadOnlyList<string> Tags)`.
  - `PreparedDocument` gains a fifth member `IReadOnlyList<string>? Tags`; `DocumentService` takes `TagService`.
  - `DocumentDto` and `DocumentSummaryDto` gain `IReadOnlyList<string> Tags` as their last member; `DocRoute.Tags(string Path)`; `PUT /projects/{slug}/docs/{path}/tags`; `?tag=` on the document list.

- [ ] **Step 1: Write the failing tests**

`tests/Ddm.Api.Tests/TagNameTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Tags;

namespace Ddm.Api.Tests;

public class TagNameTests
{
    [Theory]
    [InlineData("guide", "guide")] [InlineData("  Getting Started ", "getting-started")]
    [InlineData("snake_case", "snake-case")] [InlineData("V2", "v2")] [InlineData("a  _ b", "a-b")]
    public void Normalizes(string raw, string expected) => Assert.Equal(expected, TagName.Normalize(raw));

    [Theory] [InlineData("")] [InlineData("-lead")] [InlineData("emoji✓")] [InlineData("a/b")]
    public void Rejects(string raw) => Assert.Equal("invalid_tag", Assert.Throws<ApiException>(() => TagName.Normalize(raw)).Code);

    [Fact] public void Rejects_names_longer_than_50() =>
        Assert.Equal("invalid_tag", Assert.Throws<ApiException>(() => TagName.Normalize(new string('a', 51))).Code);

    [Fact] public void Sets_are_deduplicated_and_ordered() => Assert.Equal(["a", "b"], TagName.NormalizeSet(["b", "A", "a "]));

    [Fact]
    public void More_than_20_tags_is_rejected() =>
        Assert.Equal("too_many_tags",
            Assert.Throws<ApiException>(() => TagName.NormalizeSet(Enumerable.Range(0, 21).Select(i => (string?)$"t{i}"))).Code);

    [Fact] public void An_absent_filter_is_empty() => Assert.Empty(TagName.Filter(null));

    [Fact] public void Front_matter_without_tags_is_null() => Assert.Null(TagName.FromFrontMatter("{\"title\":\"x\"}"));
    [Fact] public void Front_matter_list() => Assert.Equal(["2024", "guide"], TagName.FromFrontMatter("{\"tags\":[\"guide\",2024]}"));
    [Fact] public void Front_matter_single_string() => Assert.Equal(["guide"], TagName.FromFrontMatter("{\"tags\":\"Guide\"}"));
    [Fact] public void Front_matter_null_is_an_empty_set() => Assert.Empty(TagName.FromFrontMatter("{\"tags\":null}")!);

    [Fact] public void Front_matter_objects_are_rejected() =>
        Assert.Equal("invalid_tag", Assert.Throws<ApiException>(() => TagName.FromFrontMatter("{\"tags\":{\"a\":1}}")).Code);

    [Fact] public void Declared_in_front_matter_only_checks_the_key() =>
        Assert.True(TagName.DeclaredInFrontMatter("{\"tags\":{\"a\":1}}"));
}
```

Append to `tests/Ddm.Api.Tests/DocRouteTests.cs`:
```csharp
    [Fact] public void Tags() => Assert.Equal(new DocRoute.Tags("guides/setup.md"), DocRoute.Parse("guides/setup.md/tags"));
    [Fact] public void A_document_named_tags_is_still_a_document() =>
        Assert.Equal(new DocRoute.Current("a/tags.md"), DocRoute.Parse("a/tags.md"));
```

`tests/Ddm.Api.Tests/DocumentTagTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Documents;
using Ddm.Api.Tags;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class DocumentTagTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private async Task<HttpClient> AliceAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        return alice;
    }

    private static Task<HttpResponseMessage> PutTagsAsync(HttpClient c, string url, params string[] tags) =>
        c.PutAsJsonAsync(url, new { tags });

    private static async Task<IReadOnlyList<string>> DocTagsAsync(HttpClient c, string path) =>
        (await ReadAsync<DocumentDto>(await GetDocAsync(c, "p", path, "application/json"))).Tags;

    [Fact]
    public async Task Front_matter_tags_are_normalised_and_returned()
    {
        var alice = await AliceAsync();
        var r = await PutDocAsync(alice, "p", "a.md", "---\ntags: [Guide, Getting Started]\n---\n# A");
        Assert.Equal(["getting-started", "guide"], (await ReadAsync<DocumentDto>(r)).Tags);
        Assert.Equal(["getting-started", "guide"], await DocTagsAsync(alice, "a.md"));
    }

    [Fact]
    public async Task Invalid_front_matter_tags_fail_the_write()
    {
        var alice = await AliceAsync();
        var r = await PutDocAsync(alice, "p", "a.md", "---\ntags: [\"bad/tag\"]\n---\n# A");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("invalid_tag", await ProblemCodeAsync(r));
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
    }

    [Fact]
    public async Task Api_tags_are_rejected_when_front_matter_owns_them()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "---\ntags: [guide]\n---\n# A");
        var r = await PutTagsAsync(alice, "/api/v1/projects/p/docs/a.md/tags", "other");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("tags_managed_by_front_matter", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Api_tags_replace_a_documents_tags_without_a_new_version()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# A");
        var r = await PutTagsAsync(alice, "/api/v1/projects/p/docs/a.md/tags", "b", "a");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(["a", "b"], (await ReadAsync<TagsDto>(r)).Tags);
        await PutTagsAsync(alice, "/api/v1/projects/p/docs/a.md/tags", "c");
        var doc = await GetDocAsync(alice, "p", "a.md", "application/json");
        Assert.Equal("\"v1\"", doc.Headers.ETag!.Tag);
        Assert.Equal(["c"], (await ReadAsync<DocumentDto>(doc)).Tags);
    }

    // Review Focus 4
    [Fact]
    public async Task Removing_the_tags_key_keeps_tags_and_an_empty_list_clears_them()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "---\ntags: [guide]\n---\n# A");
        await PutDocAsync(alice, "p", "a.md", "# A without front matter", ifMatch: "\"v1\"");
        Assert.Equal(["guide"], await DocTagsAsync(alice, "a.md"));
        await PutDocAsync(alice, "p", "a.md", "---\ntags: []\n---\n# A", ifMatch: "\"v2\"");
        Assert.Empty(await DocTagsAsync(alice, "a.md"));
    }

    // Review Focus 4
    [Fact]
    public async Task A_revived_document_keeps_its_tags()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# A");
        await PutTagsAsync(alice, "/api/v1/projects/p/docs/a.md/tags", "keep");
        await alice.DeleteAsync("/api/v1/projects/p/docs/a.md");
        await PutDocAsync(alice, "p", "a.md", "# back");
        Assert.Equal(["keep"], await DocTagsAsync(alice, "a.md"));
    }

    [Fact]
    public async Task Tags_on_a_missing_or_tombstoned_document_are_404()
    {
        var alice = await AliceAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await PutTagsAsync(alice, "/api/v1/projects/p/docs/nope.md/tags", "x")).StatusCode);
        await PutDocAsync(alice, "p", "gone.md", "# g");
        await alice.DeleteAsync("/api/v1/projects/p/docs/gone.md");
        Assert.Equal(HttpStatusCode.NotFound, (await PutTagsAsync(alice, "/api/v1/projects/p/docs/gone.md/tags", "x")).StatusCode);
    }

    [Fact]
    public async Task A_tags_body_is_required()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "# A");
        var r = await alice.PutAsJsonAsync("/api/v1/projects/p/docs/a.md/tags", new { });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("validation_failed", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task List_filters_by_every_tag_given_and_includes_tags()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "---\ntags: [x, y]\n---\n# A");
        await PutDocAsync(alice, "p", "b.md", "---\ntags: [x]\n---\n# B");
        await PutDocAsync(alice, "p", "c.md", "# C");
        var both = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs?tag=x&tag=Y"));
        Assert.Equal(["a.md"], both.Items.Select(d => d.Path));
        Assert.Equal(["x", "y"], both.Items[0].Tags);
        var x = await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs?tag=x"));
        Assert.Equal(["a.md", "b.md"], x.Items.Select(d => d.Path));
    }

    [Fact]
    public async Task Vocabulary_counts_live_items_and_admins_can_delete_a_tag()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "---\ntags: [x, y]\n---\n# A");
        await PutDocAsync(alice, "p", "b.md", "---\ntags: [x]\n---\n# B");
        await alice.DeleteAsync("/api/v1/projects/p/docs/b.md");
        var vocab = await ReadAsync<Page<TagCountDto>>(await alice.GetAsync("/api/v1/projects/p/tags"));
        Assert.Equal([("x", 1), ("y", 1)], vocab.Items.Select(t => (t.Name, t.Count)));

        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/tags/x")).StatusCode);
        Assert.Equal(["y"], await DocTagsAsync(alice, "a.md"));
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync("/api/v1/projects/p/tags/x")).StatusCode);
    }

    [Fact]
    public async Task Editors_cannot_delete_tags_and_readers_cannot_set_them()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "a.md", "---\ntags: [x]\n---\n# A");
        await PutDocAsync(alice, "p", "b.md", "# B");
        await AddMemberAsync(alice, "p", "ed", "editor");
        await AddMemberAsync(alice, "p", "rd", "reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await ClientFor("ed").DeleteAsync("/api/v1/projects/p/tags/x")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutTagsAsync(ClientFor("rd"), "/api/v1/projects/p/docs/b.md/tags", "y")).StatusCode);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TagNameTests|FullyQualifiedName~DocumentTagTests|FullyQualifiedName~DocRouteTests"`
Expected: build FAILS (`Ddm.Api.Tags` does not exist; `DocumentDto.Tags` does not exist).

- [ ] **Step 3: Implement tag names and DTOs**

`src/Ddm.Api/Tags/TagName.cs`:
```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using Ddm.Api.Common;

namespace Ddm.Api.Tags;

public static partial class TagName
{
    public const int MaxLength = 50;
    public const int MaxPerItem = 20;

    [GeneratedRegex(@"[\s_]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,49}\z")]
    private static partial Regex Valid();

    /// <summary>Trims, lowercases and turns runs of whitespace or '_' into '-'; 400 invalid_tag if the result is not a valid name.</summary>
    public static string Normalize(string? raw)
    {
        var name = Separators().Replace((raw ?? "").Trim().ToLowerInvariant(), "-");
        return Valid().IsMatch(name)
            ? name
            : throw ApiException.BadRequest("invalid_tag", "Invalid tag",
                $"'{raw}' is not a valid tag: use 1-{MaxLength} lowercase letters, digits and '-', starting with a letter or digit");
    }

    /// <summary>Normalises, de-duplicates and orders a tag set; 400 too_many_tags above the per-item limit.</summary>
    public static IReadOnlyList<string> NormalizeSet(IEnumerable<string?> raw)
    {
        var set = raw.Select(Normalize).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return set.Count <= MaxPerItem
            ? set
            : throw ApiException.BadRequest("too_many_tags", "Too many tags", $"An item can have at most {MaxPerItem} tags");
    }

    /// <summary>Repeated <c>?tag=</c> query values, normalised. All of them must match (AND).</summary>
    public static IReadOnlyList<string> Filter(string[]? raw) => raw is null || raw.Length == 0 ? [] : NormalizeSet(raw);

    public static bool DeclaredInFrontMatter(string frontMatterJson)
    {
        using var doc = JsonDocument.Parse(frontMatterJson);
        return doc.RootElement.TryGetProperty("tags", out _);
    }

    /// <summary>The tags a document's front matter declares, or null when it has no <c>tags</c> key.</summary>
    public static IReadOnlyList<string>? FromFrontMatter(string frontMatterJson)
    {
        using var doc = JsonDocument.Parse(frontMatterJson);
        if (!doc.RootElement.TryGetProperty("tags", out var tags)) return null;
        return tags.ValueKind switch
        {
            JsonValueKind.Null => Array.Empty<string>(),
            JsonValueKind.Array => NormalizeSet(tags.EnumerateArray().Select(Scalar).ToList()),
            _ => NormalizeSet([Scalar(tags)]),
        };
    }

    // YAML turns `tags: [v2, 2024]` into a string and a number; both are fine tag names.
    private static string? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
        _ => throw ApiException.BadRequest("invalid_tag", "Invalid tag", "Front matter 'tags' must be a string or a list of strings"),
    };
}
```

`src/Ddm.Api/Tags/TagDtos.cs`:
```csharp
namespace Ddm.Api.Tags;

public sealed record TagCountDto(string Name, int Count);
public sealed record SetTagsRequest(string[]? Tags);
public sealed record TagsDto(IReadOnlyList<string> Tags);
```

- [ ] **Step 4: Implement the tag service and vocabulary endpoints**

`src/Ddm.Api/Tags/TagService.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Tags;

public sealed class TagService(DdmDbContext db)
{
    /// <summary>
    /// Stages replacing an item's tag set (names already normalised). Missing Tag rows are inserted at once with
    /// ON CONFLICT DO NOTHING, so concurrent writers never collide on a new name; inside a transaction they roll back
    /// with it, outside one a failed save leaves at most an unused tag. Assignments wait for the caller's SaveChanges.
    /// </summary>
    public async Task StageSetAsync(Guid projectId, ItemRef item, IReadOnlyList<string> names, CancellationToken ct)
    {
        foreach (var name in names)
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"Tags\" (\"Id\", \"ProjectId\", \"Name\") VALUES ({Guid.NewGuid()}, {projectId}, {name}) ON CONFLICT (\"ProjectId\", \"Name\") DO NOTHING",
                ct);

        var wanted = await db.Tags.Where(t => t.ProjectId == projectId && names.Contains(t.Name)).Select(t => t.Id).ToListAsync(ct);
        var current = await db.TagAssignments.Where(a => a.ItemType == item.Type && a.ItemId == item.Id).ToListAsync(ct);
        db.TagAssignments.RemoveRange(current.Where(a => !wanted.Contains(a.TagId)));
        foreach (var tagId in wanted.Where(id => current.All(a => a.TagId != id)))
            db.TagAssignments.Add(new TagAssignment { TagId = tagId, ItemType = item.Type, ItemId = item.Id });
    }

    /// <summary>Replaces an item's tags from an API request and saves. Tags are metadata: no new version, no ETag change.</summary>
    public async Task<IReadOnlyList<string>> SetFromRequestAsync(
        Caller caller, Guid projectId, ItemRef item, string target, SetTagsRequest? body, CancellationToken ct)
    {
        if (body?.Tags is null) throw ApiException.BadRequest("validation_failed", "The request is not valid", "tags is required");
        var names = TagName.NormalizeSet(body.Tags);
        await StageSetAsync(projectId, item, names, ct);
        db.Audit(caller, projectId, "tags.set", target);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.Conflict("tags_conflict", "The tags were changed concurrently; retry");
        }
        return names;
    }

    /// <summary>Tag names for each item, ordered by name; items without tags map to an empty list.</summary>
    public async Task<Dictionary<Guid, IReadOnlyList<string>>> TagsForAsync(ItemType type, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var rows = await (from a in db.TagAssignments
                          join t in db.Tags on a.TagId equals t.Id
                          where a.ItemType == type && ids.Contains(a.ItemId)
                          select new { a.ItemId, t.Name }).ToListAsync(ct);
        var result = ids.Distinct().ToDictionary(id => id, _ => (IReadOnlyList<string>)Array.Empty<string>());
        foreach (var g in rows.GroupBy(r => r.ItemId))
            result[g.Key] = g.Select(r => r.Name).Order(StringComparer.Ordinal).ToList();
        return result;
    }

    public async Task<IReadOnlyList<string>> TagsForAsync(ItemRef item, CancellationToken ct) =>
        (await TagsForAsync(item.Type, [item.Id], ct))[item.Id];

    /// <summary>
    /// Ids of items carrying every one of <paramref name="names"/>. An item's tags all belong to its own project and
    /// names are unique per project, so each name matches at most one assignment per item and a count suffices.
    /// </summary>
    public IQueryable<Guid> ItemsWithAll(ItemType type, IReadOnlyList<string> names)
    {
        var count = names.Count;
        return from a in db.TagAssignments
               join t in db.Tags on a.TagId equals t.Id
               where a.ItemType == type && names.Contains(t.Name)
               group a by a.ItemId into g
               where g.Count() == count
               select g.Key;
    }
}
```

`src/Ddm.Api/Tags/TagEndpoints.cs`:
```csharp
using System.Security.Claims;
using System.Text.Json;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Tags;

public static class TagEndpoints
{
    public static void MapTags(this RouteGroupBuilder v1)
    {
        v1.MapGet("/projects/{slug}/tags", ListAsync);
        v1.MapDelete("/projects/{slug}/tags/{name}", DeleteAsync);
    }

    /// <summary>Reads a <c>{"tags": [...]}</c> body for the per-item tag routes.</summary>
    public static async Task<SetTagsRequest?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType()) throw new ApiException(415, "unsupported_media_type", "Send tags as application/json");
        try { return await request.ReadFromJsonAsync<SetTagsRequest>(ct); }
        catch (JsonException) { throw ApiException.BadRequest("validation_failed", "The request is not valid", "The body is not valid JSON"); }
    }

    private static async Task<IResult> ListAsync(
        string slug, string? cursor, int? limit, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);

        // Count only assignments on live items, so a tombstone's tags do not inflate the vocabulary.
        var live = db.TagAssignments.Where(a =>
            a.ItemType == ItemType.Project
            || (a.ItemType == ItemType.Document && db.Documents.Any(d => d.Id == a.ItemId && d.DeletedAt == null))
            || (a.ItemType == ItemType.Asset && db.Assets.Any(x => x.Id == a.ItemId && x.DeletedAt == null))
            || (a.ItemType == ItemType.Spec && db.Specs.Any(s => s.Id == a.ItemId && s.DeletedAt == null)));
        var query = db.Tags.Where(t => t.ProjectId == access.Project.Id);
        if (after is not null) query = query.Where(t => string.Compare(t.Name, after) > 0);
        var rows = await query.OrderBy(t => t.Name)
            .Select(t => new TagCountDto(t.Name, live.Count(a => a.TagId == t.Id)))
            .Take(take + 1).ToListAsync(ct);
        return Results.Ok(Paging.ToPage(rows, take, t => t.Name));
    }

    private static async Task<IResult> DeleteAsync(
        string slug, string name, ClaimsPrincipal user, ProjectAuthorizer authz, DdmDbContext db, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Admin, ct);
        var tag = await db.Tags.SingleOrDefaultAsync(t => t.ProjectId == access.Project.Id && t.Name == name, ct)
                  ?? throw ApiException.NotFound("tag_not_found", "Tag not found");
        db.Tags.Remove(tag); // the database cascades to its assignments
        db.Audit(caller, access.Project.Id, "tag.delete", name);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
```

- [ ] **Step 5: Apply front matter tags on document writes**

In `src/Ddm.Api/Documents/DocumentService.cs`:
- add `using Ddm.Api.Tags;`
- change the record to `public sealed record PreparedDocument(string Path, byte[] Bytes, string Sha256, ParsedMarkdown Parsed, IReadOnlyList<string>? Tags)` and document `Tags` as "the front matter's tag set, or null when it has no tags key";
- change the class declaration to `public sealed class DocumentService(DdmDbContext db, IBlobStore blobs, TagService tags)`;
- in `Prepare`, return `new(path, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), parsed, TagName.FromFrontMatter(parsed.FrontMatterJson));`
- replace `StageAsync` so it is async and applies the tags:
```csharp
    /// <summary>
    /// Stages the next version, creating, updating or reviving the row. Does not save or upload: the caller
    /// has checked preconditions and stored the blob. Reviving continues the version numbering.
    /// </summary>
    public async Task<WriteResult> StageAsync(
        Caller caller, Project project, PreparedDocument prepared, DocumentState? state, string? message, CancellationToken ct)
    {
        var doc = state?.Doc ?? new Document { ProjectId = project.Id, Path = prepared.Path };
        var created = state is not { IsLive: true };
        var version = new ContentVersion
        {
            ItemType = ItemType.Document, ItemId = doc.Id, Number = (state?.Current?.Number ?? 0) + 1,
            ContentRef = prepared.BlobKey(project.Id), ContentSha256 = prepared.Sha256, Author = caller.Actor, Message = message,
        };
        doc.Title = prepared.Parsed.Title;
        doc.FrontMatter = prepared.Parsed.FrontMatterJson;
        doc.CurrentVersionId = version.Id;
        doc.UpdatedAt = version.CreatedAt;
        doc.DeletedAt = null;
        if (state is null) db.Documents.Add(doc);
        db.Versions.Add(version);
        db.Audit(caller, project.Id, created ? "doc.create" : "doc.update", prepared.Path);
        // Front matter owns the tag set when it has a tags key; otherwise API-set tags are left alone.
        if (prepared.Tags is { } names) await tags.StageSetAsync(project.Id, new ItemRef(ItemType.Document, doc.Id), names, ct);
        return new WriteResult(doc.Id, prepared.Path, prepared.Parsed, version, created, Changed: true);
    }
```

In `src/Ddm.Api/Documents/DocRoute.cs`, add `public sealed record Tags(string Path) : DocRoute;`, change the regex to
```csharp
    [GeneratedRegex(@"^(?<path>.+\.md)(?:(?<tags>/tags)|/versions(?:/(?<n>[0-9]+)(?<restore>/restore)?)?)?\z")]
```
and in `Parse`, after `var path = m.Groups["path"].Value;` add `if (m.Groups["tags"].Success) return new Tags(path);`. Update the class summary to mention `…/tags`.

In `src/Ddm.Api/Documents/DocumentDtos.cs`:
```csharp
public sealed record DocumentDto(string Path, string Title, int Version, DateTimeOffset UpdatedAt, JsonElement FrontMatter, IReadOnlyList<string> Tags)
{
    public static DocumentDto From(string path, ParsedMarkdown parsed, ContentVersion version, IReadOnlyList<string> tags)
    {
        using var doc = JsonDocument.Parse(parsed.FrontMatterJson);
        return new(path, parsed.Title, version.Number, version.CreatedAt, doc.RootElement.Clone(), tags);
    }
}

public sealed record CreateDocumentRequest(string? Path, string? Content, string? Message);
public sealed record DocumentSummaryDto(string Path, string Title, int Version, DateTimeOffset UpdatedAt, IReadOnlyList<string> Tags);
public sealed record VersionDto(int Number, string Author, string? Message, DateTimeOffset CreatedAt);
```

- [ ] **Step 6: Rewrite the document endpoints**

Replace `src/Ddm.Api/Documents/DocumentEndpoints.cs` with:
```csharp
using System.Security.Claims;
using System.Text;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Tags;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Documents;

public static class DocumentEndpoints
{
    public static void MapDocuments(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/projects/{slug}/docs");
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapGet("{**rest}", GetAsync);
        g.MapPut("{**rest}", PutAsync);
        g.MapDelete("{**rest}", DeleteAsync);
        g.MapPost("{**rest}", RestoreAsync);
    }

    private static async Task<IResult> CreateAsync(
        string slug, CreateDocumentRequest? body, HttpContext http, ClaimsPrincipal user,
        ProjectAuthorizer authz, DocumentService docs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        var path = DocumentPath.Require(body?.Path);
        if (body!.Content is null) throw ApiException.BadRequest("validation_failed", "The request is not valid", "content is required");
        if (body.Message is { Length: > 500 }) throw ApiException.BadRequest("validation_failed", "The request is not valid", "message is limited to 500 characters");
        if (await docs.ExistsAsync(access.Project, path, ct))
            throw ApiException.Conflict("document_exists", $"A document already exists at {path}");

        var result = await docs.WriteAsync(caller, access.Project, DocumentService.Prepare(path, Encoding.UTF8.GetBytes(body.Content)),
            body.Message, new WritePrecondition(null, false, IfNoneMatchAny: true), requireIfMatch: false, ct);
        return await WrittenAsync(http, slug, result, tags, created: true, ct);
    }

    private static async Task<IResult> GetAsync(
        string slug, string rest, string? cursor, int? limit, HttpContext http, ClaimsPrincipal user,
        ProjectAuthorizer authz, DocumentService docs, MarkdownRenderer renderer, TagService tags, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        switch (DocRoute.Parse(rest))
        {
            case DocRoute.Current c:
            {
                var path = DocumentPath.Require(c.Path);
                var (_, version) = await docs.GetCurrentAsync(access.Project, path, ct);
                return await RespondAsync(http, docs, renderer, tags, path, version, ct);
            }
            case DocRoute.History h:
                return Results.Ok(await docs.ListVersionsAsync(access.Project, DocumentPath.Require(h.Path), limit, cursor, ct));
            case DocRoute.Snapshot s:
            {
                var path = DocumentPath.Require(s.Path);
                var version = await docs.GetVersionAsync(access.Project, path, s.Number, ct);
                return await RespondAsync(http, docs, renderer, tags, path, version, ct);
            }
            default:
                throw ApiException.NotFound("not_found", "No such route");
        }
    }

    private static async Task<IResult> PutAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        DocumentService docs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        switch (DocRoute.Parse(rest))
        {
            case DocRoute.Current route:
            {
                var path = DocumentPath.Require(route.Path);
                var pre = Preconditions.Parse(http.Request.Headers);
                var message = MessageFrom(http.Request);
                var bytes = await ReadBodyAsync(http.Request, ct);
                var result = await docs.WriteAsync(caller, access.Project, DocumentService.Prepare(path, bytes), message, pre, requireIfMatch: true, ct);
                return await WrittenAsync(http, slug, result, tags, result.Created, ct);
            }
            case DocRoute.Tags route:
            {
                var doc = await docs.RequireDocumentAsync(access.Project, DocumentPath.Require(route.Path), includeDeleted: false, ct);
                if (TagName.DeclaredInFrontMatter(doc.FrontMatter))
                    throw ApiException.Conflict("tags_managed_by_front_matter",
                        "This document's tags come from its front matter; change the tags key in the document instead");
                var names = await tags.SetFromRequestAsync(caller, access.Project.Id, new ItemRef(ItemType.Document, doc.Id), doc.Path,
                    await TagEndpoints.ReadBodyAsync(http.Request, ct), ct);
                return Results.Ok(new TagsDto(names));
            }
            default:
                throw ApiException.NotFound("not_found", "No such route");
        }
    }

    private static async Task<IResult> ListAsync(
        string slug, string? prefix, bool? deleted, string[]? tag, string? cursor, int? limit, ClaimsPrincipal user,
        ProjectAuthorizer authz, DdmDbContext db, TagService tags, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);
        if (prefix is { Length: > DocumentPath.MaxLength })
            throw ApiException.BadRequest("validation_failed", "The request is not valid", "prefix is too long");
        var filter = TagName.Filter(tag);

        var docsQuery = db.Documents.Where(d => d.ProjectId == access.Project.Id);
        docsQuery = deleted == true ? docsQuery.Where(d => d.DeletedAt != null) : docsQuery.Where(d => d.DeletedAt == null);
        if (!string.IsNullOrEmpty(prefix)) docsQuery = docsQuery.Where(d => d.Path.StartsWith(prefix));
        if (filter.Count > 0)
        {
            var tagged = tags.ItemsWithAll(ItemType.Document, filter);
            docsQuery = docsQuery.Where(d => tagged.Contains(d.Id));
        }
        if (after is not null) docsQuery = docsQuery.Where(d => string.Compare(d.Path, after) > 0);

        var rows = await (from d in docsQuery
                          join v in db.Versions on d.CurrentVersionId equals (Guid?)v.Id
                          orderby d.Path
                          select new { d.Id, d.Path, d.Title, v.Number, d.UpdatedAt })
            .Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, d => d.Path);
        var tagMap = await tags.TagsForAsync(ItemType.Document, page.Items.Select(d => d.Id).ToList(), ct);
        var items = page.Items.Select(d => new DocumentSummaryDto(d.Path, d.Title, d.Number, d.UpdatedAt, tagMap[d.Id])).ToList();
        return Results.Ok(new Page<DocumentSummaryDto>(items, page.Next));
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
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        DocumentService docs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        if (DocRoute.Parse(rest) is not DocRoute.Restore route) throw ApiException.NotFound("not_found", "No such route");
        var result = await docs.RestoreAsync(caller, access.Project, DocumentPath.Require(route.Path), route.Number,
            Preconditions.Parse(http.Request.Headers), ct);
        return await WrittenAsync(http, slug, result, tags, created: false, ct);
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

    private static async Task<IResult> WrittenAsync(HttpContext http, string slug, WriteResult r, TagService tags, bool created, CancellationToken ct)
    {
        http.Response.Headers.ETag = Preconditions.ETag(r.Version.Number);
        var dto = DocumentDto.From(r.Path, r.Parsed, r.Version, await tags.TagsForAsync(new ItemRef(ItemType.Document, r.DocumentId), ct));
        return created ? Results.Created($"/api/v1/projects/{slug}/docs/{r.Path}", dto) : Results.Ok(dto);
    }

    internal static async Task<IResult> RespondAsync(
        HttpContext http, DocumentService docs, MarkdownRenderer renderer, TagService tags, string path, ContentVersion version, CancellationToken ct)
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
            DocFormat.Json => Results.Ok(DocumentDto.From(path, parsed, version,
                await tags.TagsForAsync(new ItemRef(ItemType.Document, version.ItemId), ct))),
            _ => Results.Text(text, "text/markdown; charset=utf-8"),
        };
    }
}
```
Note the status codes are unchanged: a `PUT` that creates or revives is `201`; restore is always `200`.

- [ ] **Step 7: Register the module**

In `src/Ddm.Api/Program.cs` add `using Ddm.Api.Tags;`, add `builder.Services.AddScoped<TagService>();` after the `MarkdownRenderer` line, and add `v1.MapTags();` after `v1.MapDocuments();`.

- [ ] **Step 8: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~Tag|FullyQualifiedName~Document|FullyQualifiedName~DocRoute"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Ddm.Api tests/Ddm.Api.Tests
git commit -m "feat: project-scoped tags on documents with front matter ownership and filters" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Project tags

**Files:**
- Modify: `src/Ddm.Api/Projects/ProjectDtos.cs`, `src/Ddm.Api/Projects/ProjectEndpoints.cs`
- Test: `tests/Ddm.Api.Tests/ProjectTagTests.cs`

**Interfaces:**
- Consumes: `TagService`, `TagName` (Task 4).
- Produces: `ProjectDto` gains a last member `IReadOnlyList<string> Tags`; `ProjectDto.From(Project, Role, IReadOnlyList<string> tags)`; `UpdateProjectRequest` gains `string[]? Tags`; `GET /projects?tag=`.

- [ ] **Step 1: Write the failing tests**

`tests/Ddm.Api.Tests/ProjectTagTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Projects;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class ProjectTagTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private static Task<HttpResponseMessage> SetTagsAsync(HttpClient c, string slug, params string[] tags) =>
        c.PatchAsJsonAsync($"/api/v1/projects/{slug}", new { tags });

    [Fact]
    public async Task Admins_set_project_tags_with_patch()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        var r = await SetTagsAsync(alice, "p", "Platform", "api");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(["api", "platform"], (await ReadAsync<ProjectDto>(r)).Tags);
        Assert.Equal(["api", "platform"], (await ReadAsync<ProjectDto>(await alice.GetAsync("/api/v1/projects/p"))).Tags);
    }

    [Fact]
    public async Task A_patch_without_tags_leaves_them_alone()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await SetTagsAsync(alice, "p", "api");
        var r = await alice.PatchAsJsonAsync("/api/v1/projects/p", new { name = "Renamed" });
        Assert.Equal(["api"], (await ReadAsync<ProjectDto>(r)).Tags);
    }

    [Fact]
    public async Task A_new_project_has_no_tags()
    {
        var alice = ClientFor("alice");
        Assert.Empty((await CreateProjectAsync(alice, "p")).Tags);
    }

    [Fact]
    public async Task Editors_cannot_set_project_tags()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await AddMemberAsync(alice, "p", "ed", "editor");
        Assert.Equal(HttpStatusCode.Forbidden, (await SetTagsAsync(ClientFor("ed"), "p", "x")).StatusCode);
    }

    [Fact]
    public async Task Tag_filter_never_reveals_projects_the_caller_cannot_see()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "secret");
        await CreateProjectAsync(alice, "open", "internal");
        await SetTagsAsync(alice, "secret", "shared");
        await SetTagsAsync(alice, "open", "shared");

        var bob = await ReadAsync<Page<ProjectDto>>(await ClientFor("bob").GetAsync("/api/v1/projects?tag=shared"));
        Assert.Equal(["open"], bob.Items.Select(p => p.Slug));
        var mine = await ReadAsync<Page<ProjectDto>>(await alice.GetAsync("/api/v1/projects?tag=shared"));
        Assert.Equal(["open", "secret"], mine.Items.Select(p => p.Slug));
        Assert.Empty((await ReadAsync<Page<ProjectDto>>(await alice.GetAsync("/api/v1/projects?tag=nothing"))).Items);
    }

    [Fact]
    public async Task A_read_token_cannot_see_or_set_another_projects_tags()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "a");
        await CreateProjectAsync(alice, "b");
        await PutDocAsync(alice, "b", "x.md", "# x");
        var token = TokenClient(await CreateTokenAsync(alice, "a", "read"));
        Assert.Equal(HttpStatusCode.NotFound, (await token.GetAsync("/api/v1/projects/b/tags")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await token.PutAsJsonAsync("/api/v1/projects/b/docs/x.md/tags", new { tags = new[] { "t" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetTagsAsync(token, "a", "t")).StatusCode);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ProjectTagTests"`
Expected: build FAILS (`ProjectDto.Tags` does not exist).

- [ ] **Step 3: Implement**

`src/Ddm.Api/Projects/ProjectDtos.cs`: change the two records to
```csharp
public sealed record ProjectDto(string Slug, string Name, string Description, string Visibility, string Role, DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tags)
{
    public static ProjectDto From(Project p, Role role, IReadOnlyList<string> tags) =>
        new(p.Slug, p.Name, p.Description, Wire.Lower(p.Visibility), Wire.Lower(role), p.CreatedAt, tags);
}

public sealed record UpdateProjectRequest(string? Name, string? Description, string? Visibility, string[]? Tags);
```

In `src/Ddm.Api/Projects/ProjectEndpoints.cs` (add `using Ddm.Api.Tags;`):

`CreateAsync`: `return Results.Created($"/api/v1/projects/{project.Slug}", ProjectDto.From(project, Role.Admin, []));`

`ListAsync`:
```csharp
    private static async Task<IResult> ListAsync(
        ClaimsPrincipal user, string? cursor, int? limit, string[]? tag, ProjectAuthorizer authz, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);
        var filter = TagName.Filter(tag);

        // Filtering narrows what VisibleTo allows; it can never widen it.
        var query = authz.VisibleTo(caller);
        if (filter.Count > 0)
        {
            var tagged = tags.ItemsWithAll(ItemType.Project, filter);
            query = query.Where(p => tagged.Contains(p.Id));
        }
        if (after is not null) query = query.Where(p => string.Compare(p.Slug, after) > 0);
        var rows = await query.OrderBy(p => p.Slug).Take(take + 1).ToListAsync(ct);

        var page = Paging.ToPage(rows, take, p => p.Slug);
        var roles = await authz.RolesAsync(caller, [.. page.Items], ct);
        var tagMap = await tags.TagsForAsync(ItemType.Project, page.Items.Select(p => p.Id).ToList(), ct);
        var dtos = page.Items.Select(p => ProjectDto.From(p, roles[p.Id], tagMap[p.Id])).ToList();
        return Results.Ok(new Page<ProjectDto>(dtos, page.Next));
    }
```

`GetAsync` (add `TagService tags` parameter):
```csharp
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        return Results.Ok(ProjectDto.From(access.Project, access.Role,
            await tags.TagsForAsync(new ItemRef(ItemType.Project, access.Project.Id), ct)));
```

`UpdateAsync` (add `TagService tags` parameter):
```csharp
        var p = access.Project;
        var names = body.Tags is null ? null : TagName.NormalizeSet(body.Tags);
        if (body.Name is not null) p.Name = ProjectValidation.Name(body.Name);
        if (body.Description is not null) p.Description = ProjectValidation.Description(body.Description);
        if (body.Visibility is not null) p.Visibility = ProjectValidation.ParseVisibility(body.Visibility);
        if (names is not null) await tags.StageSetAsync(p.Id, new ItemRef(ItemType.Project, p.Id), names, ct);
        db.Audit(caller, p.Id, "project.update", p.Slug);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.Conflict("tags_conflict", "The tags were changed concurrently; retry");
        }
        return Results.Ok(ProjectDto.From(p, access.Role, await tags.TagsForAsync(new ItemRef(ItemType.Project, p.Id), ct)));
```
(All validation, including tag names, runs before anything is staged.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~Project|FullyQualifiedName~Me"`
Expected: PASS (existing project tests deserialize `ProjectDto` and ignore the extra member).

- [ ] **Step 5: Commit**

```bash
git add src/Ddm.Api/Projects tests/Ddm.Api.Tests/ProjectTagTests.cs
git commit -m "feat: project tags on PATCH and tag filtering of the project list" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Asset types and paths

**Files:**
- Create: `src/Ddm.Api/Assets/AssetTypes.cs`, `src/Ddm.Api/Assets/AssetPath.cs`, `src/Ddm.Api/Assets/AssetRoute.cs`
- Test: `tests/Ddm.Api.Tests/AssetTypesTests.cs`, `tests/Ddm.Api.Tests/AssetPathTests.cs`

**Interfaces:**
- Consumes: `ContentPath.ValidateSegments` (Task 1).
- Produces (namespace `Ddm.Api.Assets`):
  - `record AssetType(string Extension, string MediaType, bool IsImage, Func<byte[], bool> Sniff)`.
  - `AssetTypes.ForPath(string path) → AssetType?` (by extension, case-insensitive); `AssetTypes.Extensions`.
  - `AssetPath.Require(string? path) → AssetType` (400 `invalid_path`, 415 `unsupported_asset_type`); `AssetPath.IsValid(string path) → bool`.
  - `AssetRoute.Parse(string rest) → AssetRoute.Current(Path) | AssetRoute.Tags(Path)`.

- [ ] **Step 1: Write the failing tests**

`tests/Ddm.Api.Tests/AssetTypesTests.cs`:
```csharp
using Ddm.Api.Assets;

namespace Ddm.Api.Tests;

public class AssetTypesTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    private static AssetType Type(string path) => AssetTypes.ForPath(path)!;

    [Theory]
    [InlineData("a.png", "image/png")] [InlineData("A.PNG", "image/png")] [InlineData("x/y.jpeg", "image/jpeg")]
    [InlineData("d.svg", "image/svg+xml")] [InlineData("r.yml", "application/yaml; charset=utf-8")]
    public void Types_come_from_the_extension(string path, string media) => Assert.Equal(media, Type(path).MediaType);

    [Theory] [InlineData("a.exe")] [InlineData("a.html")] [InlineData("a.zip")] [InlineData("noext")] [InlineData("a.md")]
    public void Other_extensions_are_not_allowed(string path) => Assert.Null(AssetTypes.ForPath(path));

    [Fact] public void Png_bytes_match_png() => Assert.True(Type("a.png").Sniff(Png));
    [Fact] public void Png_bytes_do_not_match_jpeg() => Assert.False(Type("a.jpg").Sniff(Png));
    [Fact] public void A_png_renamed_svg_is_not_svg() => Assert.False(Type("a.svg").Sniff(Png));

    [Fact]
    public void Svg_with_a_script_is_still_svg() =>
        Assert.True(Type("a.svg").Sniff(Utf8("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")));

    [Fact]
    public void Svg_with_a_dtd_or_external_entity_is_refused() =>
        Assert.False(Type("a.svg").Sniff(Utf8(
            "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg>&x;</svg>")));

    [Fact] public void Html_is_not_svg() => Assert.False(Type("a.svg").Sniff(Utf8("<html><body/></html>")));

    [Fact]
    public void Text_must_be_utf8_without_nul()
    {
        var txt = Type("a.txt");
        Assert.True(txt.Sniff(Utf8("héllo")));
        Assert.False(txt.Sniff([0x68, 0x00, 0x69]));
        Assert.False(txt.Sniff([0xC3, 0x28]));
    }

    [Fact]
    public void Gif_webp_ico_and_pdf_signatures()
    {
        Assert.True(Type("a.gif").Sniff(Utf8("GIF89a....")));
        Assert.True(Type("a.webp").Sniff(Utf8("RIFF\0\0\0\0WEBPVP8 ")));
        Assert.True(Type("a.ico").Sniff([0, 0, 1, 0, 1, 0]));
        Assert.True(Type("a.pdf").Sniff(Utf8("%PDF-1.7\n")));
        Assert.False(Type("a.pdf").Sniff(Utf8("<html>")));
    }
}
```

`tests/Ddm.Api.Tests/AssetPathTests.cs`:
```csharp
using Ddm.Api.Assets;
using Ddm.Api.Common;

namespace Ddm.Api.Tests;

public class AssetPathTests
{
    [Fact] public void A_valid_path_returns_its_type() => Assert.Equal("image/png", AssetPath.Require("images/a.png").MediaType);

    [Theory] [InlineData("a.md")] [InlineData("a//b.png")] [InlineData(".hidden.png")] [InlineData("")]
    public void Bad_paths_and_markdown_are_400(string path) =>
        Assert.Equal("invalid_path", Assert.Throws<ApiException>(() => AssetPath.Require(path)).Code);

    [Fact]
    public void Unknown_extensions_are_415()
    {
        var ex = Assert.Throws<ApiException>(() => AssetPath.Require("tool.exe"));
        Assert.Equal(415, ex.Status);
        Assert.Equal("unsupported_asset_type", ex.Code);
    }

    [Fact] public void IsValid_mirrors_Require() => Assert.True(AssetPath.IsValid("a/b.svg") && !AssetPath.IsValid("a/b.md"));

    [Fact] public void Plain_route() => Assert.Equal(new AssetRoute.Current("images/a.png"), AssetRoute.Parse("images/a.png"));
    [Fact] public void Tags_route() => Assert.Equal(new AssetRoute.Tags("images/a.png"), AssetRoute.Parse("images/a.png/tags"));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AssetTypesTests|FullyQualifiedName~AssetPathTests"`
Expected: build FAILS (`Ddm.Api.Assets` does not exist).

- [ ] **Step 3: Implement**

`src/Ddm.Api/Assets/AssetTypes.cs`:
```csharp
using System.Text;
using System.Xml;

namespace Ddm.Api.Assets;

public sealed record AssetType(string Extension, string MediaType, bool IsImage, Func<byte[], bool> Sniff);

/// <summary>
/// The upload allow-list. The type comes from the extension and must be confirmed by the bytes;
/// the client's Content-Type is never trusted.
/// </summary>
public static class AssetTypes
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly AssetType[] All =
    [
        new(".png", "image/png", true, b => StartsWith(b, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])),
        new(".jpg", "image/jpeg", true, IsJpeg),
        new(".jpeg", "image/jpeg", true, IsJpeg),
        new(".gif", "image/gif", true, b => StartsWith(b, "GIF87a"u8) || StartsWith(b, "GIF89a"u8)),
        new(".webp", "image/webp", true, b => b.Length >= 12 && StartsWith(b, "RIFF"u8) && b.AsSpan(8, 4).SequenceEqual("WEBP"u8)),
        new(".ico", "image/x-icon", true, b => StartsWith(b, [0x00, 0x00, 0x01, 0x00])),
        new(".svg", "image/svg+xml", true, IsSvg),
        new(".pdf", "application/pdf", false, b => StartsWith(b, "%PDF-"u8)),
        new(".txt", "text/plain; charset=utf-8", false, IsText),
        new(".csv", "text/csv; charset=utf-8", false, IsText),
        new(".json", "application/json; charset=utf-8", false, IsText),
        new(".yaml", "application/yaml; charset=utf-8", false, IsText),
        new(".yml", "application/yaml; charset=utf-8", false, IsText),
    ];

    private static readonly Dictionary<string, AssetType> ByExtension = All.ToDictionary(t => t.Extension, StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> Extensions => All.Select(t => t.Extension);

    public static AssetType? ForPath(string path) => ByExtension.GetValueOrDefault(System.IO.Path.GetExtension(path));

    private static bool StartsWith(byte[] bytes, ReadOnlySpan<byte> prefix) => bytes.AsSpan().StartsWith(prefix);

    private static bool IsJpeg(byte[] b) => StartsWith(b, [0xFF, 0xD8, 0xFF]);

    private static bool IsText(byte[] b)
    {
        if (b.AsSpan().Contains((byte)0)) return false;
        try
        {
            StrictUtf8.GetString(b);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>Well-formed XML whose root is &lt;svg&gt;. DTDs are refused, so entity expansion and external entities cannot run.</summary>
    private static bool IsSvg(byte[] b)
    {
        if (!IsText(b)) return false;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(b), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            });
            var root = reader.MoveToContent() == XmlNodeType.Element ? reader.LocalName : null;
            while (reader.Read()) { }
            return root == "svg";
        }
        catch (XmlException) { return false; }
    }
}
```

`src/Ddm.Api/Assets/AssetPath.cs`:
```csharp
using Ddm.Api.Common;

namespace Ddm.Api.Assets;

public static class AssetPath
{
    /// <summary>Validates an asset path and returns its type: 400 invalid_path for a bad path or markdown, 415 off the allow-list.</summary>
    public static AssetType Require(string? path)
    {
        if (ContentPath.ValidateSegments(path) is { } error) throw ApiException.BadRequest("invalid_path", "Invalid asset path", error);
        if (path!.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw ApiException.BadRequest("invalid_path", "Invalid asset path", "Markdown files are documents: publish them under /docs");
        return AssetTypes.ForPath(path)
               ?? throw new ApiException(415, "unsupported_asset_type", "This file type is not allowed",
                   $"Allowed extensions: {string.Join(", ", AssetTypes.Extensions)}");
    }

    public static bool IsValid(string path) =>
        ContentPath.ValidateSegments(path) is null
        && !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && AssetTypes.ForPath(path) is not null;
}
```

`src/Ddm.Api/Assets/AssetRoute.cs`:
```csharp
namespace Ddm.Api.Assets;

/// <summary>
/// Splits the catch-all part of /assets/{**rest}. Asset paths must end in an allow-listed extension,
/// so a trailing "/tags" segment can never be part of one.
/// </summary>
public abstract record AssetRoute
{
    public sealed record Current(string Path) : AssetRoute;
    public sealed record Tags(string Path) : AssetRoute;

    private const string TagsSuffix = "/tags";

    public static AssetRoute Parse(string rest) =>
        rest.EndsWith(TagsSuffix, StringComparison.Ordinal) ? new Tags(rest[..^TagsSuffix.Length]) : new Current(rest);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~AssetTypesTests|FullyQualifiedName~AssetPathTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Ddm.Api/Assets tests/Ddm.Api.Tests/AssetTypesTests.cs tests/Ddm.Api.Tests/AssetPathTests.cs
git commit -m "feat: asset allow-list with byte sniffing and asset paths" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Asset storage and endpoints

**Files:**
- Create: `src/Ddm.Api/Common/RequestBody.cs`, `src/Ddm.Api/Assets/AssetOptions.cs`, `src/Ddm.Api/Assets/AssetService.cs`, `src/Ddm.Api/Assets/AssetDtos.cs`, `src/Ddm.Api/Assets/AssetResponses.cs`, `src/Ddm.Api/Assets/AssetEndpoints.cs`, `src/Ddm.Api/Assets/AssetSetup.cs`
- Modify: `src/Ddm.Api/Storage/IBlobStore.cs`, `src/Ddm.Api/Storage/S3BlobStore.cs`, `tests/Ddm.Api.Tests/Infrastructure/InMemoryBlobStore.cs`, `tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs`, `src/Ddm.Api/Program.cs`
- Test: `tests/Ddm.Api.Tests/AssetTests.cs`, `tests/Ddm.Api.Tests/S3BlobStoreTests.cs`

**Interfaces:**
- Consumes: `AssetPath`, `AssetType`, `AssetRoute` (Task 6); `ShaPrecondition`, `Preconditions.ParseSha/ShaETag` (Task 1); `TagService`, `TagName`, `TagEndpoints.ReadBodyAsync`, `TagsDto` (Task 4); `Asset` (Task 2).
- Produces:
  - `IBlobStore.OpenReadAsync(string key, CancellationToken) → Task<Stream?>`.
  - `RequestBody.AllowUpTo(HttpContext, long bytes)`; `RequestBody.ReadLimitedAsync(HttpRequest, long max, string tooLargeMessage, CancellationToken) → Task<byte[]>`.
  - `record PreparedAsset(string Path, byte[] Bytes, string Sha256, AssetType Type)` with `BlobKey(Guid projectId)`; `record AssetWriteResult(Asset Asset, bool Created, bool Changed)`.
  - `AssetService` (scoped):
    - `long MaxBytes`.
    - `PreparedAsset Prepare(string path, byte[] bytes)`.
    - `Task UploadAsync(Project, PreparedAsset, CancellationToken)`.
    - `Task<AssetWriteResult> WriteAsync(Caller, Project, PreparedAsset, ShaPrecondition, bool requireIfMatch, CancellationToken)`.
    - `AssetWriteResult Stage(Caller, Project, PreparedAsset, Asset? existing)`.
    - `void StageDelete(Caller, Project, Asset)`.
    - `Task<Asset?> FindAsync(Project, string path, CancellationToken)`: includes tombstones, tracked.
    - `Task<bool> ExistsAsync(Project, string path, CancellationToken)`.
    - `Task<Asset> RequireLiveAsync(Project, string path, CancellationToken)`.
    - `Task DeleteAsync(Caller, Project, string path, ShaPrecondition, CancellationToken)`.
  - `AssetResponses.Csp`; `AssetResponses.ServeAsync(HttpContext, IBlobStore, string storageKey, string contentType, long size, string sha256, string fileName, string cacheControl, CancellationToken) → Task<IResult>`.
  - `AssetDto(string Path, string ContentType, long Size, string Sha256, IReadOnlyList<string> Tags, DateTimeOffset UpdatedAt, string UpdatedBy)`.
  - `AssetSetup.AddDdmAssets(IServiceCollection, IConfiguration)`; routes under `/projects/{slug}/assets`.
  - Test helpers on `ApiTestBase`: `PngBytes`, `PngVariant(byte)`, `Sha(byte[])`, `PutAssetAsync(...)`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs` (add `using System.Security.Cryptography;`):
```csharp
    /// <summary>The smallest bytes our sniffer accepts as PNG.</summary>
    protected static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    /// <summary>A different PNG, for replace tests.</summary>
    protected static byte[] PngVariant(byte n) => [.. PngBytes, n];

    protected static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    protected static Task<HttpResponseMessage> PutAssetAsync(
        HttpClient client, string slug, string path, byte[] bytes, string? ifMatch = null, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/projects/{slug}/assets/{path}") { Content = new ByteArrayContent(bytes) };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return client.SendAsync(request);
    }
```

Append to `tests/Ddm.Api.Tests/S3BlobStoreTests.cs`:
```csharp
    [Fact]
    public async Task Open_read_streams_the_object()
    {
        var bytes = new byte[300_000];
        Random.Shared.NextBytes(bytes);
        await _store.PutAsync("stream", bytes, "application/octet-stream", default);
        await using var stream = await _store.OpenReadAsync("stream", default);
        using var ms = new MemoryStream();
        await stream!.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
    }

    [Fact]
    public async Task Open_read_of_a_missing_key_is_null() => Assert.Null(await _store.OpenReadAsync("nope", default));
```

`tests/Ddm.Api.Tests/AssetTests.cs`:
```csharp
using Ddm.Api.Assets;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Tags;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class AssetTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private async Task<HttpClient> AliceAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        return alice;
    }

    private static string ETagOf(byte[] bytes) => $"\"{Sha(bytes)}\"";

    [Fact]
    public async Task Put_creates_an_asset_and_get_streams_it_back_with_safe_headers()
    {
        var alice = await AliceAsync();
        var r = await PutAssetAsync(alice, "p", "images/a.png", PngBytes);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal(ETagOf(PngBytes), r.Headers.ETag!.Tag);
        var dto = await ReadAsync<AssetDto>(r);
        Assert.Equal(("images/a.png", "image/png", (long)PngBytes.Length), (dto.Path, dto.ContentType, dto.Size));

        var get = await alice.GetAsync("/api/v1/projects/p/assets/images/a.png");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(PngBytes, await get.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", get.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", get.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(AssetResponses.Csp, get.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Null(get.Content.Headers.ContentDisposition);
    }

    [Fact]
    public async Task Head_returns_headers_without_a_body()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        var head = await alice.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/v1/projects/p/assets/a.png"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(PngBytes.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task If_none_match_with_the_current_etag_is_304()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects/p/assets/a.png");
        request.Headers.TryAddWithoutValidation("If-None-Match", ETagOf(PngBytes));
        Assert.Equal(HttpStatusCode.NotModified, (await alice.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Replacing_needs_the_current_etag_and_identical_bytes_are_a_no_op()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        Assert.Equal((HttpStatusCode)428, (await PutAssetAsync(alice, "p", "a.png", PngVariant(1))).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutAssetAsync(alice, "p", "a.png", PngVariant(1), ifMatch: ETagOf(PngVariant(9)))).StatusCode);

        var same = await PutAssetAsync(alice, "p", "a.png", PngBytes, ifMatch: ETagOf(PngBytes));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        var replaced = await PutAssetAsync(alice, "p", "a.png", PngVariant(1), ifMatch: ETagOf(PngBytes));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        Assert.Equal(ETagOf(PngVariant(1)), replaced.Headers.ETag!.Tag);
        Assert.Equal(PngVariant(1), await (await alice.GetAsync("/api/v1/projects/p/assets/a.png")).Content.ReadAsByteArrayAsync());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DdmDbContext>();
        Assert.Equal(2, await db.AuditEntries.CountAsync(e => e.Action.StartsWith("asset.")));
    }

    [Fact]
    public async Task Post_multipart_creates_and_refuses_an_existing_path()
    {
        var alice = await AliceAsync();
        MultipartFormDataContent Form() => new()
        {
            { new StringContent("logo.png"), "path" },
            { new ByteArrayContent(PngBytes), "file", "logo.png" },
        };
        Assert.Equal(HttpStatusCode.Created, (await alice.PostAsync("/api/v1/projects/p/assets", Form())).StatusCode);
        var again = await alice.PostAsync("/api/v1/projects/p/assets", Form());
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("asset_exists", await ProblemCodeAsync(again));
    }

    [Fact]
    public async Task A_png_renamed_svg_is_rejected_and_nothing_is_stored()
    {
        var alice = await AliceAsync();
        var r = await PutAssetAsync(alice, "p", "a.svg", PngBytes);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("asset_type_mismatch", await ProblemCodeAsync(r));
        Assert.Equal(0, Factory.Blobs.Count);
    }

    [Theory]
    [InlineData("tool.exe", 415, "unsupported_asset_type")]
    [InlineData("page.md", 400, "invalid_path")]
    public async Task Disallowed_paths_are_refused(string path, int status, string code)
    {
        var alice = await AliceAsync();
        var r = await PutAssetAsync(alice, "p", path, PngBytes);
        Assert.Equal((HttpStatusCode)status, r.StatusCode);
        Assert.Equal(code, await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Oversized_uploads_are_413()
    {
        var alice = await AliceAsync();
        var big = new byte[10 * 1024 * 1024 + 1];
        PngBytes.CopyTo(big, 0);
        var r = await PutAssetAsync(alice, "p", "big.png", big);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal("payload_too_large", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task Svg_and_text_are_served_inertly()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "x.svg", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"));
        await PutAssetAsync(alice, "p", "notes.txt", Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"));

        var svg = await alice.GetAsync("/api/v1/projects/p/assets/x.svg");
        Assert.Contains("sandbox", svg.Headers.GetValues("Content-Security-Policy").Single());
        var txt = await alice.GetAsync("/api/v1/projects/p/assets/notes.txt");
        Assert.Equal("text/plain", txt.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", txt.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", txt.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Delete_tombstones_and_the_list_shows_it_with_deleted_true()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        await PutAssetAsync(alice, "p", "b.png", PngBytes);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/assets/a.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/projects/p/assets/a.png")).StatusCode);
        var live = await ReadAsync<Page<AssetDto>>(await alice.GetAsync("/api/v1/projects/p/assets"));
        Assert.Equal(["b.png"], live.Items.Select(a => a.Path));
        var deleted = await ReadAsync<Page<AssetDto>>(await alice.GetAsync("/api/v1/projects/p/assets?deleted=true"));
        Assert.Equal(["a.png"], deleted.Items.Select(a => a.Path));
        Assert.Equal(HttpStatusCode.Created, (await PutAssetAsync(alice, "p", "a.png", PngBytes)).StatusCode);
    }

    [Fact]
    public async Task Readers_can_read_but_not_write()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        await AddMemberAsync(alice, "p", "rd", "reader");
        var reader = ClientFor("rd");
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/projects/p/assets/a.png")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAssetAsync(reader, "p", "b.png", PngBytes)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.DeleteAsync("/api/v1/projects/p/assets/a.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ClientFor("stranger").GetAsync("/api/v1/projects/p/assets/a.png")).StatusCode);
    }

    [Fact]
    public async Task Asset_tags_can_be_set_and_filtered()
    {
        var alice = await AliceAsync();
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        await PutAssetAsync(alice, "p", "b.png", PngBytes);
        var r = await alice.PutAsJsonAsync("/api/v1/projects/p/assets/a.png/tags", new { tags = new[] { "Logo" } });
        Assert.Equal(["logo"], (await ReadAsync<TagsDto>(r)).Tags);
        var page = await ReadAsync<Page<AssetDto>>(await alice.GetAsync("/api/v1/projects/p/assets?tag=logo"));
        Assert.Equal(["a.png"], page.Items.Select(a => a.Path));
        Assert.Equal(["logo"], page.Items[0].Tags);
    }

    [Fact]
    public async Task A_storage_outage_fails_the_upload_and_stores_nothing()
    {
        var alice = await AliceAsync();
        Factory.Blobs.FailNextPut = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await PutAssetAsync(alice, "p", "a.png", PngBytes)).StatusCode);
        Assert.Empty((await ReadAsync<Page<AssetDto>>(await alice.GetAsync("/api/v1/projects/p/assets"))).Items);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AssetTests|FullyQualifiedName~S3BlobStoreTests"`
Expected: build FAILS (`OpenReadAsync`, `AssetDto`, `AssetResponses` do not exist).

- [ ] **Step 3: Add streaming reads to blob storage**

`src/Ddm.Api/Storage/IBlobStore.cs`, add:
```csharp
    /// <summary>Opens the object for streaming; the caller disposes the stream. Returns null when the key does not exist.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct);
```

`src/Ddm.Api/Storage/S3BlobStore.cs`, add:
```csharp
    public async Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        try
        {
            var response = await s3.GetObjectAsync(bucket, key, ct);
            return response.ResponseStream; // disposing the stream releases the connection
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }
```

`tests/Ddm.Api.Tests/Infrastructure/InMemoryBlobStore.cs`, add:
```csharp
    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct) =>
        Task.FromResult<Stream?>(_blobs.TryGetValue(key, out var b) ? new MemoryStream(b, writable: false) : null);
```

- [ ] **Step 4: Add bounded body reads**

`src/Ddm.Api/Common/RequestBody.cs`:
```csharp
using Microsoft.AspNetCore.Http.Features;

namespace Ddm.Api.Common;

public static class RequestBody
{
    /// <summary>Raises the server's body-size limit for this request (Kestrel's default is 30 MB).</summary>
    public static void AllowUpTo(HttpContext http, long bytes)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = bytes;
    }

    /// <summary>Reads the whole body, failing with 413 as soon as it passes <paramref name="max"/>, whatever Content-Length claimed.</summary>
    public static async Task<byte[]> ReadLimitedAsync(HttpRequest request, long max, string tooLargeMessage, CancellationToken ct)
    {
        if (request.ContentLength > max) throw ApiException.PayloadTooLarge(tooLargeMessage);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > max) throw ApiException.PayloadTooLarge(tooLargeMessage);
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }
}
```

- [ ] **Step 5: Implement the asset service**

`src/Ddm.Api/Assets/AssetOptions.cs`:
```csharp
namespace Ddm.Api.Assets;

public sealed class AssetOptions
{
    public long MaxBytes { get; set; } = 10 * 1024 * 1024;
}
```

`src/Ddm.Api/Assets/AssetService.cs`:
```csharp
using System.Security.Cryptography;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Assets;

/// <summary>A validated upload, ready to store. Preparing touches neither the database nor blob storage.</summary>
public sealed record PreparedAsset(string Path, byte[] Bytes, string Sha256, AssetType Type)
{
    public string BlobKey(Guid projectId) => $"projects/{projectId:N}/assets/{Sha256}";
}

public sealed record AssetWriteResult(Asset Asset, bool Created, bool Changed);

public sealed class AssetService(DdmDbContext db, IBlobStore blobs, IOptions<AssetOptions> options)
{
    public long MaxBytes => options.Value.MaxBytes;

    /// <summary>Validates path, size and type; the type is confirmed from the bytes, never from the client's Content-Type.</summary>
    public PreparedAsset Prepare(string path, byte[] bytes)
    {
        var type = AssetPath.Require(path);
        if (bytes.Length > MaxBytes) throw ApiException.PayloadTooLarge($"Assets are limited to {MaxBytes} bytes");
        if (!type.Sniff(bytes))
            throw ApiException.BadRequest("asset_type_mismatch", "The file's content does not match its extension",
                $"{path} does not contain {type.MediaType} data");
        return new(path, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), type);
    }

    public Task UploadAsync(Project project, PreparedAsset prepared, CancellationToken ct) =>
        blobs.PutAsync(prepared.BlobKey(project.Id), prepared.Bytes, prepared.Type.MediaType, ct);

    public async Task<AssetWriteResult> WriteAsync(
        Caller caller, Project project, PreparedAsset prepared, ShaPrecondition pre, bool requireIfMatch, CancellationToken ct)
    {
        var existing = await FindAsync(project, prepared.Path, ct);
        var live = existing is { DeletedAt: null } ? existing : null;
        CheckPreconditions(live, pre, requireIfMatch);
        if (live is not null && live.Sha256 == prepared.Sha256) return new(live, Created: false, Changed: false);

        await UploadAsync(project, prepared, ct);
        var result = Stage(caller, project, prepared, existing);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.PreconditionFailed("The asset was created by another writer; fetch it and retry");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ApiException.PreconditionFailed("The asset was changed or deleted by another writer");
        }
        return result;
    }

    /// <summary>Stages creating, replacing or reviving the row. The caller has stored the blob and will save.</summary>
    public AssetWriteResult Stage(Caller caller, Project project, PreparedAsset prepared, Asset? existing)
    {
        var created = existing is not { DeletedAt: null };
        var asset = existing ?? new Asset
        {
            ProjectId = project.Id, Path = prepared.Path, ContentType = "", Sha256 = "", StorageKey = "", UpdatedBy = "",
        };
        asset.ContentType = prepared.Type.MediaType;
        asset.Size = prepared.Bytes.Length;
        asset.Sha256 = prepared.Sha256;
        asset.StorageKey = prepared.BlobKey(project.Id);
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        asset.UpdatedBy = caller.Actor;
        asset.DeletedAt = null;
        if (existing is null) db.Assets.Add(asset);
        db.Audit(caller, project.Id, created ? "asset.create" : "asset.update", prepared.Path);
        return new(asset, created, Changed: true);
    }

    /// <summary>Tombstones a tracked, live asset. The blob stays, since old document versions may still show it.</summary>
    public void StageDelete(Caller caller, Project project, Asset asset)
    {
        asset.DeletedAt = DateTimeOffset.UtcNow;
        asset.UpdatedAt = asset.DeletedAt.Value;
        asset.UpdatedBy = caller.Actor;
        db.Audit(caller, project.Id, "asset.delete", asset.Path);
    }

    /// <summary>The row at this path, including a tombstone, tracked for update.</summary>
    public Task<Asset?> FindAsync(Project project, string path, CancellationToken ct) =>
        db.Assets.SingleOrDefaultAsync(a => a.ProjectId == project.Id && a.Path == path, ct);

    public Task<bool> ExistsAsync(Project project, string path, CancellationToken ct) =>
        db.Assets.AnyAsync(a => a.ProjectId == project.Id && a.Path == path && a.DeletedAt == null, ct);

    public async Task<Asset> RequireLiveAsync(Project project, string path, CancellationToken ct) =>
        await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.ProjectId == project.Id && a.Path == path && a.DeletedAt == null, ct)
        ?? throw ApiException.NotFound("asset_not_found", "Asset not found");

    /// <summary>The row version makes a delete racing a replace fail cleanly, whichever commits second.</summary>
    public async Task DeleteAsync(Caller caller, Project project, string path, ShaPrecondition pre, CancellationToken ct)
    {
        var asset = await db.Assets.SingleOrDefaultAsync(a => a.ProjectId == project.Id && a.Path == path && a.DeletedAt == null, ct)
                    ?? throw ApiException.NotFound("asset_not_found", "Asset not found");
        if (pre.IfMatchSha is { } sha && sha != asset.Sha256)
            throw ApiException.PreconditionFailed("The asset has changed since you fetched it");
        StageDelete(caller, project, asset);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw ApiException.PreconditionFailed("The asset was changed by another writer");
        }
    }

    private static void CheckPreconditions(Asset? live, ShaPrecondition pre, bool requireIfMatch)
    {
        if (live is null)
        {
            if (pre.HasIfMatch) throw ApiException.PreconditionFailed("The asset does not exist");
            return;
        }
        if (pre.IfNoneMatchAny) throw ApiException.PreconditionFailed("The asset already exists");
        if (!pre.HasIfMatch)
        {
            if (requireIfMatch)
                throw ApiException.PreconditionRequired("Replacing an existing asset requires an If-Match header with its current ETag");
            return;
        }
        if (pre.IfMatchSha is { } sha && sha != live.Sha256)
            throw ApiException.PreconditionFailed("The asset has changed since you fetched it");
    }
}
```

`src/Ddm.Api/Assets/AssetDtos.cs`:
```csharp
using Ddm.Api.Domain;

namespace Ddm.Api.Assets;

public sealed record AssetDto(string Path, string ContentType, long Size, string Sha256, IReadOnlyList<string> Tags,
    DateTimeOffset UpdatedAt, string UpdatedBy)
{
    public static AssetDto From(Asset a, IReadOnlyList<string> tags) =>
        new(a.Path, a.ContentType, a.Size, a.Sha256, tags, a.UpdatedAt, a.UpdatedBy);
}
```

- [ ] **Step 6: Serve assets safely and map the endpoints**

`src/Ddm.Api/Assets/AssetResponses.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Documents;
using Ddm.Api.Storage;
using Microsoft.Net.Http.Headers;

namespace Ddm.Api.Assets;

/// <summary>Streams stored bytes with the headers that keep untrusted content inert (shared by the API and /content).</summary>
public static class AssetResponses
{
    public const string Csp = "default-src 'none'; style-src 'unsafe-inline'; sandbox";

    public static async Task<IResult> ServeAsync(
        HttpContext http, IBlobStore blobs, string storageKey, string contentType, long size, string sha256,
        string fileName, string cacheControl, CancellationToken ct)
    {
        var etag = Preconditions.ShaETag(sha256);
        var headers = http.Response.Headers;
        headers.ETag = etag;
        headers.CacheControl = cacheControl;
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = Csp;
        if (!contentType.StartsWith("image/", StringComparison.Ordinal))
            headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();

        if (http.Request.GetTypedHeaders().IfNoneMatch.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Tag.Value == etag))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        if (HttpMethods.IsHead(http.Request.Method))
        {
            http.Response.ContentType = contentType;
            http.Response.ContentLength = size;
            return Results.Empty;
        }

        var stream = await blobs.OpenReadAsync(storageKey, ct)
                     ?? throw new ApiException(500, "content_missing", "Stored content is missing", $"No object for {storageKey}");
        http.Response.ContentLength = size;
        return Results.Stream(stream, contentType);
    }

    public static string FileNameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
}
```

`src/Ddm.Api/Assets/AssetEndpoints.cs`:
```csharp
using System.Security.Claims;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Storage;
using Ddm.Api.Tags;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Assets;

public static class AssetEndpoints
{
    private const long MultipartOverhead = 64 * 1024;

    public static void MapAssets(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/projects/{slug}/assets");
        g.MapGet("", ListAsync);
        g.MapPost("", UploadAsync);
        g.MapMethods("{**rest}", [HttpMethods.Get, HttpMethods.Head], GetAsync);
        g.MapPut("{**rest}", PutAsync);
        g.MapDelete("{**rest}", DeleteAsync);
    }

    private static async Task<IResult> PutAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        AssetService assets, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        switch (AssetRoute.Parse(rest))
        {
            case AssetRoute.Tags route:
            {
                var asset = await assets.RequireLiveAsync(access.Project, route.Path, ct);
                var names = await tags.SetFromRequestAsync(caller, access.Project.Id, new ItemRef(ItemType.Asset, asset.Id), asset.Path,
                    await TagEndpoints.ReadBodyAsync(http.Request, ct), ct);
                return Results.Ok(new TagsDto(names));
            }
            case AssetRoute.Current route:
            {
                AssetPath.Require(route.Path); // fail on a bad path or type before reading the body
                var pre = Preconditions.ParseSha(http.Request.Headers);
                RequestBody.AllowUpTo(http, assets.MaxBytes);
                var bytes = await RequestBody.ReadLimitedAsync(http.Request, assets.MaxBytes, $"Assets are limited to {assets.MaxBytes} bytes", ct);
                var result = await assets.WriteAsync(caller, access.Project, assets.Prepare(route.Path, bytes), pre, requireIfMatch: true, ct);
                return await WrittenAsync(http, slug, result, tags, ct);
            }
            default:
                throw ApiException.NotFound("not_found", "No such route");
        }
    }

    private static async Task<IResult> UploadAsync(
        string slug, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, AssetService assets, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        if (!http.Request.HasFormContentType) throw new ApiException(415, "unsupported_media_type", "Upload assets as multipart/form-data");
        var limit = assets.MaxBytes + MultipartOverhead;
        if (http.Request.ContentLength > limit) throw TooLarge(assets);
        RequestBody.AllowUpTo(http, limit);

        IFormCollection form;
        try { form = await http.Request.ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = limit }, ct); }
        catch (InvalidDataException) { throw TooLarge(assets); }
        var path = form["path"].ToString();
        var file = form.Files["file"] ?? throw ApiException.BadRequest("validation_failed", "The request is not valid", "file is required");
        AssetPath.Require(path);
        if (file.Length > assets.MaxBytes) throw TooLarge(assets);
        if (await assets.ExistsAsync(access.Project, path, ct)) throw ApiException.Conflict("asset_exists", $"An asset already exists at {path}");

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var result = await assets.WriteAsync(caller, access.Project, assets.Prepare(path, ms.ToArray()),
            new ShaPrecondition(null, false, IfNoneMatchAny: true), requireIfMatch: false, ct);
        return await WrittenAsync(http, slug, result, tags, ct);
    }

    private static async Task<IResult> GetAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        AssetService assets, IBlobStore blobs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        if (AssetRoute.Parse(rest) is not AssetRoute.Current route) throw ApiException.NotFound("not_found", "No such route");
        var asset = await assets.RequireLiveAsync(access.Project, route.Path, ct);
        return await AssetResponses.ServeAsync(http, blobs, asset.StorageKey, asset.ContentType, asset.Size, asset.Sha256,
            AssetResponses.FileNameOf(asset.Path), "private, no-cache", ct);
    }

    private static async Task<IResult> ListAsync(
        string slug, string? prefix, bool? deleted, string[]? tag, string? cursor, int? limit, ClaimsPrincipal user,
        ProjectAuthorizer authz, DdmDbContext db, TagService tags, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);
        if (prefix is { Length: > ContentPath.MaxLength })
            throw ApiException.BadRequest("validation_failed", "The request is not valid", "prefix is too long");
        var filter = TagName.Filter(tag);

        var query = db.Assets.AsNoTracking().Where(a => a.ProjectId == access.Project.Id);
        query = deleted == true ? query.Where(a => a.DeletedAt != null) : query.Where(a => a.DeletedAt == null);
        if (!string.IsNullOrEmpty(prefix)) query = query.Where(a => a.Path.StartsWith(prefix));
        if (filter.Count > 0)
        {
            var tagged = tags.ItemsWithAll(ItemType.Asset, filter);
            query = query.Where(a => tagged.Contains(a.Id));
        }
        if (after is not null) query = query.Where(a => string.Compare(a.Path, after) > 0);

        var rows = await query.OrderBy(a => a.Path).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, a => a.Path);
        var tagMap = await tags.TagsForAsync(ItemType.Asset, page.Items.Select(a => a.Id).ToList(), ct);
        return Results.Ok(new Page<AssetDto>(page.Items.Select(a => AssetDto.From(a, tagMap[a.Id])).ToList(), page.Next));
    }

    private static async Task<IResult> DeleteAsync(
        string slug, string rest, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, AssetService assets, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        if (AssetRoute.Parse(rest) is not AssetRoute.Current route) throw ApiException.NotFound("not_found", "No such route");
        await assets.DeleteAsync(caller, access.Project, route.Path, Preconditions.ParseSha(http.Request.Headers), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> WrittenAsync(HttpContext http, string slug, AssetWriteResult r, TagService tags, CancellationToken ct)
    {
        http.Response.Headers.ETag = Preconditions.ShaETag(r.Asset.Sha256);
        var dto = AssetDto.From(r.Asset, await tags.TagsForAsync(new ItemRef(ItemType.Asset, r.Asset.Id), ct));
        return r.Created ? Results.Created($"/api/v1/projects/{slug}/assets/{r.Asset.Path}", dto) : Results.Ok(dto);
    }

    private static ApiException TooLarge(AssetService assets) => ApiException.PayloadTooLarge($"Assets are limited to {assets.MaxBytes} bytes");
}
```
The multipart form is read with `ReadFormAsync` rather than `IFormFile` parameter binding, which would require antiforgery middleware; bearer-token APIs are not exposed to CSRF.

`src/Ddm.Api/Assets/AssetSetup.cs`:
```csharp
namespace Ddm.Api.Assets;

public static class AssetSetup
{
    public static IServiceCollection AddDdmAssets(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AssetOptions>(config.GetSection("Assets"));
        services.AddScoped<AssetService>();
        return services;
    }
}
```

In `src/Ddm.Api/Program.cs`: add `using Ddm.Api.Assets;`, `builder.Services.AddDdmAssets(builder.Configuration);` after the `TagService` line, and `v1.MapAssets();` after `v1.MapTags();`.

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~Asset|FullyQualifiedName~S3BlobStoreTests"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Ddm.Api tests/Ddm.Api.Tests
git commit -m "feat: assets with sniffed types, SHA ETags, tombstones and inert serving" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Signed content URLs

**Files:**
- Create: `src/Ddm.Api/Assets/ContentOptions.cs`, `src/Ddm.Api/Assets/ContentUrlSigner.cs`, `src/Ddm.Api/Assets/ContentEndpoints.cs`, `src/Ddm.Api/Assets/ContentSetup.cs`
- Modify: `src/Ddm.Api/Program.cs`, `src/Ddm.Api/appsettings.Development.json`, `tests/Ddm.Api.Tests/Infrastructure/DdmApiFactory.cs`, `tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs`
- Test: `tests/Ddm.Api.Tests/ContentUrlSignerTests.cs`, `tests/Ddm.Api.Tests/ContentEndpointTests.cs`

**Interfaces:**
- Consumes: `AssetResponses.ServeAsync`, `AssetResponses.FileNameOf` (Task 7); `Asset` (Task 2).
- Produces:
  - `ContentOptions { string BaseUrl; string SigningKey }` bound from `Content`.
  - `enum SignatureCheck { Valid, Invalid, Expired }`.
  - `ContentUrlSigner` (singleton):
    - `string UrlFor(Guid projectId, string sha256)`.
    - `static long ExpiryAt(DateTimeOffset now)`.
    - `string Sign(Guid projectId, string sha256, long exp)`.
    - `SignatureCheck Verify(Guid projectId, string sha256, long exp, string? sig)`.
  - `ContentSetup.AddDdmContent(IServiceCollection, IConfiguration)` (also registers `TimeProvider.System`); `ContentSetup.EnsureContentConfigured(WebApplication)`; `ContentEndpoints.MapContent(WebApplication)` → anonymous `GET /content/{projectId:guid}/{sha}?exp=&sig=`.
  - `DdmApiFactory` gains an optional `settings` dictionary applied last; the test config supplies `Content:SigningKey`. `ApiTestBase.ProjectIdAsync(string slug) → Task<Guid>`.

- [ ] **Step 1: Give the test host a signing key and settings overrides**

Replace `tests/Ddm.Api.Tests/Infrastructure/DdmApiFactory.cs` with:
```csharp
using Ddm.Api.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ddm.Api.Tests.Infrastructure;

public sealed class DdmApiFactory(
    string connectionString, bool migrate = true, string environment = "Testing", IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    public const string ContentSigningKey = "test-content-signing-key-0123456789-abcdef";

    public InMemoryBlobStore Blobs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ddm"] = connectionString,
                ["Database:MigrateOnStart"] = migrate ? "true" : "false",
                ["Auth:DevSigningKey"] = TestAuth.SigningKey,
                ["Auth:Issuer"] = TestAuth.Issuer,
                ["Auth:Audience"] = TestAuth.Audience,
                ["Content:SigningKey"] = ContentSigningKey,
            });
            if (settings is not null) config.AddInMemoryCollection(settings);
        });

        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IBlobStore>();
            s.AddSingleton<IBlobStore>(Blobs);
        });
    }
}
```

Add to `tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs` (add `using Ddm.Api.Data;`, `using Microsoft.EntityFrameworkCore;`, `using Microsoft.Extensions.DependencyInjection;`):
```csharp
    /// <summary>Project ids are not part of the API surface, but signed content URLs are built from them.</summary>
    protected async Task<Guid> ProjectIdAsync(string slug)
    {
        using var scope = Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<DdmDbContext>().Projects.Where(p => p.Slug == slug).Select(p => p.Id).SingleAsync();
    }
```

- [ ] **Step 2: Write the failing tests**

`tests/Ddm.Api.Tests/ContentUrlSignerTests.cs`:
```csharp
using Ddm.Api.Assets;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Tests;

public class ContentUrlSignerTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly Guid Project = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly string Sha = new('a', 64);

    private static (ContentUrlSigner Signer, FixedTime Time) Create(DateTimeOffset now)
    {
        var time = new FixedTime(now);
        var options = Options.Create(new ContentOptions
        {
            BaseUrl = "https://content.example", SigningKey = "unit-test-content-signing-key-0123456789",
        });
        return (new ContentUrlSigner(options, time), time);
    }

    [Fact]
    public void Urls_are_stable_within_a_day_and_change_across_days()
    {
        var day = new DateTimeOffset(2026, 10, 3, 0, 0, 1, TimeSpan.Zero);
        var morning = Create(day).Signer.UrlFor(Project, Sha);
        Assert.Equal(morning, Create(day.AddHours(23)).Signer.UrlFor(Project, Sha));
        Assert.NotEqual(morning, Create(day.AddDays(1)).Signer.UrlFor(Project, Sha));
        Assert.StartsWith($"https://content.example/content/{Project:N}/{Sha}?exp=", morning);
    }

    [Fact]
    public void Expiry_is_between_24_and_48_hours_away()
    {
        var now = new DateTimeOffset(2026, 10, 3, 13, 30, 0, TimeSpan.Zero);
        var exp = DateTimeOffset.FromUnixTimeSeconds(ContentUrlSigner.ExpiryAt(now));
        Assert.InRange(exp - now, TimeSpan.FromHours(24), TimeSpan.FromHours(48));
    }

    [Fact]
    public void A_valid_signature_verifies_until_it_expires()
    {
        var (signer, time) = Create(DateTimeOffset.UtcNow);
        var exp = ContentUrlSigner.ExpiryAt(time.Now);
        var sig = signer.Sign(Project, Sha, exp);
        Assert.Equal(SignatureCheck.Valid, signer.Verify(Project, Sha, exp, sig));
        time.Now = DateTimeOffset.FromUnixTimeSeconds(exp);
        Assert.Equal(SignatureCheck.Expired, signer.Verify(Project, Sha, exp, sig));
    }

    [Fact]
    public void Tampering_with_any_part_invalidates_the_signature()
    {
        var (signer, time) = Create(DateTimeOffset.UtcNow);
        var exp = ContentUrlSigner.ExpiryAt(time.Now);
        var sig = signer.Sign(Project, Sha, exp);
        Assert.Equal(SignatureCheck.Invalid, signer.Verify(Guid.NewGuid(), Sha, exp, sig));
        Assert.Equal(SignatureCheck.Invalid, signer.Verify(Project, new string('b', 64), exp, sig));
        Assert.Equal(SignatureCheck.Invalid, signer.Verify(Project, Sha, exp + 86_400, sig));
        Assert.Equal(SignatureCheck.Invalid, signer.Verify(Project, Sha, exp, sig[..^1] + (sig[^1] == 'A' ? 'B' : 'A')));
        Assert.Equal(SignatureCheck.Invalid, signer.Verify(Project, Sha, exp, null));
    }
}
```

`tests/Ddm.Api.Tests/ContentEndpointTests.cs`:
```csharp
using Ddm.Api.Assets;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class ContentEndpointTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private ContentUrlSigner Signer => Factory.Services.GetRequiredService<ContentUrlSigner>();

    private async Task<(HttpClient Alice, Guid ProjectId)> WithAssetAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await PutAssetAsync(alice, "p", "images/a.png", PngBytes);
        return (alice, await ProjectIdAsync("p"));
    }

    [Fact]
    public async Task A_signed_link_serves_the_asset_without_credentials()
    {
        var (_, pid) = await WithAssetAsync();
        var url = Signer.UrlFor(pid, Sha(PngBytes));
        Assert.StartsWith("/content/", url); // Testing has no Content:BaseUrl, so links are same-origin
        var r = await Anonymous().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(PngBytes, await r.Content.ReadAsByteArrayAsync());
        Assert.True(r.Headers.CacheControl!.Private);
        Assert.InRange(r.Headers.CacheControl.MaxAge!.Value, TimeSpan.FromHours(23), TimeSpan.FromHours(48));
        Assert.Equal(AssetResponses.Csp, r.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task A_tampered_link_is_403_invalid_signature()
    {
        var (_, pid) = await WithAssetAsync();
        var url = Signer.UrlFor(pid, Sha(PngBytes)) + "x";
        var r = await Anonymous().GetAsync(url);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("invalid_signature", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task An_expired_link_is_403_signature_expired()
    {
        var (_, pid) = await WithAssetAsync();
        var sha = Sha(PngBytes);
        var past = DateTimeOffset.UtcNow.AddDays(-3).ToUnixTimeSeconds();
        var r = await Anonymous().GetAsync($"/content/{pid:N}/{sha}?exp={past}&sig={Signer.Sign(pid, sha, past)}");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("signature_expired", await ProblemCodeAsync(r));
    }

    [Fact]
    public async Task A_valid_signature_cannot_reach_another_projects_content()
    {
        var (alice, _) = await WithAssetAsync();
        await CreateProjectAsync(alice, "q");
        var r = await Anonymous().GetAsync(Signer.UrlFor(await ProjectIdAsync("q"), Sha(PngBytes)));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Tombstoned_assets_are_still_served_to_old_links()
    {
        var (alice, pid) = await WithAssetAsync();
        await alice.DeleteAsync("/api/v1/projects/p/assets/images/a.png");
        Assert.Equal(HttpStatusCode.OK, (await Anonymous().GetAsync(Signer.UrlFor(pid, Sha(PngBytes)))).StatusCode);
    }

    [Fact]
    public async Task The_app_refuses_to_start_without_a_content_signing_key()
    {
        await using var factory = new DdmApiFactory("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p", migrate: false,
            settings: new Dictionary<string, string?> { ["Content:SigningKey"] = "" });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Content:SigningKey", ex.ToString());
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ContentUrlSignerTests|FullyQualifiedName~ContentEndpointTests"`
Expected: build FAILS (`ContentUrlSigner`, `ContentOptions` do not exist).

- [ ] **Step 4: Implement**

`src/Ddm.Api/Assets/ContentOptions.cs`:
```csharp
namespace Ddm.Api.Assets;

public sealed class ContentOptions
{
    /// <summary>Origin that serves /content, separate from the app in production. Empty means same-origin relative links.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>HMAC key for content links; at least 32 characters.</summary>
    public string SigningKey { get; set; } = "";
}
```

`src/Ddm.Api/Assets/ContentUrlSigner.cs`:
```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Assets;

public enum SignatureCheck { Valid, Invalid, Expired }

/// <summary>
/// Signs /content links so browsers can load images without a bearer token. Expiry is bucketed to 24-hour UTC
/// windows (the end of today plus one day), so a link is stable, and so cacheable, for a day and lives at most 48 hours.
/// </summary>
public sealed class ContentUrlSigner(IOptions<ContentOptions> options, TimeProvider time)
{
    private const long Day = 86_400;

    public string UrlFor(Guid projectId, string sha256)
    {
        var exp = ExpiryAt(time.GetUtcNow());
        return $"{options.Value.BaseUrl.TrimEnd('/')}/content/{projectId:N}/{sha256}?exp={exp}&sig={Sign(projectId, sha256, exp)}";
    }

    public static long ExpiryAt(DateTimeOffset now) => (now.ToUnixTimeSeconds() / Day + 2) * Day;

    public string Sign(Guid projectId, string sha256, long exp)
    {
        var mac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(options.Value.SigningKey), Encoding.UTF8.GetBytes($"{projectId:N}/{sha256}/{exp}"));
        return Base64Url.EncodeToString(mac);
    }

    public SignatureCheck Verify(Guid projectId, string sha256, long exp, string? sig)
    {
        if (string.IsNullOrEmpty(sig)) return SignatureCheck.Invalid;
        var expected = Encoding.UTF8.GetBytes(Sign(projectId, sha256, exp));
        if (!CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(sig))) return SignatureCheck.Invalid;
        return time.GetUtcNow().ToUnixTimeSeconds() >= exp ? SignatureCheck.Expired : SignatureCheck.Valid;
    }
}
```

`src/Ddm.Api/Assets/ContentEndpoints.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Assets;

public static class ContentEndpoints
{
    /// <summary>Anonymous: the signature is the capability. Outside /api/v1 so it can live on a separate content origin.</summary>
    public static void MapContent(this WebApplication app) =>
        app.MapGet("/content/{projectId:guid}/{sha}", ServeAsync).AllowAnonymous();

    private static async Task<IResult> ServeAsync(
        Guid projectId, string sha, long? exp, string? sig, HttpContext http,
        ContentUrlSigner signer, TimeProvider time, DdmDbContext db, IBlobStore blobs, CancellationToken ct)
    {
        var expiry = exp ?? 0;
        switch (signer.Verify(projectId, sha, expiry, sig))
        {
            case SignatureCheck.Invalid: throw ApiException.Forbidden("invalid_signature", "The content link is not valid");
            case SignatureCheck.Expired: throw ApiException.Forbidden("signature_expired", "The content link has expired");
        }

        // Tombstoned assets still count: a page rendered before the delete may still show the image.
        var asset = await db.Assets.AsNoTracking()
                        .Where(a => a.ProjectId == projectId && a.Sha256 == sha)
                        .OrderBy(a => a.Path)
                        .FirstOrDefaultAsync(ct)
                    ?? throw ApiException.NotFound("not_found", "Content not found");
        var maxAge = Math.Max(0, expiry - time.GetUtcNow().ToUnixTimeSeconds());
        return await AssetResponses.ServeAsync(http, blobs, asset.StorageKey, asset.ContentType, asset.Size, asset.Sha256,
            AssetResponses.FileNameOf(asset.Path), $"private, max-age={maxAge}", ct);
    }
}
```

`src/Ddm.Api/Assets/ContentSetup.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Assets;

public static class ContentSetup
{
    public static IServiceCollection AddDdmContent(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ContentOptions>(config.GetSection("Content"));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ContentUrlSigner>();
        return services;
    }

    /// <summary>Fail at startup, not on the first page render, if content links cannot be issued safely.</summary>
    public static void EnsureContentConfigured(this WebApplication app)
    {
        var o = app.Services.GetRequiredService<IOptions<ContentOptions>>().Value;
        if (o.SigningKey.Length < 32)
            throw new InvalidOperationException("Content:SigningKey must be set to at least 32 characters.");
        if (string.IsNullOrEmpty(o.BaseUrl) && !(app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")))
            throw new InvalidOperationException("Content:BaseUrl must name a separate origin for user content outside Development.");
    }
}
```

In `src/Ddm.Api/Program.cs`:
- `builder.Services.AddDdmContent(builder.Configuration);` after the `AddDdmAssets` line;
- `app.EnsureContentConfigured();` immediately **after** `app.EnsureAuthConfigured();` (so the existing Production test still fails on `Auth:Authority` first);
- `app.MapContent();` after `app.MapPrometheusScrapingEndpoint();`.

In `src/Ddm.Api/appsettings.Development.json`, add `"Content": { "SigningKey": "dev-only-content-signing-key-change-me-0123456789" }`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~Content|FullyQualifiedName~AuthTests|FullyQualifiedName~HealthTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Ddm.Api tests/Ddm.Api.Tests
git commit -m "feat: HMAC-signed content links served anonymously from /content" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Images in rendered markdown

**Files:**
- Create: `src/Ddm.Api/Assets/AssetResolver.cs`
- Modify: `src/Ddm.Api/Common/ContentPath.cs`, `src/Ddm.Api/Documents/MarkdownRenderer.cs`, `src/Ddm.Api/Documents/DocumentEndpoints.cs`, `src/Ddm.Api/Assets/AssetSetup.cs`
- Test: `tests/Ddm.Api.Tests/ContentPathTests.cs`, `tests/Ddm.Api.Tests/MarkdownRendererTests.cs`, `tests/Ddm.Api.Tests/DocumentImageTests.cs`

**Interfaces:**
- Consumes: `ContentUrlSigner.UrlFor` (Task 8); `AssetPath.IsValid` (Task 6); `DocumentEndpoints.RespondAsync` (Task 4).
- Produces:
  - `ContentPath.Resolve(string fromPath, string reference) → string?`.
  - `MarkdownRenderer.MissingAssetClass = "ddm-missing-asset"`; `MarkdownRenderer.ImagePaths(string markdown, string documentPath) → IReadOnlySet<string>`; `MarkdownRenderer.ToHtml(string markdown, string documentPath, IReadOnlyDictionary<string, string> assetUrls) → string`. `ToHtml(string)` is unchanged.
  - `AssetResolver.UrlsForAsync(Project, IReadOnlySet<string> paths, CancellationToken) → Task<IReadOnlyDictionary<string, string>>` (scoped).
  - `RespondAsync` gains `Project project` and `AssetResolver assets` parameters.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Ddm.Api.Tests/ContentPathTests.cs`:
```csharp
    [Theory]
    [InlineData("guides/setup.md", "img/a.png", "guides/img/a.png")]
    [InlineData("guides/setup.md", "../images/a.png", "images/a.png")]
    [InlineData("guides/setup.md", "./a.png", "guides/a.png")]
    [InlineData("a/b/c.md", "../../x.png", "x.png")]
    [InlineData("index.md", "images/a%2Db.png", "images/a-b.png")]
    public void Resolves_relative_references(string from, string reference, string expected) =>
        Assert.Equal(expected, ContentPath.Resolve(from, reference));

    [Theory]
    [InlineData("index.md", "../escape.png")] [InlineData("a.md", "/abs.png")] [InlineData("a.md", "https://x/y.png")]
    [InlineData("a.md", "//host/y.png")] [InlineData("a.md", "a.png?x=1")] [InlineData("a.md", "a.png#top")]
    [InlineData("a.md", "data:image/png;base64,AA")] [InlineData("a.md", "")]
    public void Refuses_anything_that_is_not_a_plain_relative_path(string from, string reference) =>
        Assert.Null(ContentPath.Resolve(from, reference));
```

Append to `tests/Ddm.Api.Tests/MarkdownRendererTests.cs`:
```csharp
    [Fact]
    public void Image_paths_are_resolved_against_the_document_folder()
    {
        var paths = Renderer.ImagePaths(
            "![a](img/a.png) ![b](../logo.svg) ![c](https://x.example/c.png) ![d](../../escape.png) ![e](notes.md)", "guides/setup.md");
        Assert.True(paths.SetEquals(["guides/img/a.png", "logo.svg"]));
    }

    [Fact]
    public void Resolved_images_get_their_urls_and_missing_ones_are_marked()
    {
        var html = Renderer.ToHtml("![a](img/a.png) ![b](img/missing.png) ![c](https://x.example/c.png)", "guides/setup.md",
            new Dictionary<string, string> { ["guides/img/a.png"] = "https://content.example/content/p/abc?exp=1&sig=s" });
        Assert.Contains("src=\"https://content.example/content/p/abc?exp=1&amp;sig=s\"", html);
        Assert.Contains("class=\"ddm-missing-asset\"", html);
        Assert.Contains("src=\"https://x.example/c.png\"", html);
    }

    [Fact]
    public void Resolution_does_not_weaken_sanitizing()
    {
        var html = Renderer.ToHtml("![x](data:image/svg+xml;base64,PHN2Zz4=) <script>alert(1)</script>", "a.md",
            new Dictionary<string, string>());
        Assert.DoesNotContain("data:", html);
        Assert.DoesNotContain("<script", html);
    }
```

`tests/Ddm.Api.Tests/DocumentImageTests.cs`:
```csharp
using System.Text.RegularExpressions;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class DocumentImageTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private static string ImageSrc(string html) =>
        WebUtility.HtmlDecode(Regex.Match(html, "src=\"(/content/[^\"]+)\"").Groups[1].Value);

    private static async Task<string> HtmlAsync(HttpClient c, string path) =>
        await (await GetDocAsync(c, "p", path, "text/html")).Content.ReadAsStringAsync();

    // Review Focus 5
    [Fact]
    public async Task Rendered_images_point_at_signed_links_that_load_without_credentials()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await PutAssetAsync(alice, "p", "images/flow.png", PngBytes);
        await PutDocAsync(alice, "p", "guides/deep/setup.md", "# Setup\n\n![flow](../../images/flow.png)\n\n![gone](missing.png)\n");

        var html = await HtmlAsync(alice, "guides/deep/setup.md");
        var src = ImageSrc(html);
        Assert.NotEmpty(src);
        var image = await Anonymous().GetAsync(src);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(PngBytes, await image.Content.ReadAsByteArrayAsync());
        Assert.Contains("ddm-missing-asset", html);
    }

    // Review Focus 5
    [Fact]
    public async Task A_replaced_image_gets_a_new_url()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        await PutAssetAsync(alice, "p", "a.png", PngBytes);
        await PutDocAsync(alice, "p", "index.md", "![a](a.png)");
        var before = ImageSrc(await HtmlAsync(alice, "index.md"));

        await PutAssetAsync(alice, "p", "a.png", PngVariant(1), ifMatch: $"\"{Sha(PngBytes)}\"");
        var after = ImageSrc(await HtmlAsync(alice, "index.md"));
        Assert.NotEqual(before, after);
        Assert.Equal(PngVariant(1), await (await Anonymous().GetAsync(after)).Content.ReadAsByteArrayAsync());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ContentPathTests|FullyQualifiedName~MarkdownRendererTests|FullyQualifiedName~DocumentImageTests"`
Expected: build FAILS (`ContentPath.Resolve`, `ImagePaths` do not exist).

- [ ] **Step 3: Implement relative path resolution**

Add to `src/Ddm.Api/Common/ContentPath.cs`:
```csharp
    /// <summary>
    /// Resolves a relative reference (such as an image URL in markdown) against the folder of <paramref name="fromPath"/>.
    /// Returns null for anything that is not a plain relative path: a scheme, a leading '/', a query or fragment,
    /// a backslash, or '..' climbing above the project root. The caller still validates the result.
    /// </summary>
    public static string? Resolve(string fromPath, string reference)
    {
        if (reference.Length == 0 || reference.StartsWith('/') || reference.IndexOfAny([':', '?', '#', '\\']) >= 0) return null;
        var segments = new List<string>(fromPath.Split('/')[..^1]);
        foreach (var part in Uri.UnescapeDataString(reference).Split('/'))
        {
            switch (part)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (segments.Count == 0) return null;
                    segments.RemoveAt(segments.Count - 1);
                    break;
                default:
                    segments.Add(part);
                    break;
            }
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }
```

- [ ] **Step 4: Teach the renderer to resolve images**

Replace `src/Ddm.Api/Documents/MarkdownRenderer.cs` with:
```csharp
using Ddm.Api.Assets;
using Ddm.Api.Common;
using Ganss.Xss;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Ddm.Api.Documents;

public sealed class MarkdownRenderer
{
    public const string MissingAssetClass = "ddm-missing-asset";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseAlertBlocks()
        .Build();

    /// <summary>Renders markdown to HTML, then sanitizes against an allow-list. Raw HTML in the source is untrusted.</summary>
    public string ToHtml(string markdown) => Sanitize(Markdown.ToHtml(markdown, Pipeline));

    /// <summary>Project asset paths that <paramref name="markdown"/> uses as images, resolved against the document's folder.</summary>
    public IReadOnlySet<string> ImagePaths(string markdown, string documentPath) =>
        Images(Markdown.Parse(markdown, Pipeline))
            .Select(image => ResolveAsset(documentPath, image.Url))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Renders with relative images pointed at <paramref name="assetUrls"/> (keyed by resolved asset path).
    /// A relative image with no URL keeps its src and gains <see cref="MissingAssetClass"/>; absolute URLs are untouched.
    /// </summary>
    public string ToHtml(string markdown, string documentPath, IReadOnlyDictionary<string, string> assetUrls)
    {
        var doc = Markdown.Parse(markdown, Pipeline);
        foreach (var image in Images(doc))
        {
            if (!IsRelative(image.Url)) continue;
            if (ResolveAsset(documentPath, image.Url) is { } path && assetUrls.TryGetValue(path, out var url)) image.Url = url;
            else image.GetAttributes().AddClass(MissingAssetClass);
        }
        return Sanitize(doc.ToHtml(Pipeline));
    }

    private static List<LinkInline> Images(MarkdownDocument doc) =>
        doc.Descendants<LinkInline>().Where(l => l.IsImage && !string.IsNullOrEmpty(l.Url)).ToList();

    private static bool IsRelative(string? url) => url is { Length: > 0 } && !url.StartsWith('/') && !url.Contains(':');

    private static string? ResolveAsset(string documentPath, string? url) =>
        url is not null && ContentPath.Resolve(documentPath, url) is { } path && AssetPath.IsValid(path) ? path : null;

    private static string Sanitize(string html)
    {
        var sanitizer = new HtmlSanitizer(); // a fresh instance per call: sanitizers are not safe to share once configured
        sanitizer.AllowedAttributes.Add("class");
        sanitizer.AllowedAttributes.Add("id");
        return sanitizer.Sanitize(html);
    }
}
```

`src/Ddm.Api/Assets/AssetResolver.cs`:
```csharp
using Ddm.Api.Data;
using Ddm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Assets;

/// <summary>Maps the asset paths a page references to signed content URLs, in one query.</summary>
public sealed class AssetResolver(DdmDbContext db, ContentUrlSigner signer)
{
    public async Task<IReadOnlyDictionary<string, string>> UrlsForAsync(Project project, IReadOnlySet<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0) return new Dictionary<string, string>();
        var wanted = paths.ToList();
        var rows = await db.Assets.AsNoTracking()
            .Where(a => a.ProjectId == project.Id && a.DeletedAt == null && wanted.Contains(a.Path))
            .Select(a => new { a.Path, a.Sha256 })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Path, r => signer.UrlFor(project.Id, r.Sha256), StringComparer.Ordinal);
    }
}
```

In `src/Ddm.Api/Assets/AssetSetup.cs`, add `services.AddScoped<AssetResolver>();`.

- [ ] **Step 5: Use it when serving HTML**

In `src/Ddm.Api/Documents/DocumentEndpoints.cs` (add `using Ddm.Api.Assets;`):
- `GetAsync` gains an `AssetResolver assets` parameter and passes `access.Project, assets` to both `RespondAsync` calls:
  `return await RespondAsync(http, docs, renderer, tags, access.Project, assets, path, version, ct);`
- `RespondAsync` becomes:
```csharp
    internal static async Task<IResult> RespondAsync(
        HttpContext http, DocumentService docs, MarkdownRenderer renderer, TagService tags, Project project, AssetResolver assets,
        string path, ContentVersion version, CancellationToken ct)
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
            DocFormat.Html => Results.Text(renderer.ToHtml(parsed.Body, path,
                await assets.UrlsForAsync(project, renderer.ImagePaths(parsed.Body, path), ct)), "text/html; charset=utf-8"),
            DocFormat.Json => Results.Ok(DocumentDto.From(path, parsed, version,
                await tags.TagsForAsync(new ItemRef(ItemType.Document, version.ItemId), ct))),
            _ => Results.Text(text, "text/markdown; charset=utf-8"),
        };
    }
```
An older version renders against the **current** assets (assets are not versioned).

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ContentPathTests|FullyQualifiedName~MarkdownRendererTests|FullyQualifiedName~DocumentImageTests|FullyQualifiedName~DocumentWriteReadTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Ddm.Api tests/Ddm.Api.Tests
git commit -m "feat: resolve relative images in rendered markdown to signed content links" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: OpenAPI spec validation

**Files:**
- Create: `src/Ddm.Api/Specs/SpecName.cs`, `src/Ddm.Api/Specs/PointerLocator.cs`, `src/Ddm.Api/Specs/SpecValidator.cs`
- Modify: `src/Ddm.Api/Ddm.Api.csproj` (two packages)
- Test: `tests/Ddm.Api.Tests/SpecNameTests.cs`, `tests/Ddm.Api.Tests/PointerLocatorTests.cs`, `tests/Ddm.Api.Tests/SpecValidatorTests.cs`

**Interfaces:**
- Consumes: `SafeYaml.Check` (Task 1); `ApiException.Unprocessable` (Task 1).
- Produces (namespace `Ddm.Api.Specs`):
  - `SpecName.IsValid(string?) → bool`, `SpecName.Require(string?) → string` (400 `invalid_spec_name`).
  - `PointerLocator.PointerFrom(string? pointer, string message) → string?`; `PointerLocator.Locate(YamlNode root, string? pointer) → (int Line, int Column)?`.
  - `record SpecError(string? Pointer, int? Line, int? Column, string Message)`.
  - `record SpecOperation(string Method, string Path, string? OperationId, string? Summary, IReadOnlyList<string> Tags)`.
  - `record ValidatedSpec(string Format, string OpenApiVersion, string Title, string ApiVersion, IReadOnlyList<SpecOperation> Operations, byte[] NormalizedJson)`.
  - `SpecValidator.MaxDepth = 64`; `SpecValidator.ValidateAsync(byte[] bytes, CancellationToken) → Task<ValidatedSpec>`: throws 400 `invalid_encoding`, 422 `unsupported_openapi_version` or 422 `invalid_spec`, the last two with `errors: SpecError[]`.

- [ ] **Step 1: Add the packages**

Run:
```bash
dotnet add src/Ddm.Api package Microsoft.OpenApi --version 2.12.0
dotnet add src/Ddm.Api package Microsoft.OpenApi.YamlReader --version 2.12.0
dotnet list src/Ddm.Api package --include-transitive | grep -i openapi
```
Expected: `Microsoft.OpenApi` and `Microsoft.OpenApi.YamlReader` both at 2.12.0, matching what `Microsoft.AspNetCore.OpenApi` resolves. Do not take 3.x.

- [ ] **Step 2: Write the failing tests**

`tests/Ddm.Api.Tests/SpecNameTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Specs;

namespace Ddm.Api.Tests;

public class SpecNameTests
{
    [Theory] [InlineData("pets")] [InlineData("payments.v2")] [InlineData("a_b-c")] [InlineData("0")]
    public void Valid(string name) => Assert.Equal(name, SpecName.Require(name));

    [Theory] [InlineData("")] [InlineData("Pets")] [InlineData("-x")] [InlineData(".x")] [InlineData("a/b")] [InlineData(null)]
    public void Invalid(string? name) => Assert.Equal("invalid_spec_name", Assert.Throws<ApiException>(() => SpecName.Require(name)).Code);

    [Fact] public void At_most_100_characters() => Assert.False(SpecName.IsValid(new string('a', 101)));
}
```

`tests/Ddm.Api.Tests/PointerLocatorTests.cs`:
```csharp
using Ddm.Api.Specs;
using YamlDotNet.RepresentationModel;

namespace Ddm.Api.Tests;

public class PointerLocatorTests
{
    private static YamlNode Load(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream.Documents[0].RootNode;
    }

    [Fact]
    public void Locates_a_nested_mapping_value()
    {
        var root = Load("openapi: 3.0.3\npaths:\n  /pets:\n    get:\n      responses: {}\n");
        Assert.Equal((5, 18), PointerLocator.Locate(root, "#/paths/~1pets/get/responses"));
    }

    [Fact]
    public void Locates_sequence_items_in_json()
    {
        var root = Load("{\n  \"tags\": [\n    {\"name\": \"a\"},\n    {\"name\": \"b\"}\n  ]\n}");
        Assert.Equal(4, PointerLocator.Locate(root, "/tags/1")!.Value.Line);
    }

    [Fact]
    public void A_missing_segment_falls_back_to_the_deepest_existing_node()
    {
        var root = Load("info:\n  version: '1'\n");
        Assert.Equal(2, PointerLocator.Locate(root, "#/info/title")!.Value.Line);
    }

    [Fact] public void Unknown_pointers_are_null() => Assert.Null(PointerLocator.Locate(Load("a: 1\n"), "#/zzz"));

    [Fact]
    public void Pointers_quoted_in_messages_are_extracted() =>
        Assert.Equal("#/paths", PointerLocator.PointerFrom("", "nope is not a valid property at #/paths"));

    [Fact] public void A_given_pointer_wins() => Assert.Equal("#/a", PointerLocator.PointerFrom("#/a", "at #/b"));
}
```

`tests/Ddm.Api.Tests/SpecValidatorTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Specs;

namespace Ddm.Api.Tests;

public class SpecValidatorTests
{
    private const string Pets = """
        openapi: 3.0.3
        info:
          title: Pets
          version: "1.2"
        paths:
          /pets:
            get:
              operationId: listPets
              summary: List pets
              tags: [pets]
              responses:
                '200':
                  description: ok
        """;

    private static Task<ValidatedSpec> Validate(string text) => SpecValidator.ValidateAsync(Encoding.UTF8.GetBytes(text), default);

    private static async Task<(string Code, JsonElement Errors)> FailAsync(string text)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Validate(text));
        var errors = JsonSerializer.SerializeToElement(ex.Extensions?["errors"], new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (ex.Code, errors);
    }

    [Fact]
    public async Task A_valid_yaml_spec_yields_its_summary_and_normalised_json()
    {
        var spec = await Validate(Pets);
        Assert.Equal(("yaml", "3.0.3", "Pets", "1.2"), (spec.Format, spec.OpenApiVersion, spec.Title, spec.ApiVersion));
        var op = Assert.Single(spec.Operations);
        Assert.Equal(("get", "/pets", "listPets", "List pets"), (op.Method, op.Path, op.OperationId, op.Summary));
        Assert.Equal(["pets"], op.Tags);
        using var json = JsonDocument.Parse(spec.NormalizedJson);
        Assert.Equal("Pets", json.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_json_3_1_spec_is_accepted_and_stays_3_1()
    {
        var spec = await Validate("{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"J\",\"version\":\"1\"},\"paths\":{}}");
        Assert.Equal("json", spec.Format);
        using var json = JsonDocument.Parse(spec.NormalizedJson);
        Assert.StartsWith("3.1", json.RootElement.GetProperty("openapi").GetString());
    }

    [Fact]
    public async Task Swagger_2_is_unsupported()
    {
        var (code, errors) = await FailAsync("swagger: '2.0'\ninfo: {title: x, version: '1'}\npaths: {}\n");
        Assert.Equal("unsupported_openapi_version", code);
        Assert.Equal(1, errors.GetArrayLength());
    }

    // Spec review focus 9
    [Fact]
    public async Task Every_error_is_reported_with_its_line()
    {
        var (code, errors) = await FailAsync("""
            openapi: 3.0.3
            info:
              version: "1"
            paths:
              /a:
                get:
                  responses: {}
            """);
        Assert.Equal("invalid_spec", code);
        Assert.True(errors.GetArrayLength() >= 2);
        var responses = errors.EnumerateArray().Single(e => e.GetProperty("pointer").GetString() == "#/paths/~1a/get/responses");
        Assert.Equal(7, responses.GetProperty("line").GetInt32());
    }

    // Spec review focus 9
    [Fact]
    public async Task Errors_in_json_specs_carry_lines_too()
    {
        var (_, errors) = await FailAsync(
            "{\n  \"openapi\": \"3.0.3\",\n  \"info\": {\"title\": \"t\", \"version\": \"1\"},\n  \"paths\": {\n    \"/a\": {\"get\": {\"responses\": {}}}\n  }\n}");
        Assert.Equal(5, errors[0].GetProperty("line").GetInt32());
    }

    // Spec review focus 4
    [Fact]
    public async Task External_refs_are_refused_at_their_location()
    {
        var (code, errors) = await FailAsync("""
            openapi: 3.0.3
            info: {title: t, version: "1"}
            paths:
              /a:
                get:
                  responses:
                    '200':
                      description: ok
                      content:
                        application/json:
                          schema:
                            $ref: 'https://evil.example/pet.yaml'
            """);
        Assert.Equal("invalid_spec", code);
        var e = Assert.Single(errors.EnumerateArray());
        Assert.Equal(12, e.GetProperty("line").GetInt32());
        Assert.Contains("evil.example", e.GetProperty("message").GetString());
    }

    // Spec review focus 4
    [Fact]
    public async Task Yaml_alias_bombs_are_refused_before_parsing()
    {
        var (code, errors) = await FailAsync("openapi: 3.0.3\na: &a [x, x]\nb: &b [*a, *a]\nc: [*b, *b]\n");
        Assert.Equal("invalid_spec", code);
        Assert.Contains("aliases", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Yaml_syntax_errors_report_a_line()
    {
        var (code, errors) = await FailAsync("openapi: 3.0.3\ninfo: {title: t, version: '1'\npaths: {}\n");
        Assert.Equal("invalid_spec", code);
        Assert.True(errors[0].GetProperty("line").GetInt32() >= 2);
    }

    [Fact] public async Task A_yaml_list_is_not_a_spec() => Assert.Equal("invalid_spec", (await FailAsync("- a\n- b\n")).Code);

    [Fact]
    public async Task Non_utf8_is_a_400() =>
        Assert.Equal("invalid_encoding", (await Assert.ThrowsAsync<ApiException>(() => SpecValidator.ValidateAsync([0xC3, 0x28], default))).Code);
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SpecNameTests|FullyQualifiedName~PointerLocatorTests|FullyQualifiedName~SpecValidatorTests"`
Expected: build FAILS (`Ddm.Api.Specs` does not exist).

- [ ] **Step 4: Implement names and pointer location**

`src/Ddm.Api/Specs/SpecName.cs`:
```csharp
using System.Text.RegularExpressions;
using Ddm.Api.Common;

namespace Ddm.Api.Specs;

public static partial class SpecName
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,99}\z")]
    private static partial Regex Valid();

    public static bool IsValid(string? name) => name is not null && Valid().IsMatch(name);

    public static string Require(string? name) =>
        IsValid(name)
            ? name!
            : throw ApiException.BadRequest("invalid_spec_name", "Invalid spec name",
                "Spec names are 1-100 characters: lowercase letters, digits, '.', '_' and '-', starting with a letter or digit");
}
```

`src/Ddm.Api/Specs/PointerLocator.cs`:
```csharp
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Ddm.Api.Specs;

/// <summary>Maps a JSON pointer, as OpenAPI diagnostics report them, to a line and column in the source text.</summary>
public static partial class PointerLocator
{
    [GeneratedRegex(@"#/\S*")]
    private static partial Regex PointerInText();

    /// <summary>The pointer itself, or one quoted in the message when the library leaves the pointer empty.</summary>
    public static string? PointerFrom(string? pointer, string message)
    {
        if (!string.IsNullOrEmpty(pointer)) return pointer;
        var m = PointerInText().Match(message);
        return m.Success ? m.Value : null;
    }

    /// <summary>The 1-based position of the deepest node the pointer reaches, or null if not even its first segment exists.</summary>
    public static (int Line, int Column)? Locate(YamlNode root, string? pointer)
    {
        if (pointer is null) return null;
        var p = pointer.StartsWith('#') ? pointer[1..] : pointer;
        if (!p.StartsWith('/')) return null;

        var node = root;
        var found = false;
        foreach (var raw in p[1..].Split('/'))
        {
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            YamlNode? next = node switch
            {
                YamlMappingNode map => map.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode s && s.Value == segment).Value,
                YamlSequenceNode seq when int.TryParse(segment, out var i) && i >= 0 && i < seq.Children.Count => seq.Children[i],
                _ => null,
            };
            if (next is null) break;
            node = next;
            found = true;
        }
        return found ? ((int)node.Start.Line, (int)node.Start.Column) : null;
    }
}
```

- [ ] **Step 5: Implement the validator**

`src/Ddm.Api/Specs/SpecValidator.cs`:
```csharp
using System.Text;
using System.Text.RegularExpressions;
using Ddm.Api.Common;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Microsoft.OpenApi.YamlReader;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Ddm.Api.Specs;

public sealed record SpecError(string? Pointer, int? Line, int? Column, string Message);
public sealed record SpecOperation(string Method, string Path, string? OperationId, string? Summary, IReadOnlyList<string> Tags);
public sealed record ValidatedSpec(
    string Format, string OpenApiVersion, string Title, string ApiVersion, IReadOnlyList<SpecOperation> Operations, byte[] NormalizedJson);

/// <summary>
/// Validates an OpenAPI 3.0/3.1 document. Our own checks run first (UTF-8, alias and depth guard, version gate,
/// no external $ref) so the library never sees hostile input; then Microsoft.OpenApi parses and validates it.
/// Every problem is reported at once, each with the line and column it points at.
/// </summary>
public static partial class SpecValidator
{
    public const int MaxDepth = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [GeneratedRegex(@"^3\.[01]\.\d+\z")]
    private static partial Regex SupportedVersion();

    public static string FormatOf(string text) => text.TrimStart('﻿', ' ', '\t', '\r', '\n').StartsWith('{') ? "json" : "yaml";

    public static async Task<ValidatedSpec> ValidateAsync(byte[] bytes, CancellationToken ct)
    {
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { throw ApiException.BadRequest("invalid_encoding", "Specs must be valid UTF-8"); }
        var format = FormatOf(text);

        if (SafeYaml.Check(text, MaxDepth) is { } problem) throw Invalid([new(null, problem.Line, problem.Column, problem.Message)]);
        YamlNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count != 1) throw Invalid([new(null, null, null, "A spec must be a single YAML or JSON document")]);
            root = stream.Documents[0].RootNode;
        }
        catch (YamlException ex) { throw Invalid([new(null, (int)ex.Start.Line, (int)ex.Start.Column, ex.Message)]); }
        if (root is not YamlMappingNode map) throw Invalid([At(root, null, "A spec must be a YAML or JSON object")]);

        var versionNode = map.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode { Value: "openapi" }).Value as YamlScalarNode;
        if (versionNode?.Value is not { } version || !SupportedVersion().IsMatch(version))
            throw ApiException.Unprocessable("unsupported_openapi_version", "Only OpenAPI 3.0 and 3.1 are supported",
                [At(versionNode ?? root, "#/openapi", $"Expected openapi: 3.0.x or 3.1.x, found '{versionNode?.Value ?? "nothing"}'")]);

        var errors = ExternalRefs(root, "#").ToList();
        if (errors.Count > 0) throw Invalid(errors);

        var settings = new OpenApiReaderSettings(); // LoadExternalRefs defaults to false
        settings.AddYamlReader();
        OpenApiDocument? doc;
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var read = await OpenApiDocument.LoadAsync(ms, format, settings, ct);
            doc = read.Document;
            errors = (read.Diagnostic?.Errors ?? []).Select(e => ErrorAt(root, e.Pointer, e.Message)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ApiException)
        {
            throw Invalid([new(null, null, null, ex.Message)]);
        }
        if (errors.Count > 0) throw Invalid(errors);
        if (doc is null) throw Invalid([new(null, null, null, "The document could not be read as OpenAPI")]);

        return new(format, version, doc.Info?.Title ?? "", doc.Info?.Version ?? "", Operations(doc), Normalize(doc, version));
    }

    private static SpecError ErrorAt(YamlNode root, string? rawPointer, string message)
    {
        var pointer = PointerLocator.PointerFrom(rawPointer, message);
        var at = PointerLocator.Locate(root, pointer);
        return new(pointer, at?.Line, at?.Column, message);
    }

    /// <summary>Every $ref that does not start with '#'. Specs must be self-contained: resolving a URL or file is SSRF.</summary>
    private static IEnumerable<SpecError> ExternalRefs(YamlNode node, string pointer)
    {
        switch (node)
        {
            case YamlMappingNode map:
                foreach (var (key, value) in map.Children)
                {
                    var name = (key as YamlScalarNode)?.Value ?? "";
                    var child = $"{pointer}/{name.Replace("~", "~0").Replace("/", "~1")}";
                    if (name == "$ref" && value is YamlScalarNode { Value: { } target } && !target.StartsWith('#'))
                        yield return At(value, child, $"External $ref '{target}' is not allowed: specs must be self-contained");
                    else
                        foreach (var e in ExternalRefs(value, child)) yield return e;
                }
                break;
            case YamlSequenceNode seq:
                for (var i = 0; i < seq.Children.Count; i++)
                    foreach (var e in ExternalRefs(seq.Children[i], $"{pointer}/{i}")) yield return e;
                break;
        }
    }

    private static IReadOnlyList<SpecOperation> Operations(OpenApiDocument doc) =>
        (from path in doc.Paths ?? new OpenApiPaths()
         from op in path.Value.Operations ?? new()
         select new SpecOperation(
             op.Key.Method.ToLowerInvariant(), path.Key, op.Value.OperationId, op.Value.Summary,
             op.Value.Tags?.Select(t => t.Name ?? "").ToList() ?? [])).ToList();

    /// <summary>The parsed document re-serialised as JSON in its own OpenAPI version, with internal $refs kept (schemas may be recursive).</summary>
    private static byte[] Normalize(OpenApiDocument doc, string version)
    {
        using var writer = new StringWriter();
        var json = new OpenApiJsonWriter(writer);
        if (version.StartsWith("3.1", StringComparison.Ordinal)) doc.SerializeAsV31(json);
        else doc.SerializeAsV3(json);
        return Encoding.UTF8.GetBytes(writer.ToString());
    }

    private static SpecError At(YamlNode node, string? pointer, string message) =>
        new(pointer, (int)node.Start.Line, (int)node.Start.Column, message);

    private static ApiException Invalid(IReadOnlyList<SpecError> errors) =>
        ApiException.Unprocessable("invalid_spec", "The OpenAPI document is not valid", errors, $"{errors.Count} problem(s) found");
}
```
Library facts this relies on (verified against 2.12.0): YAML syntax errors in `LoadAsync` throw (our `SafeYaml` pre-check reports them first); validation problems come back in `Diagnostic.Errors` with a JSON pointer, which is sometimes empty with the pointer quoted in the message; external `$ref`s are not reported, hence our own scan; `OpenApiPathItem.Operations` is keyed by `System.Net.Http.HttpMethod`. If `LoadAsync` in your restore has no `CancellationToken` parameter, drop the `ct` argument.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SpecNameTests|FullyQualifiedName~PointerLocatorTests|FullyQualifiedName~SpecValidatorTests|FullyQualifiedName~OpenApiTests"`
Expected: PASS. `OpenApiTests` proves the API's own OpenAPI document still works with the pinned package.

- [ ] **Step 7: Commit**

```bash
git add src/Ddm.Api tests/Ddm.Api.Tests
git commit -m "feat: OpenAPI 3.x validation with located errors and no external refs" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: Spec storage and endpoints

**Files:**
- Create: `src/Ddm.Api/Specs/SpecOptions.cs`, `src/Ddm.Api/Specs/SpecService.cs`, `src/Ddm.Api/Specs/SpecDtos.cs`, `src/Ddm.Api/Specs/SpecEndpoints.cs`, `src/Ddm.Api/Specs/SpecSetup.cs`
- Modify: `src/Ddm.Api/Program.cs`, `tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs`
- Test: `tests/Ddm.Api.Tests/SpecTests.cs`

**Interfaces:**
- Consumes: `SpecValidator`, `SpecName`, `ValidatedSpec` (Task 10); `RequestBody` (Task 7); `TagService`, `TagEndpoints.ReadBodyAsync`, `TagName`, `TagsDto` (Task 4); `Preconditions.Parse`, `WritePrecondition`, `VersionDto`, `DocumentEndpoints.MessageFrom` (phase 1); `Spec`, `ContentVersion.NormalizedRef` (Task 2).
- Produces:
  - `record PreparedSpec(string Name, byte[] Bytes, string Sha256, ValidatedSpec Spec)` with `BlobKey(Guid)` and `NormalizedKey(Guid)`.
  - `record SpecState(Spec Spec, ContentVersion? Current)` with `IsLive`; `record SpecWriteResult(Spec Spec, ContentVersion Version, bool Created, bool Changed)`.
  - `SpecService` (scoped):
    - `long MaxBytes`.
    - `Task<PreparedSpec> PrepareAsync(string name, byte[] bytes, CancellationToken)`.
    - `Task UploadAsync(Project, PreparedSpec, CancellationToken)`.
    - `Task<SpecWriteResult> WriteAsync(Caller, Project, PreparedSpec, string? message, WritePrecondition, bool requireIfMatch, CancellationToken)`.
    - `SpecWriteResult Stage(Caller, Project, PreparedSpec, SpecState?, string? message)`.
    - `void StageDelete(Caller, Project, Spec)`.
    - `Task<SpecState?> FindAsync(Project, string name, CancellationToken)`.
    - `Task<Spec> RequireAsync(Project, string name, bool includeDeleted, CancellationToken)`.
    - `GetCurrentAsync`, `GetVersionAsync`, `ListVersionsAsync`, `ReadAsync(string key, CancellationToken)`, `RestoreAsync`, `DeleteAsync`.
    - `static string MediaTypeFor(string format)`.
  - `SpecDto(string Name, string Title, string ApiVersion, string OpenApiVersion, string Format, int Version, int OperationCount, IReadOnlyList<string> Tags, DateTimeOffset UpdatedAt)`; `CreateSpecRequest(string? Name, string? Content, string? Message)`.
  - Routes under `/projects/{slug}/specs` (spec section 4.1). `SpecSetup.AddDdmSpecs(IServiceCollection, IConfiguration)`.
  - Test helper `ApiTestBase.PutSpecAsync(HttpClient, string slug, string name, string content, string? ifMatch = null, string mediaType = "application/yaml")`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs`:
```csharp
    protected static Task<HttpResponseMessage> PutSpecAsync(
        HttpClient client, string slug, string name, string content, string? ifMatch = null, string mediaType = "application/yaml")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/projects/{slug}/specs/{name}")
        {
            Content = new StringContent(content, Encoding.UTF8, mediaType),
        };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request);
    }
```

`tests/Ddm.Api.Tests/SpecTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Specs;
using Ddm.Api.Tags;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class SpecTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private const string V1 = "openapi: 3.0.3\ninfo: {title: Pets, version: '1'}\npaths:\n  /pets:\n    get:\n      responses:\n        '200': {description: ok}\n";
    private static readonly string V2 = V1.Replace("version: '1'", "version: '2'");

    private async Task<HttpClient> AliceAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        return alice;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Put_creates_a_spec_and_get_returns_the_original_bytes()
    {
        var alice = await AliceAsync();
        var r = await PutSpecAsync(alice, "p", "pets", V1);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v1\"", r.Headers.ETag!.Tag);
        var dto = await ReadAsync<SpecDto>(r);
        Assert.Equal(("Pets", "1", "3.0.3", "yaml", 1, 1), (dto.Title, dto.ApiVersion, dto.OpenApiVersion, dto.Format, dto.Version, dto.OperationCount));

        var get = await alice.GetAsync("/api/v1/projects/p/specs/pets");
        Assert.Equal("application/yaml", get.Content.Headers.ContentType!.MediaType);
        Assert.Equal(V1, await get.Content.ReadAsStringAsync());
        Assert.Equal("\"v1\"", get.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Normalized_json_and_operations_are_served()
    {
        var alice = await AliceAsync();
        await PutSpecAsync(alice, "p", "pets", V1);
        var normalized = await JsonAsync(await alice.GetAsync("/api/v1/projects/p/specs/pets/normalized"));
        Assert.Equal("Pets", normalized.GetProperty("info").GetProperty("title").GetString());
        var ops = await JsonAsync(await alice.GetAsync("/api/v1/projects/p/specs/pets/operations"));
        Assert.Equal("get", ops[0].GetProperty("method").GetString());
        Assert.Equal("/pets", ops[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Invalid_specs_are_422_with_errors_and_nothing_is_stored()
    {
        var alice = await AliceAsync();
        var r = await PutSpecAsync(alice, "p", "pets", "openapi: 3.0.3\ninfo: {version: '1'}\npaths: {}\n");
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        var body = await JsonAsync(r);
        Assert.Equal("invalid_spec", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").GetArrayLength() > 0);
        Assert.Equal(0, Factory.Blobs.Count);
    }

    [Fact]
    public async Task Updates_need_if_match_and_identical_content_is_a_no_op()
    {
        var alice = await AliceAsync();
        await PutSpecAsync(alice, "p", "pets", V1);
        Assert.Equal((HttpStatusCode)428, (await PutSpecAsync(alice, "p", "pets", V2)).StatusCode);
        Assert.Equal("\"v1\"", (await PutSpecAsync(alice, "p", "pets", V1, ifMatch: "\"v1\"")).Headers.ETag!.Tag);
        var r = await PutSpecAsync(alice, "p", "pets", V2, ifMatch: "\"v1\"");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("\"v2\"", r.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutSpecAsync(alice, "p", "pets", V1, ifMatch: "\"v1\"")).StatusCode);
    }

    [Fact]
    public async Task History_old_versions_and_restore()
    {
        var alice = await AliceAsync();
        await PutSpecAsync(alice, "p", "pets", V1);
        await PutSpecAsync(alice, "p", "pets", V2, ifMatch: "\"v1\"");

        var history = await ReadAsync<Page<VersionDto>>(await alice.GetAsync("/api/v1/projects/p/specs/pets/versions"));
        Assert.Equal([2, 1], history.Items.Select(v => v.Number));
        Assert.Equal(V1, await (await alice.GetAsync("/api/v1/projects/p/specs/pets/versions/1")).Content.ReadAsStringAsync());
        var old = await JsonAsync(await alice.GetAsync("/api/v1/projects/p/specs/pets/versions/1/normalized"));
        Assert.Equal("1", old.GetProperty("info").GetProperty("version").GetString());

        var restored = await alice.PostAsync("/api/v1/projects/p/specs/pets/versions/1/restore", null);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal("\"v3\"", restored.Headers.ETag!.Tag);
        Assert.Equal(V1, await (await alice.GetAsync("/api/v1/projects/p/specs/pets")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Delete_tombstones_and_a_put_revives_with_the_next_version()
    {
        var alice = await AliceAsync();
        await PutSpecAsync(alice, "p", "pets", V1);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p/specs/pets")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/api/v1/projects/p/specs/pets")).StatusCode);
        Assert.Empty((await ReadAsync<Page<SpecDto>>(await alice.GetAsync("/api/v1/projects/p/specs"))).Items);
        Assert.Single((await ReadAsync<Page<SpecDto>>(await alice.GetAsync("/api/v1/projects/p/specs?deleted=true"))).Items);
        var r = await PutSpecAsync(alice, "p", "pets", V2);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("\"v2\"", r.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Post_creates_from_json_and_refuses_an_existing_name()
    {
        var alice = await AliceAsync();
        var r = await alice.PostAsJsonAsync("/api/v1/projects/p/specs", new { name = "pets", content = V1, message = "first" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var again = await alice.PostAsJsonAsync("/api/v1/projects/p/specs", new { name = "pets", content = V1 });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("spec_exists", await ProblemCodeAsync(again));
    }

    [Fact]
    public async Task Bad_names_and_media_types_are_refused()
    {
        var alice = await AliceAsync();
        var badName = await PutSpecAsync(alice, "p", "Bad_Name", V1);
        Assert.Equal("invalid_spec_name", await ProblemCodeAsync(badName));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await PutSpecAsync(alice, "p", "pets", V1, mediaType: "text/plain")).StatusCode);
    }

    [Fact]
    public async Task Json_specs_keep_their_format()
    {
        var alice = await AliceAsync();
        var json = "{\"openapi\":\"3.1.0\",\"info\":{\"title\":\"J\",\"version\":\"1\"},\"paths\":{}}";
        Assert.Equal("json", (await ReadAsync<SpecDto>(await PutSpecAsync(alice, "p", "j", json, mediaType: "application/json"))).Format);
        Assert.Equal("application/json", (await alice.GetAsync("/api/v1/projects/p/specs/j")).Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Spec_tags_can_be_set_and_filtered()
    {
        var alice = await AliceAsync();
        await PutSpecAsync(alice, "p", "pets", V1);
        await PutSpecAsync(alice, "p", "other", V1);
        Assert.Equal(["public"], (await ReadAsync<TagsDto>(await alice.PutAsJsonAsync("/api/v1/projects/p/specs/pets/tags", new { tags = new[] { "public" } }))).Tags);
        var page = await ReadAsync<Page<SpecDto>>(await alice.GetAsync("/api/v1/projects/p/specs?tag=public"));
        Assert.Equal(["pets"], page.Items.Select(s => s.Name));
    }

    [Fact]
    public async Task Readers_cannot_write_specs()
    {
        var alice = await AliceAsync();
        await AddMemberAsync(alice, "p", "rd", "reader");
        Assert.Equal(HttpStatusCode.Forbidden, (await PutSpecAsync(ClientFor("rd"), "p", "pets", V1)).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_project_removes_spec_versions()
    {
        var alice = await AliceAsync();
        await PutSpecAsync(alice, "p", "pets", V1);
        Assert.Equal(HttpStatusCode.NoContent, (await alice.DeleteAsync("/api/v1/projects/p")).StatusCode);
        using var scope = Factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<DdmDbContext>().Versions.CountAsync());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SpecTests"`
Expected: build FAILS (`SpecDto` does not exist).

- [ ] **Step 3: Implement the spec service**

`src/Ddm.Api/Specs/SpecOptions.cs`:
```csharp
namespace Ddm.Api.Specs;

public sealed class SpecOptions
{
    public long MaxBytes { get; set; } = 5 * 1024 * 1024;
}
```

`src/Ddm.Api/Specs/SpecService.cs`:
```csharp
using System.Security.Cryptography;
using System.Text.Json;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Specs;

/// <summary>A validated spec, ready to store. Preparing touches neither the database nor blob storage.</summary>
public sealed record PreparedSpec(string Name, byte[] Bytes, string Sha256, ValidatedSpec Spec)
{
    public string BlobKey(Guid projectId) => $"projects/{projectId:N}/specs/{Sha256}.{Spec.Format}";
    public string NormalizedKey(Guid projectId) => $"projects/{projectId:N}/specs/{Sha256}.normalized.json";
}

public sealed record SpecState(Spec Spec, ContentVersion? Current)
{
    public bool IsLive => Spec.DeletedAt is null;
}

public sealed record SpecWriteResult(Spec Spec, ContentVersion Version, bool Created, bool Changed);

public sealed class SpecService(DdmDbContext db, IBlobStore blobs, IOptions<SpecOptions> options)
{
    public long MaxBytes => options.Value.MaxBytes;

    public static string MediaTypeFor(string format) =>
        format == "json" ? "application/json; charset=utf-8" : "application/yaml; charset=utf-8";

    public async Task<PreparedSpec> PrepareAsync(string name, byte[] bytes, CancellationToken ct)
    {
        SpecName.Require(name);
        if (bytes.Length > MaxBytes) throw ApiException.PayloadTooLarge($"Specs are limited to {MaxBytes} bytes");
        var spec = await SpecValidator.ValidateAsync(bytes, ct);
        return new(name, bytes, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), spec);
    }

    public async Task UploadAsync(Project project, PreparedSpec prepared, CancellationToken ct)
    {
        await blobs.PutAsync(prepared.BlobKey(project.Id), prepared.Bytes, MediaTypeFor(prepared.Spec.Format), ct);
        await blobs.PutAsync(prepared.NormalizedKey(project.Id), prepared.Spec.NormalizedJson, "application/json", ct);
    }

    public async Task<SpecWriteResult> WriteAsync(
        Caller caller, Project project, PreparedSpec prepared, string? message, WritePrecondition pre, bool requireIfMatch, CancellationToken ct)
    {
        var state = await FindAsync(project, prepared.Name, ct);
        var live = state is { IsLive: true } ? state : null;
        CheckPreconditions(live, pre, requireIfMatch);
        if (live?.Current is { } current && current.ContentSha256 == prepared.Sha256)
            return new(live.Spec, current, Created: false, Changed: false);

        await UploadAsync(project, prepared, ct);
        var result = Stage(caller, project, prepared, state, message);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.PreconditionFailed("The spec was changed by another writer; fetch the latest version and retry");
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ApiException.PreconditionFailed("The spec was changed or deleted by another writer");
        }
        return result;
    }

    /// <summary>Stages the next version, creating, updating or reviving the row. Does not save or upload.</summary>
    public SpecWriteResult Stage(Caller caller, Project project, PreparedSpec prepared, SpecState? state, string? message)
    {
        var spec = state?.Spec ?? new Spec { ProjectId = project.Id, Name = prepared.Name };
        var created = state is not { IsLive: true };
        var version = new ContentVersion
        {
            ItemType = ItemType.Spec, ItemId = spec.Id, Number = (state?.Current?.Number ?? 0) + 1,
            ContentRef = prepared.BlobKey(project.Id), NormalizedRef = prepared.NormalizedKey(project.Id),
            ContentSha256 = prepared.Sha256, Author = caller.Actor, Message = message,
        };
        var v = prepared.Spec;
        spec.Title = Truncate(v.Title, 300);
        spec.ApiVersion = Truncate(v.ApiVersion, 100);
        spec.OpenApiVersion = v.OpenApiVersion;
        spec.Format = v.Format;
        spec.Operations = JsonSerializer.Serialize(v.Operations, JsonSerializerOptions.Web);
        spec.OperationCount = v.Operations.Count;
        spec.CurrentVersionId = version.Id;
        spec.UpdatedAt = version.CreatedAt;
        spec.DeletedAt = null;
        if (state is null) db.Specs.Add(spec);
        db.Versions.Add(version);
        db.Audit(caller, project.Id, created ? "spec.create" : "spec.update", prepared.Name);
        return new(spec, version, created, Changed: true);
    }

    /// <summary>Tombstones a tracked, live spec. Does not save.</summary>
    public void StageDelete(Caller caller, Project project, Spec spec)
    {
        spec.DeletedAt = DateTimeOffset.UtcNow;
        spec.UpdatedAt = spec.DeletedAt.Value;
        db.Audit(caller, project.Id, "spec.delete", spec.Name);
    }

    /// <summary>The row with this name, including a tombstone, tracked for update.</summary>
    public async Task<SpecState?> FindAsync(Project project, string name, CancellationToken ct)
    {
        var spec = await db.Specs.SingleOrDefaultAsync(s => s.ProjectId == project.Id && s.Name == name, ct);
        if (spec is null) return null;
        var current = spec.CurrentVersionId is { } cid ? await db.Versions.SingleAsync(v => v.Id == cid, ct) : null;
        return new(spec, current);
    }

    public async Task<Spec> RequireAsync(Project project, string name, bool includeDeleted, CancellationToken ct) =>
        await db.Specs.AsNoTracking()
            .SingleOrDefaultAsync(s => s.ProjectId == project.Id && s.Name == name && (includeDeleted || s.DeletedAt == null), ct)
        ?? throw ApiException.NotFound("spec_not_found", "Spec not found");

    public async Task<(Spec Spec, ContentVersion Version)> GetCurrentAsync(Project project, string name, CancellationToken ct)
    {
        var spec = await RequireAsync(project, name, includeDeleted: false, ct);
        return (spec, await db.Versions.AsNoTracking().SingleAsync(v => v.Id == spec.CurrentVersionId, ct));
    }

    public async Task<ContentVersion> GetVersionAsync(Project project, string name, int number, CancellationToken ct)
    {
        var spec = await RequireAsync(project, name, includeDeleted: true, ct);
        return await db.Versions.AsNoTracking().SingleOrDefaultAsync(
                   v => v.ItemType == ItemType.Spec && v.ItemId == spec.Id && v.Number == number, ct)
               ?? throw ApiException.NotFound("version_not_found", "Version not found");
    }

    public async Task<Page<VersionDto>> ListVersionsAsync(Project project, string name, int? limit, string? cursor, CancellationToken ct)
    {
        var spec = await RequireAsync(project, name, includeDeleted: true, ct);
        var take = Paging.ParseLimit(limit);
        var before = Paging.DecodeLongCursor(cursor);
        var query = db.Versions.AsNoTracking().Where(v => v.ItemType == ItemType.Spec && v.ItemId == spec.Id);
        if (before is not null) query = query.Where(v => v.Number < before);
        var rows = await query.OrderByDescending(v => v.Number).Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, v => v.Number.ToString());
        return new(page.Items.Select(v => new VersionDto(v.Number, v.Author, v.Message, v.CreatedAt)).ToList(), page.Next);
    }

    public async Task<byte[]> ReadAsync(string key, CancellationToken ct) =>
        await blobs.GetAsync(key, ct) ?? throw new ApiException(500, "content_missing", "Stored content is missing", $"No object for {key}");

    public async Task<SpecWriteResult> RestoreAsync(
        Caller caller, Project project, string name, int number, WritePrecondition pre, CancellationToken ct)
    {
        var version = await GetVersionAsync(project, name, number, ct);
        var bytes = await ReadAsync(version.ContentRef, ct);
        return await WriteAsync(caller, project, await PrepareAsync(name, bytes, ct), $"Restore version {number}", pre, requireIfMatch: false, ct);
    }

    /// <summary>The row version makes a delete racing an update fail cleanly, whichever commits second.</summary>
    public async Task DeleteAsync(Caller caller, Project project, string name, WritePrecondition pre, CancellationToken ct)
    {
        var state = await FindAsync(project, name, ct);
        if (state is not { IsLive: true }) throw ApiException.NotFound("spec_not_found", "Spec not found");
        if (pre.IfMatchVersion is { } v && v != state.Current!.Number)
            throw ApiException.PreconditionFailed($"The spec is at version {state.Current.Number}, not {v}");
        StageDelete(caller, project, state.Spec);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw ApiException.PreconditionFailed("The spec was changed by another writer");
        }
    }

    private static void CheckPreconditions(SpecState? live, WritePrecondition pre, bool requireIfMatch)
    {
        if (live is null)
        {
            if (pre.HasIfMatch) throw ApiException.PreconditionFailed("The spec does not exist");
            return;
        }
        if (pre.IfNoneMatchAny) throw ApiException.PreconditionFailed("The spec already exists");
        if (!pre.HasIfMatch)
        {
            if (requireIfMatch)
                throw ApiException.PreconditionRequired("Updating an existing spec requires an If-Match header with its current ETag");
            return;
        }
        if (pre.IfMatchVersion is { } v && v != live.Current!.Number)
            throw ApiException.PreconditionFailed($"The spec is at version {live.Current.Number}, not {v}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
```

`src/Ddm.Api/Specs/SpecDtos.cs`:
```csharp
using Ddm.Api.Domain;

namespace Ddm.Api.Specs;

public sealed record SpecDto(string Name, string Title, string ApiVersion, string OpenApiVersion, string Format, int Version,
    int OperationCount, IReadOnlyList<string> Tags, DateTimeOffset UpdatedAt)
{
    public static SpecDto From(Spec s, int version, IReadOnlyList<string> tags) =>
        new(s.Name, s.Title, s.ApiVersion, s.OpenApiVersion, s.Format, version, s.OperationCount, tags, s.UpdatedAt);
}

public sealed record CreateSpecRequest(string? Name, string? Content, string? Message);
```

- [ ] **Step 4: Map the endpoints**

`src/Ddm.Api/Specs/SpecEndpoints.cs`:
```csharp
using System.Security.Claims;
using System.Text;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Tags;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Specs;

public static class SpecEndpoints
{
    private static readonly string[] SpecMediaTypes = ["application/yaml", "application/x-yaml", "text/yaml", "application/json"];

    public static void MapSpecs(this RouteGroupBuilder v1)
    {
        // Spec names contain no '/', so plain route segments suffice (no catch-all).
        var g = v1.MapGroup("/projects/{slug}/specs");
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapGet("{name}", GetAsync);
        g.MapPut("{name}", PutAsync);
        g.MapDelete("{name}", DeleteAsync);
        g.MapGet("{name}/normalized", GetNormalizedAsync);
        g.MapGet("{name}/operations", GetOperationsAsync);
        g.MapPut("{name}/tags", PutTagsAsync);
        g.MapGet("{name}/versions", ListVersionsAsync);
        g.MapGet("{name}/versions/{n:int}", GetVersionAsync);
        g.MapGet("{name}/versions/{n:int}/normalized", GetVersionNormalizedAsync);
        g.MapPost("{name}/versions/{n:int}/restore", RestoreAsync);
    }

    private static async Task<IResult> CreateAsync(
        string slug, CreateSpecRequest? body, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        SpecService specs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        var name = SpecName.Require(body?.Name);
        if (body!.Content is null) throw ApiException.BadRequest("validation_failed", "The request is not valid", "content is required");
        if (body.Message is { Length: > 500 }) throw ApiException.BadRequest("validation_failed", "The request is not valid", "message is limited to 500 characters");
        if (await specs.FindAsync(access.Project, name, ct) is { IsLive: true })
            throw ApiException.Conflict("spec_exists", $"A spec named {name} already exists");

        var result = await specs.WriteAsync(caller, access.Project, await specs.PrepareAsync(name, Encoding.UTF8.GetBytes(body.Content), ct),
            body.Message, new WritePrecondition(null, false, IfNoneMatchAny: true), requireIfMatch: false, ct);
        return await WrittenAsync(http, slug, result, tags, created: true, ct);
    }

    private static async Task<IResult> PutAsync(
        string slug, string name, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        SpecService specs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        SpecName.Require(name);
        var type = http.Request.GetTypedHeaders().ContentType?.MediaType.Value?.ToLowerInvariant();
        if (type is null || !SpecMediaTypes.Contains(type))
            throw new ApiException(415, "unsupported_media_type", "Send specs as application/yaml or application/json");
        var pre = Preconditions.Parse(http.Request.Headers);
        var message = DocumentEndpoints.MessageFrom(http.Request);
        RequestBody.AllowUpTo(http, specs.MaxBytes);
        var bytes = await RequestBody.ReadLimitedAsync(http.Request, specs.MaxBytes, $"Specs are limited to {specs.MaxBytes} bytes", ct);

        var result = await specs.WriteAsync(caller, access.Project, await specs.PrepareAsync(name, bytes, ct), message, pre, requireIfMatch: true, ct);
        return await WrittenAsync(http, slug, result, tags, result.Created, ct);
    }

    private static async Task<IResult> GetAsync(
        string slug, string name, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var (spec, version) = await specs.GetCurrentAsync(access.Project, name, ct);
        return Original(http, spec.Format, version, await specs.ReadAsync(version.ContentRef, ct));
    }

    private static async Task<IResult> GetNormalizedAsync(
        string slug, string name, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var (_, version) = await specs.GetCurrentAsync(access.Project, name, ct);
        return Normalized(http, version, await specs.ReadAsync(version.NormalizedRef!, ct));
    }

    private static async Task<IResult> GetOperationsAsync(
        string slug, string name, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var spec = await specs.RequireAsync(access.Project, name, includeDeleted: false, ct);
        return Results.Text(spec.Operations, "application/json");
    }

    private static async Task<IResult> PutTagsAsync(
        string slug, string name, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        SpecService specs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        var spec = await specs.RequireAsync(access.Project, name, includeDeleted: false, ct);
        var names = await tags.SetFromRequestAsync(caller, access.Project.Id, new ItemRef(ItemType.Spec, spec.Id), spec.Name,
            await TagEndpoints.ReadBodyAsync(http.Request, ct), ct);
        return Results.Ok(new TagsDto(names));
    }

    private static async Task<IResult> ListVersionsAsync(
        string slug, string name, string? cursor, int? limit, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        return Results.Ok(await specs.ListVersionsAsync(access.Project, name, limit, cursor, ct));
    }

    private static async Task<IResult> GetVersionAsync(
        string slug, string name, int n, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var version = await specs.GetVersionAsync(access.Project, name, n, ct);
        var bytes = await specs.ReadAsync(version.ContentRef, ct);
        // An old version may predate a format change, so its format comes from its own bytes, not the current row.
        return Original(http, SpecValidator.FormatOf(Encoding.UTF8.GetString(bytes)), version, bytes);
    }

    private static async Task<IResult> GetVersionNormalizedAsync(
        string slug, string name, int n, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var version = await specs.GetVersionAsync(access.Project, name, n, ct);
        return Normalized(http, version, await specs.ReadAsync(version.NormalizedRef!, ct));
    }

    private static async Task<IResult> RestoreAsync(
        string slug, string name, int n, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz,
        SpecService specs, TagService tags, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        var result = await specs.RestoreAsync(caller, access.Project, name, n, Preconditions.Parse(http.Request.Headers), ct);
        return await WrittenAsync(http, slug, result, tags, created: false, ct);
    }

    private static async Task<IResult> DeleteAsync(
        string slug, string name, HttpContext http, ClaimsPrincipal user, ProjectAuthorizer authz, SpecService specs, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        await specs.DeleteAsync(caller, access.Project, name, Preconditions.Parse(http.Request.Headers), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListAsync(
        string slug, bool? deleted, string[]? tag, string? cursor, int? limit, ClaimsPrincipal user,
        ProjectAuthorizer authz, DdmDbContext db, TagService tags, CancellationToken ct)
    {
        var access = await authz.RequireAsync(Caller.From(user), slug, Role.Reader, ct);
        var take = Paging.ParseLimit(limit);
        var after = Paging.DecodeCursor(cursor);
        var filter = TagName.Filter(tag);

        var query = db.Specs.AsNoTracking().Where(s => s.ProjectId == access.Project.Id);
        query = deleted == true ? query.Where(s => s.DeletedAt != null) : query.Where(s => s.DeletedAt == null);
        if (filter.Count > 0)
        {
            var tagged = tags.ItemsWithAll(ItemType.Spec, filter);
            query = query.Where(s => tagged.Contains(s.Id));
        }
        if (after is not null) query = query.Where(s => string.Compare(s.Name, after) > 0);

        var rows = await (from s in query
                          join v in db.Versions on s.CurrentVersionId equals (Guid?)v.Id
                          orderby s.Name
                          select new { Spec = s, v.Number })
            .Take(take + 1).ToListAsync(ct);
        var page = Paging.ToPage(rows, take, r => r.Spec.Name);
        var tagMap = await tags.TagsForAsync(ItemType.Spec, page.Items.Select(r => r.Spec.Id).ToList(), ct);
        return Results.Ok(new Page<SpecDto>(page.Items.Select(r => SpecDto.From(r.Spec, r.Number, tagMap[r.Spec.Id])).ToList(), page.Next));
    }

    private static IResult Original(HttpContext http, string format, ContentVersion version, byte[] bytes)
    {
        http.Response.Headers.ETag = Preconditions.ETag(version.Number);
        http.Response.Headers.CacheControl = "private, no-cache";
        return Results.Bytes(bytes, SpecService.MediaTypeFor(format));
    }

    private static IResult Normalized(HttpContext http, ContentVersion version, byte[] json)
    {
        http.Response.Headers.ETag = Preconditions.ETag(version.Number);
        http.Response.Headers.CacheControl = "private, no-cache";
        return Results.Bytes(json, "application/json");
    }

    private static async Task<IResult> WrittenAsync(HttpContext http, string slug, SpecWriteResult r, TagService tags, bool created, CancellationToken ct)
    {
        http.Response.Headers.ETag = Preconditions.ETag(r.Version.Number);
        var dto = SpecDto.From(r.Spec, r.Version.Number, await tags.TagsForAsync(new ItemRef(ItemType.Spec, r.Spec.Id), ct));
        return created ? Results.Created($"/api/v1/projects/{slug}/specs/{r.Spec.Name}", dto) : Results.Ok(dto);
    }
}
```
`src/Ddm.Api/Specs/SpecSetup.cs`:
```csharp
namespace Ddm.Api.Specs;

public static class SpecSetup
{
    public static IServiceCollection AddDdmSpecs(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SpecOptions>(config.GetSection("Specs"));
        services.AddScoped<SpecService>();
        return services;
    }
}
```

In `src/Ddm.Api/Program.cs`: add `using Ddm.Api.Specs;`, `builder.Services.AddDdmSpecs(builder.Configuration);` after `AddDdmContent`, and `v1.MapSpecs();` after `v1.MapAssets();`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~Spec|FullyQualifiedName~ProjectTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Ddm.Api tests/Ddm.Api.Tests
git commit -m "feat: versioned OpenAPI specs with normalised JSON, operations and tags" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Archive reader

**Files:**
- Create: `src/Ddm.Api/Publishing/ArchiveReader.cs`, `tests/Ddm.Api.Tests/Infrastructure/Archives.cs`
- Test: `tests/Ddm.Api.Tests/ArchiveReaderTests.cs`

**Interfaces:**
- Consumes: `ApiException.Unprocessable` (Task 1).
- Produces (namespace `Ddm.Api.Publishing`):
  - `enum ArchiveFormat { TarGz, Zip }`.
  - `record ArchiveFile(string Path, byte[] Content)`; `record ArchiveContents(IReadOnlyList<ArchiveFile> Files, IReadOnlyList<string> Ignored)`.
  - `record ArchiveLimits(long MaxExpandedBytes, int MaxEntries, long MaxEntryBytes)`.
  - `record PublishError(string Path, string Code, string Message, int? Line = null)`.
  - `ArchiveReader.ReadAsync(Stream, ArchiveFormat, ArchiveLimits, CancellationToken) → Task<ArchiveContents>`. It throws `400 invalid_archive`, `413 archive_too_large`, or `422 publish_invalid` whose `errors` hold `PublishError`s with codes `unsafe_path`, `unsupported_entry` or `duplicate_entry`.
  - Test helper `Archives`: `Text`, `File`, `Directory`, `Symlink`, `HardLink`, `TarGz((name, bytes)…)`, `TarGzEntries(TarEntry…)`, `Zip((name, bytes)…)`, `ZipEntries((name, bytes, unixMode)…)`.

- [ ] **Step 1: Write the test helper and the failing tests**

`tests/Ddm.Api.Tests/Infrastructure/Archives.cs`:
```csharp
using System.Formats.Tar;
using System.IO.Compression;

namespace Ddm.Api.Tests.Infrastructure;

/// <summary>Builds publish archives in memory.</summary>
public static class Archives
{
    public static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    public static TarEntry File(string name, byte[] content) =>
        new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) };

    public static TarEntry Directory(string name) => new PaxTarEntry(TarEntryType.Directory, name);

    public static TarEntry Symlink(string name, string target) => new PaxTarEntry(TarEntryType.SymbolicLink, name) { LinkName = target };

    public static TarEntry HardLink(string name, string target) => new PaxTarEntry(TarEntryType.HardLink, name) { LinkName = target };

    /// <summary>A tar.gz of regular files.</summary>
    public static byte[] TarGz(params (string Name, byte[] Content)[] files) =>
        TarGzEntries(files.Select(f => File(f.Name, f.Content)).ToArray());

    public static byte[] TarGzEntries(params TarEntry[] entries)
    {
        using var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            foreach (var e in entries) tar.WriteEntry(e);
        return ms.ToArray();
    }

    public static byte[] Zip(params (string Name, byte[] Content)[] files) =>
        ZipEntries(files.Select(f => (f.Name, f.Content, 0)).ToArray());

    /// <summary>A zip whose entries may carry a Unix mode in their external attributes (0xA1FF marks a symlink).</summary>
    public static byte[] ZipEntries(params (string Name, byte[] Content, int UnixMode)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content, mode) in files)
            {
                var entry = zip.CreateEntry(name);
                if (mode != 0) entry.ExternalAttributes = mode << 16;
                using var s = entry.Open();
                s.Write(content);
            }
        return ms.ToArray();
    }
}
```

`tests/Ddm.Api.Tests/ArchiveReaderTests.cs`:
```csharp
using System.Formats.Tar;
using Ddm.Api.Common;
using Ddm.Api.Publishing;
using Ddm.Api.Tests.Infrastructure;

namespace Ddm.Api.Tests;

public class ArchiveReaderTests
{
    private static readonly ArchiveLimits Limits = new(MaxExpandedBytes: 1_000_000, MaxEntries: 100, MaxEntryBytes: 500_000);

    private static Task<ArchiveContents> ReadAsync(byte[] archive, ArchiveFormat format = ArchiveFormat.TarGz, ArchiveLimits? limits = null) =>
        ArchiveReader.ReadAsync(new MemoryStream(archive), format, limits ?? Limits, default);

    private static async Task<(string Code, List<string> EntryCodes)> FailAsync(
        byte[] archive, ArchiveFormat format = ArchiveFormat.TarGz, ArchiveLimits? limits = null)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => ReadAsync(archive, format, limits));
        var entries = (ex.Extensions?["errors"] as IEnumerable<PublishError>)?.Select(e => e.Code).ToList() ?? [];
        return (ex.Code, entries);
    }

    [Fact]
    public async Task Reads_files_and_skips_directories()
    {
        var c = await ReadAsync(Archives.TarGzEntries(Archives.Directory("guides/"), Archives.File("guides/a.md", Archives.Text("# a")), Archives.File("b.png", [1, 2])));
        Assert.Equal(["guides/a.md", "b.png"], c.Files.Select(f => f.Path));
        Assert.Equal("# a", Encoding.UTF8.GetString(c.Files[0].Content));
    }

    // Review Focus 3: what `tar czf docs.tgz .` produces on macOS
    [Fact]
    public async Task Dot_slash_prefixes_are_stripped_and_hidden_entries_ignored()
    {
        var c = await ReadAsync(Archives.TarGzEntries(
            Archives.Directory("./"), Archives.File("./index.md", Archives.Text("# i")), Archives.File("./._index.md", [0]),
            Archives.File("./.git/config", Archives.Text("x")), Archives.File("./.DS_Store", [0]), Archives.File("./empty.txt", [])));
        Assert.Equal(["index.md", "empty.txt"], c.Files.Select(f => f.Path));
        Assert.Empty(c.Files[1].Content);
        Assert.Equal(["._index.md", ".git/config", ".DS_Store"], c.Ignored);
    }

    [Fact]
    public async Task Zip_archives_read_the_same_way()
    {
        var c = await ReadAsync(Archives.Zip(("a.md", Archives.Text("# a")), ("img/b.png", [1])), ArchiveFormat.Zip);
        Assert.Equal(["a.md", "img/b.png"], c.Files.Select(f => f.Path));
    }

    // Spec review focus 3: archive hostility
    [Theory]
    [InlineData("../evil.md")] [InlineData("a/../../evil.md")] [InlineData("/etc/passwd.md")] [InlineData("C:/x.md")]
    public async Task Unsafe_paths_fail_the_whole_archive(string name)
    {
        var (code, entries) = await FailAsync(Archives.TarGzEntries(Archives.File("ok.md", Archives.Text("# ok")), Archives.File(name, Archives.Text("x"))));
        Assert.Equal("publish_invalid", code);
        Assert.Equal(["unsafe_path"], entries);
    }

    [Fact]
    public async Task Symlinks_and_hard_links_fail_the_archive()
    {
        var (code, entries) = await FailAsync(Archives.TarGzEntries(Archives.Symlink("a.md", "/etc/passwd"), Archives.HardLink("b.md", "a.md")));
        Assert.Equal("publish_invalid", code);
        Assert.Equal(["unsupported_entry", "unsupported_entry"], entries);
    }

    [Fact]
    public async Task Zip_symlinks_fail_the_archive()
    {
        var (code, entries) = await FailAsync(Archives.ZipEntries(("link.md", Archives.Text("/etc/passwd"), 0xA1FF)), ArchiveFormat.Zip);
        Assert.Equal("publish_invalid", code);
        Assert.Equal(["unsupported_entry"], entries);
    }

    [Fact]
    public async Task Duplicate_paths_fail_the_archive()
    {
        var (_, entries) = await FailAsync(Archives.TarGzEntries(Archives.File("a.md", Archives.Text("1")), Archives.File("./a.md", Archives.Text("2"))));
        Assert.Equal(["duplicate_entry"], entries);
    }

    [Fact]
    public async Task Too_many_entries_is_413()
    {
        var files = Enumerable.Range(0, 11).Select(i => Archives.File($"f{i}.md", Archives.Text("x"))).ToArray();
        var (code, _) = await FailAsync(Archives.TarGzEntries(files), limits: Limits with { MaxEntries = 10 });
        Assert.Equal("archive_too_large", code);
    }

    [Fact]
    public async Task A_zip_bomb_stops_at_the_expanded_limit()
    {
        var zeros = new byte[600_000]; // compresses to almost nothing
        var (code, _) = await FailAsync(Archives.TarGz(("a.txt", zeros), ("b.txt", zeros)), limits: Limits with { MaxEntryBytes = 1_000_000 });
        Assert.Equal("archive_too_large", code);
    }

    [Fact]
    public async Task An_oversized_entry_is_413()
    {
        var (code, _) = await FailAsync(Archives.TarGz(("big.txt", new byte[500_001])));
        Assert.Equal("archive_too_large", code);
    }

    [Theory] [InlineData(ArchiveFormat.TarGz)] [InlineData(ArchiveFormat.Zip)]
    public async Task Corrupt_archives_are_400(ArchiveFormat format)
    {
        var (code, _) = await FailAsync([1, 2, 3, 4, 5], format);
        Assert.Equal("invalid_archive", code);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ArchiveReaderTests"`
Expected: build FAILS (`Ddm.Api.Publishing` does not exist).

- [ ] **Step 3: Implement**

`src/Ddm.Api/Publishing/ArchiveReader.cs`:
```csharp
using System.Formats.Tar;
using System.IO.Compression;
using Ddm.Api.Common;

namespace Ddm.Api.Publishing;

public enum ArchiveFormat { TarGz, Zip }

public sealed record ArchiveFile(string Path, byte[] Content);
public sealed record ArchiveContents(IReadOnlyList<ArchiveFile> Files, IReadOnlyList<string> Ignored);
public sealed record ArchiveLimits(long MaxExpandedBytes, int MaxEntries, long MaxEntryBytes);

/// <summary>A problem with one archive entry; any of these fails the whole publish.</summary>
public sealed record PublishError(string Path, string Code, string Message, int? Line = null);

/// <summary>
/// Reads a tar.gz or zip into memory, refusing anything that could write outside the project or exhaust the
/// server: links, absolute or '..' paths, too many entries, and more expanded bytes than allowed. Hidden entries
/// (any segment starting with '.') are skipped and reported, so a checkout's .git or .github never publishes.
/// </summary>
public static class ArchiveReader
{
    private enum EntryKind { File, Directory, Metadata, Unsupported }

    public static async Task<ArchiveContents> ReadAsync(Stream archive, ArchiveFormat format, ArchiveLimits limits, CancellationToken ct)
    {
        var state = new State(limits);
        try
        {
            if (format == ArchiveFormat.TarGz) await ReadTarAsync(archive, state, ct);
            else await ReadZipAsync(archive, state, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or FormatException)
        {
            throw ApiException.BadRequest("invalid_archive", "The archive could not be read", ex.Message);
        }
        if (state.Errors.Count > 0)
            throw ApiException.Unprocessable("publish_invalid", "The archive cannot be published", state.Errors,
                $"{state.Errors.Count} problem(s) found");
        return new(state.Files, state.Ignored);
    }

    private static async Task ReadTarAsync(Stream archive, State state, CancellationToken ct)
    {
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        await using var tar = new TarReader(gzip, leaveOpen: true);
        while (await tar.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            state.CountEntry();
            var kind = entry.EntryType switch
            {
                TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile => EntryKind.File,
                TarEntryType.Directory => EntryKind.Directory,
                TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes => EntryKind.Metadata,
                _ => EntryKind.Unsupported,
            };
            if (kind == EntryKind.Metadata) continue;
            if (state.Accept(entry.Name, kind) is { } path) await state.AddAsync(path, entry.DataStream ?? Stream.Null, ct);
        }
    }

    private static async Task ReadZipAsync(Stream archive, State state, CancellationToken ct)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            state.CountEntry();
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000; // 0x8000 file, 0x4000 directory, 0xA000 symlink
            var kind = entry.FullName.EndsWith('/') ? EntryKind.Directory
                : unixType is 0 or 0x8000 ? EntryKind.File
                : EntryKind.Unsupported;
            if (state.Accept(entry.FullName, kind) is not { } path) continue;
            await using var data = entry.Open();
            await state.AddAsync(path, data, ct);
        }
    }

    private sealed class State(ArchiveLimits limits)
    {
        public readonly List<ArchiveFile> Files = [];
        public readonly List<string> Ignored = [];
        public readonly List<PublishError> Errors = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private long _expanded;
        private int _entries;

        public void CountEntry()
        {
            if (++_entries > limits.MaxEntries) throw TooLarge($"The archive has more than {limits.MaxEntries} entries");
        }

        /// <summary>Normalises an entry name. Returns the path to read, or null when the entry is skipped or refused (refusals are recorded).</summary>
        public string? Accept(string rawName, EntryKind kind)
        {
            var name = rawName.Replace('\\', '/');
            if (name.StartsWith('/') || (name.Length >= 2 && name[1] == ':'))
            {
                Errors.Add(new(rawName, "unsafe_path", "Absolute paths are not allowed in a publish archive"));
                return null;
            }
            var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToList();
            if (segments.Contains(".."))
            {
                Errors.Add(new(rawName, "unsafe_path", "'..' is not allowed in archive paths"));
                return null;
            }
            if (segments.Count == 0) return null; // the archive root, e.g. "./"
            var path = string.Join('/', segments);
            if (segments.Any(s => s.StartsWith('.')))
            {
                if (kind != EntryKind.Directory) Ignored.Add(path);
                return null;
            }
            switch (kind)
            {
                case EntryKind.Unsupported:
                    Errors.Add(new(path, "unsupported_entry", "Only regular files and directories can be published; links are not allowed"));
                    return null;
                case EntryKind.Directory:
                    return null;
            }
            if (!_seen.Add(path))
            {
                Errors.Add(new(path, "duplicate_entry", "The archive contains this path more than once"));
                return null;
            }
            return path;
        }

        public async Task AddAsync(string path, Stream data, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = await data.ReadAsync(buffer, ct)) > 0)
            {
                _expanded += n;
                if (_expanded > limits.MaxExpandedBytes) throw TooLarge($"The archive expands to more than {limits.MaxExpandedBytes} bytes");
                if (ms.Length + n > limits.MaxEntryBytes) throw TooLarge($"{path} is larger than {limits.MaxEntryBytes} bytes");
                ms.Write(buffer, 0, n);
            }
            Files.Add(new(path, ms.ToArray()));
        }
    }

    private static ApiException TooLarge(string detail) => new(413, "archive_too_large", "The archive is too large", detail);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ArchiveReaderTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Ddm.Api/Publishing tests/Ddm.Api.Tests
git commit -m "feat: hardened tar.gz and zip reader for bulk publish" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: Publish planner

**Files:**
- Create: `src/Ddm.Api/Publishing/PublishPlanner.cs`
- Test: `tests/Ddm.Api.Tests/PublishPlannerTests.cs`

**Interfaces:**
- Consumes: `AssetTypes.ForPath` (Task 6); `ItemType` (Task 2).
- Produces (namespace `Ddm.Api.Publishing`):
  - `enum PlanAction { Create, Update, Unchanged, Delete }`.
  - `record ItemKey(ItemType Type, string Key)`: the key is the path, or the name for a spec.
  - `record PlanStep(ItemType Type, string Key, PlanAction Action)`.
  - `record PublishPlan(IReadOnlyList<PlanStep> Steps, int InScope)` with `Count(PlanAction)`, `IsMassDelete`, `SameAs(PublishPlan)`.
  - `PublishPlanner.Plan(IReadOnlyDictionary<ItemKey, string> wanted, IReadOnlyDictionary<ItemKey, string> live, string prefix) → PublishPlan` (values are SHA-256s; steps sorted by type, then key).
  - `PublishPlanner.Classify(string path, byte[] content) → ItemType?`; `PublishPlanner.SpecNameFor(string path) → string`.

- [ ] **Step 1: Write the failing tests**

`tests/Ddm.Api.Tests/PublishPlannerTests.cs`:
```csharp
using Ddm.Api.Domain;
using Ddm.Api.Publishing;

namespace Ddm.Api.Tests;

public class PublishPlannerTests
{
    private static ItemKey Doc(string path) => new(ItemType.Document, path);
    private static ItemKey Asset(string path) => new(ItemType.Asset, path);
    private static ItemKey Spec(string name) => new(ItemType.Spec, name);
    private static Dictionary<ItemKey, string> Map(params (ItemKey Key, string Sha)[] items) => items.ToDictionary(i => i.Key, i => i.Sha);
    private static PlanAction ActionOf(PublishPlan plan, string key) => plan.Steps.Single(s => s.Key == key).Action;

    [Fact]
    public void Creates_updates_unchanged_and_deletes()
    {
        var plan = PublishPlanner.Plan(
            Map((Doc("new.md"), "1"), (Doc("same.md"), "2"), (Doc("changed.md"), "3")),
            Map((Doc("same.md"), "2"), (Doc("changed.md"), "old"), (Doc("gone.md"), "4")), "");
        Assert.Equal(PlanAction.Create, ActionOf(plan, "new.md"));
        Assert.Equal(PlanAction.Unchanged, ActionOf(plan, "same.md"));
        Assert.Equal(PlanAction.Update, ActionOf(plan, "changed.md"));
        Assert.Equal(PlanAction.Delete, ActionOf(plan, "gone.md"));
        Assert.Equal(3, plan.InScope);
    }

    [Fact]
    public void A_prefix_limits_deletes_to_its_subtree_and_never_deletes_specs()
    {
        var plan = PublishPlanner.Plan(
            Map((Doc("guides/a.md"), "1")),
            Map((Doc("guides/a.md"), "1"), (Doc("guides/old.md"), "2"), (Doc("other/keep.md"), "3"),
                (Spec("api"), "4"), (Asset("guides/img.png"), "5")),
            "guides/");
        Assert.Equal(["guides/img.png", "guides/old.md"],
            plan.Steps.Where(s => s.Action == PlanAction.Delete).Select(s => s.Key).Order(StringComparer.Ordinal));
        Assert.Equal(3, plan.InScope);
    }

    [Fact]
    public void A_full_publish_deletes_specs_missing_from_the_archive()
    {
        var plan = PublishPlanner.Plan(Map((Doc("a.md"), "1")), Map((Doc("a.md"), "1"), (Spec("api"), "2")), "");
        Assert.Equal(PlanAction.Delete, ActionOf(plan, "api"));
    }

    [Theory]
    [InlineData(1, 1, true)]    // everything in scope
    [InlineData(10, 6, true)]   // more than half of 10+
    [InlineData(10, 5, false)]  // exactly half
    [InlineData(9, 8, false)]   // scopes under 10 only trip when everything goes
    [InlineData(4, 0, false)]
    [InlineData(0, 0, false)]
    public void Mass_delete_guard(int inScope, int deletes, bool expected)
    {
        var live = Map(Enumerable.Range(0, inScope).Select(i => (Doc($"d{i}.md"), "x")).ToArray());
        var wanted = Map(Enumerable.Range(deletes, inScope - deletes).Select(i => (Doc($"d{i}.md"), "x")).ToArray());
        Assert.Equal(expected, PublishPlanner.Plan(wanted, live, "").IsMassDelete);
    }

    [Fact]
    public void Plans_from_equal_inputs_are_the_same()
    {
        var wanted = Map((Doc("b.md"), "1"), (Asset("a.png"), "2"));
        var live = Map((Doc("c.md"), "3"));
        Assert.True(PublishPlanner.Plan(wanted, live, "").SameAs(PublishPlanner.Plan(wanted, live, "")));
        Assert.False(PublishPlanner.Plan(wanted, live, "").SameAs(PublishPlanner.Plan(wanted, Map((Doc("b.md"), "1")), "")));
    }

    [Theory]
    [InlineData("a.md", "# x", ItemType.Document)]
    [InlineData("api.yaml", "openapi: 3.0.3\ninfo: {}\n", ItemType.Spec)]
    [InlineData("api.yml", "---\nopenapi: 3.1.0\n", ItemType.Spec)]
    [InlineData("api.json", "{\"info\": {}, \"openapi\": \"3.0.0\"}", ItemType.Spec)]
    [InlineData("config.yaml", "name: x\nnested:\n  openapi: 3\n", ItemType.Asset)]
    [InlineData("data.json", "{\"a\": {\"openapi\": 1}}", ItemType.Asset)]
    [InlineData("logo.png", "", ItemType.Asset)]
    public void Classifies_files(string path, string content, ItemType expected) =>
        Assert.Equal(expected, PublishPlanner.Classify(path, Encoding.UTF8.GetBytes(content)));

    [Theory] [InlineData("app.exe")] [InlineData("Makefile")]
    public void Unsupported_files_classify_as_null(string path) => Assert.Null(PublishPlanner.Classify(path, [1]));

    [Fact] public void Spec_names_come_from_the_lowercased_file_stem() =>
        Assert.Equal("payments.v2", PublishPlanner.SpecNameFor("apis/Payments.v2.yaml"));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PublishPlannerTests"`
Expected: build FAILS (`PublishPlanner` does not exist).

- [ ] **Step 3: Implement**

`src/Ddm.Api/Publishing/PublishPlanner.cs`:
```csharp
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ddm.Api.Assets;
using Ddm.Api.Domain;

namespace Ddm.Api.Publishing;

public enum PlanAction { Create, Update, Unchanged, Delete }

/// <summary>Identifies an item in a project: its path, or its name for a spec.</summary>
public sealed record ItemKey(ItemType Type, string Key);

public sealed record PlanStep(ItemType Type, string Key, PlanAction Action);

public sealed record PublishPlan(IReadOnlyList<PlanStep> Steps, int InScope)
{
    public int Count(PlanAction action) => Steps.Count(s => s.Action == action);

    /// <summary>Deleting everything in scope, or more than half of a scope of at least 10 items, needs explicit consent.</summary>
    public bool IsMassDelete
    {
        get
        {
            var deletes = Count(PlanAction.Delete);
            return deletes > 0 && (deletes == InScope || (InScope >= 10 && deletes * 2 > InScope));
        }
    }

    public bool SameAs(PublishPlan other) => InScope == other.InScope && Steps.SequenceEqual(other.Steps);
}

public static partial class PublishPlanner
{
    /// <param name="wanted">What the archive holds, keyed by item, valued by content SHA-256.</param>
    /// <param name="live">The project's live items (tombstones excluded), valued by their current SHA-256.</param>
    /// <param name="prefix">The publish scope: documents and assets under it; specs only when it is empty.</param>
    public static PublishPlan Plan(IReadOnlyDictionary<ItemKey, string> wanted, IReadOnlyDictionary<ItemKey, string> live, string prefix)
    {
        bool InScope(ItemKey k) => k.Type == ItemType.Spec ? prefix.Length == 0 : k.Key.StartsWith(prefix, StringComparison.Ordinal);

        var steps = new List<PlanStep>();
        foreach (var (key, sha) in wanted)
        {
            var action = !live.TryGetValue(key, out var current) ? PlanAction.Create
                : current == sha ? PlanAction.Unchanged
                : PlanAction.Update;
            steps.Add(new(key.Type, key.Key, action));
        }
        foreach (var key in live.Keys.Where(k => InScope(k) && !wanted.ContainsKey(k)))
            steps.Add(new(key.Type, key.Key, PlanAction.Delete));

        steps.Sort((a, b) => a.Type != b.Type ? a.Type.CompareTo(b.Type) : string.CompareOrdinal(a.Key, b.Key));
        return new(steps, live.Keys.Count(InScope));
    }

    [GeneratedRegex(@"^[""']?openapi[""']?[ \t]*:", RegexOptions.Multiline)]
    private static partial Regex TopLevelYamlOpenApiKey();

    [GeneratedRegex(@"^\s*\{\s*""openapi""\s*:")]
    private static partial Regex LeadingJsonOpenApiKey();

    /// <summary>
    /// What an archive file becomes: markdown is a document; YAML or JSON with a top-level openapi key is a spec;
    /// any other allow-listed type is an asset; anything else is null (unsupported).
    /// </summary>
    public static ItemType? Classify(string path, byte[] content)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".md") return ItemType.Document;
        if (ext is ".yaml" or ".yml" or ".json" && LooksLikeSpec(ext, content)) return ItemType.Spec;
        return AssetTypes.ForPath(path) is not null ? ItemType.Asset : null;
    }

    /// <summary>A spec's name is its file stem, lowercased: apis/Payments.v2.yaml → payments.v2.</summary>
    public static string SpecNameFor(string path) => System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

    private static bool LooksLikeSpec(string ext, byte[] content)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException) { return false; }
        if (ext != ".json") return TopLevelYamlOpenApiKey().IsMatch(text);
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("openapi", out _);
        }
        catch (JsonException)
        {
            // Broken JSON that starts like a spec is still a spec, so validation reports its errors instead of storing it as an asset.
            return LeadingJsonOpenApiKey().IsMatch(text);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~PublishPlannerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Ddm.Api/Publishing tests/Ddm.Api.Tests/PublishPlannerTests.cs
git commit -m "feat: publish planner with scope, mass-delete guard and file classification" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: Publish service and endpoint

**Files:**
- Create: `src/Ddm.Api/Publishing/PublishOptions.cs`, `src/Ddm.Api/Publishing/PublishDtos.cs`, `src/Ddm.Api/Publishing/PublishService.cs`, `src/Ddm.Api/Publishing/PublishEndpoints.cs`, `src/Ddm.Api/Publishing/PublishSetup.cs`
- Modify: `src/Ddm.Api/Program.cs`, `tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs`
- Test: `tests/Ddm.Api.Tests/PublishTests.cs`

**Interfaces:**
- Consumes:
  - `ArchiveReader`, `ArchiveContents`, `ArchiveLimits`, `ArchiveFormat`, `PublishError` (Task 12); `PublishPlanner`, `PublishPlan`, `PlanStep`, `ItemKey`, `PlanAction` (Task 13).
  - `DocumentService.Prepare/UploadAsync/StageAsync/StageDelete`, `DocumentState` (Tasks 3 and 4).
  - `AssetService.Prepare/UploadAsync/Stage/StageDelete/MaxBytes` (Task 7); `SpecService.PrepareAsync/UploadAsync/Stage/StageDelete/MaxBytes`, `SpecState`, `SpecError` (Tasks 10 and 11).
  - `ContentPath.ValidateFolder`, `ApiException.Unprocessable` (Task 1); `ProjectLocks.LockAsync`, `DocumentEndpoints.MessageFrom` (phase 1).
- Produces:
  - `PublishOptions { long MaxArchiveBytes = 100 MB; long MaxExpandedBytes = 250 MB; int MaxEntries = 5000 }` bound from `Publish`.
  - `record PublishRequest(string Prefix, string Message, bool DryRun, bool AllowMassDelete)`.
  - `record PublishedItem(string Type, string Key, int? Version)`; `record PublishResult(bool DryRun, IReadOnlyList<PublishedItem> Created, IReadOnlyList<PublishedItem> Updated, IReadOnlyList<PublishedItem> Deleted, int Unchanged, IReadOnlyList<string> Ignored)`.
  - `PublishService.PublishAsync(Caller, Project, ArchiveContents, PublishRequest, CancellationToken) → Task<PublishResult>`.
  - `POST /api/v1/projects/{slug}/publish?prefix=&message=&dryRun=&allowMassDelete=` (editor).
  - Test helper `ApiTestBase.PublishAsync(HttpClient, string slug, byte[] archive, string query = "", string mediaType = "application/gzip")`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Ddm.Api.Tests/Infrastructure/ApiTestBase.cs`:
```csharp
    protected static Task<HttpResponseMessage> PublishAsync(
        HttpClient client, string slug, byte[] archive, string query = "", string mediaType = "application/gzip")
    {
        var content = new ByteArrayContent(archive);
        content.Headers.ContentType = new(mediaType);
        return client.PostAsync($"/api/v1/projects/{slug}/publish{query}", content);
    }
```

`tests/Ddm.Api.Tests/PublishTests.cs`:
```csharp
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Publishing;
using Ddm.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ddm.Api.Tests;

public class PublishTests(PostgresFixture pg) : ApiTestBase(pg)
{
    private const string Spec = "openapi: 3.0.3\ninfo: {title: Pay, version: '1'}\npaths:\n  /pay:\n    post:\n      responses:\n        '200': {description: ok}\n";
    private static byte[] T(string s) => Encoding.UTF8.GetBytes(s);
    private static byte[] Folder(params (string Name, byte[] Content)[] files) => Archives.TarGz(files);

    private async Task<HttpClient> AliceAsync()
    {
        var alice = ClientFor("alice");
        await CreateProjectAsync(alice, "p");
        return alice;
    }

    private static async Task<PublishResult> OkAsync(Task<HttpResponseMessage> call)
    {
        var r = await call;
        Assert.True(r.StatusCode == HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
        return await ReadAsync<PublishResult>(r);
    }

    [Fact]
    public async Task A_folder_of_markdown_images_and_a_spec_publishes_in_one_call()
    {
        var alice = await AliceAsync();
        var ci = TokenClient(await CreateTokenAsync(alice, "p", "write"));
        var result = await OkAsync(PublishAsync(ci, "p", Folder(
            ("index.md", T("---\ntags: [home]\n---\n# Home\n\n![logo](images/logo.png)")),
            ("images/logo.png", PngBytes),
            ("apis/payments.yaml", T(Spec))), "?message=abc123"));

        Assert.Equal(3, result.Created.Count);
        Assert.Contains(result.Created, i => i is { Type: "document", Key: "index.md", Version: 1 });
        Assert.Contains(result.Created, i => i is { Type: "asset", Key: "images/logo.png", Version: null });
        Assert.Contains(result.Created, i => i is { Type: "spec", Key: "payments", Version: 1 });
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/v1/projects/p/specs/payments")).StatusCode);
        var history = await ReadAsync<Page<VersionDto>>(await alice.GetAsync("/api/v1/projects/p/docs/index.md/versions"));
        Assert.Equal("abc123", history.Items[0].Message);
        Assert.Equal(["home"], (await ReadAsync<DocumentDto>(await GetDocAsync(alice, "p", "index.md", "application/json"))).Tags);
        Assert.Contains("/content/", await (await GetDocAsync(alice, "p", "index.md", "text/html")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Republishing_the_same_folder_changes_nothing()
    {
        var alice = await AliceAsync();
        var folder = Folder(("a.md", T("# a")), ("b.png", PngBytes));
        await OkAsync(PublishAsync(alice, "p", folder));
        var again = await OkAsync(PublishAsync(alice, "p", folder));
        Assert.Empty(again.Created);
        Assert.Empty(again.Updated);
        Assert.Empty(again.Deleted);
        Assert.Equal(2, again.Unchanged);
    }

    [Fact]
    public async Task Mirror_deletes_what_the_folder_no_longer_has_and_it_can_be_restored()
    {
        var alice = await AliceAsync();
        await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a")), ("b.md", T("# b")), ("c.md", T("# c")))));
        var result = await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a")), ("b.md", T("# b2")))));
        Assert.Equal(["b.md"], result.Updated.Select(i => i.Key));
        Assert.Equal(["c.md"], result.Deleted.Select(i => i.Key));
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "c.md")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alice.PostAsync("/api/v1/projects/p/docs/c.md/versions/1/restore", null)).StatusCode);
    }

    [Fact]
    public async Task A_prefix_publishes_into_a_subtree_and_leaves_the_rest_alone()
    {
        var alice = await AliceAsync();
        await PutDocAsync(alice, "p", "handwritten.md", "# keep");
        await PutDocAsync(alice, "p", "guides/a.md", "# a");
        await PutDocAsync(alice, "p", "guides/old.md", "# old");
        var result = await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a")), ("setup.md", T("# s"))), "?prefix=guides/"));
        Assert.Equal(["guides/setup.md"], result.Created.Select(i => i.Key));
        Assert.Equal(["guides/old.md"], result.Deleted.Select(i => i.Key));
        Assert.Equal(1, result.Unchanged);
        Assert.Equal(HttpStatusCode.OK, (await GetDocAsync(alice, "p", "handwritten.md")).StatusCode);
    }

    // Spec review focus 1: mirror safety
    [Fact]
    public async Task Empty_or_half_built_archives_trip_the_mass_delete_guard()
    {
        var alice = await AliceAsync();
        (string, byte[])[] Docs(int n) => Enumerable.Range(0, n).Select(i => ($"d{i}.md", T($"# {i}"))).ToArray();
        await OkAsync(PublishAsync(alice, "p", Folder(Docs(12))));

        var empty = await PublishAsync(alice, "p", Archives.TarGzEntries());
        Assert.Equal(HttpStatusCode.Conflict, empty.StatusCode);
        Assert.Equal("publish_mass_delete", await ProblemCodeAsync(empty));
        Assert.Equal("publish_mass_delete", await ProblemCodeAsync(await PublishAsync(alice, "p", Folder(Docs(5)))));
        Assert.Equal(12, (await ReadAsync<Page<DocumentSummaryDto>>(await alice.GetAsync("/api/v1/projects/p/docs"))).Items.Count);

        var forced = await OkAsync(PublishAsync(alice, "p", Archives.TarGzEntries(), "?allowMassDelete=true"));
        Assert.Equal(12, forced.Deleted.Count);
    }

    // Spec review focus 2: atomicity
    [Fact]
    public async Task One_bad_file_fails_the_publish_and_writes_nothing()
    {
        var alice = await AliceAsync();
        var r = await PublishAsync(alice, "p", Folder(
            ("good.md", T("# good")),
            ("bad.svg", PngBytes),
            ("api.yaml", T("openapi: 3.0.3\ninfo: {version: '1'}\npaths: {}\n")),
            ("tool.exe", [1])));
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("publish_invalid", body.GetProperty("code").GetString());
        var codes = body.GetProperty("errors").EnumerateArray().Select(e => e.GetProperty("code").GetString()).ToList();
        Assert.Contains("asset_type_mismatch", codes);
        Assert.Contains("invalid_spec", codes);
        Assert.Contains("unsupported_file", codes);
        Assert.Equal(0, Factory.Blobs.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "good.md")).StatusCode);
    }

    [Fact]
    public async Task Two_spec_files_with_the_same_stem_are_refused()
    {
        var alice = await AliceAsync();
        var r = await PublishAsync(alice, "p", Folder(("v1/api.yaml", T(Spec)), ("v2/api.yaml", T(Spec))));
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("duplicate_spec_name", body.GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Dry_run_reports_the_plan_and_writes_nothing()
    {
        var alice = await AliceAsync();
        var plan = await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a"))), "?dryRun=true"));
        Assert.True(plan.DryRun);
        Assert.Equal(["a.md"], plan.Created.Select(i => i.Key));
        Assert.Null(plan.Created[0].Version);
        Assert.Equal(HttpStatusCode.NotFound, (await GetDocAsync(alice, "p", "a.md")).StatusCode);
        Assert.Equal(0, Factory.Blobs.Count);
    }

    [Fact]
    public async Task Zip_archives_publish_too()
    {
        var alice = await AliceAsync();
        var result = await OkAsync(PublishAsync(alice, "p", Archives.Zip(("a.md", T("# a"))), mediaType: "application/zip"));
        Assert.Equal(["a.md"], result.Created.Select(i => i.Key));
    }

    [Fact]
    public async Task Hidden_files_are_ignored_and_reported()
    {
        var alice = await AliceAsync();
        var result = await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a")), (".github/workflows/docs.yml", T("on: push")))));
        Assert.Equal([".github/workflows/docs.yml"], result.Ignored);
    }

    [Fact]
    public async Task Hostile_archives_are_rejected_before_anything_is_written()
    {
        var alice = await AliceAsync();
        var r = await PublishAsync(alice, "p", Archives.TarGzEntries(
            Archives.File("a.md", T("# a")), Archives.Symlink("b.md", "/etc/passwd")));
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("publish_invalid", await ProblemCodeAsync(r));
        Assert.Equal(0, Factory.Blobs.Count);
    }

    [Fact]
    public async Task Bad_media_types_and_prefixes_are_refused()
    {
        var alice = await AliceAsync();
        var folder = Folder(("a.md", T("# a")));
        Assert.Equal("unsupported_archive", await ProblemCodeAsync(await PublishAsync(alice, "p", folder, mediaType: "text/plain")));
        Assert.Equal("invalid_path", await ProblemCodeAsync(await PublishAsync(alice, "p", folder, "?prefix=guides")));
    }

    [Fact]
    public async Task Readers_and_read_tokens_cannot_publish()
    {
        var alice = await AliceAsync();
        await AddMemberAsync(alice, "p", "rd", "reader");
        var folder = Folder(("a.md", T("# a")));
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(ClientFor("rd"), "p", folder)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PublishAsync(TokenClient(await CreateTokenAsync(alice, "p", "read")), "p", folder)).StatusCode);
    }

    [Fact]
    public async Task Publishing_is_audited_per_item_and_as_a_whole()
    {
        var alice = await AliceAsync();
        await OkAsync(PublishAsync(alice, "p", Folder(("a.md", T("# a")), ("b.md", T("# b")))));
        using var scope = Factory.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<DdmDbContext>().AuditEntries.Select(e => e.Action).ToListAsync();
        Assert.Equal(2, actions.Count(a => a == "doc.create"));
        Assert.Single(actions, a => a == "publish");
    }

    // Review Focus 1
    [Fact]
    public async Task A_publish_racing_single_writes_is_all_or_nothing_and_never_500s()
    {
        var alice = await AliceAsync();
        for (var round = 0; round < 15; round++)
        {
            var path = $"r{round}/a.md";
            await PutDocAsync(alice, "p", path, "# v1");
            var results = await Task.WhenAll(
                PutDocAsync(alice, "p", path, $"# edited {round}", ifMatch: "\"v1\""),
                PublishAsync(alice, "p", Folder(("a.md", T($"# published {round}"))), $"?prefix=r{round}/"));

            Assert.All(results, r => Assert.NotEqual(HttpStatusCode.InternalServerError, r.StatusCode));
            Assert.Contains(results[1].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            var history = await ReadAsync<Page<VersionDto>>(await alice.GetAsync($"/api/v1/projects/p/docs/{path}/versions"));
            var expected = 1 + (results[0].StatusCode == HttpStatusCode.OK ? 1 : 0) + (results[1].StatusCode == HttpStatusCode.OK ? 1 : 0);
            Assert.Equal(expected, history.Items.Count);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~PublishTests"`
Expected: build FAILS (`PublishResult` does not exist).

- [ ] **Step 3: Implement options, DTOs and the service**

`src/Ddm.Api/Publishing/PublishOptions.cs`:
```csharp
namespace Ddm.Api.Publishing;

public sealed class PublishOptions
{
    public long MaxArchiveBytes { get; set; } = 100L * 1024 * 1024;
    public long MaxExpandedBytes { get; set; } = 250L * 1024 * 1024;
    public int MaxEntries { get; set; } = 5000;
}
```

`src/Ddm.Api/Publishing/PublishDtos.cs`:
```csharp
namespace Ddm.Api.Publishing;

public sealed record PublishRequest(string Prefix, string Message, bool DryRun, bool AllowMassDelete);

/// <summary>Version is the new version number for documents and specs; null for assets and in a dry run.</summary>
public sealed record PublishedItem(string Type, string Key, int? Version);

public sealed record PublishResult(
    bool DryRun, IReadOnlyList<PublishedItem> Created, IReadOnlyList<PublishedItem> Updated, IReadOnlyList<PublishedItem> Deleted,
    int Unchanged, IReadOnlyList<string> Ignored);
```

`src/Ddm.Api/Publishing/PublishService.cs`:
```csharp
using Ddm.Api.Assets;
using Ddm.Api.Common;
using Ddm.Api.Data;
using Ddm.Api.Documents;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Specs;
using Microsoft.EntityFrameworkCore;

namespace Ddm.Api.Publishing;

/// <summary>
/// Mirrors an archive into a project in one transaction: validate every file, plan against the live state,
/// upload new blobs, then re-plan under the project lock and stage every change through the content services.
/// </summary>
public sealed class PublishService(DdmDbContext db, DocumentService docs, AssetService assets, SpecService specs)
{
    /// <summary>One archive file, validated and ready to stage. Exactly one of Doc, Asset and Spec is set.</summary>
    private sealed record Prepared(ItemKey Key, string Sha256, PreparedDocument? Doc, PreparedAsset? Asset, PreparedSpec? Spec);

    /// <summary>Every document, asset and spec row of a project (tombstones included) and the versions their pointers name.</summary>
    private sealed record Snapshot(
        Dictionary<string, Document> DocRows, Dictionary<string, Asset> AssetRows, Dictionary<string, Spec> SpecRows,
        Dictionary<Guid, ContentVersion> Versions)
    {
        public Dictionary<ItemKey, string> Live()
        {
            var live = new Dictionary<ItemKey, string>();
            foreach (var d in DocRows.Values.Where(d => d.DeletedAt is null && d.CurrentVersionId is not null))
                live[new(ItemType.Document, d.Path)] = Versions[d.CurrentVersionId!.Value].ContentSha256;
            foreach (var a in AssetRows.Values.Where(a => a.DeletedAt is null))
                live[new(ItemType.Asset, a.Path)] = a.Sha256;
            foreach (var s in SpecRows.Values.Where(s => s.DeletedAt is null && s.CurrentVersionId is not null))
                live[new(ItemType.Spec, s.Name)] = Versions[s.CurrentVersionId!.Value].ContentSha256;
            return live;
        }

        public DocumentState? DocStateAt(string path) =>
            DocRows.TryGetValue(path, out var d) ? new(d, d.CurrentVersionId is { } id ? Versions[id] : null) : null;

        public SpecState? SpecStateNamed(string name) =>
            SpecRows.TryGetValue(name, out var s) ? new(s, s.CurrentVersionId is { } id ? Versions[id] : null) : null;
    }

    public async Task<PublishResult> PublishAsync(Caller caller, Project project, ArchiveContents archive, PublishRequest request, CancellationToken ct)
    {
        var prepared = await PrepareAllAsync(archive, request.Prefix, ct);
        var wanted = prepared.ToDictionary(p => p.Key, p => p.Sha256);

        var plan = PublishPlanner.Plan(wanted, (await LoadAsync(project, track: false, ct)).Live(), request.Prefix);
        if (plan.IsMassDelete && !request.AllowMassDelete)
            throw new ApiException(409, "publish_mass_delete", "This publish would delete most of the content in scope",
                $"It deletes {plan.Count(PlanAction.Delete)} of {plan.InScope} items under {ScopeName(request.Prefix)}. " +
                "Re-run with allowMassDelete=true if that is intended.");
        if (request.DryRun) return Result(plan, archive.Ignored, versions: null, dryRun: true);

        var byKey = prepared.ToDictionary(p => p.Key);
        foreach (var step in plan.Steps.Where(s => s.Action is PlanAction.Create or PlanAction.Update))
            await UploadAsync(project, byKey[new(step.Type, step.Key)], ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.LockAsync(project.Id, ct); // one publish per project at a time
        var snapshot = await LoadAsync(project, track: true, ct);
        // Plan again from the rows we are about to write over. If anything in scope moved, stop rather than guess:
        // the blobs uploaded above match the first plan only.
        if (!PublishPlanner.Plan(wanted, snapshot.Live(), request.Prefix).SameAs(plan)) throw Conflict();

        var versions = new Dictionary<ItemKey, int>();
        foreach (var step in plan.Steps.Where(s => s.Action != PlanAction.Unchanged))
            await StageAsync(caller, project, step, byKey, snapshot, request.Message, versions, ct);
        db.Audit(caller, project.Id, "publish",
            $"{ScopeName(request.Prefix)}: +{plan.Count(PlanAction.Create)} ~{plan.Count(PlanAction.Update)} -{plan.Count(PlanAction.Delete)}");
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueViolation())
        {
            // A single-item write committed between our read and our save: the row versions or version numbers collided.
            throw Conflict();
        }
        return Result(plan, archive.Ignored, versions, dryRun: false);
    }

    private async Task<List<Prepared>> PrepareAllAsync(ArchiveContents archive, string prefix, CancellationToken ct)
    {
        var result = new List<Prepared>();
        var errors = new List<PublishError>();
        var specSources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in archive.Files)
        {
            var path = prefix + file.Path;
            try
            {
                switch (PublishPlanner.Classify(file.Path, file.Content))
                {
                    case ItemType.Document:
                    {
                        var doc = DocumentService.Prepare(path, file.Content);
                        result.Add(new(new(ItemType.Document, path), doc.Sha256, doc, null, null));
                        break;
                    }
                    case ItemType.Asset:
                    {
                        var asset = assets.Prepare(path, file.Content);
                        result.Add(new(new(ItemType.Asset, path), asset.Sha256, null, asset, null));
                        break;
                    }
                    case ItemType.Spec:
                    {
                        var name = PublishPlanner.SpecNameFor(file.Path);
                        if (!specSources.TryAdd(name, path))
                        {
                            errors.Add(new(path, "duplicate_spec_name", $"{specSources[name]} already publishes the spec '{name}'"));
                            break;
                        }
                        var spec = await specs.PrepareAsync(name, file.Content, ct);
                        result.Add(new(new(ItemType.Spec, name), spec.Sha256, null, null, spec));
                        break;
                    }
                    default:
                        errors.Add(new(path, "unsupported_file", "Only markdown, OpenAPI specs and allow-listed asset types can be published"));
                        break;
                }
            }
            catch (ApiException ex) when (ex.Status < 500)
            {
                if (ex.Extensions?.GetValueOrDefault("errors") is IEnumerable<SpecError> specErrors)
                    errors.AddRange(specErrors.Select(e => new PublishError(path, ex.Code, e.Message, e.Line)));
                else
                    errors.Add(new(path, ex.Code, ex.Detail ?? ex.Title));
            }
        }
        if (errors.Count > 0)
            throw ApiException.Unprocessable("publish_invalid", "The archive cannot be published", errors, $"{errors.Count} problem(s) found");
        return result;
    }

    private async Task<Snapshot> LoadAsync(Project project, bool track, CancellationToken ct)
    {
        IQueryable<T> Rows<T>(DbSet<T> set) where T : class => track ? set : set.AsNoTracking();
        var pid = project.Id;
        var docRows = await Rows(db.Documents).Where(d => d.ProjectId == pid).ToDictionaryAsync(d => d.Path, StringComparer.Ordinal, ct);
        var assetRows = await Rows(db.Assets).Where(a => a.ProjectId == pid).ToDictionaryAsync(a => a.Path, StringComparer.Ordinal, ct);
        var specRows = await Rows(db.Specs).Where(s => s.ProjectId == pid).ToDictionaryAsync(s => s.Name, StringComparer.Ordinal, ct);
        var ids = docRows.Values.Select(d => d.CurrentVersionId).Concat(specRows.Values.Select(s => s.CurrentVersionId)).OfType<Guid>().ToList();
        var versions = await Rows(db.Versions).Where(v => ids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        return new(docRows, assetRows, specRows, versions);
    }

    private async Task UploadAsync(Project project, Prepared p, CancellationToken ct)
    {
        if (p.Doc is { } doc) await docs.UploadAsync(project, doc, ct);
        else if (p.Asset is { } asset) await assets.UploadAsync(project, asset, ct);
        else await specs.UploadAsync(project, p.Spec!, ct);
    }

    private async Task StageAsync(
        Caller caller, Project project, PlanStep step, Dictionary<ItemKey, Prepared> byKey, Snapshot snapshot,
        string message, Dictionary<ItemKey, int> versions, CancellationToken ct)
    {
        var key = new ItemKey(step.Type, step.Key);
        var delete = step.Action == PlanAction.Delete;
        switch (step.Type)
        {
            case ItemType.Document when delete:
                docs.StageDelete(caller, project, snapshot.DocRows[step.Key]);
                break;
            case ItemType.Document:
                versions[key] = (await docs.StageAsync(caller, project, byKey[key].Doc!, snapshot.DocStateAt(step.Key), message, ct)).Version.Number;
                break;
            case ItemType.Asset when delete:
                assets.StageDelete(caller, project, snapshot.AssetRows[step.Key]);
                break;
            case ItemType.Asset:
                assets.Stage(caller, project, byKey[key].Asset!, snapshot.AssetRows.GetValueOrDefault(step.Key));
                break;
            case ItemType.Spec when delete:
                specs.StageDelete(caller, project, snapshot.SpecRows[step.Key]);
                break;
            case ItemType.Spec:
                versions[key] = specs.Stage(caller, project, byKey[key].Spec!, snapshot.SpecStateNamed(step.Key), message).Version.Number;
                break;
        }
    }

    private static PublishResult Result(PublishPlan plan, IReadOnlyList<string> ignored, Dictionary<ItemKey, int>? versions, bool dryRun)
    {
        List<PublishedItem> Of(PlanAction action) => plan.Steps
            .Where(s => s.Action == action)
            .Select(s => new PublishedItem(Wire.Lower(s.Type), s.Key,
                versions is not null && versions.TryGetValue(new(s.Type, s.Key), out var v) ? v : null))
            .ToList();
        return new(dryRun, Of(PlanAction.Create), Of(PlanAction.Update), Of(PlanAction.Delete), plan.Count(PlanAction.Unchanged), ignored);
    }

    private static string ScopeName(string prefix) => prefix.Length == 0 ? "/" : prefix;

    private static ApiException Conflict() =>
        new(409, "publish_conflict", "Content changed while publishing", "Another write touched content in scope during this publish; retry it");
}
```

- [ ] **Step 4: Map the endpoint**

`src/Ddm.Api/Publishing/PublishEndpoints.cs`:
```csharp
using System.Security.Claims;
using Ddm.Api.Assets;
using Ddm.Api.Common;
using Ddm.Api.Documents;
using Ddm.Api.Domain;
using Ddm.Api.Identity;
using Ddm.Api.Specs;
using Microsoft.Extensions.Options;

namespace Ddm.Api.Publishing;

public static class PublishEndpoints
{
    public static void MapPublish(this RouteGroupBuilder v1) => v1.MapPost("/projects/{slug}/publish", PublishAsync);

    private static async Task<IResult> PublishAsync(
        string slug, string? prefix, bool? dryRun, bool? allowMassDelete, HttpContext http, ClaimsPrincipal user,
        ProjectAuthorizer authz, PublishService publisher, AssetService assets, SpecService specs,
        IOptions<PublishOptions> options, CancellationToken ct)
    {
        var caller = Caller.From(user);
        var access = await authz.RequireAsync(caller, slug, Role.Editor, ct);
        var format = http.Request.GetTypedHeaders().ContentType?.MediaType.Value?.ToLowerInvariant() switch
        {
            "application/gzip" or "application/x-gzip" or "application/x-tar+gzip" => ArchiveFormat.TarGz,
            "application/zip" => ArchiveFormat.Zip,
            _ => throw new ApiException(415, "unsupported_archive", "Send a tar.gz (application/gzip) or zip (application/zip) archive"),
        };
        prefix ??= "";
        if (ContentPath.ValidateFolder(prefix) is { } error) throw ApiException.BadRequest("invalid_path", "Invalid prefix", error);
        var message = DocumentEndpoints.MessageFrom(http.Request) ?? "Publish";
        var o = options.Value;

        // Spool the upload to a temp file (deleted on close) so a 100 MB archive never sits in memory whole.
        RequestBody.AllowUpTo(http, o.MaxArchiveBytes);
        if (http.Request.ContentLength > o.MaxArchiveBytes) throw TooLarge(o);
        await using var spool = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        var buffer = new byte[81920];
        int n;
        while ((n = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (spool.Length + n > o.MaxArchiveBytes) throw TooLarge(o);
            await spool.WriteAsync(buffer.AsMemory(0, n), ct);
        }
        spool.Position = 0;

        var maxEntry = Math.Max(DocumentService.MaxBytes, Math.Max(assets.MaxBytes, specs.MaxBytes));
        var archive = await ArchiveReader.ReadAsync(spool, format, new ArchiveLimits(o.MaxExpandedBytes, o.MaxEntries, maxEntry), ct);
        var result = await publisher.PublishAsync(caller, access.Project, archive,
            new PublishRequest(prefix, message, dryRun == true, allowMassDelete == true), ct);
        return Results.Ok(result);
    }

    private static ApiException TooLarge(PublishOptions o) =>
        new(413, "archive_too_large", "The archive is too large", $"Archives are limited to {o.MaxArchiveBytes} bytes");
}
```

`src/Ddm.Api/Publishing/PublishSetup.cs`:
```csharp
namespace Ddm.Api.Publishing;

public static class PublishSetup
{
    public static IServiceCollection AddDdmPublishing(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PublishOptions>(config.GetSection("Publish"));
        services.AddScoped<PublishService>();
        return services;
    }
}
```

In `src/Ddm.Api/Program.cs`: add `using Ddm.Api.Publishing;`, `builder.Services.AddDdmPublishing(builder.Configuration);` after `AddDdmSpecs`, and `v1.MapPublish();` after `v1.MapSpecs();`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~PublishTests"`
Expected: PASS, including the racing test (Review Focus 1) and the mass-delete and atomicity tests.

- [ ] **Step 6: Commit**

```bash
git add src/Ddm.Api tests/Ddm.Api.Tests
git commit -m "feat: atomic mirror publish of a docs folder from tar.gz or zip" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 15: Documentation and full verification

**Files:**
- Modify: `README.md`, `tests/Ddm.Api.Tests/OpenApiTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: README configuration and CI publishing docs; an OpenAPI test that pins the new routes.

- [ ] **Step 1: Pin the new routes in the API's own OpenAPI document**

In `tests/Ddm.Api.Tests/OpenApiTests.cs`, add after the existing `Assert.True(paths.TryGetProperty(...))` lines:
```csharp
        Assert.True(paths.TryGetProperty("/api/v1/projects/{slug}/specs/{name}", out _));
        Assert.True(paths.TryGetProperty("/api/v1/projects/{slug}/publish", out _));
        Assert.True(paths.TryGetProperty("/api/v1/projects/{slug}/tags", out _));
```

Run: `dotnet test --filter "FullyQualifiedName~OpenApiTests"`
Expected: PASS.

- [ ] **Step 2: Document configuration and publishing**

In `README.md`, add these rows to the configuration table:
```markdown
| `Assets:MaxBytes` | Largest asset upload in bytes (default 10 MB) |
| `Specs:MaxBytes` | Largest OpenAPI spec in bytes (default 5 MB) |
| `Content:SigningKey` | HMAC key (32+ characters) for signed image links in rendered pages; required everywhere |
| `Content:BaseUrl` | Separate origin that serves `/content` (required outside Development; empty means same-origin) |
| `Publish:MaxArchiveBytes`, `Publish:MaxExpandedBytes`, `Publish:MaxEntries` | Bulk publish limits (100 MB, 250 MB, 5,000 entries) |
```
change the endpoints line to:
```markdown
Endpoints: `/healthz` (liveness), `/readyz` (database), `/metrics` (Prometheus, internal only), `/api/v1/openapi.json`, `/content/{project}/{sha}` (signed image links, anonymous).
```
and add a section:
````markdown
## Publishing from CI

A write token can mirror a docs folder into a project in one atomic call. Markdown becomes documents, YAML/JSON
with a top-level `openapi` key becomes a spec named after the file, and allow-listed images and files become assets.
Anything in the project (or under `prefix`) that the folder no longer has is deleted; deletes keep history and can be restored.

    tar czf docs.tgz -C docs .
    curl --fail -X POST \
      -H "Authorization: Bearer $DDM_TOKEN" -H "Content-Type: application/gzip" \
      --data-binary @docs.tgz \
      "https://ddm.example.com/api/v1/projects/payments/publish?message=$GITHUB_SHA"

Add `dryRun=true` to see the plan without writing. A publish that would delete everything in scope, or more than half
of 10+ items, is refused with `409 publish_mass_delete` unless you add `allowMassDelete=true`.
````

- [ ] **Step 3: Run the whole suite**

Run: `dotnet build && dotnet test`
Expected: the build introduces no new warnings compared with `main`, and every test passes (phase 1 and phase 2).

- [ ] **Step 4: Commit**

```bash
git add README.md tests/Ddm.Api.Tests/OpenApiTests.cs
git commit -m "docs: phase 2 configuration and CI publishing" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Spec Coverage

| Spec section | Tasks |
| --- | --- |
| 2.1 Shared item seam | 2 |
| 2.2 Tombstones (documents / assets / specs) | 3 / 7 / 11 |
| 2.3–2.5 New entities, changed entities, blob keys | 2, 3, 7, 11 |
| 3.1–3.4 Asset endpoints, validation, storage, inert serving | 6, 7 |
| 3.5 Signed content URLs and image rewriting | 8, 9 |
| 4.1–4.3 Spec endpoints, validation pipeline, stored data | 10, 11 |
| 5.1–5.4 Tag names, endpoints, front matter, filtering | 4, 5, 7, 11 |
| 6.1–6.6 Publish endpoint, archive rules, classification, pipeline, response, batchable services | 3, 7, 11, 12, 13, 14 |
| 7 Changes to phase 1 code | 1, 2, 3, 4, 5, 7, 9 |
| 8 Module layout and configuration | all; README in 15 |
| 9 Error codes | 1, 4, 6, 7, 8, 10, 11, 12, 14 |
| 10 Testing and review focus | each task's tests; Review Focus above |
