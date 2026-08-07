# BStone Texture Studio

A browser-based studio for AI-redrawing the wall and sprite art of
**Blake Stone: Aliens of Gold / Planet Strike**, producing hi-res external
texture packs for the [BStone](https://github.com/bibendovsky/bstone) source
port. Every texture is genuinely *redrawn* — composed into sprite sheets,
sent to an image model with rich per-cell context, then sliced, alpha-keyed,
hand-placed and packed — not batch-upscaled.

**Use it here: <https://adamcoulteroz.github.io/bstone-texture-studio/>**

Everything runs client-side in your browser (Blazor WebAssembly). Your game
data, artwork and API key never touch a server of ours — the workspace is a
local folder you pick, and generation calls go straight from your browser to
the Gemini API with your own key.

## What you need

- A **Chromium browser** (Chrome or Edge) — the workspace uses the File
  System Access API.
- Your own legally owned **Blake Stone game data** (`VSWAP.BS6` for Aliens of
  Gold, `VSWAP.VSI` for Planet Strike).
- A **Gemini API key** for generation.

## Workflow

1. Open a workspace folder and import your VSWAP.
2. Curate items and frames, arrange sheets in groups (seamless runs
   supported), attach style / character / approved-frame references.
3. Generate redraws, review & revise regions, then adjust each sprite's
   placement before applying.
4. Pack the results with `TextureStudio.Pack` and run
   `bstone --mod_dir <workspace>/pack` with
   *Options → Video → Texturing → External Textures* enabled.

## Running locally

```bash
dotnet run --project src/TextureStudio.App
```

Requires a .NET SDK with the `wasm-tools` workload. The pack/re-slice CLIs
live in `src/TextureStudio.Pack` and `src/TextureStudio.Rekey`.

## Credits & license

GPL-2.0-or-later — see [LICENSE](LICENSE). The VSWAP/sprite format handling
is ported from [BStone](https://github.com/bibendovsky/bstone) by Boris I.
Bendovsky, based on the Blake Stone source by JAM Productions, published
under the GPL by Apogee Software. Blake Stone remains a trademark of its
owners; this tool ships no game data.
