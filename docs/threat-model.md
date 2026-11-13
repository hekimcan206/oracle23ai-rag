# Threat model

Scope: the API (`oracle23ai-rag`), its two databases, the seed job and the benchmark harness. All data in the
repository is synthetic; the model below is written for the day the same design holds real documents (for example
hospital patient-information leaflets or internal procedures), where KVKK (Kişisel Verilerin Korunması Kanunu, the
Turkish personal data protection law) applies.

## Assets

| Asset | Why it matters |
|---|---|
| Document text and chunks | The knowledge base; may contain personal or confidential data in a real deployment |
| Embeddings | Derived from the text; partially invertible for lexical embedders like the one used here (see I-2) |
| Document integrity (ETAG) | A silent lost update means answers cite text nobody approved |
| Answers and citations | Users act on them; a citation must point at text that really says what the answer says |
| Database accounts | Owner accounts can change schema; application accounts can change content |

## Actors

- **API client**: may be authenticated by a gateway in front of the service; the service itself has no user model.
- **Editor**: writes documents through `PUT`; may race other editors.
- **Operator**: runs `docker compose`, the seed job and the benchmark with owner credentials.
- **Attacker**: on the network in front of the API, or holding a leaked application password.

## Threats and mitigations (STRIDE)

| # | Threat | Mitigation | Status |
|---|---|---|---|
| S-1 | Anyone who can reach the API can write documents | No authentication in the service by design (demo); deploy behind a gateway with authentication. Ports bind to 127.0.0.1 in compose | **Open: deployment responsibility** |
| T-1 | SQL injection through search knobs | `k`, `EFSEARCH`, `PROBES` are integers range-checked by `SearchQuery.Validate` before being formatted into `FETCH APPROX ... PARAMETERS`, which accepts no bind variables. Everything else is bound (`:q`, `:doc`, `$1`) | Mitigated, unit + golden tested |
| T-2 | Injection through schema or user names | `SqlIdentifier` allow-list (`^[A-Za-z][A-Za-z0-9_]{0,29}$`) for every identifier formatted into SQL | Mitigated, tested |
| T-3 | Lost update: two editors overwrite each other | `If-Match` → duality-view `_metadata.etag` (Oracle, `ORA-42699`) or row lock + content hash (PostgreSQL) → `412`; a conditional write never creates | Mitigated, contract-tested on both databases |
| T-4 | Concurrent unconditional writes corrupt a document | Oracle: `ORA-42698` retried as a whole transaction (bounded); PostgreSQL: document row lock. Final state is always exactly one written version, no orphan chunks | Mitigated, 8-writer contract test |
| T-5 | Stale or mismatched vectors (another embedding model) silently degrade answers | Model id recorded in `rag_meta`; installers refuse a different model | Mitigated, tested |
| T-6 | Application account tampers with chunk text without changing the document's ETAG | Oracle: app account may only `UPDATE (embedding)` on `rag_chunk`; all text changes go through the view. PostgreSQL: the app role has table DML (no duality view), so this guarantee is Oracle-only | Oracle mitigated and tested; PostgreSQL **accepted** and documented |
| R-1 | No record of who changed a document | Structured request logs with trace id; no per-user audit trail (no user model) | **Open: next steps** |
| I-1 | Error details leak schema information | ProblemDetails without exception text; health endpoints return status only | Mitigated |
| I-2 | Embeddings leak document content | A lexical random-projection embedding of a short chunk is partly invertible by dictionary attack; treat vectors with the same classification as the text | Accepted, documented |
| I-3 | Logs leak document text | Logs carry ids, store names and timings, never document text or questions | Mitigated |
| D-1 | Oversized documents or queries exhaust memory | 1 MB request body limit, 200,000-character documents, 1,000-character questions, `k ≤ 100`, `ef_search ≤ 1000` | Mitigated, API-tested |
| D-2 | Expensive exact scans on large tables | Exact search is a full scan by definition; production callers should use approximate mode. No rate limiting in the service | **Open: gateway / next steps** |
| E-1 | Application account changes the schema | Separate owner and application accounts; the app account has no `CREATE`, `ALTER` or `DROP` (Oracle `ORA-01031`/`ORA-41900`, PostgreSQL `42501`) | Mitigated, integration-tested |
| E-2 | `public` schema abuse in PostgreSQL | `REVOKE ALL ON SCHEMA public FROM PUBLIC`; pgvector installed in its own `extensions` schema | Mitigated |
| E-3 | Container breakout impact | Chiseled .NET runtime, non-root user (CI-verified), no shell | Mitigated |

## Prompt injection

There is no LLM in the answer path: answers are verbatim sentences of stored documents. A document that contains
"ignore previous instructions" is returned as text if it is the best match, never executed. Adding a generative model
later reintroduces this threat; the extractive answerer would then remain useful as a grounding check.

## Secrets

No secrets in the repository. Compose uses `.env` (git-ignored) with demo defaults documented in `.env.example`; tests
and the benchmark use throwaway credentials for throwaway containers.
