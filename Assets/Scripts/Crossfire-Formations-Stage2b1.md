# IRONVEIL — Stage 2b.1: Facing-Pair Corridor Reservation

Status: implemented, **formations still OFF by default** (`BaseSpawner ▸ generateStrategicFormations`,
script default and MainBuild scene). With formations off, no corridor exists and every seed reproduces
the existing layout bit for bit (seed 12345 = `e71caf6c…`). There are no clusters, combat or troop AI.

Builds on `Crossfire-Formations-Stage2b.md`.

## What is reserved

Each placed facing pair reserves up to three oriented strips on XZ (`FormationCorridorSegment`, built by
`FacingPairFormations.BuildCorridor` from the placed members):

| strip | from | to | width |
|---|---|---|---|
| `axis` | A's front attack position | B's front attack position | `corridorWidth` |
| `approach A` | A's front attack position | along A's own (jittered) forward for `MaxExtraApproach` (10), clipped at the front-to-front distance | `corridorWidth` |
| `approach B` | same for B | | `corridorWidth` |

The strips begin at the front attack positions (3.3 ahead of each centre). Nothing behind or beside a
member is reserved, and nothing outside the space between the two fronts is reserved. Because the members
face each other, both frontal attack approaches lie in that space. With yaw jitter (≤ 4°), an approach
strip drifts at most ≈ 0.7 units from the axis over its 10 units, so the union of the strips is barely
wider than the axis strip.

## Enforcement (only when formations are ON)

* **Ordinary land bases**: `TryEvaluateLandPose` rejects any footprint that overlaps a reserved strip
  ("footprint inside reserved formation corridor"). This runs after the existing checks. Rejections
  restore UnityEngine.Random exactly like the other post-yaw rejections.
* **Naval / rail bases**: the base footprint and the hangar footprint are both checked
  (`TryFindRiverPlacement` / `TryFindRailPlacement`).
* **Other formations**: a later pair's members are rejected by the same land check if they fall in an
  earlier corridor. A candidate pair is also rejected if its own corridor contains an earlier member's
  footprint ("corridor blocked by another formation member") or overlaps an earlier corridor
  ("corridor crosses another formation corridor").
* A member may touch **its own** pair's corridor (identified by base ID). This also keeps
  `ProbeLandPose` replays of members exact.
* The test is an exact oriented-rectangle overlap (`FootprintsOverlapXZ`) against the measured footprint
  rectangles already used for spacing. Corridor enforcement needs `footprintAwareSpacing`, which is on in
  both scenes. A warning is logged if it is off.

Pair selection, draw order, spacing, jitter and validation are unchanged. A rejected candidate simply
moves to the next attempt of the same private stream. The pair centre is still uniform over the whole
battlefield. Nothing groups pairs together, and the corridor-vs-corridor rule keeps them apart.

## Settings — `BaseSpawner ▸ facingPairSettings` (new fields)

| field | default | meaning |
|---|---|---|
| `reserveCorridors` | true | master toggle (only read when formations are ON) |
| `corridorWidth` | **8 (PLACEHOLDER)** | full strip width, Unity units |
| `reserveFrontalApproaches` | true | add the two jittered approach strips |

**`corridorWidth` = 8 is a placeholder, not a troop-clearance value.** It is about one 6.5× base
diameter (≈ 6.2) plus margin, and equals two land footprint clearances (2 × 4, the existing gap between
ordinary bases). Replace it once troop dimensions and formation widths are known. With
`reserveCorridors = false`, ON reproduces the Stage 2b layout bit for bit.

## Editor-readable output

* The formation report has a `reserved corridor:` line per pair (kind, length × width, heading). Its
  footer gives the number of strips and the number of ordinary placement attempts rejected by corridors
  (`LastCorridorRejections`).
* `FacingPairRecord.corridor` holds the strips. `FormationMemberInfo` shows them in the inspector and
  draws them as yellow wire boxes when selected.

## API note

`ProbeLandPose(type, xz, yaw, out result, level, requestIndex = -1)`: the new optional `requestIndex`
exempts that base's own pair corridor. The default treats every corridor as foreign, as for a new base.
Existing calls are unchanged.

## Test results (scratch copy, seeds 12345 / 777 / 424242 / 1000)

* OFF: bit-identical to pre-2b.1 for all four seeds (12345 = `e71caf6c…`). Terrain `b31cdae0…` is unchanged.
* ON with `reserveCorridors = false`: bit-identical to the Stage 2b ON layouts (`0ba77ba1`, `8aa928f8`,
  `7eafc748`, `e65de248`).
* Stage 2b without reservation had 3 corridor intrusions: 12345 FORM_PAIR_001 ← Tank_098; 1000
  FORM_PAIR_006 ← Tank_086 and FORM_PAIR_007 ← Air_009. With 2b.1 there are 0.
* ON fingerprints: 12345 `e0540206`, 777 `8aa928f8` (unchanged), 424242 `7eafc748` (unchanged),
  1000 `781933fa`. Pair members and poses are identical to Stage 2b in every seed. Only the intruding
  ordinary bases moved (1 and 3 rejected attempts).
* 7/7/7/8 pairs. 350 bases, exact type and level distributions, 6.5×, 0 footprint overlaps,
  70/70 route placements validated. BaseSpawnerValidationRunner: 81 PASS / 0 FAIL.
