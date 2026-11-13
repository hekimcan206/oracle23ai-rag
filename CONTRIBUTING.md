# Contributing

Thanks for taking the time to improve oracle23ai-rag.

## Prerequisites

- .NET SDK 10.0.400 or later (`global.json` rolls forward to the latest feature band)
- Docker (integration tests start Oracle AI Database Free and pgvector through Testcontainers; `docker compose up`
  runs the demo). Oracle Database Free needs about 2.5 GB of memory.

## Workflow

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Branch from `main` and keep each pull request to one concern.
3. Before pushing:

   ```bash
   dotnet build                                                # must be warning-free: warnings are errors
   dotnet test --project tests/OracleRag.UnitTests             # unit, property, golden and API tests (seconds)
   dotnet test --project tests/OracleRag.IntegrationTests      # Oracle + pgvector contract suite (Docker, ~2 min)
   dotnet format --verify-no-changes
   ```

## Commit messages

[Conventional Commits](https://www.conventionalcommits.org/) are required. They keep the history readable and make
the hand-written CHANGELOG easy to assemble at release time:

- `feat:` new capability (minor bump)
- `fix:` bug fix (patch bump)
- `feat!:` or a `BREAKING CHANGE:` footer (major bump)
- `docs:`, `test:`, `build:`, `ci:`, `refactor:`, `perf:`, `chore:` for everything else

## Changing a store

Both stores implement `IVectorStore`, and `tests/OracleRag.IntegrationTests/VectorStoreContract.cs` runs the same
suite against each. A behaviour change goes into the contract first, so the two databases cannot drift apart. SQL that
the stores generate is pinned in `tests/OracleRag.UnitTests/Golden/generated-sql.txt`; regenerate approval files with
`UPDATE_GOLDEN=1 dotnet test --project tests/OracleRag.UnitTests` and review the diff like code.

## Changing the embedder or the corpus generator

Both feed every committed number. A change to `LexicalProjectionEmbedder` must bump its model id (the installers then
refuse old schemas) and update the pinned-vector test; a change to `CorpusGenerator` shows up in the golden corpus file.
Re-run the benchmark and update `docs/benchmarks.md` and the README in the same pull request.

## Schema and grants

Oracle scripts live in `src/OracleRag.Oracle/Sql/` (statements end with a line holding a single `/`), PostgreSQL in
`src/OracleRag.Postgres/Sql/`. The application account's grants are part of the security model: a change needs a
matching least-privilege integration test.

## Architecture decisions

A change to a decision recorded in `docs/adr/` needs a new ADR that supersedes the old one (MADR: context, options
considered, decision, consequences). Do not edit the old ADR in place.
