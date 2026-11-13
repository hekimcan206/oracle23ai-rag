# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-11-13

### Added

- `IVectorStore` contract with two implementations over one logical schema: Oracle AI Vector Search (ODP.NET 23.26,
  `VECTOR(384, FLOAT32)`, `VECTOR_DISTANCE`, `FETCH EXACT` / `FETCH APPROX`) and PostgreSQL 17 with pgvector 0.8.1.
- Oracle write path through a JSON Relational Duality view: one JSON document becomes document and chunk rows in one
  transaction, embeddings are set by array binding, and the view's ETAG drives `If-Match` / `412` concurrency.
- HNSW and IVF index management with the same parameters on both databases, plus binary-COPY and array-bind bulk
  loaders for benchmarks.
- Deterministic 384-dimension lexical projection embedder, structure-aware Markdown chunker with contextual headers,
  Turkish letter folding, and an extractive answerer that cites its chunk or abstains.
- Seeded synthetic corpus of near-duplicate clinic guides with answerable and unanswerable questions.
- Minimal API (`/v1/stores/{store}/documents|search|ask`), OpenAPI + Scalar, Serilog JSON logs, OpenTelemetry traces
  and metrics, health checks, and a `seed` command.
- Unit, FsCheck property, golden and API tests; a Testcontainers contract suite run against both databases.
- Benchmark harness (both databases limited to 2 CPUs, one at a time) with measured results in `docs/benchmarks.md`.
- Chiseled non-root container image, `docker compose up` demo, CI with Oracle and pgvector integration jobs, a compose
  demo job, dependency audit and CodeQL.

### Notes

- Concurrent unconditional writes to one Oracle document fail with `ORA-42698` (duality-view updates are lock-free);
  the store retries them, and conditional writes report the conflict as `412`. Found by the contract suite.
- A pgvector "exact" query with a tie-breaker in `ORDER BY` can still be planned through the HNSW index (Incremental
  Sort); the PostgreSQL store disables index scans for exact statements. Pinned by an `EXPLAIN` test.

[1.0.0]: https://github.com/hekimcan206/oracle23ai-rag/releases/tag/v1.0.0
