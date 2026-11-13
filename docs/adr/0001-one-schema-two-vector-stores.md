# 0001: One logical schema, two vector stores, compared on identical vectors and identical knobs

- Status: accepted
- Date: 2026-11-13

## Context

The question this repository answers is practical: a team that already runs Oracle wants retrieval-augmented
generation (RAG). Should the vectors live next to the relational data in Oracle AI Vector Search, or in PostgreSQL
with pgvector, which most RAG tutorials assume? A useful answer needs the same documents, the same chunks, the same
vectors and the same index parameters on both sides, plus a ground truth to measure approximate search against.
Vendor benchmarks fail all four conditions.

The Oracle side is Oracle Database Free from `gvenzl/oracle-free:23-slim-faststart`. The image now reports itself as
*Oracle AI Database 26ai Free, Release 23.26.3*: the "26ai" name is Oracle's rebrand of the 23ai code line, and every
feature used here (the `VECTOR` type, `VECTOR_DISTANCE`, HNSW and IVF vector indexes, JSON Relational Duality) arrived
in 23ai. The repository keeps the 23ai name it was planned under and says which build was measured.

## Options considered

1. *Compare each database at its own defaults.* Easy, and meaningless: Oracle's default HNSW parameters, its default
   target accuracy and pgvector's `ef_search = 40` are different operating points.
2. *Use each database's high-level knob* (Oracle `TARGET ACCURACY`, pgvector `ef_search`). Oracle's target accuracy is
   translated into internal parameters, so equal-looking settings are not equal work.
3. *Same structural parameters on both sides* (chosen). HNSW with 16 neighbours and `efConstruction` 64 (pgvector's
   defaults, written explicitly for Oracle), the query-time candidate list set per query on both (Oracle
   `FETCH APPROX ... WITH TARGET ACCURACY PARAMETERS (EFSEARCH n)`, pgvector `hnsw.ef_search`), and IVF with the same
   partition count and probe sweep (`NEIGHBOR PARTITIONS` / `lists`, `NEIGHBOR PARTITION PROBES` / `ivfflat.probes`).

## Decision

- **Same logical schema.** `rag_document(doc_id, title, source)` and `rag_chunk(chunk_id, doc_id, ord, heading, body,
  content_hash, embedding)` in both databases, cosine distance everywhere, one `IVectorStore` contract.
- **Ground truth in .NET.** `InMemoryVectorStore` computes exact cosine distances in double precision. The integration
  suite asserts that each database's exact search returns the same ranking within 1e-5 (float32 rounding), so "exact"
  is verified before "approximate" is measured against it.
- **Approximate queries keep the index usable; exact queries keep it out.** The top-k runs on the chunk table alone,
  ordered by the distance expression, and the document join happens afterwards. Oracle states exactness in the SQL
  (`FETCH EXACT` / `FETCH APPROX`). pgvector has no such clause: whether a query is exact is a planner decision. A
  tie-breaker in `ORDER BY` does *not* keep the HNSW index out (the planner uses it as the presorted key of an
  Incremental Sort), so an "exact" query on a table with an index silently returns approximate neighbours. The
  PostgreSQL store's exact mode therefore sets `enable_indexscan = off` for the statement. Both behaviours are pinned
  by `EXPLAIN`-based integration tests.
- **Literals only where the SQL grammar forces them.** `FETCH APPROX ... PARAMETERS (EFSEARCH n)` takes no bind
  variables, so `k`, `EFSEARCH` and `PROBES` are formatted as integers after a range check (`SearchQuery.Validate`).
  Nothing the caller writes as text reaches the SQL.
- **Equal hardware.** Oracle Database Free uses at most two CPU threads. The benchmark limits both containers to
  2 CPUs (`NanoCPUs`), runs one database at a time, and gives pgvector `max_parallel_maintenance_workers = 1`, so
  neither side gets cores the other cannot use.
- **Vector pool sized at first start.** HNSW indexes live in the SGA vector memory area. On this build
  `vector_memory_size` is 0 by default and cannot be raised online (`ORA-51950`), so the init script sets it in the
  spfile and restarts the instance once, before the database is reported ready.

## Consequences

- Recall and latency are comparable row by row, and the comparison is reproducible from one command.
- The results describe Oracle Database *Free* under its resource caps, not Enterprise Edition on dedicated hardware;
  the README says so next to every number.
- Measuring at Oracle's own `TARGET ACCURACY` operating points is not covered; the API exposes the default
  `FETCH APPROX` (no parameters), which is what an application gets out of the box, and the benchmark reports RAG
  quality at that setting.

## Sources

- Oracle, *CREATE VECTOR INDEX* (23ai SQL reference): https://docs.oracle.com/en/database/oracle/oracle-database/23/sqlrf/create-vector-index.html
- Oracle, *Hierarchical Navigable Small World Index Syntax and Parameters* (23ai and 26ai): https://docs.oracle.com/en/database/oracle/oracle-database/23/vecse/hierarchical-navigable-small-world-index-syntax-and-parameters.html
- Oracle blog, *Using HNSW vector indexes in AI Vector Search* (vector pool, `VECTOR_MEMORY_SIZE`): https://blogs.oracle.com/database/using-hnsw-vector-indexes-in-ai-vector-search
- pgvector 0.8.0 release announcement (iterative scans, `hnsw.ef_search` default 40): https://www.postgresql.org/about/news/pgvector-080-released-2952/
- ODP.NET, *Artificial Intelligence Vectors and Semantic Search* (`OracleDbType.Vector*`, `OracleVector`): https://docs.oracle.com/en/database/oracle/oracle-database/23/odpnt/featAIVectorSearch.html
