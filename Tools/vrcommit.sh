#!/usr/bin/env bash
# vrcommit — checkpoint the working tree to GitHub in one command.
# Usage:  bash Tools/vrcommit.sh "what changed"
# (Secundo still reviews git status for big Editor-generated batches —
#  use this for routine checkpoints of known work.)
set -euo pipefail
cd "$(dirname "$0")/.."
MSG="${1:-checkpoint $(date '+%Y-%m-%d %H:%M')}"
git add -A
git status --short
git commit -m "$MSG" || echo "nothing new to commit"
git pull --rebase
git push
echo "vrcommit done: $MSG"
