# History

Significant decisions and turning points, oldest first. Day-to-day changes live
in the git log; this records *why* the shape of the project changed.

## 2026-08-04: Establish the Studio as a Browser-Only Workspace App

- Chose Blazor WebAssembly with a File System Access workspace over a desktop or
  server tool, so the whole pipeline runs client-side and game data, artwork and
  API keys never leave the machine.
- Made the workspace the lifecycle: open a folder first, everything auto-persists
  into it (`project.json` plus PNG trees), reopening restores the session. No
  separate project files, no import/export step.
- Accepted GPL-2.0-or-later, because the VSWAP/sprite format handling in Core is
  ported from bstone's GPL sources.

## 2026-08-04: Settle the Light/Dark Variant Model

- Corrected the transform direction: **all redraws are drawn light**. `DerivedDark`
  tiles have no art of their own and are darkened from their light source's
  redraw; `AlternateDark` tiles darken their *own* redraw.
- Reduced `AlternateDark.LightSourceKey` to a brightness *reference* quoted in the
  prompt, rather than an art source.
- Excluded derived darks from groups and sheets at every entry point — they are
  generated, never redrawn.

## 2026-08-05: Introduce the Category → Item → Frame Layer

- Replaced flat per-tile metadata with a three-layer model: items own Name,
  Category and animation; frames keep only the differentiator (Purpose) and their
  light/dark Role.
- Made purposes describe *what the art shows*, not the engine token — frame
  constants are reused art conventions and mislead (a "SWING" constant whose
  sprites show a sidearm and muzzle flash).
- Treated the curated metadata as the owner's data: enrichment fills empty fields
  only and never overwrites hand-authored names.

## 2026-08-05: Move Pixel Work Out of C#

- Established the rule that no multi-megapixel loop or large buffer marshal runs
  on the single WASM thread: composing, previewing, annotating and grid-building
  moved to browser canvases behind `ImageCodec`.
- Made the remaining C# pixel work (slice, key) chunked with yields and progress,
  which removed the browser's "page unresponsive" warnings.
- Kept job sheets as PNG bytes with lazily-decoded pixels, so switching between
  jobs is instant.

## 2026-08-05: Split Slicing into Review and Placement Steps

- Stopped auto-applying sliced results. Slicing now stops in a placement grid
  where each keyed sprite sits over a ghost of its original and can be moved,
  scaled, rotated and aligned before baking.
- Added per-tile include checkboxes so a partial slice can adopt some frames and
  leave the rest untouched.
- This step is what makes the output hand-finished rather than machine-fitted —
  the owner hand-placed ~50 frames in one sitting on its first outing.

## 2026-08-05: Add Per-Frame Versions and Identity References

- Recorded a version per tile on every apply, so frames can be cherry-picked
  across different generation runs independently of the group revision.
- Added per-group character references and an automatic grid of already-approved
  frames as identity anchors, with per-frame exclusion — the mechanism that keeps
  a character consistent across dozens of sprites.
- Kept reference previews on their own URL cache lanes, because the display lane
  applies the source/redraw toggle and dark derivation.

## 2026-08-05: Ship the External Texture Pack Path

- Discovered bstone already probes `<search path>/aog/{wall|sprite}_<id:08>.png`
  in the hardware renderer, so the pack needs no engine changes — only
  `--mod_dir` and the External Textures option.
- Added `TextureStudio.Pack` to turn a workspace into a mod directory,
  synthesizing dark variants from the project's dark params.
- First pack: 939 textures, played in-game for hours.

## 2026-08-06: Keep Implementation Out of User Prompts

- Moved sheet mechanics (aspect, registration, canvas rules) out of the editable
  style description into a hidden injected prompt, with a migration that strips
  the legacy seeded text from existing workspaces.
- Added prompt aliases at item and frame level so safety-filter-tripping wording
  (gore, bodily fluids) can be reworded for the model without touching curated
  metadata.
- Made every attachment enumerated by index in the prompt so image references and
  payload order can never drift apart.

## 2026-08-06: Fix Silent Save Loss

- Found that project-data fields bound with plain `@bind` never notified, so no
  autosave was ever scheduled and edits vanished on reload.
- Adopted the rule that project bindings use `@bind:after="State.Notify"`, and
  settings drawers flush immediately on close rather than relying on the debounce.

## 2026-08-06: Replace Group-Wide Seamless with Per-Run Joins

- Replaced the whole-group "seamless rows" flag with per-selection joins: any
  contiguous run of cells can be butted edge-to-edge, and several runs can coexist
  with ordinary cells on one sheet.
- Rebuilt the layout planner around runs (never wrap mid-run, grid at least as
  wide as the longest run, saved gutters surface as an end gap) and made the
  platter render from that same manifest, so the preview is the sheet.
- Extended the slicer and the prompt to be run-aware rather than sheet-aware.

## 2026-08-07: Give Results Their Own Tabs

- Moved review/placement out of a panel under the group grid into main-area tabs:
  the grid is a permanent first tab, and each opened job is a closable tab
  decoupled from the current group selection.
- Made starting a job *not* steal focus — a job opens a tab only when its toast or
  jobs-list entry is clicked.

## 2026-08-07: Make Generation Interruptible and Serial

- Added a 5-second arming countdown before every API call, so a mistaken Generate
  can be cancelled at zero cost; cancellation also aborts in-flight calls.
- Switched multi-version runs from parallel to strictly serial after rate limits,
  added an explicit `Max concurrent jobs` setting (default 1), and gave transient
  failures escalating backoff.
- Surfaced all of it as job cards: countdown, cancelling, cancelled, ready — with
  status-coloured counts on the Jobs button.

## 2026-08-07: Persist Jobs and Session State

- Persisted the jobs rail (newest 50) into `project.json`, mapping in-flight
  states to `Interrupted` on restore rather than pretending they survived.
- Snapshotted placement sessions to `jobs/<id>/` so Adjust & Apply resumes after a
  refresh with tuning intact and no re-slice.
- Restored what was open — group, tabs, item filters, redraw toggle — and learned
  the hard way that the UI-state object is rebuilt wholesale on save, so any new
  preference must be carried through it.

## 2026-08-07: Publish Publicly

- Published the repo and a GitHub Pages build, with a first commit gated on
  secret scans and a `.gitignore` covering build output.
- Moved one-off rescue scripts out of the repo: two were completed migrations,
  one had been ported into the app, and the rest were incident tools.
- Deferred deploy CI while the app sits on a preview SDK band; the manual publish
  recipe is documented instead.

## 2026-08-07: Preserve Sprite Geometry Through the Round Trip

- Rendered joined runs as a single flex strip after image-bleed and stretch
  workarounds were rejected — one box partitions exactly, so seams are structurally
  impossible and art is never distorted.
- Made the composed sheet *demonstrate* the framing we want back: sprite cells are
  inset so matte shows on all four sides, which is more reliable than asking for
  it in prose alone.
- Stopped force-squaring non-square model output; sprite crops now scale
  aspect-true onto matte, and detected boxes erode past the border antialiasing.

## 2026-08-07: Add OpenAI GPT Image as a Second Provider

- Added `gpt-image-2`/`1.5`/`1`/`1-mini` beside the Gemini models, each provider
  with its own workspace key file and routing by model id.
- Mapped the existing Effort control onto both providers (Gemini thinking level,
  OpenAI quality) rather than exposing provider-specific controls.
- Kept the error-message shape consistent across clients so the shared retry
  classifier works for both.

## 2026-08-08: Make Games Plugins — Retro Texture Studio

- Renamed the project from *BStone Texture Studio*: the pipeline was never
  Blake-Stone-specific, only its front and back doors were. Repo renamed
  `bstone-texture-studio` → `retro-texture-studio` (the old Pages URL does not
  redirect, so the live site needs a redeploy and the personal-site link an
  update).
- Extracted every game assumption behind `Core/Games/IGame`: opening the asset
  container, editions and their detection, the light/dark pairing convention,
  the mod-dir layout, the install steps, and the engine reference table. Blake
  Stone became the first implementation rather than the only possibility.
- Split *format* from *game*: the VSWAP container and tile codecs stayed in
  `Core/Formats` (the Wolfenstein family shares them), while the palette moved
  into the plugin — it is game data, not a generic VGA table.
- Kept Core HTTP-free by having `IGameMetadata` declare a static-asset path that
  the app fetches and hands back, and made missing reference data degrade to
  blank labels instead of failing.
- Chose per-workspace game selection with a hard lock once tiles exist: the same
  tile index means different art in different games, so switching would silently
  corrupt curation. `Project.GameId` defaults to Blake Stone's id so pre-plugin
  workspaces load unchanged — checked by packing a real workspace and confirming
  the same 939 file names came out (contents were spot-checked at this point; the
  exhaustive byte comparison came with the packer move below).
- Gave the workspace a **Game** drawer and a topbar chip, and moved importing
  into it — which let the Items sidebar drop its Import button. An empty items
  list then had no route to importing, so it grew a first-run empty state that
  names the game and links to the drawer.

## 2026-08-08: Let Games Find Their Own Content

- Added `IGameLocator`, porting the searching half of bstone's launcher
  (`bstone_game_source.cpp`) — its bounds (10 levels, 4096 directories), its
  marker files, its "a game folder is an answer, not a place to search under"
  rule, and its store labelling.
- Did **not** port the unprompted half — registry, Steam library manifests, GOG
  Galaxy sqlite. A browser has no ambient filesystem; every byte comes from a
  handle the user granted, so there is no equivalent to reach for. The granted
  root is remembered instead, and re-scanned silently only when no game data is
  loaded, since a scan is thousands of interop round trips.
- Kept the search root a separate, read-only handle from the workspace: it
  points at Applications or a Steam library, which must never be written to.
- Took the edition from the art file's extension rather than its contents —
  `.BS6`/`.BS1`/`.VSI` name the release outright, so the old sprite-count
  heuristic became a fallback for renamed files.
- Kept manual file-picking beside it. The locator is the fast path, not the
  only one.

## 2026-08-08: Pack From the App, and Link Rather Than Instruct

- Moved packing behind `IGame.PlanPack`, which *plans* rather than performs: a
  list of files, each naming its source tile and transform. That is what let one
  packer serve both the CLI and the browser, which share no I/O at all.
- Added a **Pack** button to the title bar. Packing no longer needs a terminal —
  the app writes `<workspace>/pack` through the workspace handle it already has,
  which is what makes the studio self-contained for a non-developer.
- Replaced the spelled-out install commands with `InstallGuide` links to the
  source port and its own documentation. Command lines differ per platform and
  rot in this repo; the port's docs do not.
- Verified the move by packing a real workspace and comparing all 939 PNGs
  against the previous implementation's output — byte-identical. Comparing
  against the workspace's *stored* `pack/` instead showed 40 differences, which
  turned out to be a stale baseline: it was built at 03:58 and those redraws
  changed between 04:37 and 05:29. Worth remembering that a workspace's `pack/`
  is only a valid baseline if nothing has been applied since.
- Hid the job toasts while the jobs popup is open: they dock in the same corner
  one layer above it, duplicating the list they cover.
- Shipped all of the above: `main` pushed, a fresh `gh-pages` build deployed at
  the new URL, and the sibling `adamcoulteroz.github.io` project-04 row, JSON-LD,
  keywords and sitemap moved with it. The old Pages URL now 404s by design —
  GitHub redirects renamed repos, not their Pages sites.

## 2026-08-08: Say So When the Browser Cannot Run It

- Added a capability gate. Everything the studio does is client-side, so on a
  browser without the File System Access API it was not degraded but dead — and
  the only symptom was a folder picker that looked cancelled. Now it says which
  APIs are missing, what each was for, and which browsers work.
- Probed for what the code actually calls rather than sniffing the user agent:
  detection cannot go stale the way "Safari doesn't support this" can.
- Split the concern the same way as everything else here — detection in
  `interop.js`, policy in C#. WebAssembly is not probed: the gate runs inside the
  Blazor app, so its own existence proves it.
- Learned the hard way that a **parameterless child component does not re-render
  when its parent calls `StateHasChanged`**. The gate sat frozen on its
  pre-check state through two attempted fixes before the cause was clear; both
  components now await one shared task in their own `OnInitializedAsync`.

## 2026-08-08: Separate Tile Identity From Tile Kind

- Split the two jobs `TileKind` was doing. Identity became an opaque game-minted
  id (`w22`, `s53`) that nothing outside the plugin parses; kind became `Full` /
  `Cutout`, the only tile distinction the pipelines are allowed to make.
- Deleted `TileRef`. "Wall" and "sprite" were a Blake Stone concept sitting in
  the middle of the app: 54 sites branched on them, and only about a third were
  really about *behaviour* — the rest were file names, chunk ranges and pairing
  rules that belonged to the game all along.
- Dropped the Walls/Sprites tabs from the Items panel. Kinds drive the pipeline,
  not browsing, so one list in the game's tile order with one flat category
  dropdown — docked under the panel title and sticky.
- Made `SheetCell` carry its kind so `SheetSlicer` branches without seeing an id,
  which is what keeps Core/Imaging free of any game dependency.
- Took enumeration order from `IGameArchive.Tiles` rather than a tile index, so
  "engine order" is something the game expresses by listing, not something the
  app computes.
- Kept workspace file names at their pre-plugin spelling (`w12` →
  `wall_00012.png`). An id scheme is not worth orphaning thousands of files for.
- Migrated persisted ids everywhere a project keys tiles by them — metadata,
  light-source links, items, group cells, seamless runs, revisions, the version
  index, archived job manifests and placements. Verified by packing the real
  workspace through the migration: 939 textures, byte-identical.
- Then deleted the migration, and `IGame.MigrateTileId` with it. Only two
  workspaces exist, so a compatibility layer for a scheme nobody else has ever
  written was pure carrying cost. Both were converted first — the author's by
  opening it, its tooling copy by refresh — and the three-day-old `.bak` files
  were converted too, since a backup that restores into ids matching no art
  reads as total data loss rather than as an old backup.
- The interface is the point: a game mints its ids and owns their spelling, so
  changing the scheme is a migration to plan, not a permanent seam.

## 2026-08-08: Stop Carrying Compatibility Code

- Deleted the remaining one-shot migrations now both workspaces are current: the
  whole-group `Seamless` → per-run conversion (and the field), the version-index
  backfill from group revisions, the style-prompt strip, the single legacy
  style-reference file, and the v1 layout string in localStorage.
- Dropped `Project.SourceFileName` — the open archive already knows its own name
  — along with the unused `Categories.Defaults` and the Pack CLI's `--game`
  alias.
- Renamed `ItemMigration` to `ItemLayerBuilder` and `EnsureItemsMigratedAsync` to
  `EnsureItemLayerAsync`. Neither was ever a migration: items are *derived* from
  the tiles, so a fresh import has none until it runs. Calling it a migration had
  it lumped in with code that was genuinely disposable.
- Found one `LastExport` manifest still carrying the old sheet-level seamless
  flag, which would have mis-cut a re-slice of that group had the fallback been
  removed blindly. The groups themselves had moved to runs years of edits ago —
  the migration had only ever rewritten live fields, never the archived snapshot.
  Converting that one manifest (three cells, sheet flag → per-cell flags, exactly
  what the fallback expanded it to) then let the whole sheet-level concept go:
  the fallback, `SheetManifest.Seamless`, and the seamless branch of `PlanLayout`.
  Worth remembering that "the model is migrated" and "the data is migrated" are
  different claims.
- Verified by re-packing: 965 textures, byte-identical to the run before the
  removals.

## 2026-08-08: Make It Installable

- Added a PWA manifest and service worker so the studio installs as an app and
  runs offline. A Blazor WASM app is nearly the ideal candidate — it is already
  static files running client-side — and the ~35 MB runtime is exactly the sort
  of payload worth caching once.
- Chose to **offer** updates rather than apply them. A service worker parks a new
  build in "waiting" until every tab on the old one closes, which for an
  installed app may be never; but taking over silently reloads the page, and the
  studio holds unsaved placement sessions and queued generations. Hence the
  yellow title-bar button, and a dismiss that lasts until the next build.
- Made the development worker wait as well. It caches nothing, but a worker that
  activated immediately would have made the update flow untestable until it was
  already in front of users.
- Generated the icon from a script instead of checking in binaries, so the tiny
  sizes are rendered directly rather than downsampled.
- Went through two icon designs. The first said "the same tile at increasing
  resolution" via quadrants subdivided 1/2x2/3x3/4x4 — meaningful, and unreadable
  below 48px. Replaced with a plain 2x2 checker: four squares, two colours, edge
  to edge. An app icon lives at 16-32px, so legibility there outranks cleverness,
  and a checker still says both "texture tile" and "transparency".
- Found and fixed a real bug on the way: the first-render setup guarded on
  `!Support.IsSupported`, which is also false *while the capability probe is
  still running*. On a supported browser that could skip every restore —
  including the workspace reconnect — depending on a race with the first render.

## 2026-08-08: The Update Button That Never Appeared

The update offer shipped, two builds were deployed, and the button never showed.
It was not the detection code — that was wired correctly all along.

- **The service worker had never successfully installed, on either deploy.** Its
  install caches every asset with the integrity hash recorded at publish time, and
  `cache.addAll` is all-or-nothing. The deploy rewrites `index.html`'s
  `<base href>` *after* publish, so that one file no longer matched its hash:
  install threw, the worker never reached "waiting", and there was nothing to
  offer. Offline had never worked either. Proved by hashing the live
  `index.html` (`sha256-MW1h1BLN…`) against what the manifest demanded
  (`sha256-58lXRxs8…`); of 7 sampled assets it was the only mismatch, and it is
  the only file the deploy edits.
- The failure mode is the problem as much as the bug: a worker whose install
  throws is indistinguishable, from the page, from a worker that is simply up to
  date. Nothing logs. So the fix is two-sided — `tools/deploy.sh` re-stamps the
  manifest and *verifies every asset*, aborting rather than pushing a tree that
  cannot install; and the page now logs a worker that goes `redundant`.
- Scripted the deploy at the same time. It had been a documented sequence of
  commands, which is precisely how a step gets left out.
- Closed two real gaps in detection while in there: nothing triggered an update
  check on load (it relied on the browser's own schedule), and `updatefound` can
  fire during `register()` — before the listener attaches — leaving a worker
  mid-install unnoticed until the next reload.
- **Corrected an earlier note.** This file and CONTEXT.md claimed service workers
  could not be verified in the embedded browser pane, which is why the flow was
  left unverified. That was wrong: the pane reports a genuine `waiting` worker,
  and the whole handover — Install → `SKIP_WAITING` → `controllerchange` → reload
  → new worker `activated` → offer cleared — was verified there. Believing the
  tooling was incapable is what let a broken deploy sit unnoticed.

## 2026-08-08: Window Controls Overlay

- Took over the title bar when installed, via
  `display_override: ["window-controls-overlay", "standalone"]`. The topbar now
  matches the OS title bar height exactly and pads itself clear of the window
  buttons, so the app reads as native rather than as a page inside a frame.
- The reserved rectangle is queried, not assumed. The buttons sit on the left on
  macOS and the right on Windows, so `interop.js` publishes both insets and the
  CSS keeps whichever side is occupied clear.
- Gated on `windowControlsOverlay.visible` rather than on the API existing —
  a supporting browser reports `false` in a normal tab, where the bar still needs
  its ordinary flow height.
- Dropped the "Retro Texture Studio" wordmark from the bar and moved the
  game/edition chip down beside the workspace it describes. With the OS buttons
  living in the bar there is less room, and both were saying something the window
  title and the status bar already say.
- Then fixed it properly against a real installed app, which is where the
  assumptions showed:
  - **The right inset was wrong.** There is only one rectangle to query — the
    usable strip — so the right inset is a subtraction against the window width.
    Doing that in JS against `innerWidth` mixed coordinate spaces with the rect.
    Both insets are now derived in CSS from `env(titlebar-area-*)`, where the terms
    share a source. Edge on macOS makes this visible in a way Windows does not: it
    has controls on *both* sides, its own `…` menu sitting opposite the traffic
    lights.
  - **The reserved strips clashed.** The browser paints them in the theme colour,
    which was one fixed dark value, so they read as black blocks either side of a
    light bar. Now scheme-scoped `theme-color` metas track `--panel` — and unlike
    the manifest's `theme_color` they apply on reload, where a manifest change needs
    a reinstall.
  - **The update offer no longer centred.** It had been centred in the *free space*
    between two spacer spans, which was near enough when the wordmark and chip
    filled the left. With those gone it drifted well left, so it is now positioned
    on the bar's centre and the trailing controls are pinned right by an auto
    margin — auto margins absorb free space before flex-grow, which is what let one
    rule serve both the offer-present and offer-absent cases.
  - **The divider stopped short at both ends.** The control strips are painted over
    the page for the full height of the title bar area — which is exactly the bar's
    height — so `border-bottom`, being the last pixel *inside* that height, was
    covered wherever a strip sat. The divider is now a `box-shadow` one pixel below
    the box, past where the strips reach, with the border kept transparent rather
    than removed so the box geometry does not shift.
  - **The overlay strips cannot be transparent, settled by experiment.** If they
    could, the page's own hairline would show through and none of the above would
    have been needed. `theme-color: transparent` is *accepted* rather than skipped
    — it is a valid CSS colour, so the fall-through to a later `<meta>` never
    happens — and Chromium then flattens the alpha: `rgba(0,0,0,0)` rendered as
    opaque black, giving black blocks at both ends. Transparency is real for
    `CoreWebView2WindowControlsOverlay.BackgroundColor` and UWP caption buttons,
    but those are native embedding APIs a PWA cannot reach. Reverted the same day;
    the tone stays.
- Two self-inflicted bugs worth recording, both caught only by checking:
  - A stray line left outside a `/* */` while editing a comment silently discarded
    the **entire** `.topbar` rule — CSS error recovery skips to the end of the next
    block. Height, both insets and `position: relative` all vanished, and the bar
    still looked plausible because the base rule's `padding: 6px 10px` remained.
  - The new "install failed" console diagnostic fired on every dev restart. A
    worker that installs fine and is then superseded also ends up `redundant`, so
    the check now requires reaching redundant *without* ever having been installed.
    A diagnostic that cries wolf is worse than the silence it replaced.
