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

Endpoints: `/healthz` (liveness), `/readyz` (database), `/metrics` (Prometheus, internal only), `/api/v1/openapi.json`.
