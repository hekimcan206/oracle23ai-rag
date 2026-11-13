# 0002: Documents are written as JSON through a duality view; embeddings stay outside the document

- Status: accepted
- Date: 2026-11-13

## Context

A RAG store holds two shapes of the same data. Clients and editors think in **documents** (a title and its
sections); retrieval needs **rows** (one chunk, one vector, one index entry). Keeping them in step is the usual
source of bugs: a re-ingested document that leaves stale chunks behind, two editors overwriting each other, a reader
seeing half of a replace.

Oracle's JSON Relational Duality views offer both shapes over one copy of the data: a JSON document is read and
written through the view, and the database maps it to normalised rows, with an ETAG in `_metadata` for optimistic
concurrency. PostgreSQL has no writable equivalent.

## Options considered

1. *Application writes rows in both databases.* Symmetric, but it would not use the Oracle feature this project is
   meant to evaluate.
2. *Embedding inside the duality document.* One write, but every GET would carry 384 floats per chunk, the ETAG would
   change whenever the embedder changes, and clients would be able to send vectors the server did not compute.
3. *Duality view for the document, embeddings set by the application in the same transaction* (chosen).

## Decision

- **Oracle.** `rag_document_dv` maps `{_id, title, source, chunks: [{chunkId, ord, heading, body, contentHash}]}` onto
  `rag_document` and `rag_chunk`, `WITH INSERT UPDATE DELETE` on both. A write is: `UPDATE rag_document_dv SET data =
  :doc` (or `INSERT` when no row was updated), then one array-bound `UPDATE rag_chunk SET embedding = :emb` for every
  chunk, then the new ETAG is read back, all in one transaction. The view inserts, updates or deletes chunk rows by
  key, so a shorter new version leaves no orphan chunks (integration-tested).
- **Least privilege follows the shape.** The application account has DML on the view, `SELECT` on the tables, and
  `UPDATE (embedding)` on `rag_chunk`, nothing else. It cannot insert a document row, edit chunk text or delete chunks
  except through the view (`ORA-41900` / `ORA-01031`, integration-tested).
- **Conditional writes.** An `If-Match` ETAG is sent as `_metadata.etag` in the document. Oracle rejects a stale one
  with `ORA-42699`, which the store maps to `EtagMismatchException` and the API to `412 Precondition Failed`. A
  conditional write for a document that no longer exists is also a conflict, never a create.
- **The ETAG is a content hash.** Oracle computes it over the document's columns, so rewriting identical content keeps
  the tag. PostgreSQL stores an application-computed SHA-256 prefix with the same property (`ContentEtag`); the shared
  contract test checks both behave alike.
- **Concurrent unconditional writes retry.** Duality-view updates do not wait on row locks. Two sessions replacing the
  same document at the same moment make one fail with `ORA-42698` ("concurrent modification detected"); this surfaced
  in the 8-writer contract test, not in the documentation. An unconditional write (last writer wins) retries the whole
  transaction up to eight times with jittered backoff; a conditional write turns `ORA-42698` into a 412, because the
  caller asked to be told about concurrent changes. PostgreSQL serialises the same race on the document row lock
  (`INSERT ... ON CONFLICT DO NOTHING` then `SELECT ... FOR UPDATE`).
- **PostgreSQL read side.** A read-only view assembles the same JSON with `jsonb_agg`, so both stores return the same
  shape from `GET /v1/stores/{store}/documents/{id}`.

## Consequences

- Re-ingest is atomic and orphan-free on both databases, and lost updates are detectable on both.
- The embedding column is nullable in Oracle, because the view inserts chunk rows before the vectors are set. No
  committed chunk is ever without one (asserted after every write test); PostgreSQL keeps `NOT NULL`.
- Every chunk is re-embedded on every write, even when only one section changed. `contentHash` is stored so a later
  version can skip unchanged chunks; that optimisation is listed under next steps, not implemented.
- The Oracle write path costs more round trips than one PostgreSQL batch (duality DML, embedding update, ETAG read);
  the per-document latency of both is measured in `docs/benchmarks.md`.

## Sources

- Oracle, *CREATE JSON RELATIONAL DUALITY VIEW* (23ai SQL reference): https://docs.oracle.com/en/database/oracle/oracle-database/23/sqlrf/create-json-relational-duality-view.html
- Oracle, *Overview of JSON-Relational Duality Views*: https://docs.oracle.com/en/database/oracle/oracle-database/26/jsnvu/overview-json-relational-duality-views.html
- Oracle, *Updating Documents/Data in Duality Views* (ETAG, optimistic concurrency): https://docs.oracle.com/en/database/oracle/oracle-database/26/jsnvu/updating-documents-data-duality-views.html
- Pretius, *Oracle Database 23ai: JSON-Relational Duality Views and the ORA-42647 error*: https://pretius.com/?p=12986
