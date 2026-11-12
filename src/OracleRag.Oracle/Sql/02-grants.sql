-- Runs as the schema owner. {{APP_USER}} is replaced by an identifier that passed an allow-list check.
-- The application account writes documents only through the duality view and may change nothing in the base tables
-- except the derived embedding column. It cannot create, alter or drop anything.
GRANT SELECT, INSERT, UPDATE, DELETE ON rag_document_dv TO {{APP_USER}}
/
GRANT SELECT ON rag_document TO {{APP_USER}}
/
GRANT SELECT, UPDATE (embedding) ON rag_chunk TO {{APP_USER}}
/
GRANT SELECT ON rag_meta TO {{APP_USER}}
/
