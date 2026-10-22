# Hollow Knight: Silksong 4:3 Aspect Ratio Patch

Native 4:3 aspect ratio patch for **Hollow Knight: Silksong**, aimed at the **Retroid Pocket Nova** (4.5", 1280x960) and any other 4:3 display. The game fills the screen with no letterbox, the view gets taller instead of narrower, and the camera stays inside each room. Part of the [Nova 4:3 collection](https://jevonlipsey.github.io/nova-43aspect/).

The patcher ships only our own patch logic and modifies your own copy of `Assembly-CSharp.dll`. **No game code is redistributed.**

> Unofficial fan project, not affiliated with or endorsed by Team Cherry. Hollow Knight: Silksong and its files belong to them. The code here is MIT licensed ([LICENSE](LICENSE)).

## Install (players)

- **Web patcher** (any browser, including your phone): <https://jevonlipsey.github.io/silksong-43aspect/>. Drop in your `Assembly-CSharp.dll`, download the patched file, put it back. It runs in your browser and the file is not uploaded.
- **Undo:** put your `.bak` back, or use Steam's **Verify integrity of game files**.

The file is in the game's `Managed` folder:

| where | path |
|---|---|
| DroidDeck | `/root/.local/share/Steam/steamapps/common/Hollow Knight Silksong/Hollow Knight Silksong_Data/Managed/Assembly-CSharp.dll` |
| DroidDeck, SD card | `/mnt/droiddeck-sd/steamapps/common/Hollow Knight Silksong/Hollow Knight Silksong_Data/Managed/Assembly-CSharp.dll` |
| Windows, Steam Deck, GameNative | `Hollow Knight Silksong_Data/Managed/Assembly-CSharp.dll` in the game folder |

1. Rename your original `Assembly-CSharp.dll` to `Assembly-CSharp.dll.bak`. Keep it as your backup.
2. Put the patched `Assembly-CSharp.dll` in the same `Managed` folder.
3. Launch the game. It fills a 4:3 screen (1280x960 on the Nova) with no letterbox. If it was already open, fully exit and relaunch it.

This is for the Windows build (which is also what DroidDeck, Steam Deck and GameNative run). A game update replaces the file, so patch again after one.

## Why the game letterboxes

Silksong's `ForceCameraAspect` clamps the screen aspect to between 1.6 (16:10) and about 2.39. Anything narrower than 16:10 is letterboxed. Between 16:10 and 16:9 the game already does the right thing: it keeps the 16:9 width and makes the view taller (Vert+), and the HUD camera keeps the HUD pinned to the top-left. 4:3 is just below the cut-off.

## What the patch changes

Six methods in `Assembly-CSharp.dll` differ from vanilla, and nothing else. The verifier proves it.

- **Aspect floor:** the 1.6 clamp becomes 4/3, so 4:3 gets the game's own Vert+ path instead of a letterbox. Narrower than 4:3 still letterboxes.
- **Field of view:** the game scales the FOV linearly by the height multiplier. That is close enough at 16:10 but makes a 4:3 view 1.41x taller instead of 1.33x. The patch uses the exact `2*atan(tan(fov/2)*mult)`.
- **Vertical camera bounds:** room and lock-area bounds are hardcoded for a 16:9-tall view. With a taller view, the unpatched camera would show past the top and bottom of every room. After the game's own clamp, the patch narrows the vertical window by the extra half-height, so the 4:3 view never shows more than a room allows. Rooms too short for the taller view are centred. At 16:9 and wider this is an exact no-op.
- **HUD and menus:** not patched. The HUD camera and canvases already handle the taller view.

## Verification

The patcher runs a verifier before it writes anything. It has 57 checks in three layers:

- **IL:** stack heights, branch targets, member resolution and visibility for every touched method. It also rejects any reference to the patcher's own .NET runtime, which would build fine and then fail when the game loads it.
- **Semantic:** a whole-module diff against the original. Exactly six methods differ, plus one new helper type, and no fields or assembly references change.
- **Simulator:** a small IL interpreter runs the injected code as emitted and checks it against an independent model. The 4:3 view never exceeds the 16:9 window, short rooms are centred, and 16:9 is untouched.

The browser runs the same patcher compiled to WebAssembly, and its output is byte-identical to the CLI's (`node web/test/parity.mjs`).

Tested on a Retroid Pocket Nova (DroidDeck, 1280x960).

## Build from source

Needs the .NET SDK. Run from the repo root:

```sh
# patch: writes the output only if the verifier passes
dotnet run --project patcher -- <path>/Assembly-CSharp.dll <Managed folder or -> Assembly-CSharp-43.dll

# verify a patched dll against the original
dotnet run --project verify -- Assembly-CSharp-43.dll <original Assembly-CSharp.dll> [Managed folder]

# web patcher
bash tools/build_web.sh
node web/test/parity.mjs [--chrome]
```

With a `Managed` folder, the verifier also resolves members against the real Unity assemblies. With `-` it runs the reference-free subset the browser uses.

Patcher exit codes: 0 ok, 1 verification failed or error, 3 already patched, 4 not Silksong's `Assembly-CSharp.dll`.

## Limits

- **Short rooms** can't be framed at 4:3 without showing past an edge, so the camera centres on them.
