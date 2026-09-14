#!/usr/bin/env bash
# ============================================================================
# scripts/push.sh — the gated push entrypoint for this repo.
#
# WHY THIS EXISTS. The pre-push gate is reached by exactly one mechanism: git
# finding an executable `pre-push` in the effective hooks directory. Nothing
# about a clone or a linked worktree announces whether that wiring happened. A
# tree whose hooks were never installed pushes completely ungated and looks,
# from the terminal, identical to a gated one — same command, same output, same
# exit 0. That is absent-is-indistinguishable-from-passing, and it does not
# matter how fail-closed the pre-push hook is if git never runs it.
#
# So the assertion comes FIRST and it is refusable. There is no bypass flag on
# purpose: "my hooks are not installed" is a setup error with a one-command
# repair, not an exception to be approved.
#
# Usage: bash scripts/push.sh [any git push arguments]
#        bash scripts/push.sh -u origin feat/dark-my-branch
# ============================================================================
set -uo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || true)"
if [[ -z "$REPO_ROOT" ]]; then
  echo "[push] REFUSED: not inside a git repository." >&2
  exit 1
fi

# Effective hooks directory: core.hooksPath when set (relative paths resolve
# against the repo root), otherwise the shared `hooks/` under the common git
# dir — which is what a linked worktree uses too.
HOOKS_PATH="$(git config --get core.hooksPath || true)"
if [[ -n "$HOOKS_PATH" ]]; then
  case "$HOOKS_PATH" in
    /*) HOOKS_DIR="$HOOKS_PATH" ;;
    *)  HOOKS_DIR="$REPO_ROOT/$HOOKS_PATH" ;;
  esac
else
  HOOKS_DIR="$(cd "$(git rev-parse --git-common-dir)" && pwd)/hooks"
fi

if [[ ! -x "$HOOKS_DIR/pre-push" ]]; then
  echo "[push] REFUSED: no executable pre-push hook at $HOOKS_DIR/pre-push." >&2
  echo "[push] A push from here would run NO gates while looking exactly like a" >&2
  echo "[push] push that ran all of them. Refusing rather than pretending." >&2
  echo "[push] Repair: reinstall the charon-cicada pre-push hook into .git/hooks/" >&2
  exit 1
fi

echo "[push] pre-push gate present: $HOOKS_DIR/pre-push"
exec git push "$@"
