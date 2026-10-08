#!/bin/bash
# Crea il ruolo con cui si collega l'applicazione e il suo database.
#
# Gira solo alla prima inizializzazione del volume. Il ruolo NON è
# superutente e NON ha BYPASSRLS: un superutente ignora sempre la Row-Level
# Security, e l'isolamento dei tenant resterebbe affidato soltanto a EF.
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres <<SQL
CREATE ROLE flarelytics LOGIN PASSWORD '${APP_DB_PASSWORD}' NOSUPERUSER NOCREATEROLE NOCREATEDB NOBYPASSRLS;
CREATE DATABASE flarelytics OWNER flarelytics;
SQL
