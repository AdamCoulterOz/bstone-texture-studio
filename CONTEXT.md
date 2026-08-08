# Project Context

## Overview

`retro-texture-studio` is a Blazor WebAssembly app for AI-redrawing the wall and
sprite art of retro games, producing hi-res external texture packs for their
source ports. Textures are genuinely *redrawn* — composed into sprite sheets, sent
to an image model with rich per-cell context, then sliced, alpha-keyed,
hand-placed and packed — not batch-upscaled.

**Games are plugins** (`Core/Games/IGame`). One ships today: **Blake Stone**
(Aliens of Gold full/shareware, Planet Strike) targeting the
[BStone](https://github.com/bibendovsky/bstone) source port. Each workspace picks
its game and edition; everything downstream of tile decoding is game-agnostic.

- Repo: <https://github.com/AdamCoulterOz/retro-texture-studio> (public, GPL-2.0-or-later)
- Live: <https://adamcoulteroz.github.io/retro-texture-studio/> (published from `gh-pages`)
- Listed as project 04 on <https://adamcoulteroz.github.io/>
- Renamed from `bstone-texture-studio` when the game abstraction landed. GitHub
  redirects the old repo URL; the old **Pages** URL does not, and now 404s. The
  sibling `adamcoulteroz.github.io` repo carries the project-04 row, structured
  data, keywords and sitemap entry — all four moved with the rename.

Everything runs client-side. The workspace is a local folder chosen via the File
System Access API; generation calls go straight from the browser to the model
provider with the user's own key.

### The pipeline end to end

1. **Choose the game** for the workspace (*Settings → Game*, or the topbar chip),
   then **import** its data. Grant a folder to search and the game's locator
   finds installed copies inside it — including in macOS application bundles —
   or pick the file yourself (`VSWAP.BS6`/`.BS1`/`.VSI` for Blake Stone). All
   tiles eager-decode into `originals/`.
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
7. **Pack** — the *Pack* button in the title bar writes `<workspace>/pack/…` in
   whatever layout the game asks for (Blake Stone:
   `pack/aog/{wall|sprite}_<id:08>.png`); `TextureStudio.Pack <workspace>` does
   the same from the CLI. Point the source port at that folder — the Game drawer
   links to its download and its own mod-loading docs.

Status at time of writing: ~939 Blake Stone textures packed and played in-game for
hours. The remaining content gap is non-Goldfire bosses and menus (menus are a
different engine system, out of scope for external textures).

## Key Files

| Path | Role |
| --- | --- |
| `src/TextureStudio.Core/` | Pure logic, no UI. Game plugins, sheet compose/slice, alpha keying, dark generation, model clients. |
| `src/TextureStudio.Core/Games/` | `IGame` / `IGameArchive` / `IGameMetadata` / `IGameLocator` / `IDirectoryTree`, plus `GameEdition`, `PackPlan` and `GameCatalog` — everything a source game dictates. See ARCHITECTURE.md for the member-by-member map. |
| `src/TextureStudio.Core/Games/BlakeStone/` | The one shipped plugin: `BlakeStoneGame` (editions, pairing, pack plan, install links), `VswapArchive`, `BlakeStonePalette`, `BlakeStoneMetadata`, `BlakeStoneLocator`. Ported from bstone, hence GPL. |
| `src/TextureStudio.App/Services/ContentSearchService.cs` | The read-only folder granted for the locator to search — an `IDirectoryTree` over a second File System Access handle, remembered separately from the workspace. |
| `src/TextureStudio.App/Services/BrowserSupport.cs` + `Pages/BrowserGate.razor` | The capability gate: which browser APIs the studio requires, and the blocking overlay shown when one is absent. |
| `src/TextureStudio.Core/Formats/` | VSWAP container + wall/sprite codecs — shared by the whole Wolfenstein family, so they sit outside any one plugin. |
| `src/TextureStudio.Core/Imaging/SheetComposer.cs` | `PlanLayoutRuns` — the layout planner. Seamless runs butt edge-to-edge and never wrap; the grid stays square and at least as wide as the longest run. Returns `PlannedLayout{Manifest, Ghosts, Side}`. |
| `src/TextureStudio.Core/Imaging/SheetSlicer.cs` | Cuts a returned sheet back into tiles: content-box detection with proportional fallback, per-cell seamless handling, sprite-box erosion, aspect-true sprite tiles on matte. |
| `src/TextureStudio.Core/Imaging/AlphaKeyer.cs` | `KeyAuto` — alpha-trust for pre-keyed cells, magenta matte key with despill + warm-glow unmix, else border flood. |
| `src/TextureStudio.Core/Imaging/SpriteFootprint.cs` | `SpritePlacement` record + `ComputePlacement`/`ApplyPlacement`/`Normalize` — the placement-tuning maths. |
| `src/TextureStudio.Core/Generation/GeminiImageClient.cs` | Gemini image API (`thinkingLevel`, `topP`, defensive response parsing). |
| `src/TextureStudio.Core/Generation/OpenAiImageClient.cs` | OpenAI GPT Image API (`/v1/images/edits` with reference inputs, quality parameter). |
| `src/TextureStudio.Core/Model/ProjectModel.cs` | `Project` and everything persisted in `project.json`. |
| `src/TextureStudio.App/Services/StudioState.cs` | ~3k lines: the single app state object. Game/edition resolution, prompt building, job lifecycle, slicing orchestration, URL caches, workspace I/O, persistence. |
| `src/TextureStudio.App/Pages/GroupPane.razor` | The main area: tabs, group grid (platter), results views, placement tuning. The busiest UI file. |
| `src/TextureStudio.App/Pages/Home.razor` | Shell: topbar, settings drawers, sidebar/props columns, statusbar. |
| `src/TextureStudio.App/Pages/{TileGridPane,InspectorPane,JobsPanel,JobToasts,StudioLightbox}.razor` | Items grid, properties, jobs list, toasts, shared lightbox. |
| `src/TextureStudio.App/Pages/Icon{Add,Dismiss,Chevron}.razor` | Shared Fluent icon components. |
| `src/TextureStudio.App/wwwroot/js/interop.js` | All pixel work that must not run in C# (`composeSheet`, `composePngGrid`, `pngPreviewDataUrl`, `annotatePng`, `imageSize`, `copyText`, `autoSizePrompt`), plus two File System Access blocks: the read-write workspace (`ws*`) and the read-only content search root (`content*`). |
| `src/TextureStudio.Pack/` | CLI: workspace → mod dir, from the game's `PlanPack`. `pack <workspace> [out-dir] [--edition <id>]`. |
| `src/TextureStudio.Rekey/` | CLI: re-slice archived generations against `project.json` manifests. Game-agnostic — it only touches workspace files. |
| `tests/TextureStudio.Core.Tests/` | 35 tests. The real-data archive test skips when game data is absent (`BSTONE_VSWAP` env override); the locator tests run against an in-memory `IDirectoryTree`. |

### Workspace layout (user data, not in this repo)

```
<workspace>/
  project.json          # the whole project: game+edition, items, meta, groups, versions, jobs, UI state
  source/               # the imported game data, verbatim
  originals/            # decoded source tiles
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

Dev server: preview name `texture-studio` (`.claude/launch.json` in this repo),
always on **port 5610** — `launchSettings.json` says 5034, so the launch config
passes `--urls http://localhost:5610` explicitly.

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
sed -i '' 's|<base href="/" />|<base href="/retro-texture-studio/" />|' index.html
touch .nojekyll
cp index.html 404.html
find . -name "*.br" -delete && find . -name "*.gz" -delete   # Pages won't content-negotiate
git init -b gh-pages && git add -A && git commit -m "Deploy: …"
git push -f https://github.com/AdamCoulterOz/retro-texture-studio.git gh-pages
```

Poll `gh api repos/AdamCoulterOz/retro-texture-studio/pages/builds/latest --jq '.status'`
until `built`. GitHub's CDN can serve the old `index.html` for a few minutes.

> The base href **must** match the repo name — a mismatch serves an index that
> cannot find its own assets. This bit once already: the rename moved the Pages
> URL (GitHub redirects renamed *repos*, not their Pages) and the published build
> kept the old base href until it was redeployed.

## Domain Model

- **Game → Edition.** A workspace targets one `Project.GameId` (the `IGame`
  plugin) and one `Project.EditionId` (empty = detected from the archive). The
  game owns tile decoding, the light/dark convention, the mod-dir layout and the
  engine reference table; nothing downstream of decoding knows which game it is.
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
  model redraws them as light art (dark inputs come back as noise). Which tiles
  pair by default is the *game's* call (`IGame.DefaultLightSource`); Blake Stone
  pairs odd walls to their even sibling.

## Current Decisions

### Games

- **One game per workspace, and it locks once there is content.** Tile ids mean
  different things in different games, so `SetGame` refuses once anything is
  imported or curated. Changing game = new workspace.
- `IGame.Id` is persisted in every `project.json` — **never** change a shipped
  one. `Project.GameId` defaults to `"blake-stone"` precisely so pre-plugin
  projects, which have no such field, land on the game they were made with; a
  test asserts the two literals agree.
- `Project.SourceFileName` keeps `[JsonPropertyName("GameName")]`: the property
  was renamed to stop it reading like the *game* rather than the imported file,
  but the JSON key must stay put or existing workspaces lose it silently.
- Edition is stored empty by default and *detected*; pinning it is a user
  override, not the norm. Importing a located copy whose edition contradicts a
  manual pin drops the pin — the file is the better witness.
- `Core/Formats` (VSWAP container + tile codecs) stays outside the plugin
  because the whole Wolfenstein family shares it; only the **palette** is game
  data, so it lives in `Games/BlakeStone/`.
- Core must stay HTTP-free, so `IGameMetadata` declares an `AssetPath` and the
  app feeds it bytes. Reference data failing to load is never fatal — lookups go
  null and the studio keeps working.
- **The locator only searches what the user granted.** bstone's launcher also
  finds installs unprompted (registry, Steam `libraryfolders.vdf`, GOG Galaxy
  sqlite); none of that exists in a browser, so only the "search under a folder
  the user chose" half was ported. The granted root is remembered in IndexedDB
  and re-scanned silently on load — but *only* when no game data is loaded yet,
  because a scan is up to 4096 interop round trips.
- The search root is a **second, read-only** handle, never the workspace: it
  points at Applications or a Steam library, which must not be written to.
- Edition comes from the art file's extension (`.BS6`/`.BS1`/`.VSI`), not from
  the archive's contents. Sprite count is only the fallback for a renamed file.
- **Packing is planned, not performed, by the game.** `PlanPack` returns a file
  list; the app and the CLI each read art and write bytes their own way. That is
  the only reason one packer can serve both a browser and a console app.
- Install steps are **links, not commands** (`InstallGuide`). Spelled-out command
  lines differ per platform and rot here; the port's own docs do not. The README
  anchors are section-numbered, so re-check them when bumping a port version.

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
- Razor HTML-encodes the result of an `@(…)` expression, so put the *literal*
  character in the C# string: `@("<workspace>")` renders `<workspace>`, while
  `@("&lt;workspace&gt;")` renders the entity text verbatim.
- **A parameterless child component will not re-render because its parent
  called `StateHasChanged`.** With no parameters there is nothing to diff, so a
  child rendering off injected service state stays frozen at whatever that state
  was on first render. Either give the child a parameter, subscribe it to an
  event (as every pane does with `State.OnChange`), or — for a one-shot answer —
  have the child `await` the same shared task in its own `OnInitializedAsync`.
  `BrowserSupport.EnsureCheckedAsync` exists for exactly that: the shell and the
  gate both await one probe, and neither has to render before the other.

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
- `.badge` is `position: absolute` — it exists to overlay grid tiles. A new badge
  used as inline text must say `position: static`, or it renders on top of its
  neighbour instead of beside it.
- `.drawer-body label` is a full-width stacked field, so a `.file-button` label
  in a drawer stretches into a bar unless it opts out
  (`.drawer-body label.file-button` restores `inline-flex` + row).
- Bottom-right surfaces share one corner and are layered: drawer 40/41, backdrop
  59, menus and the jobs popup 60, lightbox 60/61, **job toasts 65**. The toasts
  sit above the jobs popup on purpose, which is why the popup hides them rather
  than fighting the z-index.

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
  session restore (group/tabs/filters), job numbering and banners at scale, and
  the in-app **Pack** button at full size — its plan is verified byte-for-byte
  against the CLI, but ~939 canvas PNG encodes in one browser pass has only ever
  been reasoned about, never timed. If it drags, the fix is the established one:
  chunk harder and report progress.
- **UI automation stops at the directory picker, not at the door.** The FS Access
  picker is a native dialog nothing can drive, so anything workspace-gated
  (import, pack, generate) still cannot be driven end to end. Everything *else*
  is automatable: panes, drawers, menus and state transitions drive fine, and a
  service whose only dependency is interop can be exercised by overriding the
  `studioInterop` function from the console — which is how the locator's search,
  results and edition detection were verified against a fake directory tree
  without granting anything. A dev-only escape hatch (in-memory import, or a
  `?demo` flag opening a bundled read-only workspace) would close the remaining
  gap; that's a product change and needs the owner's call.
- **No deploy CI** — revisit once the .NET SDK for this app is on a stable
  channel rather than a preview band. Until then every deploy is the manual
  recipe above, so the published build silently lags `main` between releases.
- **Content gaps** (owner's own work): non-Goldfire bosses; menus/VGA lumps are
  a separate engine system and out of scope.
- Ideas parked: an `oxipng` optimization pass folded into `Pack`; a share package
  for the bstone maintainer (optimized zip + before/after strip + write-up).
