#!/bin/bash
# ccc-state.sh — Writes session state for CCC to read.
# Called by Claude Code hooks with a state argument:
#   working  — UserPromptSubmit, PostToolUse (Claude is processing)
#   waiting  — Notification (Claude needs user input)
#   stop     — Stop (check pane to distinguish idle from waiting)
#
# Requires CCC_SESSION_NAME env var (injected by CCC when creating sessions).
#
# State flow:
#   UserPromptSubmit → "working"  (user sent input)
#   Notification     → "waiting"  (permission prompt or question)
#   PostToolUse      → "working"  (tool completed — corrects false positives
#                                   from auto-accepted permission prompts)
#   Stop             → "idle" or "waiting" (pane content check)

set -euo pipefail

STATE_DIR="$HOME/.ccc/states"
SESSION_NAME="${CCC_SESSION_NAME:-}"

# Only run inside CCC-managed sessions
if [[ -z "$SESSION_NAME" ]]; then
    exit 0
fi

mkdir -p "$STATE_DIR"

case "${1:-}" in
    working|waiting)
        echo "$1" > "$STATE_DIR/$SESSION_NAME"
        ;;
    stop)
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
