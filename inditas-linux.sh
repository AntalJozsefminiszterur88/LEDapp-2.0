#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DEBUG_APP="$ROOT_DIR/src/LedController.UI/bin/Debug/net8.0/LEDapp-2.0"
PUBLISHED_APP="$ROOT_DIR/publish/LEDapp-2.0"
PROJECT_FILE="$ROOT_DIR/src/LedController.UI/LedController.UI.csproj"

run_app() {
    local app_path="$1"
    shift

    cd -- "$(dirname -- "$app_path")"
    exec "$app_path" "$@"
}

if [[ -x "$DEBUG_APP" ]]; then
    run_app "$DEBUG_APP" "$@"
fi

if [[ -x "$PUBLISHED_APP" ]]; then
    run_app "$PUBLISHED_APP" "$@"
fi

cd -- "$ROOT_DIR"
exec dotnet run --project "$PROJECT_FILE" -f net8.0 -- "$@"
