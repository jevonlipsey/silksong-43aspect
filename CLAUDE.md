# silksong-43aspect

Native 4:3 for *Hollow Knight: Silksong* (Team Cherry, Steam appid 1030300), part of the nova-43aspect collection.

## Status
- **In progress.** Patcher, verifier and CLI are done and verify clean; device boot-test results are in `CLAUDE.local.md`. In-game behaviour (HUD, menus, map, gameplay camera) is NOT verified: adb input does not reach the game, so only the title screen can be screenshotted.

## Commands
Run from the repo root.
```bash
# patch (writes Assembly-CSharp-43.dll next to the input only if the verifier passes)
dotnet run --project patcher -- original/Managed/Assembly-CSharp.dll original/Managed Assembly-CSharp-43.dll
# without a managedDir argument the reference-free verifier subset runs (what the browser does)

# verify a patched dll against the pristine one. exit 0 and "checks: 57 passed, 0 failed, 0 skipped"
dotnet run --project verify -- Assembly-CSharp-43.dll [original.dll] [managedDir] [--reference-free]
```
Exit codes of the patcher: 0 ok, 1 verify failed/error, 3 already patched, 4 not Silksong's dll.
Pristine inputs live in `original/Managed/` (all 152 Windows managed dlls pulled from the device, sha256 in `original/hashes.txt`, gitignored, read-only). Patch only the Windows build from the device.

## Engine facts (verified)
- Unity **6000.0.50f1**, Mono scripting backend. `Assembly-CSharp.dll` (7,004,160 bytes) references **`netstandard` 2.1** (not mscorlib); Unity's `netstandard.dll` facade is in Managed. Any reference scoped to `System.Private.CoreLib`/`System.Runtime` would build and then fail at JIT on device, so the verifier bans them.
- Cecil can't rewrite the dll without resolving a few enum types (`System.Threading.ThreadPriority`, `LoadSceneMode`, PlayMaker `VariableType`) used as optional-parameter constants. `StubResolver` (patcher/PatchApi.cs) synthesises empty stub enums on demand, so no Unity dll is needed to patch (needed for the browser).
- `Platform.CreatePlatform()` exists in generic and non-generic forms; `MethodDefinition.FullName` collides, so the verifier keys methods with the generic arity.

## How the camera works (verified from the decompile)
- `ForceCameraAspect` (on the main camera) drives a `tk2dCamera`. Every resolution change calls `AutoScaleViewportShared`: the screen aspect is **clamped to [1.6, 2.3916667]**; anything narrower than 1.6 is letterboxed (viewport rect shrunk to the clamped aspect, `clearCamera` enabled). Below 16:9 it returns `heightMult = (16/9)/clampedAspect` and the main camera FOV is `initialFov * heightMult`, the HUD camera ortho size is `* heightMult`, and `anchorTopLeft` moves so the HUD stays pinned top-left. So the game's own design for narrow screens is **Vert+**: the horizontal extent stays that of 16:9 and the view gets taller.
- Consumers: `ViewportAspectChanged` (ScaleWithAspect, CameraAspectScaler, CinematicPlayer/Sequence, CameraBlurPlane, LightBlurredBackground, SceneParticlesController, CameraRenderToMesh, StatusVignette) and `MainCamHeightMultChanged` (ScreenHeightAdjustPos, StatusVignette). All take a runtime aspect, none hardcode 16:9 except as the lower end of a lerp range.
- **Camera bounds** (`CameraController`): hardcoded to a 16:9 view. Camera centre is clamped to `[14.6, sceneWidth-14.6] x [8.3, sceneHeight-8.3]` (`xLimit`/`yLimit` from `GetTilemapInfo`), and `CameraLockArea` windows (`yLockMin/yLockMax`, defaults 8.3 and `yLimit`) are in the same units. 8.3 is the half visible height at the hero plane at 16:9; 14.6 the half width. A 4:3 view is 1.333x taller (half height 11.07), so with unchanged bounds it shows 2.77 units past the top and bottom of every room and lock area.

## What the 32:9 reference mod changes (silksong32-9-mod, Sept 2025 build)
Diffed method by method against the current vanilla dll (ilspy decompile of both, `diff -r`). 167 files differ; almost all are version noise (new game content: advanced video/gamepad menus, Soul Snare quest, save-delay fix, etc.). The aspect changes are only:
- `ForceCameraAspect.AutoScaleViewport` (the older build has it inline; current has `AutoScaleViewportShared`): the clamp to [1.6, 2.39] is removed. It uses the raw screen aspect, so the viewport rect is always the full screen (no pillar/letterbox) and `heightMult = (aspect < 16/9) ? (16/9)/aspect : 1`. For ultrawide that means FOV is untouched and the view just gets wider.
- `ForceCameraAspect.Awake`: `CurrentViewportAspect` starts at the screen aspect instead of the 16:9 constant.
- `ForceCameraAspectLite.AutoScaleViewport`: same unclamped viewport math (current build delegates to the shared function).
- `PlanarRealtimeReflection`: `dest.aspect = Screen.width/Screen.height` instead of copying the source camera's aspect. Possibly an older-build difference rather than an aspect change; we do not touch it (the source camera aspect is already correct for us).
It changes **nothing** in `CameraController`/`CameraLockArea`/`CameraTarget` (so ultrawide shows past room edges horizontally), nothing in UI/canvas code, `UIManager`, resolution menus, HUD or map. Its UI behaviour (HUD pinned to the edge, menus) comes entirely from the existing camera/HUD-camera setup above. Nothing was copied from it.

## What our patch does (patcher/Program.cs)
Six methods differ from vanilla, nothing else (the verifier proves it):
1. `ForceCameraAspect.AutoScaleViewportShared`: `ldc.r4 1.6` -> `4/3`. At 1280x960 the viewport is the full screen, `heightMult = 4/3`. (Narrower than 4:3 still letterboxes, which is right.)
2. `ForceCameraAspect.AutoScaleViewport`: `fov * heightMult` -> `Nova43Camera.ScaleFov(fov, heightMult)` = `2*atan(tan(fov/2)*mult)`. Linear scaling would make the view 1.41x taller instead of 1.33x at 4:3, and the bounds fix needs the exact factor. At 16:10 the difference was small, which is why the game gets away with it.
3. `CameraController.LateUpdate`: after the game's own scene clamp (the `!AllowExitingSceneBounds` block) it runs `position.y = Fix43Y(position.y)`.
4. new `CameraController.Fix43Y(y)`: in `LOCKED` mode uses `[yLockMin, yLockMax]`, otherwise `[8.3, yLimit]`, and calls
5. new `Nova43Camera.AdjustY(y, lo, hi, mult)`: shrinks the window by `d = 8.3*(mult-1)` on each side (so the taller view shows exactly what the 16:9 view would have), and if the window is shorter than the view centres on it. `mult <= 1` (16:9 and wider) is an exact no-op.
6. new `Nova43Camera.ScaleFov`.
Only the vertical is touched: width stays 16:9-wide (14.76 half width) so the horizontal bounds are unchanged. `CameraTarget` has its own 8.3 clamp on the follow target; it is left alone, the final camera position is what is corrected. Short rooms (shorter than 22.1 units) can't be framed without showing past an edge; the camera centres instead.

HUD and menus: nothing is patched. The HUD camera and `anchorTopLeft` already handle `heightMult` (the game ships on 16:10 Deck), and the canvases are driven by `canvasScaler.referenceResolution` 1920x1080. **Unverified on device beyond the title screen.**

## Verifier (verify/)
Three layers, all gating the patcher's output:
- `IlVerifier`: banned host-runtime scopes (whole module: 9,417 member refs), for each touched method a stack-height re-derivation, branch targets, reachability, member resolution (full mode against the real Unity dlls; reference-free counts external resolves as skipped), and visibility (Mono throws on private/internal cross-type access).
- `SemanticVerifier`: whole-module diff against the original: exactly six methods differ, one new type, no field changes, assembly references identical; instruction-level diffs of the three edited methods; helper type shape.
- `Simulator`: a small IL interpreter runs the injected methods as emitted and compares with an independent model; checks the property that matters (4:3 view never exceeds the 16:9 window, short rooms centre, mult 1 is a no-op, FOV exact).

## Conventions
- Follow the nova-43aspect skill (device access, test workflow, repo conventions, web patcher design contract).
- Never commit game files, decompiler output, logs or screenshots. `reference/` (the 32:9 mod's dll) and `decomp/` are gitignored; the reference is for study only.
