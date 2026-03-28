#!/bin/sh
set -eu

NEURONA_PORT="${NEURONA_INTERNAL_PORT:-5001}"
NEURONA_HOST="${NEURONA_INTERNAL_HOST:-127.0.0.1}"
NEURONA_BASE_URL="http://${NEURONA_HOST}:${NEURONA_PORT}"

export NEURONA_SERVICE_BASE_URL="${NEURONA_SERVICE_BASE_URL:-$NEURONA_BASE_URL}"
export NEURONA_SERVICE_TIMEOUT_SECONDS="${NEURONA_SERVICE_TIMEOUT_SECONDS:-30}"
export PYTHONUNBUFFERED=1

if [ ! -x "/app/venv/bin/python" ]; then
  echo "[startup] ERROR: no existe /app/venv/bin/python"
  exit 1
fi

echo "[startup] Iniciando neurona Flask en ${NEURONA_BASE_URL}"
/app/venv/bin/python /app/neurona-service/neurona_service.py &
NEURONA_PID=$!

cleanup() {
  echo "[startup] Deteniendo neurona Flask (${NEURONA_PID})"
  kill "$NEURONA_PID" 2>/dev/null || true
}

trap cleanup INT TERM EXIT

echo "[startup] Esperando disponibilidad de la neurona..."
ATTEMPTS=0
MAX_ATTEMPTS=30

while [ "$ATTEMPTS" -lt "$MAX_ATTEMPTS" ]; do
  if ! kill -0 "$NEURONA_PID" 2>/dev/null; then
    echo "[startup] ERROR: la neurona finalizó antes de iniciar la API"
    wait "$NEURONA_PID" || true
    exit 1
  fi

  if /app/venv/bin/python - <<'PY'
import os
import socket

host = os.environ.get("NEURONA_INTERNAL_HOST", "127.0.0.1")
port = int(os.environ.get("NEURONA_INTERNAL_PORT", "5001"))

sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.settimeout(1.0)
try:
    sock.connect((host, port))
    print("ok")
    raise SystemExit(0)
except OSError:
    raise SystemExit(1)
finally:
    sock.close()
PY
  then
    echo "[startup] Neurona disponible en ${NEURONA_BASE_URL}"
    break
  fi

  ATTEMPTS=$((ATTEMPTS + 1))
  sleep 1
done

if [ "$ATTEMPTS" -ge "$MAX_ATTEMPTS" ]; then
  echo "[startup] ERROR: timeout esperando neurona en ${NEURONA_BASE_URL}"
  exit 1
fi

echo "[startup] Iniciando API .NET en puerto ${PORT:-8080}"
exec dotnet /app/arroyoSeco.API.dll