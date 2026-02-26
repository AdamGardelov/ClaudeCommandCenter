#!/bin/bash
# ccc-state.sh — Writes session state for CCC to read.
# Called by Claude Code hooks: UserPromptSubmit, Stop.
# Requires CCC_SESSION_NAME env var (injected by CCC when creating sessions).
#
# Notification events are intentionally ignored — they can fire mid-work
# (e.g. permission prompts where auto-accept continues without a new
# UserPromptSubmit), causing false "waiting for input" states.
#
# On Stop, we check the pane content to instantly distinguish idle (❯ prompt)
# from waiting-for-input (Claude asked a question).

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
EVENT=$(echo "$INPUT" | sed -n 's/.*"hook_event_name" *: *"\([^"]*\)".*/\1/p' | head -1)

case "$EVENT" in
    UserPromptSubmit)
        echo "working" > "$STATE_DIR/$SESSION_NAME"
        ;;
    Stop)
        # Check pane content to distinguish idle from waiting-for-input.
        # Look for the ❯ prompt (idle) vs a question/permission prompt (waiting).
        PANE=$(tmux capture-pane -t "$SESSION_NAME" -p -S -20 2>/dev/null || true)
        # Strip ANSI escapes for clean matching
        CLEAN=$(echo "$PANE" | sed 's/\x1b\[[0-9;]*[A-Za-z]//g; s/\x1b\][^\a]*\(\a\|\x1b\\\)//g')
        if echo "$CLEAN" | grep -q '❯'; then
            echo "idle" > "$STATE_DIR/$SESSION_NAME"
        else
            echo "waiting" > "$STATE_DIR/$SESSION_NAME"
        fi
        ;;
esac
