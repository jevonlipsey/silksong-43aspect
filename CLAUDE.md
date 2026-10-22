# silksong-43aspect

Native 4:3 for *Hollow Knight: Silksong* (Team Cherry, Steam appid 1030300), part of the nova-43aspect collection.

## Status
- **Planned.** Scaffold only, nothing verified yet. Engine facts below are first impressions from the install folder; confirm each one before relying on it.

## Engine facts (unverified)
- Unity, Mono scripting backend (`MonoBleedingEdge/`, managed code in `Hollow Knight Silksong_Data/Managed/`). A `D3D12/` folder ships next to the exe. Confirm the Unity version and the graphics API DroidDeck ends up on.
- Expected patch shape: Mono.Cecil IL rewrite of `Assembly-CSharp.dll` + verifier + WASM web patcher, like chefrpg and gungeon.

## Plan
1. **Study the 32:9 mod.** <https://github.com/skypulsestudios/silksong32-9-mod> ships a pre-patched `Assembly-CSharp.dll` (Sept 2025) and no source or license. Use it as a reference only: decompile it and a vanilla dll, diff them method by method, and write down which methods it changed and how (camera aspect, UI/canvas scaling). Never copy its code or ship it. Its build is older than the current one, so the diff will also contain version noise; separate the aspect changes from that.
2. **Reimplement for 4:3** as our own Cecil patch against the current Windows build pulled from the device.
3. **Camera bounds:** Hollow Knight clamps the camera to per-scene bounds and lock areas. A taller view may show past the edge of rooms vertically. Check this early.
4. HUD (masks, silk, tools) anchored to the 4:3 corners; menus and the map inside the frame.

## Conventions
- Follow the nova-43aspect skill (device access, test workflow, repo conventions, web patcher design contract).
- Patch only the Windows build pulled from the device into `original/` (gitignored, read-only).
- Never commit game files, decompiler output, logs or screenshots.
