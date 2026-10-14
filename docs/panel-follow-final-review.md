# Panel follow and observed usage: root final review

Date: 2026-10-12. This is the current follow/color/estimate release. Earlier reports retain their original release counts and measurements.

## Result

Visible details/settings follow changed native ball positions continuously, including pinned drag steps, release clamp and restore. Hidden/lazy windows remain hidden; one release saves position. Placement uses actual physical rectangles, one final NOACTIVATE move and unchanged-position suppression. No interaction refresh/scan, repeated activation or intermediate panel move to the ball origin is introduced.

Fresh remaining policy is centrally owned by `QuotaProjection`: 0–30 red, 31–70 yellow and 71–100 green. Ring, card remaining numbers/bars and tray share it. Unknown/stale remain neutral. Fresh zero draws no fill arc and has red `0%` text. Details connection health is independent: Fresh green, Stale yellow, Error red, other states neutral.

The daily card retains device-local sessions/Tokens and adds neutral per-period `约xx%` observed estimates. Used 10% → 15% means about 5% of the period's full budget. The reducer uses only existing successful official observations, requires usable same-budget metadata and two comparable samples, and handles missing/reset/canceled/date/account/budget boundaries conservatively. Recovery rebaselines without inferring a gap. Safe totals may exceed 100%; gaps remain Partial. Estimates reset at restart/local midnight/account invalidation, are not Token conversions or official whole-day totals, and may include current-account activity on other devices. Local Tokens and quota estimates have independent scopes and availability.

Official transport, sign-in ownership, bounded local scanning, settings and startup truth remain in place. No observation persistence, new timer/request, private HTTP integration or credential/account write was added.

## Evidence and independent verification

Sequential implementation and checking used the explicitly selected Codeg Codex CLI runner. Real reports were read from disk; earlier missing handoffs were not accepted. The task's implementation/check reports are archived under `.trellis/tasks/archive/2026-10/10-12-panel-follow-observed-usage/`.

Current checker acceptance root:

`artifacts/verification/panel-follow-observed-usage/check/codex-check-20261012-130423/`

| Check | Confirmed result |
|---|---|
| Root Release build | 0 warnings/errors |
| Root Release tests | 237 passed, 0 failed/skipped; actual TRX counters read back |
| Root format verification | Exit 0 |
| Checker published WPF presentation | 243/243 checks, 63 PNGs |
| Checker routed events/native geometry | 84/84 checks, 26 geometry records |
| Checker normal smoke | Read-only startup, second instance exited, graceful close, 0 owned helpers after exit |
| Root identity audit | All 76 checker source entries matched before root documentation changes; built/published Core/Desktop/EXE hashes matched |
| Real preferences/startup | Settings SHA unchanged; startup registration remained absent |

Root evidence is `artifacts/verification/panel-follow-observed-usage/root/`: `build-tests-format.log`, `root-release.trx`, `root-identity.json` and `delivery-launch.json`. The source audit preceded README/spec/documentation updates; root changed no production or test source.

Root inspected current original light available, dark partial-long, waiting, local-Tokens-unavailable and dark stale cards, both-theme zero balls, unknown and 30/31/70/71 boundary balls. The amounts, actual remaining colors, independent health, unavailable/local scope and long values fit and remain readable. The checker inspected all 63 PNGs and actual bound WPF controls. Only the task's latest `published/` evidence is acceptance; red/intermediate runs remain diagnostic history.

The checker independently observed actual GetWindowRect adjacency on consecutive pinned steps, both edges, negative virtual-screen coordinates, native restore and release clamp. Example ball/details coordinates were `(-621,153)/(-1154,153)` then `(-471,228)/(-1004,228)` at DPI 120. The ordinary smoke ball was 108 physical pixels at DPI 144. These records are native geometry, not just expected outputs from the placement calculator.

## Published identity

| File | SHA-256 | MVID |
|---|---|---|
| Core DLL | `6A7DFA516EA656429A0D6844A3A0FBEF9B8FE021E6BAFB8E111CAF42EF00E5E4` | `33c2f9c4-7812-4a1d-98a1-1c23bd330504` |
| Desktop DLL | `50875FAE39A48296F7F73BADA21C17E2F9683DE96F04323F5543A93565092682` | `1dd4bf95-2f5d-422b-9e36-0613cfb33108` |
| Desktop EXE/apphost | `D46443AD5D31A6BD1444CAA046E6C759FF0F9124298E4DB1B454529E1C55D014` | Native apphost |

The unchanged apphost alone does not identify the managed revision. Published render/event/normal-smoke MVIDs match the checked Desktop DLL, and root rebuilt current source and compared both DLLs. Saved settings SHA remains `71C95F6427D55B16C4623A24E2FB1413DE25381A69EE8408A9D10217FC6AEE0C`.

## Delivery and limits

Root's delivery-launch evidence records the ordinary no-argument published process and its actually visible owned ball HWND, physical bounds/DPI and executable hash. It retains existing preferences and leaves the normal widget running. The complete self-contained release folder is `artifacts/win-x64/`. This workspace has no Git repository; none was initialized and no commit/worktree was fabricated. Only this completed task is archived; guideline bootstrap remains separate.

Delivery launch on 2026-10-12 at 13:24:36 +08:00 confirmed PID 10612, visible ball at `(2390,357)`, size `108×108` physical pixels and DPI 144. Settings hash remained identical and startup remained off. This records that launch; future user actions may change process/position/preferences.

Coverage includes routed WPF events, DispatcherTimers, production App composition and actual native geometry. It does not claim physical input injection, comprehensive mixed-DPI transitions/hotplug, Windows login/autostart execution or OS theme events. The checker used a short ordinary startup/idle smoke with no memory trim/forced GC; it is not a new long performance benchmark. Earlier resource measurements remain explicitly historical. Sampled in-memory estimates omit unobserved/restarted/cross-day/incompatible intervals by design.
