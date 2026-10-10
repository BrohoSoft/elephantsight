#!/bin/bash
# Il ruolo con cui l'app fa i backup (pg_dump): solo lettura, con BYPASSRLS.
#
# Serve perché il ruolo dell'app vede un'organizzazione alla volta (Row-Level
# Security) e un backup deve leggerle tutte. Non può scrivere niente e l'app
# lo usa solo per pg_dump.
#
# Gira da solo alla prima inizializzazione del volume. Nelle installazioni
# fatte prima dei backup si lancia una volta a mano (è idempotente):
#   docker compose exec postgres bash /docker-entrypoint-initdb.d/20-backup-role.sh
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "${POSTGRES_USER:-postgres}" --dbname postgres \
  -v password="${BACKUP_DB_PASSWORD:-${APP_DB_PASSWORD}}" <<'SQL'
SELECT format('CREATE ROLE flarelytics_backup LOGIN NOSUPERUSER NOCREATEROLE NOCREATEDB BYPASSRLS PASSWORD %L', :'password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'flarelytics_backup') \gexec
SELECT format('ALTER ROLE flarelytics_backup LOGIN BYPASSRLS PASSWORD %L', :'password') \gexec
GRANT pg_read_all_data TO flarelytics_backup;
SQL
