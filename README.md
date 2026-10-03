# DocManagers
Developer Document Manager Tooling

## Running locally

    docker compose up -d
    dotnet run --project src/Ddm.Api      # Development env migrates the DB on start
    dotnet test                           # needs Docker for Testcontainers
