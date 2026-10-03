# DDM Phase 2: Assets, Specs, Tags and Bulk Publish — Design

Oct 3, 2026 · Status: draft for review

**Parent design:** `Developer Documentation Manager Design Document.md` (repo root)
**Builds on:** phase 1 core API (`docs/superpowers/plans/2026-10-01-ddm-phase1-core-api.md`, merged in e36b0d7)

## 1. Intent

Phase 2 completes the MVP backend's content story so that a CI pipeline can publish a whole docs folder — markdown, images and OpenAPI specs — in one atomic call, and every item in a project can be tagged and filtered.

**In scope:** assets (F3), OpenAPI spec upload, validation and versioning (F4, API side only), tags (F5), bulk publish (F8, the part phase 1 deferred), and tombstone deletes for all content types.

**Out of scope (later spec):** web client and spec viewer, search (F6), version diff, webhooks (F10), outbox and workers, rendered-HTML cache, pre-signed direct uploads, thumbnails, blob garbage collection, global tags, public visibility, releases.

**Success looks like:** a CI job uploads one archive containing markdown, images and an `openapi.yaml`; the project (or a subtree of it) then exactly mirrors that folder in a single transaction; rendered pages show their images; documents and specs are versioned; every item can carry tags and be filtered by them.

### Decisions taken during brainstorming

| Topic | Decision |
| --- | --- |
| Phase 2 scope | Content types + tags + bulk publish; discovery, async and the web client follow later |
| Publish semantics | Mirror: Git is the source of truth; items missing from the archive are deleted within the publish scope; optional `prefix` limits the scope to a subtree |
| Deletes | Tombstones for documents, assets and specs; versions are kept; changes phase 1 `DELETE` behaviour |
| Assets | Images and small files, 10 MB cap, uploaded through the API; no pre-signed uploads yet |
| Tags | Project-scoped, free-form, normalised, created on first use; no global tags |
| Spec rendering | API only: original plus normalised JSON; the viewer ships with the web client |
| Structure | One module per content type plus a thin shared item seam (not a unified items table) |

Phase 1 conventions carry over unchanged: `/api/v1`, one `ProjectAuthorizer`, RFC 9457 problems with a stable `code`, cursor paging, `If-Match`/`If-None-Match: *`, `?message=` on writes, audit on every write, and `404 project_not_found` for anyone who cannot read the project.

## 2. Data model

### 2.1 Shared item seam

- `ItemType` gains `Asset = 3` and `Project = 4` (existing `Document = 1`, `Spec = 2` keep their values).
- `ItemRef(ItemType Type, Guid Id)` (in `Domain/`) is how tags and publish refer to any item.

### 2.2 Tombstones

`Document`, `Asset` and `Spec` each gain `DateTimeOffset? DeletedAt`.

- `DELETE` sets `DeletedAt` and keeps the row and all versions. The existing unique indexes `(ProjectId, Path)` / `(ProjectId, Name)` are unchanged.
- Writing to a tombstoned path or name **revives the same row**: `DeletedAt` is cleared and version numbers continue (a document deleted at v7 comes back as v8), so history is never lost and an ETag is never reused.
- Current reads, listings, tag endpoints and precondition checks treat a tombstone as missing (`404 document_not_found` etc.; `If-None-Match: *` succeeds; `If-Match` fails with 412).
- Version history (`…/versions`, `…/versions/{n}`) and restore keep working on a tombstoned item. Restore revives it.
- Listings accept `?deleted=true` to return only tombstoned items, so a client can find what to restore.
- A tombstone's tag assignments are kept, so a revived item comes back with its tags.

### 2.3 New entities

| Entity | Fields | Constraints |
| --- | --- | --- |
| `Asset` | `Id, ProjectId, Path, ContentType, Size, Sha256, StorageKey, UpdatedAt, UpdatedBy, DeletedAt` | Unique `(ProjectId, Path)`, `Path` collation `C`, max 255. Not versioned (parent design). |
| `Spec` | `Id, ProjectId, Name, Title, ApiVersion, OpenApiVersion, Format, Operations (jsonb), CurrentVersionId, UpdatedAt, DeletedAt` | Unique `(ProjectId, Name)`, collation `C`. `Format` is `yaml` or `json`. `Operations` is a JSON array of `{method, path, operationId, summary, tags}`, kept for listing now and search later. |
| `Tag` | `Id, ProjectId, Name` | Unique `(ProjectId, Name)`. Cascade on project delete. |
| `TagAssignment` | `TagId, ItemType, ItemId` | Composite primary key. Index `(ItemType, ItemId)`. Cascade on tag delete. |

### 2.4 Changed entities

- `ContentVersion` gains `string? NormalizedRef`. For specs, `ContentRef` is the original bytes and `NormalizedRef` the normalised JSON. Always null for documents.
- `Document`, `Asset`, `Spec` gain `DeletedAt` (above).

### 2.5 Blob keys (all content-addressed by SHA-256)

| Content | Key |
| --- | --- |
| Asset | `projects/{pid:N}/assets/{sha}` |
| Spec original | `projects/{pid:N}/specs/{sha}.{yaml\|json}` |
| Spec normalised | `projects/{pid:N}/specs/{sha}.normalized.json` |

One EF migration, `Phase2Content`, adds all of the above. No data backfill is needed: existing rows get `DeletedAt = null`.

## 3. Assets (F3)

### 3.1 Endpoints (`/api/v1/projects/{slug}/assets`)

| Endpoint | Role | Behaviour |
| --- | --- | --- |
| `PUT /{path}` | editor | Raw body. `If-None-Match: *` is create-only; `If-Match: "<sha>"` replaces; replacing an existing asset without `If-Match` is `428`. Identical bytes are a no-op (`200`, no audit). Returns `201` or `200` with the asset DTO and `ETag: "<sha>"`. |
| `POST /` | editor | `multipart/form-data` with `path` and `file`; create-only (`409 asset_exists` if live). |
| `GET /{path}`, `HEAD /{path}` | reader | Streams bytes with `ETag`, `Content-Type` (sniffed type), and the serving headers below. Honours `If-None-Match` with `304`. |
| `GET /?prefix=&tag=&deleted=&cursor=&limit=` | reader | Metadata list ordered by path: `{path, contentType, size, sha256, tags, updatedAt, updatedBy}`. |
| `DELETE /{path}` | editor | Tombstone. Optional `If-Match`. `204`. |

Asset ETags are the quoted lowercase hex SHA-256. `Preconditions` is generalised to parse either a version ETag (`"v7"`) or an opaque ETag, and each service checks the form it expects (`400 invalid_etag` otherwise).

### 3.2 Validation on write

- **Path:** the phase 1 segment rules, must **not** end `.md`, and the extension must be on the allow-list.
- **Size:** `Assets:MaxBytes`, default 10 MB. Checked against `Content-Length` before reading and again while reading, so a lying client still gets `413`. Per-endpoint Kestrel `MaxRequestBodySize` is set to the cap plus multipart overhead.
- **Type:** sniffed from the bytes; the client's `Content-Type` is ignored. The sniffed type must match the extension, otherwise `400 asset_type_mismatch`.

| Extension | Sniff rule | Served as |
| --- | --- | --- |
| `.png` | PNG signature | `image/png` |
| `.jpg`, `.jpeg` | `FF D8 FF` | `image/jpeg` |
| `.gif` | `GIF87a` / `GIF89a` | `image/gif` |
| `.webp` | `RIFF....WEBP` | `image/webp` |
| `.ico` | `00 00 01 00` | `image/x-icon` |
| `.svg` | valid UTF-8 XML whose root element is `svg` (parsed with DTD processing prohibited) | `image/svg+xml` |
| `.pdf` | `%PDF-` | `application/pdf` |
| `.txt`, `.csv`, `.json`, `.yaml`, `.yml` | valid UTF-8, no NUL bytes | `text/plain`, `text/csv`, `application/json`, `application/yaml` (with `charset=utf-8`) |

Any other extension is `415 unsupported_asset_type`.

### 3.3 Storage

The request body is streamed into a pooled buffer (bounded by the cap) while the SHA-256 is computed, then written to S3 before the database transaction, as documents do: a failed commit leaves only a harmless content-addressed orphan. `IBlobStore` gains `PutAsync(string key, Stream content, long length, string contentType, ct)` and `OpenReadAsync(string key, ct)` (returns `null` when missing) so reads stream instead of buffering. Replacing an asset keeps the old blob; garbage collection is out of scope.

### 3.4 Serving untrusted bytes

Every asset response (API and `/content`) sets:

- `X-Content-Type-Options: nosniff`
- `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; sandbox`
- `Content-Disposition: attachment` for every type that is not an image (PDF and text included)

### 3.5 Images in rendered markdown: signed content URLs

A browser loads `<img>` without a bearer token, so rendered HTML carries signed URLs.

1. `MarkdownRenderer` takes a render context (project, document path, asset lookup). Before rendering it walks Markdig's AST and collects every image (`LinkInline.IsImage`) whose URL is relative (no scheme, no leading `/`, no `//`).
2. Each URL is resolved against the document's folder (`guides/setup.md` + `../images/a.png` → `images/a.png`). A path that escapes the project root, or fails asset path validation, is left unresolved.
3. One query maps the resolved paths to the current SHAs of live assets.
4. Each resolved image's URL becomes `{Content:BaseUrl}/content/{projectId:N}/{sha}?exp={unix}&sig={base64url}`, where `sig = HMAC-SHA256(Content:SigningKey, "{projectId:N}/{sha}/{exp}")`. `exp` is the end of the current 24-hour UTC window plus 24 hours, so a URL is stable for a day (cacheable) and lives at most 48 hours.
5. An image with no matching asset keeps its original `src` and gains class `ddm-missing-asset`.
6. Links to other documents are left untouched; the web client routes them.

Rendering an older version resolves images against the **current** assets (assets are not versioned).

`GET /content/{projectId}/{sha}` is mapped outside the authenticated `/api/v1` group:

- Bad or missing signature: `403 invalid_signature`. Expired: `403 signature_expired`. Signature comparison is constant-time.
- The SHA must belong to an asset row (tombstoned included) in that project, otherwise `404 not_found`.
- Serves the bytes with the headers in 3.4 plus `Cache-Control: private, max-age=<seconds until exp>` and `ETag`.

`Content:BaseUrl` is a separate origin in production (parent design: user content must not share the app's origin) and the API's own origin in development. `Content:SigningKey` is required outside Development and Testing; the app refuses to start without it, matching the phase 1 auth guard.

**Accepted trade-off:** a user removed from a project can keep loading images from HTML they already fetched for up to 48 hours. They gain no new access.

## 4. OpenAPI specs (F4)

### 4.1 Endpoints (`/api/v1/projects/{slug}/specs`)

Spec names match `^[a-z0-9][a-z0-9._-]{0,99}$`. Names contain no `/`, so the suffix routes below are unambiguous.

| Endpoint | Role | Behaviour |
| --- | --- | --- |
| `GET /?tag=&deleted=&cursor=&limit=` | reader | `{name, title, apiVersion, openApiVersion, format, version, operationCount, tags, updatedAt}` ordered by name. |
| `POST /` | editor | JSON `{name, content, message}`; create-only (`409 spec_exists`). Format detected from content. |
| `PUT /{name}` | editor | Raw body with `Content-Type` `application/yaml`, `application/x-yaml`, `text/yaml` or `application/json` (`415` otherwise). Same precondition rules as documents (`"v7"` ETags, `428` on update without `If-Match`, `If-None-Match: *`). Identical bytes are a no-op. `?message=`. |
| `GET /{name}` | reader | The original bytes in their original media type, `ETag: "v{n}"`. |
| `GET /{name}/normalized` | reader | The normalised JSON (`application/json`). |
| `GET /{name}/operations` | reader | The stored operations summary. |
| `GET /{name}/versions`, `GET /{name}/versions/{n}`, `POST /{name}/versions/{n}/restore` | reader / reader / editor | Same shape and semantics as documents. `GET …/versions/{n}/normalized` returns that version's normalised JSON. |
| `DELETE /{name}` | editor | Tombstone. Optional `If-Match`. |

### 4.2 Validation pipeline

1. **Size:** `Specs:MaxBytes`, default 5 MB; `413` above it.
2. **Encoding and format:** strict UTF-8 (`400 invalid_encoding`). A document whose first non-whitespace character is `{` is JSON, otherwise YAML.
3. **Safe-YAML pre-scan:** the alias and depth scan in `FrontMatter` is extracted into a shared `SafeYaml` helper. Specs reject aliases and nesting deeper than 64 levels before any library sees the content.
4. **Version gate:** the top-level `openapi` field must be `3.0.x` or `3.1.x`. Swagger 2.0 or a missing field is `422 unsupported_openapi_version`.
5. **External references:** every `$ref` must start with `#`. Any other `$ref` is reported as an error at its location (`external_ref_not_allowed`), and the parser is configured never to load external references (SSRF control from the parent design).
6. **Parse and validate** with `Microsoft.OpenApi` 2.x and `Microsoft.OpenApi.YamlReader`, collecting all errors (warnings are ignored).
7. **Locate errors:** each error's JSON pointer is mapped to a line and column by walking a YamlDotNet representation of the same text, which handles JSON input too. If a pointer cannot be mapped, `line` and `column` are null.

Any error fails the write with `422 invalid_spec` and an `errors` array, so a client sees every problem at once:

```json
{
  "type": "about:blank", "title": "The OpenAPI document is not valid", "status": 422, "code": "invalid_spec",
  "errors": [ { "pointer": "/paths/~1pets/get/responses", "line": 14, "column": 9, "message": "…" } ]
}
```

### 4.3 What a successful write stores

- `Title` = `info.title`, `ApiVersion` = `info.version`, `OpenApiVersion` = the document's `openapi` value, `Format` as detected.
- `Operations` from every path item × operation.
- The **normalised form**: the parsed document serialised as JSON in its own OpenAPI version, with internal `$ref`s preserved (not inlined, because schemas may be recursive). It is stored as a second blob referenced by `NormalizedRef`.
- A new `ContentVersion` (`ItemType.Spec`), the current pointer move, and an audit entry (`spec.create` / `spec.update`), in one transaction, as documents do.

## 5. Tags (F5)

### 5.1 Names

Input is trimmed and lowercased, and runs of whitespace or `_` become `-`. The result must match `^[a-z0-9][a-z0-9-]{0,49}$`, otherwise `400 invalid_tag` naming the offending value. An item has at most 20 tags (`400 too_many_tags`). Duplicates after normalisation collapse silently. A `Tag` row is created on first use in a project.

### 5.2 Endpoints

| Endpoint | Role | Behaviour |
| --- | --- | --- |
| `GET /projects/{slug}/tags` | reader | Project vocabulary `{name, count}`, where count is assignments on live items, ordered by name. |
| `DELETE /projects/{slug}/tags/{name}` | admin | Removes the tag and all its assignments. `404 tag_not_found` if absent. |
| `PUT /projects/{slug}/docs/{path}/tags` | editor | Body `{"tags": [...]}` replaces the set. `409 tags_managed_by_front_matter` if the current version's front matter has a `tags` key. |
| `PUT /projects/{slug}/assets/{path}/tags` | editor | Replaces the set. |
| `PUT /projects/{slug}/specs/{name}/tags` | editor | Replaces the set. |
| `PATCH /projects/{slug}` with `tags` | admin | Project tags travel on the existing project PATCH (`UpdateProjectRequest` gains `tags`). |

`DocRoute` gains a `/tags` suffix (unambiguous because document paths end `.md`). Asset routes use the same suffix rule, which is unambiguous because asset paths must end in an allow-listed extension.

Tag writes are metadata, not content: they do not create versions or change ETags, use last-writer-wins without preconditions, and are audited as `tags.set` with the item as target. Setting tags on a tombstoned or missing item is `404`.

### 5.3 Front matter

On every document write, if the front matter has a `tags` key (a string or a list of strings), the document's tag set is replaced with it inside the write transaction. An invalid tag fails the write with `400 invalid_tag`. If the key is absent, existing API-set tags are left unchanged.

### 5.4 Filtering

Every listing (projects, documents, assets, specs) accepts repeated `?tag=` parameters, combined with AND. `GET /projects?tag=` matches tag names across all projects the caller can see. Filtering applies after `ProjectAuthorizer.VisibleTo`, so it never widens visibility. Every list DTO and the document JSON representation gain `tags: string[]`, and `ProjectDto` gains `tags`.

## 6. Bulk publish (F8)

### 6.1 Endpoint

`POST /api/v1/projects/{slug}/publish?prefix=&message=&dryRun=&allowMassDelete=` — editor role (a write token qualifies).

- **Body:** `application/gzip` (a tar.gz) or `application/zip`, otherwise `415 unsupported_archive`.
- **`prefix`:** optional folder such as `guides/v2/`. It must be empty or a valid folder path ending `/`. Archive entries map to `prefix + entryPath`, so CI can tar the *contents* of its docs folder.
- **`message`:** used as the version message for every item written; defaults to `Publish`. CI typically passes the commit SHA.
- **`dryRun=true`:** returns the plan without writing anything.

### 6.2 Archive rules

| Rule | Limit or behaviour |
| --- | --- |
| Compressed size | `Publish:MaxArchiveBytes`, default 100 MB → `413 archive_too_large` |
| Total uncompressed size | `Publish:MaxExpandedBytes`, default 250 MB, counted while extracting (zip-bomb guard) → `413 archive_too_large` |
| Entry count | `Publish:MaxEntries`, default 5,000 → `413 archive_too_large` |
| Entry kinds | Regular files and directories only. Symlinks, hard links and devices fail the publish. |
| Entry paths | Normalised to `/` separators. Absolute paths and `..` segments fail the publish. |
| Hidden entries | Any entry with a segment starting `.` (`.git/`, `.DS_Store`, `.github/`) is skipped and listed in `ignored`. |

The upload is streamed to a temporary file (deleted on completion) and read from there. It is never held in memory whole. The endpoint's Kestrel `MaxRequestBodySize` is set to `Publish:MaxArchiveBytes`.

Per-entry limits are the per-type limits (documents 1 MB, assets `Assets:MaxBytes`, specs `Specs:MaxBytes`). A spec file stem that is not a valid spec name after lowercasing is an `invalid_spec_name` error.

### 6.3 Classification

| Entry | Becomes |
| --- | --- |
| `*.md` | Document at `prefix + path` |
| `*.yaml`, `*.yml`, `*.json` whose top level has an `openapi` key | Spec named after the file stem, lowercased (`apis/payments.v2.yaml` → `payments.v2`) |
| Any other allow-listed asset extension | Asset at `prefix + path` |
| Anything else | Error `unsupported_file` |

Two spec files with the same stem are an error (`duplicate_spec_name`).

### 6.4 Pipeline

1. **Validate everything, write nothing.** Every entry runs through its type's full validation (paths, sizes, UTF-8, front matter and tags, asset sniffing, spec validation). All errors are collected; if any exist the response is `422 publish_invalid` with `errors: [{path, code, message, line?}]`.
2. **Plan.** Each item is compared by SHA-256 with the live project state:
   - `create`: missing or tombstoned
   - `update`: different SHA
   - `unchanged`: same SHA
   - `delete`: live, in scope, absent from the archive

   **Scope:** documents and assets whose path starts with `prefix` (everything when `prefix` is empty). Specs have no paths, so they are deleted only when `prefix` is empty. With a prefix, spec files in the archive are still created and updated.
3. **Mass-delete guard.** If the plan deletes every in-scope item, or more than half of them when the scope holds at least 10 items, the response is `409 publish_mass_delete` with the counts, unless `allowMassDelete=true`. This protects against an empty or half-built archive; tombstones make any delete recoverable regardless.
4. **Dry run** stops here and returns the plan.
5. **Upload blobs** for every `create` and `update` in the plan. Content-addressed, so a later failure leaves only harmless orphans.
6. **One database transaction.** Take the project row lock (`ProjectLocks.LockAsync`, which also serialises concurrent publishes to one project). Under the lock, recompute the plan from the current state.
   - If the recomputed plan differs from the plan in step 2 (someone wrote to an in-scope item meanwhile), roll back with `409 publish_conflict`. Otherwise every blob the plan needs is already uploaded.
   - Stage every create, update, revival and tombstone, front-matter tag sets, and per-item audit entries (`doc.update` etc. with the actor), plus one `publish` audit entry summarising counts. Commit.
   - Publish does not use `If-Match`: Git wins over interactive edits made *before* the publish started.
   - A concurrent single-item write that takes a version number after the re-plan causes a unique violation, which also rolls back with `409 publish_conflict`. The conflict is safe to retry.

### 6.5 Response

`200` (also for dry run, with `"dryRun": true`):

```json
{
  "dryRun": false,
  "created":   [ { "type": "document", "key": "guides/setup.md", "version": 1 } ],
  "updated":   [ { "type": "spec", "key": "payments", "version": 4 } ],
  "deleted":   [ { "type": "asset", "key": "images/old.png" } ],
  "unchanged": 37,
  "ignored":   [ ".github/workflows/docs.yml" ]
}
```

### 6.6 Making services batchable

Publish must stage many writes in one transaction, so each content service splits its write into two halves:

- `PrepareAsync(project, key, bytes)` validates, computes the SHA and derived data (parsed front matter, sniffed type, spec summary and normalised JSON), and returns a `Prepared*` record. It touches neither the database nor blob storage.
- `Stage(caller, project, prepared, existing, message)` adds entities to the `DbContext` without saving.

The single-item endpoints call prepare → upload blob → stage → `SaveChanges`, so publish and single writes share exactly one code path per type.

## 7. Changes to phase 1 code

| Area | Change |
| --- | --- |
| `DocumentService.DeleteAsync` | Tombstones instead of deleting the row and its versions; keeps the `FOR UPDATE` lock and `If-Match` check. |
| `DocumentService` reads, list, preconditions | Treat `DeletedAt != null` as missing; writes revive tombstones and continue numbering; history and restore work on tombstones. Document list gains `?deleted=` and `?tag=`. |
| `DocumentService.WriteAsync` | Split into prepare and stage (6.6); applies front-matter tags. |
| `DocRoute` | Adds the `/tags` suffix. |
| `Preconditions` | Accepts opaque ETags as well as `"v{n}"`. |
| `FrontMatter` | YAML safety scan extracted to `Common/SafeYaml`. |
| `MarkdownRenderer` | Takes a render context and rewrites relative image URLs (3.5). |
| `ProjectDto`, `UpdateProjectRequest`, `GET /projects` | `tags` field, `?tag=` filter. |
| `IBlobStore` | Streaming put and read (3.3). |
| `Program.cs` | One registration line per new module; `/content` mapped outside the `/api/v1` group. |

## 8. Module layout

```
src/Ddm.Api/
  Common/      + SafeYaml, ContentPath (shared segment rules; DocumentPath and AssetPath build on it)
  Domain/      + Asset, Spec, Tag, TagAssignment, ItemRef
  Assets/      AssetPath, ContentSniffer, AssetService, AssetEndpoints, AssetDtos,
               ContentUrlSigner, ContentEndpoints (/content), AssetResolver (used by MarkdownRenderer)
  Specs/       SpecName, SpecValidator, PointerLocator, SpecService, SpecEndpoints, SpecRoute, SpecDtos
  Tags/        TagName, TagService (SetAsync(ItemRef, names), filter helpers), TagEndpoints
  Publishing/  ArchiveReader (tar.gz + zip, limits), PublishPlanner (classify + diff),
               PublishService (guard + apply), PublishEndpoints, PublishDtos
```

New packages: `Microsoft.OpenApi` and `Microsoft.OpenApi.YamlReader` (2.x). Archives use the built-in `System.Formats.Tar`, `System.IO.Compression.GZipStream` and `ZipArchive`.

New configuration: `Assets:MaxBytes`, `Specs:MaxBytes`, `Content:BaseUrl`, `Content:SigningKey`, `Publish:MaxArchiveBytes`, `Publish:MaxExpandedBytes`, `Publish:MaxEntries`, all documented in the README.

## 9. Error codes added

| Status | Codes |
| --- | --- |
| 400 | `invalid_tag`, `too_many_tags`, `asset_type_mismatch`, `invalid_spec_name`, `invalid_etag` (extended), `invalid_encoding` (reused) |
| 403 | `invalid_signature`, `signature_expired` |
| 404 | `asset_not_found`, `spec_not_found`, `tag_not_found` |
| 409 | `asset_exists`, `spec_exists`, `tags_managed_by_front_matter`, `publish_mass_delete`, `publish_conflict` |
| 413 | `payload_too_large` (reused), `archive_too_large` |
| 415 | `unsupported_asset_type`, `unsupported_archive` |
| 422 | `invalid_spec`, `unsupported_openapi_version`, `publish_invalid` |

Errors that list several problems (`invalid_spec`, `publish_invalid`) carry an `errors` extension array. `ProblemCodes.ForStatus` gains `422 => "unprocessable_content"` as the fallback.

## 10. Testing

Same infrastructure as phase 1: xUnit v2, Testcontainers PostgreSQL, `InMemoryBlobStore` (extended with the streaming methods), and the RustFS-backed S3 tests for the new `IBlobStore` methods. One test file per feature.

**Review focus** (each needs explicit tests):

1. **Mirror safety:** an empty archive, a wrong prefix and a half-sized archive all hit `publish_mass_delete`; `allowMassDelete=true` proceeds; every deleted item is restorable afterwards.
2. **Atomicity:** a publish with one invalid file writes nothing; a publish racing a single-item `PUT` ends with either all of the publish or a `409 publish_conflict`, never a partial state.
3. **Archive hostility:** `..` paths, absolute paths, symlinks and hard links, a zip bomb exceeding the expanded limit, and more than the maximum number of entries are each rejected before anything is written.
4. **Untrusted content:** a PNG renamed `.svg`, an SVG with a `<script>` (served with the sandbox CSP), an SVG with a DTD or external entity, an HTML file renamed `.txt` (served as `attachment` with `nosniff`), and a spec with an external `$ref` or a YAML alias bomb.
5. **Tombstones:** delete then re-create continues version numbers; restore of a tombstoned document revives it; tombstones are absent from listings and current reads but present with `?deleted=true`.
6. **Signed URLs:** a tampered signature, an expired `exp`, a SHA from another project, and a valid URL; bucketed `exp` is stable within a window.
7. **Tag isolation:** `?tag=` on `GET /projects` never returns a project the caller cannot see; a read token for project A cannot read or set tags in project B.
8. **Front matter ownership:** a document with `tags:` rejects `PUT …/tags` with `409`; removing the `tags:` key in a later write leaves the existing tags in place.
9. **Spec errors:** line and column numbers are correct for a YAML spec and a JSON spec; multiple errors are all reported.
