-- Runs as the schema owner. {{DIMENSIONS}} is replaced by a validated integer before execution.
-- Statements are separated by a line holding a single '/'.

CREATE TABLE rag_document (
    doc_id   VARCHAR2(200) CONSTRAINT rag_document_pk PRIMARY KEY,
    title    VARCHAR2(400) NOT NULL,
    source   VARCHAR2(100) NOT NULL
)
/
-- The embedding is nullable only because the duality view inserts chunk rows before the application sets their
-- vectors in the same transaction; no committed chunk is ever without one (integration-tested).
CREATE TABLE rag_chunk (
    chunk_id      VARCHAR2(210) CONSTRAINT rag_chunk_pk PRIMARY KEY,
    doc_id        VARCHAR2(200) NOT NULL CONSTRAINT rag_chunk_doc_fk REFERENCES rag_document (doc_id),
    ord           NUMBER(5)     NOT NULL,
    heading       VARCHAR2(400) NOT NULL,
    body          VARCHAR2(4000) NOT NULL,
    content_hash  VARCHAR2(64)  NOT NULL,
    embedding     VECTOR({{DIMENSIONS}}, FLOAT32),
    CONSTRAINT rag_chunk_doc_ord_uq UNIQUE (doc_id, ord)
)
/
CREATE TABLE rag_meta (
    meta_key    VARCHAR2(50)  CONSTRAINT rag_meta_pk PRIMARY KEY,
    meta_value  VARCHAR2(200) NOT NULL
)
/
-- One JSON document per source document, backed by two normalised tables. Writing a document through the view
-- inserts, updates or deletes the chunk rows it implies, atomically, and every read carries an ETAG for optimistic
-- concurrency. The embedding is deliberately not part of the document: it is derived data the application owns.
CREATE OR REPLACE JSON RELATIONAL DUALITY VIEW rag_document_dv AS
SELECT JSON {
    '_id'    : d.doc_id,
    'title'  : d.title,
    'source' : d.source,
    'chunks' : [
        SELECT JSON {
            'chunkId'     : c.chunk_id,
            'ord'         : c.ord,
            'heading'     : c.heading,
            'body'        : c.body,
            'contentHash' : c.content_hash
        }
        FROM rag_chunk c WITH INSERT UPDATE DELETE
        WHERE c.doc_id = d.doc_id
    ]
}
FROM rag_document d WITH INSERT UPDATE DELETE
/
