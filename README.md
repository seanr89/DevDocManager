# DocManagers
Developer Document Manager Tooling

## Running locally

    docker compose up -d
    dotnet run --project src/Ddm.Api      # Development env migrates the DB on start
    dotnet test                           # needs Docker for Testcontainers

## Configuration

| Key | Purpose |
| --- | --- |
| `ConnectionStrings:Ddm` | PostgreSQL connection string |
| `Database:MigrateOnStart` | `true` applies EF migrations at startup (Development default) |
| `Auth:Authority`, `Auth:Audience` | OIDC provider for user bearer tokens (required in Production) |
| `Auth:DevSigningKey`, `Auth:Issuer` | Symmetric dev/test signing key; the app refuses to start with it in Production |
| `Storage:ServiceUrl`, `AccessKey`, `SecretKey`, `Bucket`, `ForcePathStyle` | S3-compatible object storage |
| `Storage:CreateBucket` | `true` creates the bucket at startup (local S3: RustFS in docker-compose) |
| `Assets:MaxBytes` | Largest asset upload in bytes (default 10 MB) |
| `Specs:MaxBytes` | Largest OpenAPI spec in bytes (default 5 MB) |
| `Content:SigningKey` | HMAC key (32+ characters) for signed image links in rendered pages; required everywhere |
| `Content:BaseUrl` | Separate origin that serves `/content` (required outside Development and the Testing environment; empty means same-origin) |
| `Publish:MaxArchiveBytes`, `Publish:MaxExpandedBytes`, `Publish:MaxEntries` | Bulk publish limits (100 MB, 250 MB, 5,000 entries) |

Endpoints: `/healthz` (liveness), `/readyz` (database), `/metrics` (Prometheus, internal only), `/api/v1/openapi.json`, `/content/{projectId}/{sha}` (signed image links, anonymous).

## Publishing from CI

A write token (or a user with at least the Editor role on the project) can mirror a docs folder into a project in one atomic call. Markdown becomes documents, YAML/JSON
with a top-level `openapi` key becomes a spec named after the file, and allow-listed images and files become assets.
Anything in the project (or under `prefix`) that the folder no longer has is deleted; deletes keep history and can be restored: documents and specs through their version history (`POST .../versions/{n}/restore`); assets have no restore endpoint, publishing the file again revives it.

    tar czf docs.tgz -C docs .
    curl --fail -X POST \
      -H "Authorization: Bearer $DDM_TOKEN" -H "Content-Type: application/gzip" \
      --data-binary @docs.tgz \
      "https://ddm.example.com/api/v1/projects/payments/publish?message=$GITHUB_SHA"

The body is a tar.gz (`application/gzip`) or a zip (`application/zip`).

Add `dryRun=true` to see the plan without writing. A publish, dry run or not, that would delete everything in scope, or more than half
of 10+ items, is refused with `409 publish_mass_delete` unless you add `allowMassDelete=true`; a dry run gets the same refusal.
