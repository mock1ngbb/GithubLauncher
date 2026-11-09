#!/usr/bin/env bash
# scripts/push.sh — the push entrypoint for this repo. Thin on purpose: it pushes.
# (It used to refuse unless the charon-cicada pre-push hook was installed; that
# hook and the whole cicada stack were deleted in the 2026-11-09 teardown.)
#
# Usage: bash scripts/push.sh [any git push arguments]
#        bash scripts/push.sh -u origin feat/dark-my-branch
set -euo pipefail
exec git push "$@"
