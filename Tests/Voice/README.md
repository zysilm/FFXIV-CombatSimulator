# Enemy voices

Run `dotnet run --project Tests/Voice/Voice.csproj -c Release`.
The tests link the pure voice code (no Dalamud, no game memory): appearance to PC-shout mapping
(every reachable combo is one of the 42 files that exist), the character-creation voice-id table
(future seam), stable NPC voice hashing over all 84 files, path building and language fallback
(a missing file is never returned), entry labels (unlabeled means silent, fallbacks only use
labeled entries, user labels can remove shipped ones), event priority order, the rate limiter
(same-actor gap, per-event cooldowns, token bucket, concurrency, critical events, 60 s flood
bound), closest-N arbitration and the per-enemy transition watch (no events on first sight,
engage, cast start, heavy hit, one low-HP line per life, single death).

These offline tests do not certify native playback, volume routing, 3D positioning or what any
sound entry actually sounds like; see docs/acceptance/voice.md for the in-game checklist.
