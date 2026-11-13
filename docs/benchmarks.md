# Benchmarks

Everything on this page was measured on the machine described below with
`benchmarks/OracleRag.Benchmarks`. Raw output of every run is committed under `benchmarks/results/`. Nothing is
estimated or extrapolated.

## Method

- **Data.** `CorpusGenerator.Generate(3700)` (seed 2026): 18,500 synthetic clinic guides, 49,318 chunks, embedded with
  the deterministic `lexproj-384-v1` embedder (384 dimensions, unit length, cosine distance). Both databases receive
  byte-identical vectors.
- **Ground truth.** Exact top-10 for every query, computed in .NET (double precision) over the same vectors. Each
  database's own exact search is also measured and agreed with it on 300/300 queries (recall 1.0000).
- **Queries.** 300 questions sampled with a fixed seed from the corpus's question set (274 answerable, 26 about a topic
  the named clinic does not cover). Single client, sequential, after 30 warm-up queries per configuration. Latency is
  measured in the client and includes the Docker Desktop port-forwarding round trip; p50 is the median of 300 queries.
- **Equal hardware.** One database at a time, each container limited to **2 CPUs** (`NanoCPUs`; Oracle Database Free
  never uses more than two threads), Oracle 4 GB / PostgreSQL 3 GB memory limits. Oracle: `vector_memory_size = 512M`.
  PostgreSQL: `shared_buffers = 512MB`, `maintenance_work_mem = 1GB` for index builds, `max_parallel_maintenance_workers
  = 1`, `max_parallel_workers_per_gather = 1`, 1.5 GB `/dev/shm` (the first attempt failed with
  `could not resize shared memory segment ... No space left on device`: a parallel HNSW build allocates
  `maintenance_work_mem` in dynamic shared memory, and Docker's default `/dev/shm` is 64 MB).
- **Same index parameters.** HNSW `m`/`NEIGHBORS` = 16, `ef_construction`/`EFCONSTRUCTION` = 64. IVF with
  `round(sqrt(n))` = 222 partitions. Query knobs swept identically: `ef_search` / `EFSEARCH` ∈ {10, 20, 40, 80, 160},
  `probes` / `NEIGHBOR PARTITION PROBES` ∈ {1, 4, 16, 64}.
- **Two recall definitions.** *ids*: share of the exact top-10 ids returned. *distance*: share of returned neighbours no
  farther than the true 10th neighbour (+1e-5), which forgives ties at the cut-off (the ann-benchmarks convention).
- **Writes.** "API create/replace" is `RagService.IngestAsync` per document through each store's application path:
  Oracle = duality-view `INSERT`/`UPDATE` + array-bound embedding update + ETAG read in one transaction; PostgreSQL =
  row-locked transaction with one multi-row `unnest` insert. 300 documents each. "Bulk" is the owner's loader:
  Oracle array binding in 5,000-chunk batches, PostgreSQL binary `COPY`.

## Environment

| | |
|---|---|
| Host | Intel Core i7-13620H (10 cores / 16 threads), 16 GB RAM, Windows 11 Home 10.0.26200 |
| Docker | Docker Desktop, engine 29.9.1, WSL2 kernel 6.18.58.1, VM with 16 CPUs and 7.6 GB RAM |
| Host load | 6 other containers running (a Supabase stack); host CPU load 36% when the runs started; another agent was building and testing a different project at the same time |
| Client | .NET 10.0.12, Release build, on the Windows host |
| Oracle | `gvenzl/oracle-free:23-slim-faststart` = Oracle AI Database 26ai Free 23.26.3, ODP.NET 23.26.400 |
| PostgreSQL | `pgvector/pgvector:0.8.1-pg17`, Npgsql 10.0.3, Pgvector 0.3.2 |

## Results

Three full runs (`benchmarks/results/run0..run2`). Run 0 predates the distance-based recall column; everything else is
identical. The tables give the **median of the three runs**, with the range in brackets where runs differed
noticeably. Distance-based recall equalled id-based recall in every configuration of runs 1 and 2 (no ties at the
cut-off), so one recall column is shown.

### Search: recall@10 and latency (49,318 vectors, k = 10, 2 CPUs per database)

| configuration | pgvector recall | pgvector p50 / p95 (ms) | Oracle recall | Oracle p50 / p95 (ms) |
|---|---|---|---|---|
| exact (full scan) | 1.0000 | 25.80 / 33.35 | 1.0000 | 41.13 / 51.67 |
| HNSW ef_search / EFSEARCH = 10 | 0.7627 [0.732-0.765] | 1.17 / 1.68 | 0.6553 [0.644-0.672] | 1.07 / 1.64 |
| HNSW 20 | 0.8180 [0.792-0.835] | 1.22 / 1.75 | 0.7433 [0.738-0.776] | 0.98 / 1.81 |
| HNSW 40 | 0.8507 [0.832-0.870] | 1.45 / 2.50 | 0.8010 [0.787-0.823] | 1.08 / 1.71 |
| HNSW 80 | 0.8877 [0.869-0.898] | 1.75 / 2.52 | 0.8290 [0.818-0.849] | 1.22 / 1.73 |
| HNSW 160 | 0.9210 [0.900-0.941] | 1.97 / 3.32 | 0.8700 [0.863-0.884] | 1.52 / 2.07 |
| IVF 222 partitions, probes = 1 | 0.7060 [0.662-0.707] | 1.00 / 1.42 | 0.7123 [0.685-0.712] | 1.50 / 2.05 |
| IVF probes = 4 | 0.9560 [0.954-0.962] | 1.24 / 1.79 | 0.9640 [0.944-0.964] | 3.02 / 4.25 |
| IVF probes = 16 | 0.9997 | 2.31 / 3.87 | 0.9990 | 5.62 / 7.53 |
| IVF probes = 64 | 1.0000 | 6.67 / 8.10 | 1.0000 | 14.53 / 19.47 |

p95 is shown for the run whose p50 is the median.

### Index builds

| index | pgvector build (s) | pgvector size (MB) | Oracle build (s) | Oracle size (MB) |
|---|---|---|---|---|
| HNSW m = 16, ef_construction = 64 | 7.3 [6.4-7.6] | 96.3 (index relation) | 9.0 [8.8-10.4] | 88.3 (vector pool in use) |
| IVF 222 partitions | 1.1 [0.9-1.2] | 78.1 | 28.8 [28.8-30.5] | 139.7 [139.7-143.4] (`VECTOR$` tables) |

### Writes

| path | pgvector p50 / p95 (ms) | Oracle p50 / p95 (ms) |
|---|---|---|
| API create (per document, ~2.7 chunks) | 2.05 / 3.65 [p50 1.69-2.37] | 5.97 / 13.99 [p50 5.35-6.54] |
| API replace (adds a section) | 2.89 / 4.25 [2.84-3.20] | 5.90 / 8.06 [4.97-6.17] |
| Bulk load, 49,318 chunks | 1.7 s = 28,380 chunks/s (binary COPY) | 18.5 s [9.3-21.2] = 2,672 chunks/s (array binding) |

### RAG quality (300 sampled questions, top-5, extractive answerer)

| search | hit@1 | cited and correct | abstained on 26 unanswerable |
|---|---|---|---|
| exact, both databases | 94.5% | 96.0% | 76.9% (20/26) |
| pgvector HNSW, default `ef_search = 40` | 82.1% [78.1-83.9] | 82.8% [79.2-84.7] | 80.8% [76.9-84.6] |
| Oracle HNSW, default `FETCH APPROX` (no parameters) | 85.0% [84.3-87.6] | 86.5% [85.8-88.7] | 76.9% |

With exact search both databases scored identically in every run, and their exact top-10 matched the .NET ground
truth on 300/300 queries. Answer-by-answer identity is asserted by the integration suite on a 20-entity corpus.

### What the numbers say

1. **Equal knobs are not equal recall.** With the same `m`/`NEIGHBORS`, `ef_construction`/`EFCONSTRUCTION` and
   per-query candidate list, Oracle's HNSW returned 5-11 points lower recall than pgvector at every `EFSEARCH`, at
   slightly lower latency. Oracle documents `EFSEARCH` as a query parameter but not how it maps to its own graph
   search; the comparison is therefore at matched *names*, and the recall/latency curve is the honest output.
2. **Defaults favour Oracle.** Out of the box, Oracle's `FETCH APPROX` (no parameters) answered 86.5% of questions
   correctly against pgvector's 82.8% at its default `ef_search = 40`. pgvector reaches Oracle's default quality only
   at `ef_search ≈ 80`.
3. **On this corpus, IVF beats HNSW.** Neither HNSW configuration reached 0.95 recall even at a candidate list of 160;
   IVF with 16 of 222 partitions probed reached ≥ 0.999 on both databases at 2.3 ms (pgvector) and 5.6 ms (Oracle).
   Lexical random-projection vectors of near-duplicate documents are a hard case for a graph built with m = 16 and
   ef_construction = 64 (see Limitations); a neural embedder or larger build parameters would move the HNSW curve.
4. **Exact search is a real option at this size.** 49k vectors scan in 25.8 ms (pgvector) and 41.1 ms (Oracle) on two
   CPUs. Retrieval quality loss from HNSW at default knobs (−9 to −13 points of answer accuracy) is far larger than
   anything a user notices from 20-40 ms.
5. **The duality view costs about 4 ms per document write.** One JSON document per write, a server-computed ETAG and
   no orphan chunks cost ~6 ms p50 per document against ~2 ms for the hand-written PostgreSQL transaction (three round
   trips instead of one batch). For bulk loads both databases use their native path; PostgreSQL's binary COPY was 10x
   faster than Oracle array binding here, and Oracle's bulk time varied 2.3x between runs on the shared host.
6. **Abstention is the weak spot of the answerer, not of the stores.** It refused 39/40 unanswerable questions on the
   40-entity set it was tuned on but only 20/26 on the 3,700-entity sample: with more entities, more clinic names
   contain other clinic names ("Akkent Clinic" vs "Akkent Sahil Clinic"), which defeats the name gate.

## How to reproduce

```bash
dotnet run -c Release --project benchmarks/OracleRag.Benchmarks -- --entities 3700 --queries 300 --api-docs 300
# smaller and faster: --entities 200 --queries 60 --api-docs 40
# one database only:  --only postgres   or   --only oracle
```

The harness starts both databases itself through Testcontainers (with the limits above) and writes
`<--out>/bench-<n>chunks.{json,md}` (default `benchmarks/results`).
