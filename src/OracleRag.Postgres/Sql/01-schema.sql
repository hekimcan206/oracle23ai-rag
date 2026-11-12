-- Runs as the schema owner inside one transaction. {{DIMENSIONS}} and {{APP_USER}} are replaced by validated values.
-- The same logical model as the Oracle schema: one row per document, one per chunk, the vector on the chunk.
CREATE TABLE IF NOT EXISTS rag.rag_document (
    doc_id  text PRIMARY KEY,
    title   text NOT NULL,
    source  text NOT NULL,
    -- Content hash maintained by the application (Oracle computes the duality-view ETAG itself).
    etag    text NOT NULL
);

CREATE TABLE IF NOT EXISTS rag.rag_chunk (
    chunk_id      text PRIMARY KEY,
    doc_id        text NOT NULL REFERENCES rag.rag_document (doc_id) ON DELETE CASCADE,
    ord           integer NOT NULL,
    heading       text NOT NULL,
    body          text NOT NULL,
    content_hash  text NOT NULL,
    embedding     vector({{DIMENSIONS}}) NOT NULL,
    UNIQUE (doc_id, ord)
);

CREATE TABLE IF NOT EXISTS rag.rag_meta (
    meta_key    text PRIMARY KEY,
    meta_value  text NOT NULL
);

-- Read-side equivalent of the Oracle duality view: the same JSON shape, assembled with jsonb_agg. Unlike a duality
-- view it is read-only; writes go through the application's transaction (see ADR 0002).
CREATE OR REPLACE VIEW rag.rag_document_json AS
SELECT d.doc_id,
       jsonb_build_object(
           '_id', d.doc_id,
           '_metadata', jsonb_build_object('etag', d.etag),
           'title', d.title,
           'source', d.source,
           'chunks', COALESCE((
               SELECT jsonb_agg(jsonb_build_object(
                          'chunkId', c.chunk_id, 'ord', c.ord, 'heading', c.heading,
                          'body', c.body, 'contentHash', c.content_hash) ORDER BY c.ord)
               FROM rag.rag_chunk c
               WHERE c.doc_id = d.doc_id), '[]'::jsonb)) AS data
FROM rag.rag_document d;

GRANT USAGE ON SCHEMA rag TO {{APP_USER}};
GRANT SELECT, INSERT, UPDATE, DELETE ON rag.rag_document, rag.rag_chunk TO {{APP_USER}};
GRANT SELECT ON rag.rag_document_json, rag.rag_meta TO {{APP_USER}};
