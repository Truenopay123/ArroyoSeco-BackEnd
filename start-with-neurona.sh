#!/bin/sh
set -eu

NEURONA_PORT="${NEURONA_INTERNAL_PORT:-5001}"
NEURONA_HOST="${NEURONA_INTERNAL_HOST:-127.0.0.1}"
NEURONA_BASE_URL="http://${NEURONA_HOST}:${NEURONA_PORT}"

export NEURONA_SERVICE_BASE_URL="${NEURONA_SERVICE_BASE_URL:-$NEURONA_BASE_URL}"
export NEURONA_SERVICE_TIMEOUT_SECONDS="${NEURONA_SERVICE_TIMEOUT_SECONDS:-30}"

echo "[startup] Iniciando neurona Flask en ${NEURONA_BASE_URL}"
/app/venv/bin/python /app/neurona-service/neurona_service.py > /tmp/neurona-service.log 2>&1 &
NEURONA_PID=$!

cleanup() {
  echo "[startup] Deteniendo neurona Flask (${NEURONA_PID})"
  kill "$NEURONA_PID" 2>/dev/null || true
}

trap cleanup INT TERM EXIT

echo "[startup] Iniciando API .NET en puerto ${PORT:-8080}"
exec dotnet /app/out/arroyoSeco.API.dll