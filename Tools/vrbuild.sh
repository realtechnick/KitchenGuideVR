#!/usr/bin/env bash
# vrbuild — one-command KitchenGuide iteration:
# checkpoint local work -> sync with GitHub -> headless build (Meta Quest profile) -> install + launch on the Quest 3.
# Run from anywhere:  bash Tools/vrbuild.sh
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.metavr/bin:$HOME/.unity/bin:$PATH"
UNITY="/Applications/Unity/Hub/Editor/6000.6.8f1/Unity.app/Contents/MacOS/Unity"
PKG="com.realtechnick.kitchenguide"

echo "== vrbuild: checkpoint =="
if [ -n "$(git status --porcelain)" ]; then
  git add -A
  git commit -m "vrbuild checkpoint $(date '+%Y-%m-%d %H:%M')"
else
  echo "working tree clean, nothing to checkpoint"
fi

echo "== vrbuild: sync =="
git pull --rebase
git push

echo "== vrbuild: build (Meta Quest profile, headless) =="
mkdir -p Builds
if ! "$UNITY" -batchmode -quit -projectPath "$PWD" -executeMethod VrBuild.BuildQuest -logFile "$PWD/Builds/build.log"; then
  echo "BUILD FAILED — last 40 lines of Builds/build.log:"
  tail -40 Builds/build.log
  exit 1
fi

echo "== vrbuild: install + launch =="
metavr app install "$PWD/Builds/KitchenGuide.apk" -r -g
metavr app launch "$PKG" --verify --wait-for-idle
echo "== vrbuild: done — put the headset on =="
