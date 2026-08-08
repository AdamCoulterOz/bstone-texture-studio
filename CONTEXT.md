# Project Context

## Overview

`bstone-texture-studio` is a Blazor WebAssembly app for AI-redrawing the wall and
sprite art of **Blake Stone: Aliens of Gold / Planet Strike**, producing hi-res
external texture packs for the [BStone](https://github.com/bibendovsky/bstone)
source port. Textures are genuinely *redrawn* — composed into sprite sheets, sent
to an image model with rich per-cell context, then sliced, alpha-keyed,
hand-placed and packed — not batch-upscaled.

- Repo: <https://github.com/AdamCoulterOz/bstone-texture-studio> (public, GPL-2.0-or-later)
- Live: <https://adamcoulteroz.github.io/bstone-texture-studio/> (published from `gh-pages`)
- Listed as project 04 on <https://adamcoulteroz.github.io/>

Everything runs client-side. The workspace is a local folder chosen via the File
System Access API; generation calls go straight from the browser to the model
provider with the user's own key.

### The pipeline end to end

1. **Import** `VSWAP.BS6`/`VSWAP.VSI` → all tiles eager-decode into `originals/`.
2. **Curate** items/frames (names, purposes, roles, categories, prompt aliases).
3. **Group** tiles into sheets; optionally **join** contiguous cells into seamless
   runs (murals that must flow across cell edges).
4. **Generate** — the group composes into one square sheet PNG with a generated
   prompt (style description, style refs, character ref, approved-frames grid,
   per-cell map) and goes to the model.
5. **Review & Revise** — mark coloured regions on the result, describe changes per
   set, re-run region revisions composited back into the marked areas only.
6. **Adjust & Apply** — every cell is sliced and alpha-keyed, then each sprite is
   hand-positioned (move/scale/rotate/align) over a ghost of its original before
   baking. Any tile can be unticked to keep its existing art.
7. **Pack** — `TextureStudio.Pack` writes `<workspace>/pack/aog/{wall|sprite}_<id:08>.png`;
   run `bstone --mod_dir <workspace>/pack` with
   *Options → Video → Texturing → External Textures* enabled.

Status at time of writing: ~939 textures packed and played in-game for hours. The
remaining content gap is non-Goldfire bosses and menus (menus are a different
engine system, out of scope for external textures).

## Key Files

| Path | Role |
| --- | --- |
| `src/TextureStudio.Core/` | Pure logic, no UI. VSWAP/sprite parsing (ported from bstone, hence GPL), sheet compose/slice, alpha keying, dark generation, model clients. |
| `src/TextureStudio.Core/Imaging/SheetComposer.cs` | `PlanLayoutRuns` — the layout planner. Seamless runs butt edge-to-edge and never wrap; the grid stays square and at least as wide as the longest run. Returns `PlannedLayout{Manifest, Ghosts, Side}`. |
| `src/TextureStudio.Core/Imaging/SheetSlicer.cs` | Cuts a returned sheet back into tiles: content-box detection with proportional fallback, per-cell seamless handling, sprite-box erosion, aspect-true sprite tiles on matte. |
| `src/TextureStudio.Core/Imaging/AlphaKeyer.cs` | `KeyAuto` — alpha-trust for pre-keyed cells, magenta matte key with despill + warm-glow unmix, else border flood. |
| `src/TextureStudio.Core/Imaging/SpriteFootprint.cs` | `SpritePlacement` record + `ComputePlacement`/`ApplyPlacement`/`Normalize` — the placement-tuning maths. |
| `src/TextureStudio.Core/Generation/GeminiImageClient.cs` | Gemini image API (`thinkingLevel`, `topP`, defensive response parsing). |
| `src/TextureStudio.Core/Generation/OpenAiImageClient.cs` | OpenAI GPT Image API (`/v1/images/edits` with reference inputs, quality parameter). |
| `src/TextureStudio.Core/Model/ProjectModel.cs` | `Project` and everything persisted in `project.json`. |
| `src/TextureStudio.App/Services/StudioState.cs` | ~3k lines: the single app state object. Prompt building, job lifecycle, slicing orchestration, URL caches, workspace I/O, persistence. |
| `src/TextureStudio.App/Pages/GroupPane.razor` | The main area: tabs, group grid (platter), results views, placement tuning. The busiest UI file. |
| `src/TextureStudio.App/Pages/Home.razor` | Shell: topbar, settings drawers, sidebar/props columns, statusbar. |
| `src/TextureStudio.App/Pages/{TileGridPane,InspectorPane,JobsPanel,JobToasts,StudioLightbox}.razor` | Items grid, properties, jobs list, toasts, shared lightbox. |
| `src/TextureStudio.App/Pages/Icon{Add,Dismiss,Chevron}.razor` | Shared Fluent icon components. |
| `src/TextureStudio.App/wwwroot/js/interop.js` | All pixel work that must not run in C#: `composeSheet`, `composePngGrid`, `pngPreviewDataUrl`, `annotatePng`, `imageSize`, `copyText`, `autoSizePrompt`. |
| `src/TextureStudio.Pack/` | CLI: workspace → mod dir (redraws as-is, darks synthesized). |
| `src/TextureStudio.Rekey/` | CLI: re-slice archived generations against `project.json` manifests. |
| `tests/TextureStudio.Core.Tests/` | 20 tests. The VSWAP test skips when game data is absent (`BSTONE_VSWAP` env override). |

### Workspace layout (user data, not in this repo)

```
<workspace>/
  project.json          # the whole project: items, meta, groups, versions, jobs, UI state
  originals/            # decoded VSWAP tiles
  redraws/              # applied redraw PNGs
  revisions/<group>/<id>/   # per-accepted-slice tile snapshots
  generations/          # every raw sheet that ever entered the app, verbatim
  jobs/<jobId>/         # keyed tiles of an in-progress placement session
  refs/, style-refs/    # character and style reference images
  gemini-api-key.txt    # NEVER commit
  openai-api-key.txt    # NEVER commit
  pack/                 # Pack output (rebuildable)
```

The author's workspace is `~/Code/bstone-textures`; an isolated copy for tooling
tests lives at `~/Code/bstone-textures-test` (refresh with
`rsync -a --exclude 'pack/' ~/Code/bstone-textures/ ~/Code/bstone-textures-test/`).
**Both contain API keys** — never add them to a repo, and never fire real
generations from automated tests.

## Build + Deploy Pipeline

### Local development

```bash
dotnet build src/TextureStudio.App
dotnet test tests/TextureStudio.Core.Tests
```

Dev server: preview name `texture-studio` (`.claude/launch.json` in the sibling
`bstone` repo), always on **port 5610**.

> **A stale dev server cannot serve a rebuilt app.** net11 fingerprints framework
> assets (`dotnet.<hash>.js`) and blazor-devserver maps them at process start, so
> after any rebuild the server process must be killed and restarted:
> `lsof -nP -iTCP:5610 -sTCP:LISTEN -t | xargs kill`. Never switch ports — the
> File System Access workspace handle is origin-scoped to `localhost:5610`.

The app is used in Edge/Chrome (File System Access API required). The embedded
preview pane refuses FS Access writes on restored handles.

### Deploying to GitHub Pages

No CI yet (the net11 preview SDK is awkward in Actions). Manual recipe:

```bash
dotnet publish src/TextureStudio.App -c Release -p:WasmNative=true -o <tmp>
cd <tmp>/wwwroot
sed -i '' 's|<base href="/" />|<base href="/bstone-texture-studio/" />|' index.html
touch .nojekyll
cp index.html 404.html
find . -name "*.br" -delete && find . -name "*.gz" -delete   # Pages won't content-negotiate
git init -b gh-pages && git add -A && git commit -m "Deploy: …"
git push -f https://github.com/AdamCoulterOz/bstone-texture-studio.git gh-pages
```

Poll `gh api repos/AdamCoulterOz/bstone-texture-studio/pages/builds/latest --jq '.status'`
until `built`. GitHub's CDN can serve the old `index.html` for a few minutes.

## Domain Model

- **Category → Item → Frame.** `TileItem` owns Name/Category/IsAnimation and an
  ordered list of frame keys; per-frame `TileMeta` holds Purpose (the
  differentiator only), Role, LightSourceKey, and prompt alias.
- **Tile keys** are `wall:12` / `sprite:107` (`TileRef`).
- **Groups** (`TileGroup`) are sheets: ordered `TileKeys` plus `SeamlessRuns`
  (each an ordered *contiguous* slice of TileKeys rendered butted together).
- **Jobs** (`GenerationJob`) are generate/import/revise runs with states
  `Queued → Running → AwaitingReview → Placing → Done`, plus `Failed`,
  `Cancelled`, `Interrupted`. Persisted to `Project.JobHistory` (newest first,
  cap 50).
- **Versions**: every applied slice records a `TileVersionInfo` per tile, so
  frames can be cherry-picked across runs independently.
- **Dark model**: all redraws are drawn light. `DerivedDark` = darken(light
  source's redraw). `AlternateDark` = darken(its *own* redraw); its
  `LightSourceKey` is a brightness *reference* only. Derived darks never go on
  sheets. `LightenParams` brightens AlternateDark originals *into* sheets so the
  model redraws them as light art (dark inputs come back as noise).

## Current Decisions

### Performance (WASM is single-threaded)

- **No multi-megapixel C# loops or big buffer marshals on hot paths.** Compose,
  preview, annotate, and grid building all happen browser-side in `interop.js`;
  only 64px source tiles cross the interop boundary.
- Long C# work (slicing, keying, placement restore) is **chunked** with
  `await Task.Delay(1)` between cells plus a progress status.
- **Never schedule an autosave from a render-loop hook.** Serializing
  `project.json` is a main-thread stall; saves are scheduled only from real edit
  events. (This caused a multi-second placement freeze once.)
- Heavy background writes (placement art persistence) wait ~3s for the UI to
  settle, then yield ~30ms between files.

### Blazor / Razor

- Project-data bindings need `@bind:after="State.Notify"` (textareas also
  `@bind:event="oninput"`). Plain `@bind` never notifies, so nothing ever saves.
- Drawers flush `project.json` immediately on close; otherwise a 1.5s debounce.
- Inside an `@if` block after markup you're in code context — plain `var x = …;`,
  not `@{ }` (RZ1010).
- Nested interpolated strings with quotes inside an attribute expression break
  the parser — hoist to a local.
- `RenderFragment` icon fields (`private static readonly RenderFragment X = @<svg…>;`)
  compile fine in `@code`.
- `Project.Ui` is **replaced wholesale** on every save — any new user-preference
  field on `UiState` must be carried over explicitly in `SyncJobHistory`.

### CSS

- Declaration order matters: a later rule of equal specificity wins. Use compound
  selectors (`.platter-cell.abs-cell`) for overrides, and remember that a class
  alone does not inherit a compound-scoped rule.
- Icon `<svg>`s inside flex buttons need `flex: none`; icon-only buttons need
  `padding: 0` or the glyph gets crushed.
- Standard control height across the app is **26px**; dismiss buttons are square.
- Joined runs render as **one flex strip** (`.run-strip` with `.run-cell` children
  at `aspect-ratio: 1`) — a single box partitions exactly, so no sub-pixel seams
  and no stretched art. Never re-solve seams with image bleed/stretch.

### Prompting

- Implementation details never leak into user-editable prompt fields. Mechanics
  live in the hidden `SheetMechanicsPrompt`; `MigrateStylePrompt` strips legacy
  seeded text on load.
- The prompt enumerates attachments by index (sheet, style refs in order,
  character ref, approved-frames grid) with a per-image clause, and always
  injects "use them as context only; do not copy their subject matter…" directly
  after the last style ref.
- Sprite cells compose over magenta `#FF00FF` and are **inset** (~8% margin per
  side) so the input demonstrates the framing we want back.
- Seamless strips get a CRITICAL clause forbidding splitting, gutters, dividers
  and re-spacing. (Models do sometimes re-space a butted strip into discrete
  cells anyway — see Open Questions.)
- Prompt aliases (`GenerationAlias` on item and frame) substitute for
  Name/Purpose in prompts only, to dodge safety filters on gore/fluid terms.
  Curated metadata is never touched.

### Jobs

- Every run **arms for 5 seconds** before the API call, with a countdown toast and
  a free cancel — cancelling inside the window costs nothing. Cancel also aborts
  in-flight calls.
- Versions in one Generate run go **strictly in series** (rate limits); the sheet
  is composed once up front. `MaxConcurrentJobs` (1–5, default 1) gates
  cross-job concurrency via a polled slot.
- Transient failures (429, 5xx, `RESOURCE_EXHAUSTED`, `UNAVAILABLE`, HTTP
  timeouts) retry with 5s/15s/45s backoff; user cancellation never retries.
- Starting a job does **not** open a tab — the toast or jobs-list click does.

### Providers

- Gemini and OpenAI both selectable in the Model API drawer; each has its own key
  file in the workspace. `OpenAiImageClient.IsOpenAiModel` routes by model id
  prefix (`gpt-image*`).
- The **Effort** control maps to Gemini's `thinkingLevel` and OpenAI's `quality`;
  available levels are filtered per model.
- OpenAI has no top-p, so multi-version takes are labelled "take n" rather than
  by top-p.
- GPT Image models require OpenAI **organization verification** (done for this
  account).

## Open Questions / Ambiguities

- **Models sometimes split joined strips.** Diagnosed on a real sheet: the model
  re-spaced a butted 3-cell run into discrete cells with its own ~20px
  background gutters, compressing the last member to preserve the strip's total
  span — so the slicer's butted cuts landed on separators. The prompt is now
  hardened against this; a slicer-side adaptation (detecting the model's own
  separators) was built and **deliberately reverted** — the slicer stays
  manifest-true. If the prompt proves insufficient, revisit with the owner.
- **Not yet exercised against a live API**: the OpenAI provider, the
  serial/cancel/retry paths under real rate limits, and the lighten round-trip
  quality for AlternateDark frames.
- **Not yet exercised with a real workspace**: placement resume after refresh,
  session restore (group/tabs/filters), job numbering and banners at scale.
- **UI automation is blocked at the workspace door.** The FS Access directory
  picker is a native dialog that neither the embedded pane nor Playwright can
  drive, and VSWAP import is workspace-gated — so end-to-end UI flows can't be
  automated today. A dev-only escape hatch (in-memory VSWAP import, or a `?demo`
  flag opening a bundled read-only workspace) would unlock it; that's a product
  change and needs the owner's call.
- **No deploy CI** — revisit once the .NET SDK for this app is on a stable
  channel rather than a preview band.
- **Content gaps** (owner's own work): non-Goldfire bosses; menus/VGA lumps are
  a separate engine system and out of scope.
- Ideas parked: an `oxipng` optimization pass folded into `Pack`; a share package
  for the bstone maintainer (optimized zip + before/after strip + write-up).
