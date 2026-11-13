# syntax=docker/dockerfile:1

# ---- build -------------------------------------------------------------------------------------------
# Alpine SDK: smaller pull; the output is portable IL (no apphost), so it runs unchanged on the chiseled image.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/OracleRag.Core/OracleRag.Core.csproj src/OracleRag.Core/
COPY src/OracleRag.Synthetic/OracleRag.Synthetic.csproj src/OracleRag.Synthetic/
COPY src/OracleRag.Oracle/OracleRag.Oracle.csproj src/OracleRag.Oracle/
COPY src/OracleRag.Postgres/OracleRag.Postgres.csproj src/OracleRag.Postgres/
COPY src/OracleRag.Api/OracleRag.Api.csproj src/OracleRag.Api/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/OracleRag.Api/OracleRag.Api.csproj

COPY .editorconfig ./
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/OracleRag.Api/OracleRag.Api.csproj \
        --configuration Release --no-restore --output /app/publish -p:UseAppHost=false

# ---- runtime -----------------------------------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, non-root "app" user (UID 1654).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
LABEL org.opencontainers.image.title="oracle23ai-rag" \
      org.opencontainers.image.source="https://github.com/hekimcan206/oracle23ai-rag" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "oracle23ai-rag.dll"]
