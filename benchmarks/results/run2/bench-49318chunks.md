# Benchmark: 49,318 chunks, 384 dimensions, k = 10, 300 queries

Run 2026-11-13 12:20:43Z. Client: 16 logical CPUs, Microsoft Windows 10.0.26200, .NET 10.0.12. Databases: 2 CPUs per database container (NanoCPUs). Embedder `lexproj-384-v1-5eed0a1c2026`.

## Writes

| store | API create p50 / p95 (ms) | API replace p50 / p95 (ms) | bulk load (s) | bulk chunks/s |
|---|---|---|---|---|
| PostgreSQL 17 + pgvector 0.8.1 | 2.37 / 3.56 | 3.20 / 3.92 | 1.7 | 28,842 |
| Oracle AI Database 26ai Free 23.26.3 | 5.35 / 7.59 | 4.97 / 7.61 | 9.3 | 5,328 |

## Index builds

| store | index | build (s) | size (MB) |
|---|---|---|---|
| postgres | HNSW m=16 efConstruction=64 | 6.4 | 96.3 |
| postgres | IVF partitions=222 | 0.9 | 78.1 |
| oracle | HNSW m=16 efConstruction=64 | 8.8 | 88.3 |
| oracle | IVF partitions=222 | 28.8 | 139.7 |

## Search (single client, sequential, latency includes the round trip)

| store | configuration | recall@10 (ids) | recall@10 (distance) | top-1 = exact | p50 (ms) | p95 (ms) | p99 (ms) |
|---|---|---|---|---|---|---|---|
| postgres | exact | 1.0000 | 1.0000 | 100.0 % | 23.93 | 28.02 | 33.88 |
| postgres | HNSW ef_search=10 | 0.7653 | 0.7653 | 74.0 % | 1.03 | 1.41 | 1.68 |
| postgres | HNSW ef_search=20 | 0.8180 | 0.8180 | 79.3 % | 1.13 | 1.65 | 2.13 |
| postgres | HNSW ef_search=40 | 0.8507 | 0.8507 | 85.3 % | 1.45 | 2.50 | 3.13 |
| postgres | HNSW ef_search=80 | 0.8877 | 0.8877 | 89.3 % | 1.63 | 2.78 | 3.06 |
| postgres | HNSW ef_search=160 | 0.9413 | 0.9413 | 94.7 % | 1.97 | 3.32 | 4.08 |
| postgres | IVF probes=1 | 0.7060 | 0.7060 | 68.0 % | 0.81 | 1.17 | 1.25 |
| postgres | IVF probes=4 | 0.9537 | 0.9537 | 92.0 % | 1.17 | 2.30 | 2.62 |
| postgres | IVF probes=16 | 0.9997 | 0.9997 | 99.7 % | 2.31 | 3.87 | 4.88 |
| postgres | IVF probes=64 | 1.0000 | 1.0000 | 100.0 % | 7.03 | 10.71 | 12.34 |
| oracle | exact | 1.0000 | 1.0000 | 100.0 % | 40.51 | 48.72 | 53.14 |
| oracle | HNSW ef_search=10 | 0.6723 | 0.6723 | 62.3 % | 1.07 | 1.64 | 1.91 |
| oracle | HNSW ef_search=20 | 0.7760 | 0.7760 | 74.3 % | 0.94 | 1.27 | 1.69 |
| oracle | HNSW ef_search=40 | 0.8227 | 0.8227 | 81.0 % | 1.06 | 1.51 | 1.75 |
| oracle | HNSW ef_search=80 | 0.8490 | 0.8490 | 85.0 % | 1.22 | 1.73 | 2.09 |
| oracle | HNSW ef_search=160 | 0.8843 | 0.8843 | 88.3 % | 1.18 | 1.93 | 2.49 |
| oracle | IVF probes=1 | 0.7123 | 0.7123 | 68.0 % | 1.50 | 2.05 | 2.48 |
| oracle | IVF probes=4 | 0.9640 | 0.9640 | 94.0 % | 3.02 | 4.25 | 4.93 |
| oracle | IVF probes=16 | 0.9990 | 0.9990 | 100.0 % | 5.62 | 7.53 | 8.04 |
| oracle | IVF probes=64 | 1.0000 | 1.0000 | 100.0 % | 14.39 | 17.72 | 20.82 |

## RAG quality on the sampled questions

| store | search | answerable | hit@1 | cited + correct answer | unanswerable | abstained |
|---|---|---|---|---|---|---|
| postgres | exact | 274 | 94.5 % | 96.0 % | 26 | 76.9 % |
| postgres | HNSW, default knobs | 274 | 82.1 % | 82.8 % | 26 | 76.9 % |
| oracle | exact | 274 | 94.5 % | 96.0 % | 26 | 76.9 % |
| oracle | HNSW, default knobs | 274 | 87.6 % | 88.7 % | 26 | 76.9 % |
