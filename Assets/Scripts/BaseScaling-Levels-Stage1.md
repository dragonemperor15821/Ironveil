# IRONVEIL — Stage 1: Base Scaling & Level Foundation

Status: enabled in **MainBuild** (gameplay scene) and **World Explorer**, both at 6.5x via explicit
scene-specific BaseSpawner values. The script defaults remain neutral (scale 1, checks and levels off),
so any scene that does not set these fields keeps the previous behaviour.

## BaseSpawner settings (new)

| Field | Script default | MainBuild & Explorer scenes | Purpose |
|---|---|---|---|
| `baseSpawnScale` | 1 | 6.5 | Spawn-time uniform scale of every main base and its hangar. Prefab assets are not modified. Measured footprints, hangar offsets and base–hangar gaps scale with it. Terrain, river and railway do not. |
| `scaleTerrainTolerancesWithBase` | true | true | Footprint height-variation tolerance and naval draught scale with the base (same relative flatness rule). No effect at scale 1. |
| `footprintAwareSpacing` | false | true | Rejects candidates whose transformed footprint rectangles (main base + hangar) come closer than the clearance to any placed footprint, and measures land river/rail clearance from the footprint edge. Existing pivot-spacing rules stay in force. |
| `landFootprintClearance` | 4 | 4 | Edge-to-edge gap demanded by land bases. |
| `routeFootprintClearance` | 2 | 2 | Edge-to-edge gap demanded by naval/rail bases and hangars. A pair uses the larger of the two parties' clearances. |
| `routeCorridorFootprintClearance` | 0.5 | 0.5 | Gap between a route main-base disc and any other river/railway corridor. |
| `assignBaseLevels` | false | true | Deterministic L1–L6 level per base; adds `BaseLevelInfo`. |
| `levelTargetCounts` | 59,58,58,59,58,58 | same | Target count per level (scaled by largest remainder if the total is not 350). |
| `levelVisualSet` | none | none | Optional `BaseLevelVisualSet` asset: per (type, level) replacement prefab. |

## Level system

* `BaseLevel` enum L1..L6; `GeneratedBaseRecord` (baseId, baseType, level, position, yaw).
* `BaseLevelAssignment.Assign(seed, ids, counts)`: IDs sorted ordinally, a pool with the exact
  per-level counts is shuffled with a private SplitMix64 stream salted with
  `IRONVEIL.BaseLevels.v1`. It never reads or advances `UnityEngine.Random`.
* Levels are assigned **before** placement from stable base IDs (`BASE_TANK_001` …), so a future
  per-level model (via `BaseLevelVisualSet`) is the model whose footprint placement measures.
* Data only: `BaseLevelInfo` on each main base; `BaseSpawner.GeneratedBaseRecords` for the last run.
  No combat behaviour.

## Future Flak specification — NOT IMPLEMENTED

Values are **Strategy Combat (SC) reference units**. The SC-to-Unity conversion factor has
**not** been established; do not assume 1 SC = 1 Unity unit. Do not implement range rings or
targeting until the conversion is calibrated.

| Level | Minimum range (SC) | Maximum range (SC) |
|---|---:|---:|
| L1 | 11 | ~12 |
| L2 | 11 | 15 |
| L3 | 11 | 18 |
| L4 | 11 | 20 |
| L5 | 11 | 23 |
| L6 | 11 | 29 |

Confirmed design decisions:
* Range origin: base centre.
* Distance: full 3D distance.
* Two or more bases may fire on the same troop.
* Minimum range is 11 SC for every level; maximum range increases with level.
