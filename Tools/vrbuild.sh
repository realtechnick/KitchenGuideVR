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
# NOTE: no -quit flag — VrBuild.BuildQuest calls EditorApplication.Exit with the
# build's exit code; -quit on top of it made the 6000.6 Editor abort (SIGABRT)
# during shutdown even after a successful build. Belt and braces: the verdict
# is the [VrBuild] Result line + a FRESH APK on disk, not Unity's exit code.
BUILD_START=$(date +%s)
UNITY_RC=0
"$UNITY" -batchmode -projectPath "$PWD" -executeMethod VrBuild.BuildQuest -logFile "$PWD/Builds/build.log" || UNITY_RC=$?
grep -m1 "\[VrBuild\] Result:" Builds/build.log || true
APK="$PWD/Builds/KitchenGuide.apk"
if [ ! -f "$APK" ] || [ "$(stat -f %m "$APK")" -lt "$BUILD_START" ]; then
  echo "BUILD FAILED (unity exit $UNITY_RC, no fresh APK) — last 40 lines of Builds/build.log:"
  tail -40 Builds/build.log
  exit 1
fi
if [ "$UNITY_RC" -ne 0 ]; then
  echo "note: Unity exited $UNITY_RC but a fresh APK exists — treating as built (known exit-path abort)."
fi

echo "== vrbuild: install + launch =="
metavr app install "$PWD/Builds/KitchenGuide.apk" -r -g
metavr app launch "$PKG" --verify --wait-for-idle
echo "== vrbuild: done — put the headset on =="
