#!/bin/bash
# ccc-state.sh — Writes session state for CCC to read.
# Called by Claude Code hooks: UserPromptSubmit, Stop, Notification.
# Requires CCC_SESSION_NAME env var (injected by CCC when creating sessions).

set -euo pipefail

STATE_DIR="$HOME/.ccc/states"
SESSION_NAME="${CCC_SESSION_NAME:-}"

# Only run inside CCC-managed sessions
if [[ -z "$SESSION_NAME" ]]; then
    exit 0
fi

mkdir -p "$STATE_DIR"

# Read hook event JSON from stdin
INPUT=$(cat)
EVENT=$(echo "$INPUT" | grep -o '"hook_event_name":"[^"]*"' | head -1 | cut -d'"' -f4)

case "$EVENT" in
    UserPromptSubmit)
        echo "working" > "$STATE_DIR/$SESSION_NAME"
        ;;
    Stop)
        echo "idle" > "$STATE_DIR/$SESSION_NAME"
        ;;
    Notification)
        echo "waiting" > "$STATE_DIR/$SESSION_NAME"
        ;;
esac
