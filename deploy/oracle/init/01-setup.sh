#!/bin/bash
# Runs once, on the first start of the gvenzl/oracle-free container, as the oracle OS user.
#  1. Sizes the vector pool. HNSW indexes live in the SGA's vector memory area, which is 0 by default and cannot be
#     grown online (ORA-51950), so it is set in the spfile and the instance is restarted once, here.
#  2. Creates the schema owner (DDL, used only by the installer and the benchmark) and the application account
#     (writes documents through the duality view, nothing else; grants come from the installer).
set -euo pipefail

: "${OWNER_PASSWORD:?OWNER_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"
VECTOR_MEMORY_SIZE="${VECTOR_MEMORY_SIZE:-512M}"

sqlplus -s / as sysdba <<SQL
WHENEVER SQLERROR EXIT SQL.SQLCODE
ALTER SYSTEM SET vector_memory_size = ${VECTOR_MEMORY_SIZE} SCOPE = SPFILE;
SHUTDOWN IMMEDIATE
STARTUP
EXIT
SQL

sqlplus -s / as sysdba <<SQL
WHENEVER SQLERROR EXIT SQL.SQLCODE
ALTER SESSION SET CONTAINER = FREEPDB1;

CREATE USER rag_owner IDENTIFIED BY "${OWNER_PASSWORD}" DEFAULT TABLESPACE users QUOTA UNLIMITED ON users;
GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW TO rag_owner;
-- Lets the benchmark report how much vector pool memory an HNSW index occupies.
GRANT SELECT ON v_\$vector_memory_pool TO rag_owner;

CREATE USER rag_app IDENTIFIED BY "${APP_PASSWORD}";
GRANT CREATE SESSION TO rag_app;
EXIT
SQL
