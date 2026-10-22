#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."

db_container="${1:-}"
if [[ -z "$db_container" || ! "$db_container" =~ ^[a-zA-Z0-9][a-zA-Z0-9_.-]*$ ]]; then
    printf '%s\n' 'Запуск: bash scripts/run-bot-docker.sh ИМЯ_СУЩЕСТВУЮЩЕГО_КОНТЕЙНЕРА_БД' >&2
    exit 1
fi
if [[ "$(docker inspect --format '{{.State.Running}}' "$db_container")" != true ]]; then
    printf '%s\n' 'Сначала запустите существующий контейнер PostgreSQL.' >&2
    exit 1
fi
existing_network="$(docker inspect --format '{{range $name, $_ := .NetworkSettings.Networks}}{{println $name}}{{end}}' "$db_container")"
existing_volume="$(docker inspect --format '{{range .Mounts}}{{if eq .Destination "/var/lib/postgresql/data"}}{{.Name}}{{end}}{{end}}' "$db_container")"
if [[ -z "$existing_network" || "$existing_network" == *$'\n'* || -z "$existing_volume" ]]; then
    printf '%s\n' 'Нужна одна Docker-сеть и именованный PostgreSQL-volume. Проверьте контейнер БД.' >&2
    exit 1
fi
network_aliases="$(docker inspect --format '{{range .NetworkSettings.Networks}}{{range .Aliases}}{{println .}}{{end}}{{end}}' "$db_container")"
if ! printf '%s\n' "$network_aliases" | grep -qx 'db'; then
    printf '%s\n' 'В сети БД отсутствует DNS-псевдоним db. Нужен исходный Compose-контейнер БД.' >&2
    exit 1
fi
export EXISTING_DOCKER_NETWORK="$existing_network"
export EXISTING_POSTGRES_VOLUME="$existing_volume"
printf '%s\n' 'Остановите локальный бот с тем же токеном. Запускается только контейнер приложения; БД сохраняется.'
docker compose --project-name intellect-bot --env-file .env \
    -f compose.yaml -f compose.existing-db.yaml --profile local \
    up -d --build --no-deps --wait bot
