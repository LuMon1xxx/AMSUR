# SDD ledger — plan: .opencode/compose-plus/plan-promo.md

- Spec: `.opencode/compose-plus/promo-design.md`
- Decision: `.opencode/compose-plus/decision-promo.md`
- Risks: `.opencode/compose-plus/risks-promo.md`
- Execution: in-place, user-approved `promo/**` only
- Worktree: skipped by explicit user preference for isolated in-place folder
- Commits: n/a — forbidden without a separate user command
- Preflight tracked-diff hash: `2eb4b7ad92090a2658ec898e60d84834c279c5b6`
- Current stage: S6 Task 4

## Preflight interface map

| Task | Writes | Reads/interfaces | Conflict ruling |
|---|---|---|---|
| 1 | package metadata, config, captions | spec | Foundation only; no parallel implementers |
| 2 | renderer, capture, asset test | Task 1 config/assets | Sequential; config read-only |
| 3 | TTS, music, audio test | Task 1 VO windows | Sequential; timing source remains config |
| 4 | audio graph, description, render, output test | Tasks 1–3 outputs | Sequential; exact input/filter indices frozen |
| 5 | visual fixes only | v1 keyframes/spec | Changes routed through implementer, then tester |
| 6 | acceptance report only | all promo files/output | No product-code changes |

## Rulings

- Ruling: execute sequentially — shared config and render interfaces make parallel implementers unsafe — cost if wrong: lower parallelism only.
- Ruling: cp-tester writes all test files; cp-implementer never edits them — required by Compose+ role separation.
- Ruling: use uncommitted file review instead of commit-range diffs — user did not authorize commits — cost if wrong: reviewer reads exact current files directly.
- Ruling: retain pre-existing Compose+ untracked artifacts — they belong to earlier user work; scope checks distinguish them from new promo artifacts.
- Ruling: work on master in place — user explicitly approved isolated `promo/` work and no branch/worktree was requested — risk is accepted and tracked-diff hash gates regressions.
- Ruling: package test script is `node --test "tests/*.test.mjs"` — empirical Node 24.19/Windows run showed bare `tests` is treated as a module, while the quoted glob discovers the test and fails for the intended missing config — cost if wrong: one package-script adjustment.
- Ruling: 48px minimum applies to headline, subhead and role-card titles; 32px chips/list are supporting UI text — avoids unreadably oversized tags while preserving hierarchy — cost if wrong: minor visual retune in Task 5.
- Ruling: replace TTS Rate=8 workaround with config-driven Rate=4 plus shorter voice-only copy and end window 27.00–29.85 — measured full VO is 31.9s at natural rate, while Rate=8 is ~2.4× and risks intelligibility; on-screen claims remain unchanged — cost if wrong: a few seconds of revised spec/copy, but materially safer audio.
- Ruling: FFmpeg 9 graph uses `asplit` for the voice sidechain/final mix and passes `audio-filter.txt` content via `-filter_complex` — the planned graph reused one label twice and installed FFmpeg lacks `-filter_complex_script` — cost if wrong: plan/toolchain documentation adjustment only.
- Ruling: preflight whole-repo tracked-diff hash is no longer a valid promo gate because concurrent AMSUR work appeared during execution (new untracked `PairWeekLns.cs`, `SwapLns.cs` and related tests, plus evolving tracked diff). Never revert it; use scoped `promo/**` isolation checks and disclose the concurrent baseline in final acceptance — cost if wrong: cannot prove whole-repo byte identity across a concurrently changing checkout.

## Task status

- Task 1: complete (spec ✅, quality APPROVED; 2/2 GREEN; reviewer reproduced npm test)
- Task 1: minor (deferred): automated SRT↔voice drift check and asset existence are not yet in timeline test — asset test is mandatory Task 2; final SRT/output review remains Task 4/6.
- Task 2: complete (fix round 1/5: 7/7 addressed; spec ✅; quality APPROVED; 5/5 tests; independent 5.000s H.264 spike with 4 reviewed keyframes)
- Task 3: complete (root-cause TTS fix; spec ✅; quality APPROVED; 6/6 tests; Rate4, all VO safety margins ≥0.186s, music 30.000s/48k/stereo/peak0.22)
- Task 3: minor (deferred): Python music generator is intentionally stdlib-only and slower; voice/music CLI fail-fast improvements optional; subjective intelligibility remains user review.
- Task 4: pending
- Task 5: pending
- Task 6: pending
