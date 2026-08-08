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
