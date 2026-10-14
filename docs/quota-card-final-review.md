# Quota card release: root final review

> Historical card/local-usage release. See [current follow/color/estimate final review](panel-follow-final-review.md) for the latest source and acceptance evidence.

Date: 2026-10-12. This report describes the current card/local-usage release; earlier reports under `docs/` remain the first release's history.

## Result and source identity

CodeBuddy implementation and separate sequential Codex checking completed. Root independently reran Release build, all tests and format verification: **0 warnings/errors, 185 passed, 0 failed/skipped, format exit 0**. Current checked-source hashes match the checker manifest. The verified Release Desktop/Core DLLs match the published DLLs; module ID is `58dd821b-0093-4f88-a957-fa27523b5730`.

- Desktop SHA-256: `CDD4264C8E675401E80556AC5E1CABE0F110BD04963CEC7C678B926ADFA8FEF6`.
- Core SHA-256: `AE716AEFD0DD79129E5ACE46B62D8E91068BAA840BD51CA07F78B7160ACB9838`.
- Published EXE SHA-256: `D46443AD5D31A6BD1444CAA046E6C759FF0F9124298E4DB1B454529E1C55D014`.

Root evidence: `artifacts/verification/quota-card-local-usage/root/quality-gate.log`, `final-proof.json`, and `delivery-launch.json`. The launch proof is generated for the normal widget's visible owned ball, without relying on the process's MainWindowHandle (WPF tool windows can have an empty owner handle).

## UI and interaction

Published harness reports **77/77 presentation checks** and **53/53 real WPF routed-event/timer checks**. Root visually opened the current light/dark, partial and long-value details renders. Main headers have title/duration left and explicit remaining right; bars and reset rows fit. Daily explanations are quiet in complete states and visible when coverage is partial. The ring and existing dark settings/dropdown remain readable.

Free dragging uses the actual ellipse capture surface through non-hit-testable overlays. Center/rim hit testing, capture target, arbitrary unsnapped movement, one release save, restore, clamping and lost capture were checked. Hover, pin, gear, drag and close produce no local scan or official query.

## Data and resource evidence

Only official quota/account methods are used in normal quota refresh; optional official daily read is off. The daily card is separately typed device-local activity, explicitly nonofficial, with conservative counter/date/duplicate/coverage semantics. Sanitized local/official reads succeeded; no raw records or credentials are emitted. Checker regressions covered duplicate continuations, null/invalid counter replay, oversized tails, bounded discovery progress, replacement/removal, timezone/midnight, allowed-field parsing and shutdown races.

Final normal-app smoke: 135-second startup/two cycles plus 45-second idle, 22 helper-free idle samples, average **131.65 MiB working set / 59.02 MiB private**, sampled idle CPU **0**. Simultaneous refresh working-set peak **186.48 MiB**; only one active helper sample was caught, so this is not a future upper bound. No forced GC or memory trim. Lazy details/settings were not constructed; second instance and graceful exit passed with **0 owned helpers**. The later final delivery launch intentionally leaves one normal widget running.

Current evidence is in `artifacts/verification/quota-card-local-usage/check/`: desktop reports, 17 PNGs, local/official aggregate reads, smoke/resource/cleanup reports, checked-source and binary manifests. Root's final proof records hashes and report results for this same candidate.

## Limits and bookkeeping

Duplicate logical-session files are conservative Partial totals, not a proven complete continuation merge. Rewrites preserving metadata and sampled bytes may evade cursor replacement detection; no atomic global filesystem snapshot is claimed. Physical pointer/keyboard injection, mixed-DPI/hotplug hardware, Windows 10, actual login autostart and OS theme-change events remain unverified. WPF event/capture evidence is not a substitute for those hardware checks.

Project specs now document the local source contracts, 72-DIP ring/cards, actual capture and coverage rules. The old task seed was removed from the approved PRD. `git status --porcelain` confirms this workspace has no Git repository; no commit, Git initialization or worktree was created. Task archive/journal bookkeeping is performed after the final launch/proof.
