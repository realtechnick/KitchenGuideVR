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
UNITY_RC=0
"$UNITY" -batchmode -projectPath "$PWD" -executeMethod VrBuild.BuildQuest -logFile "$PWD/Builds/build.log" || UNITY_RC=$?
grep -m1 "\[VrBuild\] Result:" Builds/build.log || true
APK="$PWD/Builds/KitchenGuide.apk"
# Verdict = the build's own Result line + an APK on disk. NOT timestamps:
# an incremental build with no player-facing changes legitimately succeeds
# without re-emitting the APK (proven by the 2026-11-07 15:44 run, whose only
# delta was an Editor .meta and which the old freshness gate misread as a
# failure even though the on-disk APK was already current).
if ! grep -q "\[VrBuild\] Result: Succeeded" Builds/build.log || [ ! -f "$APK" ]; then
  echo "BUILD FAILED (unity exit $UNITY_RC) — last 40 lines of Builds/build.log:"
  tail -40 Builds/build.log
  exit 1
fi
if [ "$UNITY_RC" -ne 0 ]; then
  echo "note: Unity exited $UNITY_RC after a Succeeded build — known 6000.6 exit-path abort; carrying on."
fi
ls -la "$APK"

echo "== vrbuild: install + launch =="
# Unity's shutdown kills the ADB server; give the link a breath, then probe
# for the device before installing. A CRC/device-detection hiccup right after
# a build is transient (seen 2026-11-07 15:42) — retry the install once.
sleep 3
for attempt in 1 2; do
  if metavr device list | grep -q "device"; then break; fi
  echo "device not visible yet (attempt $attempt) — waiting..."
  sleep 3
done
metavr app install "$APK" -r -g || { echo "install hiccup — retrying once:"; sleep 3; metavr app install "$APK" -r -g; }
metavr app launch "$PKG" --verify --wait-for-idle
echo "== vrbuild: done — put the headset on =="
