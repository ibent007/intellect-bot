#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.."
container="intellect-test-${RANDOM}-$$"
cleanup() { docker rm -f "$container" >/dev/null 2>&1 || true; }
trap cleanup EXIT
docker run -d --rm --name "$container" -e POSTGRES_USER=bot_test -e POSTGRES_PASSWORD=local-test-password -e POSTGRES_DB=bot_test -p 127.0.0.1::5432 postgres:16-bookworm >/dev/null
ready=0
for _ in {1..60}; do
  if docker exec "$container" pg_isready -U bot_test -d bot_test >/dev/null 2>&1; then ready=1; break; fi
  sleep 1
done
if [[ "$ready" != 1 ]]; then echo "Тестовый PostgreSQL не запустился." >&2; exit 1; fi
mapping="$(docker port "$container" 5432/tcp)"
port="${mapping##*:}"
export RUN_LAB1_DB_TESTS=1
export TEST_DATABASE_URL="Host=127.0.0.1;Port=$port;Database=bot_test;Username=bot_test;Password=local-test-password"
dotnet restore --locked-mode
dotnet test -c Release --no-restore --filter 'Category=Integration'
