#!/bin/sh
# Runs once, when the PostgreSQL volume is created: Keycloak gets its own database and user next to the game's.
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    -v password="$KEYCLOAK_DB_PASSWORD" <<'SQL'
CREATE USER keycloak WITH PASSWORD :'password';
CREATE DATABASE keycloak OWNER keycloak;
SQL
