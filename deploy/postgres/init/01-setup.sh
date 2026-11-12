#!/bin/bash
# Runs once, on the first start of the pgvector container, as the postgres superuser.
# Creates the extension (needs superuser), a schema owned by rag_owner (DDL, installer and benchmark only) and the
# application role rag_app, whose grants come from the installer.
set -euo pipefail

: "${OWNER_PASSWORD:?OWNER_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
     -v owner_pw="$OWNER_PASSWORD" -v app_pw="$APP_PASSWORD" <<'SQL'
REVOKE ALL ON SCHEMA public FROM PUBLIC;
-- The extension gets its own schema so the vector type and the <=> operator resolve without opening "public".
CREATE SCHEMA extensions;
CREATE EXTENSION IF NOT EXISTS vector SCHEMA extensions;
CREATE ROLE rag_owner LOGIN PASSWORD :'owner_pw';
CREATE ROLE rag_app LOGIN PASSWORD :'app_pw';
GRANT USAGE ON SCHEMA extensions TO rag_owner, rag_app;
ALTER ROLE rag_owner SET search_path = rag, extensions;
ALTER ROLE rag_app SET search_path = rag, extensions;
CREATE SCHEMA rag AUTHORIZATION rag_owner;
SQL
