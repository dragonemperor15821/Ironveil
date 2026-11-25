# IRONVEIL — Stage 2d: Formation Visualization and In-Unity Validation

Status: implemented and tested automatically in a scratch copy. **It has not yet been inspected
interactively in the production project; see "Production inspection" below.** This is debug
visualization only. There are no troops, movement, pathfinding, Flak firing, targeting, combat, capture
or production UI. The range spheres are geometric references, not an implemented weapon system.

## Architecture

The visualizer is entirely **editor-only and needs no scene changes**.

| Component | Role |
|---|---|
| `Assets/Editor/FormationDebugGeometry.cs` | Turns the records `BaseSpawner` already produced (`LastFacingPairs`, `LastClusters`: member poses, front attack positions, supporter links with 3D distances and bands, recorded coverage intervals, reserved strips) plus the range table (`CrossfireRanges`, S applied) into a cached list of draw commands for each formation, with inspector text. It recomputes no formation geometry and writes nothing back. |
| `Assets/Editor/FormationDebugWindow.cs` | `Tools ▸ IRONVEIL ▸ Formation Debug` window and a Scene-view drawer (`SceneView.duringSceneGui`, `Handles`). Toggles, formation list, frame/select, details panel, and Play-mode-only regenerate buttons. |

* **No existing file changed.** `BaseSpawner`, the formation algorithms, prefabs, terrain, river,
  railway and both scenes are untouched. Both files compile into `Assembly-CSharp-Editor`, so nothing
  is added to builds.
* **Settings live in per-user `EditorPrefs`** (`IRONVEIL.FormationDebug.*`), never in a scene or the
  project. **Every toggle defaults to OFF.** With the master switch off, the drawer returns at once.
* **No GameObjects** are created. Drawing uses `Handles` during the Scene view's Repaint event only.
* **Cached geometry.** It is rebuilt only when the spawner finishes a new run (detected by the identity
  of its report string and the formation counts). Per frame there is only filtering and drawing.
* Pair corridor width (`facingPairSettings.corridorWidth`) and cluster strip width
  (`clusterSettings.approachWidth`) stay separate settings, both 8. The outlines use the recorded strip
  geometry.

## What is drawn

| Toggle (all OFF by default) | Content |
|---|---|
| `showFormationDebug` | master switch; also draws the structure (pair line, magenta member rings; cluster boundary polygon, cyan member rings, centre dot) |
| `showFacingPairDebug` / `showClusterDebug` | formation types, independent of each other |
| `showMemberFacing` | white arrow along each member's horizontal forward |
| `showAttackPositions` | front attack position (3.3 ahead): green sphere if it has the required supporters (pair: 1, the partner; cluster: 2), red if not; label "OK/FAIL n supporters (need k)" |
| `showSupporterLines` | solid green line from each valid supporter to the target (label: distance in [min, max]); dotted dark-red line for out-of-band candidates (label: distance, InsideMinimum/BeyondMaximum) |
| `showEngagementRanges` | for formation members only: minimum boundary = 3 orthogonal dotted orange circles, maximum = 3 solid blue circles, centred on the base centre, radius = SC band × S (labelled "L5 min 20", "L5 max 24") |
| `showApproachCoverage` | each member's 10-unit approach from its front position, coloured from the recorded intervals: green = ≥ 2 supporters, yellow = 1, **red dotted = uncovered**; ticks at interval boundaries; label "covered x/10 (partial)" |
| `showApproachReservations` | translucent outlined boxes: pair axis corridor (magenta), pair approach strips (light magenta), cluster approach strips (yellow-cyan), each labelled "kind length x width" |
| `showFormationLabels` | text: formation ID and type (clusters also get a short tag C1/C2/C3, so `[C2#1]` = cluster 2, slot 1), member ID and level |
| `onlySelectedFormation` | show only the formation selected in the window, or the one that owns the selected base |
| `alwaysFullDetail` / `detailDistance` (150) | far away, only the structure and formation labels are drawn; close up, everything enabled is drawn |

Formations are told apart by labels and tags as well as colour. A selected formation also gets a
yellow ring.

## Inspector

The window lists every formation (ID, type, members, pass/fail). Clicking a row selects it, **Frame**
moves the Scene view to it, and **Select** selects its member GameObjects. Selecting any member base in
the Hierarchy or Scene view selects its formation automatically. The details panel shows the formation
ID and type, member IDs and levels, positions, forward headings, front attack positions, valid supporter
counts, every supporter's 3D distance with its min/max band and status, coverage intervals and the
support profile, and the reservation dimensions. The text is selectable, so it can be copied.

## Production inspection (MainBuild)

Formations exist only after generation, which runs in Play mode, and both formation switches are OFF
in the scene.

1. Open `Assets/Scenes/Ironveil - MainBuild.unity`. Wait for the scripts to compile; Unity also creates
   the `.meta` files for the new scripts.
2. Open **Tools ▸ IRONVEIL ▸ Formation Debug**. Dock it next to the Scene view.
3. Press **Play** and wait until the world has finished generating (350 bases).
4. In the window, under *Generation*, click:
   * **Pairs only**: facing pairs only (Stage 2b.1);
   * **Clusters only**: clusters only (Stage 2c);
   * **Both**: pairs and then clusters;
   * **None**: back to the original layout.

   Each button sets the two switches **for this Play session only** and regenerates the bases with the
   current seed. Unity reverts the switches when Play mode ends, so the scene stays OFF.
5. Tick **Show formation debug (master)**, then **All layers on**, or individual layers.
   To show **only pairs** or **only clusters**, untick *Clusters* or *Facing pairs*. These only hide;
   they don't regenerate.
6. Switch to the **Scene** tab (the drawing appears in the Scene view, not the Game view). Click a
   formation in the list and press **Frame**, or click a member base. The details appear at the bottom of
   the window. *Only the selected formation* removes the clutter around it.
7. To turn it off: click **All off** (or untick the master switch) and exit Play mode. Nothing has to be
   saved and the scene is not modified. Don't save the scene while the switches are changed in Play mode;
   Unity discards Play-mode changes anyway.

Edit mode: the window opens and the toggles work, but there is nothing to draw, because the terrain
preview in edit mode doesn't run BaseSpawner.

## Automated tests (scratch copy, seeds 12345 / 777 / 424242 / 1000)

Harness `S2dHarness`: **193 PASS / 0 FAIL.** No EditorPrefs were written.

* **Generation unchanged in all four configurations:**
  * A (both OFF): bit-identical to the pre-2c layouts; 12345 = `e71caf6c…`.
  * B (pairs only): layouts and formation reports bit-identical to Stage 2b.1.
  * C (clusters only) and D (both): bit-identical to Stage 2c (`8c20d0f6`, `e45553a5`, `ed2792bf`,
    `aff13791` / `a5d4a475`, `ea765965`, `a767a8c1`, `8f136697`).
  * The pair members in D are the same as in B (checked in the Stage 2c regression).
* **Visualization has no effect:** building the geometry leaves every record, the reserved strips and
  `UnityEngine.Random` unchanged. Regenerating with all toggles ON and with all OFF gives bit-identical
  layouts.
* **Range boundaries:** every member has 3 min + 3 max circles centred on its base centre. The radii
  equal the approved table × S (S = 1). Min circles are dotted and orange, max circles solid and blue.
  No circles are drawn for ordinary bases.
* **Supporter lines:** exactly the valid supporters, recomputed independently from the live transforms
  (full 3D, inside min and max, never itself). Out-of-band candidates appear only as dotted lines. Front
  markers show the true count and pass only at ≥ 2 (cluster) or 1 (pair).
* **Approach coverage:** drawn over exactly 10 units. The covered length matches independent 0.005-step
  sampling (largest difference 0.004). Segment colours match the actual supporter count everywhere, and
  0 uncovered samples are drawn as covered, so **coverage is never overstated**.
* **Reservations:** the drawn outlines equal `BaseSpawner`'s reserved rectangles one to one (21–33 per
  run, all corners within 1e-3), with widths of 8.
* **Labels** cover every member (ID + level) and every formation (ID + type). The **toggles** work
  (pairs-only, clusters-only, master off, only-selected, far-distance level of detail). The **details**
  text contains every field required above.
* **Performance:** the geometry builds once per generation in 0.2–13.7 ms (≤ 683 commands for 11
  formations). Per-frame filtering takes ≤ 0.02 ms and **allocates 0 bytes** (measured over 200 frames).

### Regression suites (on the Stage 2d code)

| Suite | Result |
|---|---|
| Stage 2c harness (all four configurations; pair-only = 2b.1; combined keeps the pairs; cluster crossfire, placement and reservation invariants) | **204 PASS / 0 FAIL** |
| Stage 2b.1 harness (pairs, corridors, OFF identity; covers the Stage 2b rules) | **96 / 96 PASS** |
| BaseSpawnerValidationRunner (nine types, Stage 1 placement rules) | **81 PASS / 0 FAIL** / 14 not testable (no colliders, visual review) |
| Stage 2a harness | 42 PASS / 2 FAIL: the two **known obsolete** "formations ON is informational" assertions that Stage 2b superseded. The result is identical on the pre-2c code (see the Stage 2c report), so they are not new. |

## Screenshots

These are outside the project, in the session scratchpad `out\d3\`. They are rendered by the harness
from **the same command list** the Scene view draws. Lines and labels are reproduced with temporary
primitives, top-down, so only the horizontal great circle of each range sphere is shown, and the
overlays are lifted above the terrain.

| File | Shows |
|---|---|
| `vis_B_12345_FORM_PAIR_001_{supporters,ranges,coverage_reservations,all}.png` | facing pair: supporter links, range boundaries, approach coverage + axis/approach corridors |
| `vis_C_12345_FORM_CLUSTER_001_*.png` | three-member cluster, the same four views |
| `vis_C_12345_FORM_CLUSTER_002_*.png` | four-member cluster (opposite members drawn as dotted out-of-band candidates, BeyondMaximum) |
| `vis_D_12345_*`, `vis_D_<seed>_*_all.png` | combined mode, each seed |
| `vis_overview_D_<seed>.png` | whole map with formation outlines and labels |

## Limitations

* **The `Handles` rendering path itself was not exercised in batch mode**, because batch mode has no
  Scene-view GUI. The tests verify the command list it draws and the screenshots render that same list,
  but the on-screen look in the Scene view (line thickness, label sizes, overlap at different zoom
  levels) still needs your visual check in production.
* Strips and approaches are drawn at the formation's base-centre height, with depth testing off so they
  stay visible. On sloped ground they can appear to float slightly above or below the terrain.
* Data is available in Play mode only, because formation records are not serialized and edit mode does
  not generate bases.
* Labels can still overlap when the camera is far out with detail forced on. Use the detail distance or
  *Only the selected formation*.
