# Benchmark: 49,318 chunks, 384 dimensions, k = 10, 300 queries

Run 2026-11-13 12:12:24Z. Client: 16 logical CPUs, Microsoft Windows 10.0.26200, .NET 10.0.12. Databases: 2 CPUs per database container (NanoCPUs). Embedder `lexproj-384-v1-5eed0a1c2026`.

## Writes

| store | API create p50 / p95 (ms) | API replace p50 / p95 (ms) | bulk load (s) | bulk chunks/s |
|---|---|---|---|---|
| PostgreSQL 17 + pgvector 0.8.1 | 1.69 / 2.23 | 2.84 / 3.67 | 1.7 | 28,380 |
| Oracle AI Database 26ai Free 23.26.3 | 5.97 / 13.99 | 5.90 / 8.06 | 21.2 | 2,330 |

## Index builds

| store | index | build (s) | size (MB) |
|---|---|---|---|
| postgres | HNSW m=16 efConstruction=64 | 7.3 | 96.3 |
| postgres | IVF partitions=222 | 1.1 | 78.1 |
| oracle | HNSW m=16 efConstruction=64 | 10.4 | 88.3 |
| oracle | IVF partitions=222 | 30.5 | 139.7 |

## Search (single client, sequential, latency includes the round trip)

| store | configuration | recall@10 | top-1 = exact | p50 (ms) | p95 (ms) | p99 (ms) |
|---|---|---|---|---|---|---|
| postgres | exact | 1.0000 | 100.0 % | 26.08 | 31.65 | 34.72 |
| postgres | HNSW ef_search=10 | 0.7627 | 74.3 % | 1.24 | 1.70 | 2.02 |
| postgres | HNSW ef_search=20 | 0.8350 | 82.0 % | 1.39 | 1.95 | 2.33 |
| postgres | HNSW ef_search=40 | 0.8700 | 87.3 % | 1.42 | 2.06 | 2.31 |
| postgres | HNSW ef_search=80 | 0.8980 | 91.0 % | 1.75 | 2.52 | 3.14 |
| postgres | HNSW ef_search=160 | 0.9210 | 93.0 % | 2.28 | 3.28 | 3.65 |
| postgres | IVF probes=1 | 0.6617 | 67.3 % | 1.00 | 1.42 | 1.87 |
| postgres | IVF probes=4 | 0.9560 | 93.3 % | 1.32 | 1.92 | 2.29 |
| postgres | IVF probes=16 | 1.0000 | 100.0 % | 2.51 | 3.55 | 5.26 |
| postgres | IVF probes=64 | 1.0000 | 100.0 % | 6.67 | 8.10 | 9.13 |
| oracle | exact | 1.0000 | 100.0 % | 44.07 | 52.67 | 59.16 |
| oracle | HNSW ef_search=10 | 0.6437 | 59.3 % | 1.35 | 2.20 | 2.54 |
| oracle | HNSW ef_search=20 | 0.7433 | 71.3 % | 0.98 | 1.81 | 2.87 |
| oracle | HNSW ef_search=40 | 0.8010 | 79.0 % | 1.21 | 2.14 | 2.67 |
| oracle | HNSW ef_search=80 | 0.8290 | 83.0 % | 1.17 | 2.12 | 3.23 |
| oracle | HNSW ef_search=160 | 0.8700 | 87.3 % | 1.52 | 2.40 | 2.75 |
| oracle | IVF probes=1 | 0.7123 | 68.0 % | 1.35 | 2.21 | 2.88 |
| oracle | IVF probes=4 | 0.9640 | 94.0 % | 3.11 | 5.17 | 6.25 |
| oracle | IVF probes=16 | 0.9990 | 100.0 % | 5.24 | 7.19 | 9.71 |
| oracle | IVF probes=64 | 1.0000 | 100.0 % | 14.53 | 19.47 | 25.50 |

## RAG quality on the sampled questions

| store | search | answerable | hit@1 | cited + correct answer | unanswerable | abstained |
|---|---|---|---|---|---|---|
| postgres | exact | 274 | 94.5 % | 96.0 % | 26 | 76.9 % |
| postgres | HNSW, default knobs | 274 | 83.9 % | 84.7 % | 26 | 84.6 % |
| oracle | exact | 274 | 94.5 % | 96.0 % | 26 | 76.9 % |
| oracle | HNSW, default knobs | 274 | 84.3 % | 85.8 % | 26 | 76.9 % |
