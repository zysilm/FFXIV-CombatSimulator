# FFXIV Combat Simulator

A Dalamud plugin (C#). Before doing anything, read `docs/AGENT_WORKFLOW.md`: it defines how the main session (coordinator) and sub-agents work here, the brief and report formats, and the project rules every agent follows.

In short:
- Talk to the user in Chinese.
- The main session discusses, decides, reviews, commits and pushes. Implementation goes to background sub-agents, which never commit or push.
- Never edit `CombatSimulator/Dev/Experimental`. Commit with `git add -A CombatSimulator Tests docs CombatSimulator.sln CLAUDE.md ':!CombatSimulator/Dev/Experimental'`. Never run `git stash`.
- The game hot-reloads the dev plugin from `CombatSimulator/bin/Release`. The game log is `%APPDATA%/XIVLauncher/dalamud.log`.
- The ragdoll physics library AdvancedRagdoll is a NuGet package (`PackageReference` in `CombatSimulator/CombatSimulator.csproj`), not code in this repository. To change it: work in its own repository (`C:\PROJECT\AdvancedRagdoll`, https://github.com/zysilm/AdvancedRagdoll; its tests are under `tests/` there), set the new `<Version>` in `AdvancedRagdoll.csproj`, commit and push, then push the tag `v<version>` (the package version comes from the tag); the `publish` workflow waits for approval in GitHub Actions (environment `release`) and publishes to nuget.org; once the version is restorable, bump the plugin's `PackageReference` and commit the updated `CombatSimulator/packages.lock.json`. Details: `docs/AGENT_WORKFLOW.md` §3.
- Project memory is `docs/PROJECT_LOG.md`: read its §2–§4 (decisions in force, lessons and dead ends, pending in-game verification) before changing an area.
- Feature docs live in `docs/` (for example `WAR_GENERATOR.md` and `AI_MOVEMENT_REWRITE.md`). Read the relevant one before changing that area.
