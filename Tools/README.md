# KitchenGuide dev-loop tools

The loop: Secundo writes code/config and pushes to GitHub -> Mac pulls ->
Editor-side assembly happens locally -> checkpoint -> build -> metavr deploy.

- `bash Tools/vrcommit.sh "message"` — stage everything, show the status,
  commit, sync, push. The rollback-point maker.
- `bash Tools/vrbuild.sh` — checkpoint if dirty, sync with GitHub, headless
  build via the Meta Quest build profile (entry point:
  `Assets/Editor/VrBuild.cs`), then `metavr app install` +
  `metavr app launch --verify --wait-for-idle` on the connected Quest.
  Build log lands in `Builds/build.log`; APK lands in `Builds/KitchenGuide.apk`.

GitHub is NOT live sync — commits are deliberate checkpoints
(before builds, at session end). Nothing reaches the repo until one of
these commands (or a manual commit) runs.
