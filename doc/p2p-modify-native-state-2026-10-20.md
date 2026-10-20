# Finite native behavior handoff state — MODIFY M2

2026-10-20. Base 2debfff0e3e342195f94ae8433ba08ddcfa28736. Dynamic campaign migration remains required.

## Core boundary recorded before implementation

1. Original functionality: Aging stores extra-life and young-hero dictionaries in native SyncData, plus a non-saved _gameStartDay initialized by character creation. Caravans SyncData stores rumor, looted, interacted, trade logs, last-home-visit and prohibited-kingdom state. Existing CampaignSessionCheckpoint transports CoopSession player data while preserving native actor identities and local Inventory state; it does not transport these writer-private native values.
2. Adapter evidence: writer DailyTickHero removes/advances young-hero entries and decrements extra lives; observer subscription alone cannot reproduce them. Caravan hourly AI changes last-home-visit timestamps. A finite field checkpoint adapter attached to the existing session checkpoint can transfer these values without changing native AI, aging or trading algorithms. No core algorithm rewrite is required.
3. Invariants: use existing stable Registry handles, validate every referenced object and duplicate/range before any native mutation, retain local MainHero/MainParty and player-specific UI caches, restore absolute data without RNG/events/equipment/age-transition callbacks, and reject incomplete production checkpoints. Original native Init/SyncData algorithms and shop stock remain unchanged.
4. Regressions: true detached protobuf roundtrip, all-reference validation before writes, duplicate/missing/out-of-range refusal without partial session/player/native mutation, exact dictionary/timestamp/cached-value restoration, actor preservation, no callback replay, and explicit domain coverage. Production missing adapter must refuse capture/apply; old internal managed metadata fixtures remain compatible. Final native A-B-A/day/game acceptance remains coordinator-owned.

Native evidence: installed TaleWorlds.CampaignSystem.dll SHA256 1f8e33e2ed73e6ec653d7629180afb70649ddc6e5bd1657a802a264efda1c3ae. Previously decompiled types retained under the Bootstrap M1 artifacts.

## Slice 1: Aging

A versioned finite native checkpoint extends CampaignSessionCheckpoint ProtoMember(3), not a second session or global manager. The native-state service prepares validated dictionaries before the existing fenced session install. It sets _extraLivesContainer, _heroesYoungerThanHeroComesOfAge and _gameStartDay directly under CheckpointInstall, without calling original growth/death business. This initial slice explicitly has no Caravan state and is not a complete native-world manifest.
