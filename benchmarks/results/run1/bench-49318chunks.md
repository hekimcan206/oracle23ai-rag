# Benchmark: 49,318 chunks, 384 dimensions, k = 10, 300 queries

Run 2026-11-13 12:17:05Z. Client: 16 logical CPUs, Microsoft Windows 10.0.26200, .NET 10.0.12. Databases: 2 CPUs per database container (NanoCPUs). Embedder `lexproj-384-v1-5eed0a1c2026`.

## Writes

| store | API create p50 / p95 (ms) | API replace p50 / p95 (ms) | bulk load (s) | bulk chunks/s |
|---|---|---|---|---|
| PostgreSQL 17 + pgvector 0.8.1 | 2.05 / 3.65 | 2.89 / 4.25 | 1.8 | 27,608 |
| Oracle AI Database 26ai Free 23.26.3 | 6.54 / 9.24 | 6.17 / 9.47 | 18.5 | 2,672 |

## Index builds

| store | index | build (s) | size (MB) |
|---|---|---|---|
| postgres | HNSW m=16 efConstruction=64 | 7.6 | 96.3 |
| postgres | IVF partitions=222 | 1.2 | 78.1 |
| oracle | HNSW m=16 efConstruction=64 | 9.0 | 88.3 |
| oracle | IVF partitions=222 | 28.8 | 143.4 |

## Search (single client, sequential, latency includes the round trip)

| store | configuration | recall@10 (ids) | recall@10 (distance) | top-1 = exact | p50 (ms) | p95 (ms) | p99 (ms) |
|---|---|---|---|---|---|---|---|
| postgres | exact | 1.0000 | 1.0000 | 100.0 % | 25.80 | 33.35 | 37.96 |
| postgres | HNSW ef_search=10 | 0.7320 | 0.7320 | 68.3 % | 1.17 | 1.68 | 1.99 |
| postgres | HNSW ef_search=20 | 0.7920 | 0.7920 | 75.7 % | 1.22 | 1.75 | 2.01 |
| postgres | HNSW ef_search=40 | 0.8320 | 0.8320 | 82.3 % | 1.52 | 2.40 | 2.99 |
| postgres | HNSW ef_search=80 | 0.8693 | 0.8693 | 87.3 % | 2.07 | 3.03 | 3.65 |
| postgres | HNSW ef_search=160 | 0.9003 | 0.9003 | 90.3 % | 1.96 | 2.72 | 3.15 |
| postgres | IVF probes=1 | 0.7070 | 0.7070 | 68.7 % | 1.04 | 1.63 | 1.95 |
| postgres | IVF probes=4 | 0.9617 | 0.9617 | 94.3 % | 1.24 | 1.79 | 2.00 |
| postgres | IVF probes=16 | 0.9997 | 0.9997 | 100.0 % | 2.22 | 3.34 | 4.00 |
| postgres | IVF probes=64 | 1.0000 | 1.0000 | 100.0 % | 6.57 | 8.76 | 10.64 |
| oracle | exact | 1.0000 | 1.0000 | 100.0 % | 41.13 | 51.67 | 60.80 |
| oracle | HNSW ef_search=10 | 0.6553 | 0.6553 | 65.0 % | 0.99 | 1.48 | 1.63 |
| oracle | HNSW ef_search=20 | 0.7383 | 0.7383 | 72.0 % | 1.02 | 1.52 | 1.75 |
| oracle | HNSW ef_search=40 | 0.7873 | 0.7873 | 77.3 % | 1.08 | 1.71 | 2.01 |
| oracle | HNSW ef_search=80 | 0.8180 | 0.8180 | 82.0 % | 1.33 | 1.76 | 2.37 |
| oracle | HNSW ef_search=160 | 0.8633 | 0.8633 | 86.7 % | 1.52 | 2.07 | 2.43 |
| oracle | IVF probes=1 | 0.6853 | 0.6853 | 66.0 % | 1.95 | 2.69 | 3.01 |
| oracle | IVF probes=4 | 0.9440 | 0.9440 | 91.0 % | 2.79 | 3.99 | 4.28 |
| oracle | IVF probes=16 | 0.9987 | 0.9987 | 99.7 % | 6.02 | 8.62 | 10.03 |
| oracle | IVF probes=64 | 1.0000 | 1.0000 | 100.0 % | 15.36 | 19.73 | 23.55 |

## RAG quality on the sampled questions

| store | search | answerable | hit@1 | cited + correct answer | unanswerable | abstained |
|---|---|---|---|---|---|---|
| postgres | exact | 274 | 94.5 % | 96.0 % | 26 | 76.9 % |
| postgres | HNSW, default knobs | 274 | 78.1 % | 79.2 % | 26 | 80.8 % |
| oracle | exact | 274 | 94.5 % | 96.0 % | 26 | 76.9 % |
| oracle | HNSW, default knobs | 274 | 85.0 % | 86.5 % | 26 | 76.9 % |
