# IRONVEIL — Stage 2c: Inward-Facing Strategic Clusters

Status: implemented and tested in a scratch copy. **Both formation switches are OFF** in code, in
MainBuild and in World Explorer (the scene files were not edited). With both off, seed 12345 is still
`e71caf6c…`. There is no combat, Flak firing, targeting or troop AI.
Builds on `Crossfire-Formations-Stage2a.md`, `-Stage2b.md` and `-Stage2b1.md`.

## Files

| File | Change |
|---|---|
| `ClusterFormations.cs` | **new**: `ClusterSettings`, cluster records, selection, radius window, crossfire evaluation, approach support profile, approach strips, report |
| `BaseSpawner.cs` | `generateStrategicClusters` switch and `clusterSettings`; cluster placement after the pairs; reserved strips now carry an owner list instead of exactly two IDs (pair behaviour unchanged) |
| `FormationMemberInfo.cs` | `InitializeCluster`: members, supporters at the front, per-supporter distance and band, approach profile, strips |
| `Crossfire-Formations-Stage2c.md` | this report |

Unity creates `ClusterFormations.cs.meta` and the `.md.meta` the next time the project is opened.

## Configuration (`BaseSpawner`)

| Field | Default | Notes |
|---|---|---|
| `generateStrategicFormations` | **false** | facing pairs (unchanged) |
| `generateStrategicClusters` | **false** | new, independent of the pair switch |
| `clusterSettings.minClusters` / `maxClusters` | 2 / 3 | deterministic per seed |
| `minMembers` / `maxMembers` | 3 / 4 | deterministic per cluster |
| `minSupportersPerFront` | 2 | hard requirement and never relaxed automatically |
| `radiusEdgeMargin` | 0.3 | sample this far inside the analytic radius window |
| `maxAngleJitterDegrees` / `maxRadialJitter` / `maxYawJitterDegrees` | 4 / 0.4 / 3 | controlled variation |
| `facingToleranceDegrees` | 1.5 | after terrain alignment |
| `attemptsPerCluster` / `maxMemberSubstitutions` | 6000 / 3 | |
| `reserveApproaches` | true | separate from the pair corridor settings |
| `approachWidth` | **8 (PLACEHOLDER)** | not a troop-clearance value |

The range table (L1 12–16 … L6 22–26 SC), S = 1.0, the front attack position (3.3) and the 10-unit
approach are unchanged and still configured only where they were before.

## Algorithm

1. **Order of operations.** Pairs first, when that switch is on, exactly as in Stage 2b.1. Then clusters.
   Then every other base, by the unchanged rules. UnityEngine.Random is saved and restored around the
   formation steps.
2. **Selection** (`ClusterFormations.SelectPool`, private stream `IRONVEIL.Clusters.Select.v1`). The
   eligible bases are land Heli/Air at L5/L6 with a defensive front, excluding facing-pair members. Tank,
   naval, rail, every naval or rail combination and L1–L4 are excluded. The stream draws the cluster
   count (2–3), then one member count (3–4) per cluster, then shuffles the ID-sorted pool. Duplicate IDs
   collapse and input order never matters. Each base is used at most once.
3. **Radius window.** Members facing the centre exactly on a flat plane, n = 3 or 4, adjacent angle 360/n:
   the distance from a member to its neighbour's front is `d(r) = sqrt(r² + (r−3.3)² − 2r(r−3.3)cos(360/n))`.
   The window is where d lies inside the members' shared band [max min, min max]. Results:
   3 × L5 13.16–15.47, mixed L5/L6 14.32–15.47, 3 × L6 14.32–16.63; 4 members mixed 17.12–18.54,
   4 × L6 17.12–19.96.
4. **Pose sampling** (private stream `IRONVEIL.Clusters.Place.v1`, fixed draw order). Each attempt draws
   a radius inside the window (shrunk by the margin), a centre anywhere on the battlefield and a
   rotation. Each member then draws an angle jitter, a radial jitter and a yaw jitter, and faces the
   centre plus its yaw jitter. There is no catalogue of layouts.
5. **Validation.** Every condition must hold; otherwise the next attempt runs.
   * Each member passes the real land checks against everything placed so far plus the members before it:
     bounds, river and rail clearance, flatness, slope, pivot spacing 10, footprint clearance 4, route
     corridors, and reserved pair corridors and cluster strips.
   * Facing after terrain alignment: within 1.5° of the intended heading, and within yaw jitter + 1.5°
     of the centre.
   * **Crossfire:** for every member's front attack position (3.3 ahead of its centre), every OTHER member is
     tested with the full 3D distance against its own band, min and max inclusive. At least 2 supporters
     are needed at every front.
   * **Approach strips:** each member reserves one 10 × 8 strip from its front attack position along its
     forward, and nothing behind or beside it. A strip may not contain any placed footprint (pair members,
     earlier cluster members) or another member of the same cluster, and may not overlap a pair corridor
     or another cluster's strip. Strips of the same cluster do meet in the shared centre, by design.
6. **Commit.** Members are instantiated like ordinary land bases and their footprints and strips are
   reserved. Afterwards, ordinary land bases, naval and rail bases and hangars are all kept out of the
   strips by the Stage 2b.1 mechanism. If a cluster fails 6000 attempts, up to 3 fresh member sets are
   tried. After that the cluster is reported as unfilled and its members fall back to ordinary placement.

## Results (scratch copy, seeds 12345 / 777 / 424242 / 1000)

Harness `S2cHarness`: **all checks PASS, 0 failures.**

### Switch configurations

| Configuration | Result |
|---|---|
| A — both OFF | all 4 seeds bit-identical to the pre-2c layout; 12345 = `e71caf6c…`; no plan, report or strips |
| B — pairs only | all 4 seeds bit-identical to the Stage 2b.1 layouts **and** formation reports (`e0540206`, `8aa928f8`, `7eafc748`, `781933fa`) |
| C — clusters only | 2 / 2 / 3 / 3 clusters, every requested cluster filled, no pairs |
| D — both | the same 7 / 7 / 7 / 8 pairs as B (identical members and exact poses of every pair-member object, identical pair report section); 2 / 2 / 3 / 3 clusters from the remaining bases; no reuse |

### Clusters per seed

| Mode | Seed | Cluster | Members (level) | Radius (window) | Supporters per front | Mean approach covered: ≥1 / ≥2 / outside every band (of 10) | Attempts |
|---|---|---|---|---|---|---|---|
| C | 12345 | FORM_CLUSTER_001 | BASE_AIR_019 (L5), BASE_HELI_027 (L6), BASE_AIR_053 (L5) | 15.09 (14.32-15.47) | 2/2/2 | 4.17 / 2.22 / 5.83 | 1 |
| C | 12345 | FORM_CLUSTER_002 | BASE_AIR_038 (L6), BASE_HELI_007 (L6), BASE_HELI_087 (L5), BASE_HELI_054 (L6) | 17.79 (17.12-18.54) | 2/2/2/2 | 6.77 / 0.62 / 3.23 | 2 |
| C | 777 | FORM_CLUSTER_001 | BASE_HELI_001 (L5), BASE_HELI_029 (L5), BASE_HELI_033 (L5) | 15.00 (13.16-15.47) | 2/2/2 | 3.53 / 3.08 / 6.47 | 5 |
| C | 777 | FORM_CLUSTER_002 | BASE_HELI_026 (L6), BASE_HELI_057 (L5), BASE_AIR_079 (L6) | 15.08 (14.32-15.47) | 2/2/2 | 3.17 / 1.05 / 6.83 | 5 |
| C | 424242 | FORM_CLUSTER_001 | BASE_AIR_007 (L6), BASE_HELI_026 (L5), BASE_HELI_075 (L6) | 15.13 (14.32-15.47) | 2/2/2 | 3.64 / 1.07 / 6.36 | 1 |
| C | 424242 | FORM_CLUSTER_002 | BASE_AIR_035 (L6), BASE_AIR_074 (L6), BASE_AIR_037 (L6), BASE_HELI_043 (L5) | 17.61 (17.12-18.54) | 2/2/2/2 | 6.25 / 1.74 / 3.75 | 2 |
| C | 424242 | FORM_CLUSTER_003 | BASE_HELI_027 (L6), BASE_AIR_055 (L6), BASE_HELI_014 (L5) | 14.85 (14.32-15.47) | 2/2/2 | 3.28 / 1.26 / 6.72 | 1 |
| C | 1000 | FORM_CLUSTER_001 | BASE_HELI_043 (L6), BASE_HELI_001 (L5), BASE_AIR_062 (L6) | 14.77 (14.32-15.47) | 2/2/2 | 2.64 / 0.86 / 7.36 | 1 |
| C | 1000 | FORM_CLUSTER_002 | BASE_HELI_070 (L5), BASE_HELI_015 (L6), BASE_HELI_082 (L6) | 15.08 (14.32-15.47) | 2/2/2 | 2.96 / 1.66 / 7.04 | 2 |
| C | 1000 | FORM_CLUSTER_003 | BASE_HELI_011 (L5), BASE_HELI_087 (L6), BASE_HELI_008 (L5) | 14.82 (14.32-15.47) | 2/2/2 | 3.44 / 1.62 / 6.56 | 1 |
| D | 12345 | FORM_CLUSTER_001 | BASE_HELI_018 (L5), BASE_HELI_012 (L5), BASE_AIR_037 (L5) | 14.91 (13.16-15.47) | 2/2/2 | 3.77 / 3.56 / 6.23 | 1 |
| D | 12345 | FORM_CLUSTER_002 | BASE_HELI_028 (L5), BASE_AIR_071 (L5), BASE_AIR_023 (L6), BASE_HELI_054 (L6) | 17.79 (17.12-18.54) | 2/2/2/2 | 7.72 / 1.02 / 2.28 | 2 |
| D | 777 | FORM_CLUSTER_001 | BASE_AIR_014 (L6), BASE_HELI_038 (L6), BASE_HELI_022 (L5) | 15.12 (14.32-15.47) | 2/2/2 | 2.55 / 1.23 / 7.45 | 5 |
| D | 777 | FORM_CLUSTER_002 | BASE_AIR_038 (L6), BASE_AIR_019 (L6), BASE_HELI_086 (L6) | 15.69 (14.32-16.63) | 2/2/2 | 3.80 / 2.80 / 6.20 | 2 |
| D | 424242 | FORM_CLUSTER_001 | BASE_HELI_059 (L6), BASE_HELI_048 (L5), BASE_HELI_049 (L5) | 15.13 (14.32-15.47) | 2/2/2 | 4.43 / 1.92 / 5.57 | 1 |
| D | 424242 | FORM_CLUSTER_002 | BASE_AIR_007 (L6), BASE_HELI_084 (L6), BASE_HELI_077 (L6), BASE_AIR_005 (L6) | 17.93 (17.12-19.96) | 2/2/2/2 | 5.92 / 1.58 / 4.08 | 2 |
| D | 424242 | FORM_CLUSTER_003 | BASE_AIR_046 (L6), BASE_AIR_031 (L5), BASE_HELI_082 (L5) | 14.85 (14.32-15.47) | 2/2/2 | 4.11 / 2.07 / 5.89 | 1 |
| D | 1000 | FORM_CLUSTER_001 | BASE_AIR_021 (L6), BASE_HELI_042 (L5), BASE_HELI_090 (L6) | 14.77 (14.32-15.47) | 2/2/2 | 2.64 / 0.86 / 7.36 | 1 |
| D | 1000 | FORM_CLUSTER_002 | BASE_HELI_081 (L5), BASE_HELI_044 (L5), BASE_AIR_035 (L5) | 14.88 (13.16-15.47) | 2/2/2 | 3.96 / 3.14 / 6.04 | 2 |
| D | 1000 | FORM_CLUSTER_003 | BASE_HELI_084 (L5), BASE_HELI_015 (L6), BASE_AIR_002 (L5) | 14.82 (14.32-15.47) | 2/2/2 | 3.44 / 1.62 / 6.56 | 1 |

Every front in every cluster has exactly **2** supporters. None has 3, as explained below.

### Placement and reservation (every seed, modes C and D)

* No footprint (ordinary land, naval or rail base or hangar, pair member, other cluster member, the
  cluster's own other members) overlaps any pair corridor or cluster approach strip. The checks used up
  to 420 footprints × 33 strips with an independent separating-axis test.
* No strip of one formation overlaps a strip of another formation.
* 0 physical footprint overlaps. Below-clearance pairs equal OFF (42, the existing route-base pairs).
  No cluster member pivot is within 10 of another base.
* Every cluster member re-passes `ProbeLandPose` with its exact pose.
* 350 bases; types 110/90/80/25/17/11/7/6/4; levels 59/58/58/59/58/58; every base keeps its level;
  6.5× on every main base.
* 70/70 naval and rail placements validated; 42/42 naval and 28/28 rail hangars; 0 orphan hangars.
* Terrain, river and railway hash `b31cdae0…` unchanged.
* Determinism: repeating each seed gives an identical layout and report. Different seeds give different
  members and arrangements.
* Recorded distances and band statuses match values recomputed from the live transforms (1e-3). The
  recorded approach profiles match independent sampling at 0.01-unit steps (0 mismatches).

### Targeted tests

| Test | Result |
|---|---|
| 3-member geometry | regular triangle r 14.5 (3 × L5): every front 2 supporters at 22.318 ∈ [20, 24] |
| 4-member geometry | square r 17.8 (L5/L6/L5/L6): adjacents 22.958 in band; opposite 32.300 BeyondMaximum |
| Minimum-range failure | triangle r 12: 18.003 < 20 → InsideMinimum, 0 counted |
| Maximum-range failure | triangle r 16.5: 25.774 > 24 → BeyondMaximum, 0 counted |
| Full 3D distance | flat 23.700 (in band); supporter raised 5 → 24.222 > 24, not counted |
| Multiple supporters | both others engage the same front; a base never counts as its own supporter |
| Approach coverage | triangle r 15 (L5): each supporter covers [0, 3.993]; profile [0, 3.993] ×2, [3.993, 10] ×0 |
| Support profile | [0,4] + [2,6] → [0,2]×1 [2,4]×2 [4,6]×1 [6,10]×0 |
| Physical overlap rejection | Heli probed on an ordinary base → "too close to another base" |
| Pair-corridor intrusion | footprint in FORM_PAIR_001's corridor → rejected; cluster strip crossing it → "corridor crosses another formation corridor"; Heli probe there → rejected |
| Duplicate members | duplicate IDs collapse; pair members, Tank, route and L4 are excluded |
| Determinism and variation | same seed gives the same selection; 30 seeds give 30 distinct selections and 10 count patterns |
| Radius windows | match an independent calculation (13.16–15.47, 17.12–18.54) |

### Regression suites

| Suite | Result |
|---|---|
| Stage 2b.1 harness (also covers the Stage 2b pair rules and OFF identity) | 96 / 96 PASS |
| BaseSpawnerValidationRunner (nine types, Stage 1 placement rules) | 81 PASS / 0 FAIL / 14 not testable (no colliders, visual review) |
| Stage 2a harness | 42 PASS / 2 FAIL (see below) |

The 2 Stage 2a failures assert the **Stage 2a contract that formations ON is informational only**
(layout unchanged, a 40-member plan). Stage 2b replaced that contract. The same harness on the backed-up
pre-2c code gives the identical 42 / 2 result, so these failures predate Stage 2c. All Stage 2a
probe-replay and placement-equivalence checks pass.

### Rejected candidates

All 20 cluster placements were found in 1–5 attempts with no member substitution. Rejections, summed
over all C and D runs:

| Reason | Count |
|---|---|
| member: outside world bounds | 6 |
| front supported by 1 < 2 (BeyondMaximum) | 5 |
| member: footprint too uneven | 4 |
| front supported by 1 < 2 (InsideMinimum) | 2 |
| member: footprint too close to river | 1 |

No candidate was rejected for its approach strips in these seeds. That rule is still tested directly
(see the targeted tests).

## Limitations and unmet targets

* **3 supporters per front in 4-member clusters is not achievable** with genuinely inward-facing geometry
  under the approved ranges. With 4 members on a common circle facing the centre, the adjacent members
  engage the fronts for radius 17.1–18.5 (mixed), but the opposite member only for 12.7–13.6. The windows
  never overlap. An exhaustive scan (radius 10–30, all angle combinations in 3° steps, all-L5 as the
  widest band) finds at most 2 supporters guaranteed at every front. The requirement of ≥ 2 is met
  everywhere. Ranges, S and the attack position were not changed to chase 3.
* **Approach coverage is partial.** Across all 128 in-band supporter links, a supporter engages a stretch
  that always starts at the front position and runs 0.0–5.4 units of the 10-unit approach. A value of 0
  means the supporter covers the front position itself and almost nothing beyond it. After that, troops
  advancing toward the centre come inside the minimum ring of the members across from them. Per member, on average, 2.6–7.7 of the 10 units are covered by at least one supporter, 0.6–3.6 by
  at least two, and 2.3–7.4 are outside every engagement band. Four-member clusters cover more of the
  approach by at least one supporter, but less by two. No approach is fully covered.
* `approachWidth` = 8 is a placeholder, like the Stage 2b.1 corridor width.
* **Cluster positions are a function of the seed.** The geometry draws come from the seed's placement
  stream, so for a given seed, combined mode usually reproduces the cluster-only centres with different
  (remaining) members. It changes only when the pairs force a resample: in 777, cluster 2 moved. This is
  deterministic and valid.
* Testing was automated in a scratch copy only. **No interactive testing was done in the production
  Unity project.**

## Visual outputs

The outputs are outside the production project, in the session scratchpad `out\c1\`:
* `overview_C_<seed>.png`, `overview_D_<seed>.png`: whole map, with pairs in magenta and clusters as
  cyan polygons with yellow members.
* `cluster_top_D_<seed>_<id>.png` and `cluster_D_<seed>_<id>.png`: every cluster in combined mode, top
  and oblique, plus the same for cluster-only seed 12345. Red = front attack positions, green = valid
  supporter lines, yellow = approach strips, magenta = nearby pair corridors, white = cluster centre.

## Appendix — full cluster reports (from the generator)

Each block lists the member IDs and levels, centre, member positions, forward and intended headings,
every supporter's 3D distance to every front with its band and status, coverage intervals along each
approach, the support-count profile, and the portion outside every band.

### Mode C (clusters only), seed 12345

```text
IRONVEIL Stage 2c - Strategic formation report (inward-facing clusters)
seed 12345 | S = 1 | front attack 3.3 | extra approach analysed 10 | min supporters per front 2 | approach strip width 8 (placeholder)
requested clusters 2 (members 3/4) | placed 2 | unfilled 0 | eligible L5/L6 land Heli/Air 56
excluded: level not eligible 114, no defensive front (Tank) 110, route base (naval/rail) 70

FORM_CLUSTER_001  3 members  centre (-312.35, 12.52, -79.74)  radius 15.09 (window 14.32-15.47)  rotation 358.0  shared band [22, 24]  attempts 1  substitutions 0
  [0] BASE_AIR_019   Air  L5  pos (-311.92, 12.49, -64.53)  r 15.21  slot 1.6  fwd 181.0  intended 181.0 (jitter -0.6)  facing err 0.00  to centre 0.6
  [1] BASE_HELI_027  Heli L6  pos (-299.21, 12.21, -87.23)  r 15.12  slot 119.7  fwd 299.1  intended 299.1 (jitter -0.6)  facing err 0.00  to centre 0.6
  [2] BASE_AIR_053   Air  L5  pos (-325.06, 12.85, -87.22)  r 14.75  slot 239.5  fwd 58.0  intended 58.0 (jitter -1.5)  facing err 0.02  to centre 1.6
  front of BASE_AIR_019 (-311.98, 12.49, -67.83): 2 supporter(s) at the front position
    BASE_HELI_027 L6: 3D 23.22 in [22, 26] InBand; approach coverage [0, 1.501] = 1.50/10
    BASE_AIR_053 L5: 3D 23.39 in [20, 24] InBand; approach coverage [0, 4.194] = 4.19/10
    approach profile (0 = front position, 10 = end): [0, 1.501] x2 [1.501, 4.194] x1 [4.194, 10] x0
    covered by >=1: 4.19/10  by >=2: 1.50/10  OUTSIDE every engagement band: 5.81/10 (partial)
  front of BASE_HELI_027 (-302.10, 12.21, -85.63): 2 supporter(s) at the front position
    BASE_AIR_019 L5: 3D 23.27 in [20, 24] InBand; approach coverage [0, 4.23] = 4.23/10
    BASE_AIR_053 L5: 3D 23.02 in [20, 24] InBand; approach coverage [0, 3.732] = 3.73/10
    approach profile (0 = front position, 10 = end): [0, 3.732] x2 [3.732, 4.23] x1 [4.23, 10] x0
    covered by >=1: 4.23/10  by >=2: 3.73/10  OUTSIDE every engagement band: 5.77/10 (partial)
  front of BASE_AIR_053 (-322.26, 12.85, -85.47): 2 supporter(s) at the front position
    BASE_AIR_019 L5: 3D 23.35 in [20, 24] InBand; approach coverage [0, 4.073] = 4.07/10
    BASE_HELI_027 L6: 3D 23.12 in [22, 26] InBand; approach coverage [0, 1.415] = 1.41/10
    approach profile (0 = front position, 10 = end): [0, 1.415] x2 [1.415, 4.073] x1 [4.073, 10] x0
    covered by >=1: 4.07/10  by >=2: 1.41/10  OUTSIDE every engagement band: 5.93/10 (partial)
  reserved approach strips: approach BASE_AIR_019 10.00x8 hdg 181.0; approach BASE_HELI_027 10.00x8 hdg 299.1; approach BASE_AIR_053 10.00x8 hdg 58.0
  rejected candidate poses: none

FORM_CLUSTER_002  4 members  centre (338.95, 11.84, -183.46)  radius 17.79 (window 17.12-18.54)  rotation 287.7  shared band [22, 24]  attempts 2  substitutions 0
  [0] BASE_AIR_038   Air  L6  pos (322.52, 11.67, -177.75)  r 17.39  slot 289.2  fwd 109.6  intended 109.6 (jitter +0.5)  facing err 0.00  to centre 0.5
  [1] BASE_HELI_007  Heli L6  pos (345.07, 11.78, -166.94)  r 17.61  slot 20.3  fwd 203.2  intended 203.2 (jitter +2.8)  facing err 0.00  to centre 2.8
  [2] BASE_HELI_087  Heli L5  pos (355.47, 11.43, -189.39)  r 17.55  slot 109.8  fwd 290.0  intended 290.0 (jitter +0.2)  facing err 0.00  to centre 0.2
  [3] BASE_HELI_054  Heli L6  pos (332.59, 12.47, -199.85)  r 17.58  slot 201.2  fwd 24.3  intended 24.1 (jitter +2.9)  facing err 0.21  to centre 3.1
  front of BASE_AIR_038 (325.63, 11.67, -178.86): 2 supporter(s) at the front position
    BASE_HELI_007 L6: 3D 22.80 in [22, 26] InBand; approach coverage [0, 1.32] = 1.32/10
    BASE_HELI_087 L5: 3D 31.64 in [20, 24] BeyondMaximum; approach coverage [7.644, 10] = 2.36/10
    BASE_HELI_054 L6: 3D 22.13 in [22, 26] InBand; approach coverage [0, 0.208] = 0.21/10
    approach profile (0 = front position, 10 = end): [0, 0.208] x2 [0.208, 1.32] x1 [1.32, 7.644] x0 [7.644, 10] x1
    covered by >=1: 3.68/10  by >=2: 0.21/10  OUTSIDE every engagement band: 6.32/10 (partial)
  front of BASE_HELI_007 (343.77, 11.78, -169.97): 2 supporter(s) at the front position
    BASE_AIR_038 L6: 3D 22.63 in [22, 26] InBand; approach coverage [0, 0.935] = 0.94/10
    BASE_HELI_087 L5: 3D 22.67 in [20, 24] InBand; approach coverage [0, 5.4] = 5.40/10
    BASE_HELI_054 L6: 3D 31.91 in [22, 26] BeyondMaximum; approach coverage [5.915, 9.924] = 4.01/10
    approach profile (0 = front position, 10 = end): [0, 0.935] x2 [0.935, 5.4] x1 [5.4, 5.915] x0 [5.915, 9.924] x1 [9.924, 10] x0
    covered by >=1: 9.41/10  by >=2: 0.94/10  OUTSIDE every engagement band: 0.59/10 (partial)
  front of BASE_HELI_087 (352.37, 11.43, -188.26): 2 supporter(s) at the front position
    BASE_AIR_038 L6: 3D 31.64 in [22, 26] BeyondMaximum; approach coverage [5.645, 9.645] = 4.00/10
    BASE_HELI_007 L6: 3D 22.54 in [22, 26] InBand; approach coverage [0, 0.877] = 0.88/10
    BASE_HELI_054 L6: 3D 22.94 in [22, 26] InBand; approach coverage [0, 1.527] = 1.53/10
    approach profile (0 = front position, 10 = end): [0, 0.877] x2 [0.877, 1.527] x1 [1.527, 5.645] x0 [5.645, 9.645] x1 [9.645, 10] x0
    covered by >=1: 5.53/10  by >=2: 0.88/10  OUTSIDE every engagement band: 4.47/10 (partial)
  front of BASE_HELI_054 (333.95, 12.47, -196.84): 2 supporter(s) at the front position
    BASE_AIR_038 L6: 3D 22.26 in [22, 26] InBand; approach coverage [0, 0.47] = 0.47/10
    BASE_HELI_007 L6: 3D 31.91 in [22, 26] BeyondMaximum; approach coverage [5.928, 9.947] = 4.02/10
    BASE_HELI_087 L5: 3D 22.80 in [20, 24] InBand; approach coverage [0, 4.453] = 4.45/10
    approach profile (0 = front position, 10 = end): [0, 0.47] x2 [0.47, 4.453] x1 [4.453, 5.928] x0 [5.928, 9.947] x1 [9.947, 10] x0
    covered by >=1: 8.47/10  by >=2: 0.47/10  OUTSIDE every engagement band: 1.53/10 (partial)
  reserved approach strips: approach BASE_AIR_038 10.00x8 hdg 109.6; approach BASE_HELI_007 10.00x8 hdg 203.2; approach BASE_HELI_087 10.00x8 hdg 290.0; approach BASE_HELI_054 10.00x8 hdg 24.3
  rejected candidate poses: member: outside world bounds x1
reserved corridor strips 7 | ordinary placement attempts rejected by reserved corridors 0
```

### Mode C (clusters only), seed 777

```text
IRONVEIL Stage 2c - Strategic formation report (inward-facing clusters)
seed 777 | S = 1 | front attack 3.3 | extra approach analysed 10 | min supporters per front 2 | approach strip width 8 (placeholder)
requested clusters 2 (members 3/3) | placed 2 | unfilled 0 | eligible L5/L6 land Heli/Air 52
excluded: level not eligible 118, no defensive front (Tank) 110, route base (naval/rail) 70

FORM_CLUSTER_001  3 members  centre (79.30, 9.60, -200.66)  radius 15.00 (window 13.16-15.47)  rotation 158.6  shared band [20, 24]  attempts 5  substitutions 0
  [0] BASE_HELI_001  Heli L5  pos (84.98, 9.44, -214.32)  r 14.79  slot 157.4  fwd 338.3  intended 338.3 (jitter +0.8)  facing err 0.00  to centre 0.8
  [1] BASE_HELI_029  Heli L5  pos (64.94, 9.51, -197.92)  r 14.63  slot 280.8  fwd 102.7  intended 102.7 (jitter +1.9)  facing err 0.00  to centre 1.9
  [2] BASE_HELI_033  Heli L5  pos (88.57, 9.85, -189.31)  r 14.65  slot 39.3  fwd 216.9  intended 216.9 (jitter -2.3)  facing err 0.04  to centre 2.3
  front of BASE_HELI_001 (83.76, 9.44, -211.25): 2 supporter(s) at the front position
    BASE_HELI_029 L5: 3D 23.07 in [20, 24] InBand; approach coverage [0, 3.786] = 3.79/10
    BASE_HELI_033 L5: 3D 22.47 in [20, 24] InBand; approach coverage [0, 3.072] = 3.07/10
    approach profile (0 = front position, 10 = end): [0, 3.072] x2 [3.072, 3.786] x1 [3.786, 10] x0
    covered by >=1: 3.79/10  by >=2: 3.07/10  OUTSIDE every engagement band: 6.21/10 (partial)
  front of BASE_HELI_029 (68.15, 9.51, -198.65): 2 supporter(s) at the front position
    BASE_HELI_001 L5: 3D 23.00 in [20, 24] InBand; approach coverage [0, 3.564] = 3.56/10
    BASE_HELI_033 L5: 3D 22.46 in [20, 24] InBand; approach coverage [0, 3.205] = 3.20/10
    approach profile (0 = front position, 10 = end): [0, 3.205] x2 [3.205, 3.564] x1 [3.564, 10] x0
    covered by >=1: 3.56/10  by >=2: 3.20/10  OUTSIDE every engagement band: 6.44/10 (partial)
  front of BASE_HELI_033 (86.59, 9.85, -191.95): 2 supporter(s) at the front position
    BASE_HELI_001 L5: 3D 22.43 in [20, 24] InBand; approach coverage [0, 2.97] = 2.97/10
    BASE_HELI_029 L5: 3D 22.47 in [20, 24] InBand; approach coverage [0, 3.243] = 3.24/10
    approach profile (0 = front position, 10 = end): [0, 2.97] x2 [2.97, 3.243] x1 [3.243, 10] x0
    covered by >=1: 3.24/10  by >=2: 2.97/10  OUTSIDE every engagement band: 6.76/10 (partial)
  reserved approach strips: approach BASE_HELI_001 10.00x8 hdg 338.3; approach BASE_HELI_029 10.00x8 hdg 102.7; approach BASE_HELI_033 10.00x8 hdg 216.9
  rejected candidate poses: front supported by 1 < 2 (BeyondMaximum) x1, front supported by 1 < 2 (InsideMinimum) x1, member: footprint too uneven x1, member: outside world bounds x1

FORM_CLUSTER_002  3 members  centre (-87.00, 12.64, -109.32)  radius 15.08 (window 14.32-15.47)  rotation 258.9  shared band [22, 24]  attempts 5  substitutions 0
  [0] BASE_HELI_026  Heli L6  pos (-101.39, 12.19, -112.50)  r 14.74  slot 257.5  fwd 75.9  intended 75.9 (jitter -1.7)  facing err 0.00  to centre 1.6
  [1] BASE_HELI_057  Heli L5  pos (-81.44, 13.16, -95.33)  r 15.05  slot 21.7  fwd 201.1  intended 201.1 (jitter -0.5)  facing err 0.00  to centre 0.5
  [2] BASE_AIR_079   Air  L6  pos (-76.86, 12.56, -120.31)  r 14.95  slot 137.3  fwd 316.0  intended 316.0 (jitter -1.3)  facing err 0.00  to centre 1.3
  front of BASE_HELI_026 (-98.19, 12.19, -111.69): 2 supporter(s) at the front position
    BASE_HELI_057 L5: 3D 23.43 in [20, 24] InBand; approach coverage [0, 4.1] = 4.10/10
    BASE_AIR_079 L6: 3D 23.00 in [22, 26] InBand; approach coverage [0, 1.259] = 1.26/10
    approach profile (0 = front position, 10 = end): [0, 1.259] x2 [1.259, 4.1] x1 [4.1, 10] x0
    covered by >=1: 4.10/10  by >=2: 1.26/10  OUTSIDE every engagement band: 5.90/10 (partial)
  front of BASE_HELI_057 (-82.63, 13.16, -98.41): 2 supporter(s) at the front position
    BASE_HELI_026 L6: 3D 23.48 in [22, 26] InBand; approach coverage [0, 1.767] = 1.77/10
    BASE_AIR_079 L6: 3D 22.65 in [22, 26] InBand; approach coverage [0, 0.815] = 0.81/10
    approach profile (0 = front position, 10 = end): [0, 0.815] x2 [0.815, 1.767] x1 [1.767, 10] x0
    covered by >=1: 1.77/10  by >=2: 0.81/10  OUTSIDE every engagement band: 8.23/10 (partial)
  front of BASE_AIR_079 (-79.16, 12.56, -117.94): 2 supporter(s) at the front position
    BASE_HELI_026 L6: 3D 22.89 in [22, 26] InBand; approach coverage [0, 1.064] = 1.06/10
    BASE_HELI_057 L5: 3D 22.73 in [20, 24] InBand; approach coverage [0, 3.635] = 3.64/10
    approach profile (0 = front position, 10 = end): [0, 1.064] x2 [1.064, 3.635] x1 [3.635, 10] x0
    covered by >=1: 3.64/10  by >=2: 1.06/10  OUTSIDE every engagement band: 6.36/10 (partial)
  reserved approach strips: approach BASE_HELI_026 10.00x8 hdg 75.9; approach BASE_HELI_057 10.00x8 hdg 201.1; approach BASE_AIR_079 10.00x8 hdg 316.0
  rejected candidate poses: member: footprint too uneven x2, front supported by 1 < 2 (BeyondMaximum) x1, member: footprint too close to river x1
reserved corridor strips 6 | ordinary placement attempts rejected by reserved corridors 0
```

### Mode C (clusters only), seed 424242

```text
IRONVEIL Stage 2c - Strategic formation report (inward-facing clusters)
seed 424242 | S = 1 | front attack 3.3 | extra approach analysed 10 | min supporters per front 2 | approach strip width 8 (placeholder)
requested clusters 3 (members 3/4/3) | placed 3 | unfilled 0 | eligible L5/L6 land Heli/Air 60
excluded: level not eligible 110, no defensive front (Tank) 110, route base (naval/rail) 70

FORM_CLUSTER_001  3 members  centre (199.40, 12.78, -130.80)  radius 15.13 (window 14.32-15.47)  rotation 356.2  shared band [22, 24]  attempts 1  substitutions 0
  [0] BASE_AIR_007   Air  L6  pos (198.16, 12.37, -115.83)  r 15.02  slot 355.3  fwd 176.2  intended 176.2 (jitter +0.9)  facing err 0.00  to centre 0.9
  [1] BASE_HELI_026  Heli L5  pos (213.36, 12.56, -136.80)  r 15.20  slot 113.3  fwd 292.1  intended 292.1 (jitter -1.2)  facing err 0.00  to centre 1.2
  [2] BASE_HELI_075  Heli L6  pos (186.55, 13.41, -138.35)  r 14.91  slot 239.6  fwd 62.5  intended 62.5 (jitter +2.9)  facing err 0.07  to centre 2.9
  front of BASE_AIR_007 (198.38, 12.37, -119.12): 2 supporter(s) at the front position
    BASE_HELI_026 L5: 3D 23.18 in [20, 24] InBand; approach coverage [0, 4.141] = 4.14/10
    BASE_HELI_075 L6: 3D 22.60 in [22, 26] InBand; approach coverage [0, 0.741] = 0.74/10
    approach profile (0 = front position, 10 = end): [0, 0.741] x2 [0.741, 4.141] x1 [4.141, 10] x0
    covered by >=1: 4.14/10  by >=2: 0.74/10  OUTSIDE every engagement band: 5.86/10 (partial)
  front of BASE_HELI_026 (210.31, 12.56, -135.56): 2 supporter(s) at the front position
    BASE_AIR_007 L6: 3D 23.17 in [22, 26] InBand; approach coverage [0, 1.475] = 1.48/10
    BASE_HELI_075 L6: 3D 23.94 in [22, 26] InBand; approach coverage [0, 2.241] = 2.24/10
    approach profile (0 = front position, 10 = end): [0, 1.475] x2 [1.475, 2.241] x1 [2.241, 10] x0
    covered by >=1: 2.24/10  by >=2: 1.48/10  OUTSIDE every engagement band: 7.76/10 (partial)
  front of BASE_HELI_075 (189.47, 13.41, -136.83): 2 supporter(s) at the front position
    BASE_AIR_007 L6: 3D 22.75 in [22, 26] InBand; approach coverage [0, 0.988] = 0.99/10
    BASE_HELI_026 L5: 3D 23.90 in [20, 24] InBand; approach coverage [0, 4.525] = 4.53/10
    approach profile (0 = front position, 10 = end): [0, 0.988] x2 [0.988, 4.525] x1 [4.525, 10] x0
    covered by >=1: 4.53/10  by >=2: 0.99/10  OUTSIDE every engagement band: 5.47/10 (partial)
  reserved approach strips: approach BASE_AIR_007 10.00x8 hdg 176.2; approach BASE_HELI_026 10.00x8 hdg 292.1; approach BASE_HELI_075 10.00x8 hdg 62.5
  rejected candidate poses: none

FORM_CLUSTER_002  4 members  centre (105.41, 11.75, -73.00)  radius 17.61 (window 17.12-18.54)  rotation 36.8  shared band [22, 24]  attempts 2  substitutions 0
  [0] BASE_AIR_035   Air  L6  pos (115.04, 13.31, -58.35)  r 17.53  slot 33.3  fwd 212.5  intended 212.9 (jitter -0.4)  facing err 0.40  to centre 0.8
  [1] BASE_AIR_074   Air  L6  pos (119.47, 13.08, -83.62)  r 17.62  slot 127.1  fwd 305.5  intended 305.4 (jitter -1.6)  facing err 0.03  to centre 1.6
  [2] BASE_AIR_037   Air  L6  pos (93.93, 10.44, -86.80)  r 17.95  slot 219.7  fwd 39.8  intended 39.9 (jitter +0.1)  facing err 0.10  to centre 0.0
  [3] BASE_HELI_043  Heli L5  pos (91.46, 10.18, -62.31)  r 17.58  slot 307.5  fwd 127.0  intended 126.9 (jitter -0.5)  facing err 0.06  to centre 0.4
  front of BASE_AIR_035 (113.27, 13.31, -61.13): 2 supporter(s) at the front position
    BASE_AIR_074 L6: 3D 23.34 in [22, 26] InBand; approach coverage [0, 2.072] = 2.07/10
    BASE_AIR_037 L6: 3D 32.26 in [22, 26] BeyondMaximum; approach coverage [6.319, 10] = 3.68/10
    BASE_HELI_043 L5: 3D 22.07 in [20, 24] InBand; approach coverage [0, 4.077] = 4.08/10
    approach profile (0 = front position, 10 = end): [0, 2.072] x2 [2.072, 4.077] x1 [4.077, 6.319] x0 [6.319, 10] x1
    covered by >=1: 7.76/10  by >=2: 2.07/10  OUTSIDE every engagement band: 2.24/10 (partial)
  front of BASE_AIR_074 (116.79, 13.08, -81.71): 2 supporter(s) at the front position
    BASE_AIR_035 L6: 3D 23.43 in [22, 26] InBand; approach coverage [0, 2.352] = 2.35/10
    BASE_AIR_037 L6: 3D 23.56 in [22, 26] InBand; approach coverage [0, 2.461] = 2.46/10
    BASE_HELI_043 L5: 3D 32.04 in [20, 24] BeyondMaximum; approach coverage [8.087, 10] = 1.91/10
    approach profile (0 = front position, 10 = end): [0, 2.352] x2 [2.352, 2.461] x1 [2.461, 8.087] x0 [8.087, 10] x1
    covered by >=1: 4.37/10  by >=2: 2.35/10  OUTSIDE every engagement band: 5.63/10 (partial)
  front of BASE_AIR_037 (96.05, 10.44, -84.26): 2 supporter(s) at the front position
    BASE_AIR_035 L6: 3D 32.26 in [22, 26] BeyondMaximum; approach coverage [6.306, 10] = 3.69/10
    BASE_AIR_074 L6: 3D 23.58 in [22, 26] InBand; approach coverage [0, 2.542] = 2.54/10
    BASE_HELI_043 L5: 3D 22.43 in [20, 24] InBand; approach coverage [0, 4.384] = 4.38/10
    approach profile (0 = front position, 10 = end): [0, 2.542] x2 [2.542, 4.384] x1 [4.384, 6.306] x0 [6.306, 10] x1
    covered by >=1: 8.08/10  by >=2: 2.54/10  OUTSIDE every engagement band: 1.92/10 (partial)
  front of BASE_HELI_043 (94.09, 10.18, -64.30): 2 supporter(s) at the front position
    BASE_AIR_035 L6: 3D 22.00 in [22, 26] InBand; approach coverage [0, 0.003] = 0.00/10
    BASE_AIR_074 L6: 3D 32.03 in [22, 26] BeyondMaximum; approach coverage [6.065, 10] = 3.94/10
    BASE_AIR_037 L6: 3D 22.51 in [22, 26] InBand; approach coverage [0, 0.865] = 0.87/10
    approach profile (0 = front position, 10 = end): [0, 0.003] x2 [0.003, 0.865] x1 [0.865, 6.065] x0 [6.065, 10] x1
    covered by >=1: 4.80/10  by >=2: 0.00/10  OUTSIDE every engagement band: 5.20/10 (partial)
  reserved approach strips: approach BASE_AIR_035 10.00x8 hdg 212.5; approach BASE_AIR_074 10.00x8 hdg 305.5; approach BASE_AIR_037 10.00x8 hdg 39.8; approach BASE_HELI_043 10.00x8 hdg 127.0
  rejected candidate poses: front supported by 1 < 2 (BeyondMaximum) x1

FORM_CLUSTER_003  3 members  centre (238.18, 9.57, -404.77)  radius 14.85 (window 14.32-15.47)  rotation 255.6  shared band [22, 24]  attempts 1  substitutions 0
  [0] BASE_HELI_027  Heli L6  pos (224.14, 9.67, -408.68)  r 14.57  slot 254.4  fwd 71.8  intended 71.8 (jitter -2.6)  facing err 0.00  to centre 2.6
  [1] BASE_AIR_055   Air  L6  pos (242.39, 9.52, -390.14)  r 15.23  slot 16.1  fwd 194.1  intended 194.1 (jitter -2.0)  facing err 0.00  to centre 2.0
  [2] BASE_HELI_014  Heli L5  pos (248.61, 9.54, -415.75)  r 15.14  slot 136.5  fwd 317.9  intended 317.9 (jitter +1.5)  facing err 0.00  to centre 1.5
  front of BASE_HELI_027 (227.28, 9.67, -407.65): 2 supporter(s) at the front position
    BASE_AIR_055 L6: 3D 23.14 in [22, 26] InBand; approach coverage [0, 1.338] = 1.34/10
    BASE_HELI_014 L5: 3D 22.81 in [20, 24] InBand; approach coverage [0, 3.802] = 3.80/10
    approach profile (0 = front position, 10 = end): [0, 1.338] x2 [1.338, 3.802] x1 [3.802, 10] x0
    covered by >=1: 3.80/10  by >=2: 1.34/10  OUTSIDE every engagement band: 6.20/10 (partial)
  front of BASE_AIR_055 (241.59, 9.52, -393.34): 2 supporter(s) at the front position
    BASE_HELI_027 L6: 3D 23.24 in [22, 26] InBand; approach coverage [0, 1.522] = 1.52/10
    BASE_HELI_014 L5: 3D 23.48 in [20, 24] InBand; approach coverage [0, 4.223] = 4.22/10
    approach profile (0 = front position, 10 = end): [0, 1.522] x2 [1.522, 4.223] x1 [4.223, 10] x0
    covered by >=1: 4.22/10  by >=2: 1.52/10  OUTSIDE every engagement band: 5.78/10 (partial)
  front of BASE_HELI_014 (246.40, 9.54, -413.30): 2 supporter(s) at the front position
    BASE_HELI_027 L6: 3D 22.73 in [22, 26] InBand; approach coverage [0, 0.91] = 0.91/10
    BASE_AIR_055 L6: 3D 23.50 in [22, 26] InBand; approach coverage [0, 1.802] = 1.80/10
    approach profile (0 = front position, 10 = end): [0, 0.91] x2 [0.91, 1.802] x1 [1.802, 10] x0
    covered by >=1: 1.80/10  by >=2: 0.91/10  OUTSIDE every engagement band: 8.20/10 (partial)
  reserved approach strips: approach BASE_HELI_027 10.00x8 hdg 71.8; approach BASE_AIR_055 10.00x8 hdg 194.1; approach BASE_HELI_014 10.00x8 hdg 317.9
  rejected candidate poses: none
reserved corridor strips 10 | ordinary placement attempts rejected by reserved corridors 0
```

### Mode C (clusters only), seed 1000

```text
IRONVEIL Stage 2c - Strategic formation report (inward-facing clusters)
seed 1000 | S = 1 | front attack 3.3 | extra approach analysed 10 | min supporters per front 2 | approach strip width 8 (placeholder)
requested clusters 3 (members 3/3/3) | placed 3 | unfilled 0 | eligible L5/L6 land Heli/Air 56
excluded: level not eligible 114, no defensive front (Tank) 110, route base (naval/rail) 70

FORM_CLUSTER_001  3 members  centre (270.76, 11.93, -7.30)  radius 14.77 (window 14.32-15.47)  rotation 14.7  shared band [22, 24]  attempts 1  substitutions 0
  [0] BASE_HELI_043  Heli L6  pos (274.68, 11.87, 6.73)  r 14.57  slot 15.6  fwd 198.5  intended 198.5 (jitter +2.9)  facing err 0.00  to centre 2.9
  [1] BASE_HELI_001  Heli L5  pos (281.98, 12.14, -17.19)  r 14.96  slot 131.4  fwd 309.3  intended 309.3 (jitter -2.1)  facing err 0.00  to centre 2.1
  [2] BASE_AIR_062   Air  L6  pos (256.73, 11.78, -11.70)  r 14.70  slot 252.6  fwd 75.5  intended 75.5 (jitter +2.9)  facing err 0.00  to centre 2.9
  front of BASE_HELI_043 (273.63, 11.87, 3.60): 2 supporter(s) at the front position
    BASE_HELI_001 L5: 3D 22.40 in [20, 24] InBand; approach coverage [0, 3.303] = 3.30/10
    BASE_AIR_062 L6: 3D 22.80 in [22, 26] InBand; approach coverage [0, 0.925] = 0.92/10
    approach profile (0 = front position, 10 = end): [0, 0.925] x2 [0.925, 3.303] x1 [3.303, 10] x0
    covered by >=1: 3.30/10  by >=2: 0.92/10  OUTSIDE every engagement band: 6.70/10 (partial)
  front of BASE_HELI_001 (279.42, 12.14, -15.10): 2 supporter(s) at the front position
    BASE_HELI_043 L6: 3D 22.34 in [22, 26] InBand; approach coverage [0, 0.433] = 0.43/10
    BASE_AIR_062 L6: 3D 22.95 in [22, 26] InBand; approach coverage [0, 1.115] = 1.11/10
    approach profile (0 = front position, 10 = end): [0, 0.433] x2 [0.433, 1.115] x1 [1.115, 10] x0
    covered by >=1: 1.11/10  by >=2: 0.43/10  OUTSIDE every engagement band: 8.89/10 (partial)
  front of BASE_AIR_062 (259.92, 11.78, -10.88): 2 supporter(s) at the front position
    BASE_HELI_043 L6: 3D 22.97 in [22, 26] InBand; approach coverage [0, 1.208] = 1.21/10
    BASE_HELI_001 L5: 3D 22.94 in [20, 24] InBand; approach coverage [0, 3.507] = 3.51/10
    approach profile (0 = front position, 10 = end): [0, 1.208] x2 [1.208, 3.507] x1 [3.507, 10] x0
    covered by >=1: 3.51/10  by >=2: 1.21/10  OUTSIDE every engagement band: 6.49/10 (partial)
  reserved approach strips: approach BASE_HELI_043 10.00x8 hdg 198.5; approach BASE_HELI_001 10.00x8 hdg 309.3; approach BASE_AIR_062 10.00x8 hdg 75.5
  rejected candidate poses: none

FORM_CLUSTER_002  3 members  centre (-44.02, 11.54, -375.23)  radius 15.08 (window 14.32-15.47)  rotation 48.4  shared band [22, 24]  attempts 2  substitutions 0
  [0] BASE_HELI_070  Heli L5  pos (-33.49, 11.06, -364.90)  r 14.75  slot 45.6  fwd 225.0  intended 225.0 (jitter -0.5)  facing err 0.05  to centre 0.6
  [1] BASE_HELI_015  Heli L6  pos (-40.65, 11.57, -389.93)  r 15.09  slot 167.1  fwd 345.3  intended 345.3 (jitter -1.8)  facing err 0.00  to centre 1.8
  [2] BASE_HELI_082  Heli L6  pos (-58.17, 11.99, -369.73)  r 15.18  slot 291.2  fwd 112.1  intended 112.1 (jitter +0.9)  facing err 0.00  to centre 0.9
  front of BASE_HELI_070 (-35.82, 11.06, -367.23): 2 supporter(s) at the front position
    BASE_HELI_015 L6: 3D 23.21 in [22, 26] InBand; approach coverage [0, 1.462] = 1.46/10
    BASE_HELI_082 L6: 3D 22.50 in [22, 26] InBand; approach coverage [0, 0.651] = 0.65/10
    approach profile (0 = front position, 10 = end): [0, 0.651] x2 [0.651, 1.462] x1 [1.462, 10] x0
    covered by >=1: 1.46/10  by >=2: 0.65/10  OUTSIDE every engagement band: 8.54/10 (partial)
  front of BASE_HELI_015 (-41.49, 11.57, -386.74): 2 supporter(s) at the front position
    BASE_HELI_070 L5: 3D 23.26 in [20, 24] InBand; approach coverage [0, 4.151] = 4.15/10
    BASE_HELI_082 L6: 3D 23.83 in [22, 26] InBand; approach coverage [0, 2.14] = 2.14/10
    approach profile (0 = front position, 10 = end): [0, 2.14] x2 [2.14, 4.151] x1 [4.151, 10] x0
    covered by >=1: 4.15/10  by >=2: 2.14/10  OUTSIDE every engagement band: 5.85/10 (partial)
  front of BASE_HELI_082 (-55.11, 11.99, -370.97): 2 supporter(s) at the front position
    BASE_HELI_070 L5: 3D 22.48 in [20, 24] InBand; approach coverage [0, 3.264] = 3.26/10
    BASE_HELI_015 L6: 3D 23.85 in [22, 26] InBand; approach coverage [0, 2.186] = 2.19/10
    approach profile (0 = front position, 10 = end): [0, 2.186] x2 [2.186, 3.264] x1 [3.264, 10] x0
    covered by >=1: 3.26/10  by >=2: 2.19/10  OUTSIDE every engagement band: 6.74/10 (partial)
  reserved approach strips: approach BASE_HELI_070 10.00x8 hdg 225.0; approach BASE_HELI_015 10.00x8 hdg 345.3; approach BASE_HELI_082 10.00x8 hdg 112.1
  rejected candidate poses: member: outside world bounds x1

FORM_CLUSTER_003  3 members  centre (-420.66, 11.54, 29.89)  radius 14.82 (window 14.32-15.47)  rotation 72.2  shared band [22, 24]  attempts 1  substitutions 0
  [0] BASE_HELI_011  Heli L5  pos (-406.74, 11.98, 34.32)  r 14.61  slot 72.3  fwd 251.0  intended 251.0 (jitter -1.3)  facing err 0.00  to centre 1.4
  [1] BASE_HELI_087  Heli L6  pos (-423.79, 11.34, 15.61)  r 14.61  slot 192.4  fwd 14.8  intended 14.8 (jitter +2.4)  facing err 0.03  to centre 2.4
  [2] BASE_HELI_008  Heli L5  pos (-430.99, 11.28, 40.61)  r 14.89  slot 316.1  fwd 136.5  intended 136.5 (jitter +0.5)  facing err 0.02  to centre 0.5
  front of BASE_HELI_011 (-409.86, 11.98, 33.24): 2 supporter(s) at the front position
    BASE_HELI_087 L6: 3D 22.48 in [22, 26] InBand; approach coverage [0, 0.574] = 0.57/10
    BASE_HELI_008 L5: 3D 22.39 in [20, 24] InBand; approach coverage [0, 3.171] = 3.17/10
    approach profile (0 = front position, 10 = end): [0, 0.574] x2 [0.574, 3.171] x1 [3.171, 10] x0
    covered by >=1: 3.17/10  by >=2: 0.57/10  OUTSIDE every engagement band: 6.83/10 (partial)
  front of BASE_HELI_087 (-422.95, 11.34, 18.80): 2 supporter(s) at the front position
    BASE_HELI_011 L5: 3D 22.45 in [20, 24] InBand; approach coverage [0, 2.94] = 2.94/10
    BASE_HELI_008 L5: 3D 23.24 in [20, 24] InBand; approach coverage [0, 4.132] = 4.13/10
    approach profile (0 = front position, 10 = end): [0, 2.94] x2 [2.94, 4.132] x1 [4.132, 10] x0
    covered by >=1: 4.13/10  by >=2: 2.94/10  OUTSIDE every engagement band: 5.87/10 (partial)
  front of BASE_HELI_008 (-428.72, 11.28, 38.21): 2 supporter(s) at the front position
    BASE_HELI_011 L5: 3D 22.34 in [20, 24] InBand; approach coverage [0, 3.007] = 3.01/10
    BASE_HELI_087 L6: 3D 23.13 in [22, 26] InBand; approach coverage [0, 1.336] = 1.34/10
    approach profile (0 = front position, 10 = end): [0, 1.336] x2 [1.336, 3.007] x1 [3.007, 10] x0
    covered by >=1: 3.01/10  by >=2: 1.34/10  OUTSIDE every engagement band: 6.99/10 (partial)
  reserved approach strips: approach BASE_HELI_011 10.00x8 hdg 251.0; approach BASE_HELI_087 10.00x8 hdg 14.8; approach BASE_HELI_008 10.00x8 hdg 136.5
  rejected candidate poses: none
reserved corridor strips 9 | ordinary placement attempts rejected by reserved corridors 0
```

### Mode D (pairs + clusters), seed 12345

```text
IRONVEIL Stage 2c - Strategic formation report (inward-facing clusters)
seed 12345 | S = 1 | front attack 3.3 | extra approach analysed 10 | min supporters per front 2 | approach strip width 8 (placeholder)
requested clusters 2 (members 3/4) | placed 2 | unfilled 0 | eligible L5/L6 land Heli/Air 42
excluded: already a facing-pair member 14, level not eligible 114, no defensive front (Tank) 110, route base (naval/rail) 70

FORM_CLUSTER_001  3 members  centre (-312.35, 12.51, -79.74)  radius 14.91 (window 13.16-15.47)  rotation 358.0  shared band [20, 24]  attempts 1  substitutions 0
  [0] BASE_HELI_018  Heli L5  pos (-311.93, 12.48, -64.71)  r 15.03  slot 1.6  fwd 181.0  intended 181.0 (jitter -0.6)  facing err 0.00  to centre 0.6
  [1] BASE_HELI_012  Heli L5  pos (-299.37, 12.22, -87.14)  r 14.94  slot 119.7  fwd 299.1  intended 299.1 (jitter -0.6)  facing err 0.00  to centre 0.6
  [2] BASE_AIR_037   Air  L5  pos (-324.90, 12.85, -87.13)  r 14.57  slot 239.5  fwd 58.0  intended 58.0 (jitter -1.5)  facing err 0.02  to centre 1.6
  front of BASE_HELI_018 (-311.98, 12.48, -68.01): 2 supporter(s) at the front position
    BASE_HELI_012 L5: 3D 22.92 in [20, 24] InBand; approach coverage [0, 3.671] = 3.67/10
    BASE_AIR_037 L5: 3D 23.08 in [20, 24] InBand; approach coverage [0, 3.801] = 3.80/10
    approach profile (0 = front position, 10 = end): [0, 3.671] x2 [3.671, 3.801] x1 [3.801, 10] x0
    covered by >=1: 3.80/10  by >=2: 3.67/10  OUTSIDE every engagement band: 6.20/10 (partial)
  front of BASE_HELI_012 (-302.25, 12.22, -85.54): 2 supporter(s) at the front position
    BASE_HELI_018 L5: 3D 22.97 in [20, 24] InBand; approach coverage [0, 3.825] = 3.83/10
    BASE_AIR_037 L5: 3D 22.72 in [20, 24] InBand; approach coverage [0, 3.346] = 3.35/10
    approach profile (0 = front position, 10 = end): [0, 3.346] x2 [3.346, 3.825] x1 [3.825, 10] x0
    covered by >=1: 3.83/10  by >=2: 3.35/10  OUTSIDE every engagement band: 6.17/10 (partial)
  front of BASE_AIR_037 (-322.11, 12.85, -85.38): 2 supporter(s) at the front position
    BASE_HELI_018 L5: 3D 23.04 in [20, 24] InBand; approach coverage [0, 3.689] = 3.69/10
    BASE_HELI_012 L5: 3D 22.82 in [20, 24] InBand; approach coverage [0, 3.653] = 3.65/10
    approach profile (0 = front position, 10 = end): [0, 3.653] x2 [3.653, 3.689] x1 [3.689, 10] x0
    covered by >=1: 3.69/10  by >=2: 3.65/10  OUTSIDE every engagement band: 6.31/10 (partial)
  reserved approach strips: approach BASE_HELI_018 10.00x8 hdg 181.0; approach BASE_HELI_012 10.00x8 hdg 299.1; approach BASE_AIR_037 10.00x8 hdg 58.0
  rejected candidate poses: none

FORM_CLUSTER_002  4 members  centre (338.95, 11.84, -183.46)  radius 17.79 (window 17.12-18.54)  rotation 287.7  shared band [22, 24]  attempts 2  substitutions 0
  [0] BASE_HELI_028  Heli L5  pos (322.52, 11.67, -177.75)  r 17.39  slot 289.2  fwd 109.6  intended 109.6 (jitter +0.5)  facing err 0.00  to centre 0.5
  [1] BASE_AIR_071   Air  L5  pos (345.07, 11.77, -166.94)  r 17.61  slot 20.3  fwd 203.2  intended 203.2 (jitter +2.8)  facing err 0.00  to centre 2.8
  [2] BASE_AIR_023   Air  L6  pos (355.47, 11.44, -189.39)  r 17.55  slot 109.8  fwd 290.0  intended 290.0 (jitter +0.2)  facing err 0.00  to centre 0.2
  [3] BASE_HELI_054  Heli L6  pos (332.59, 12.47, -199.85)  r 17.58  slot 201.2  fwd 24.3  intended 24.1 (jitter +2.9)  facing err 0.21  to centre 3.1
  front of BASE_HELI_028 (325.63, 11.67, -178.86): 2 supporter(s) at the front position
    BASE_AIR_071 L5: 3D 22.80 in [20, 24] InBand; approach coverage [0, 5.103] = 5.10/10
    BASE_AIR_023 L6: 3D 31.64 in [22, 26] BeyondMaximum; approach coverage [5.644, 9.644] = 4.00/10
    BASE_HELI_054 L6: 3D 22.13 in [22, 26] InBand; approach coverage [0, 0.208] = 0.21/10
    approach profile (0 = front position, 10 = end): [0, 0.208] x2 [0.208, 5.103] x1 [5.103, 5.644] x0 [5.644, 9.644] x1 [9.644, 10] x0
    covered by >=1: 9.10/10  by >=2: 0.21/10  OUTSIDE every engagement band: 0.90/10 (partial)
  front of BASE_AIR_071 (343.77, 11.77, -169.97): 2 supporter(s) at the front position
    BASE_HELI_028 L5: 3D 22.63 in [20, 24] InBand; approach coverage [0, 4.176] = 4.18/10
    BASE_AIR_023 L6: 3D 22.67 in [22, 26] InBand; approach coverage [0, 1.18] = 1.18/10
    BASE_HELI_054 L6: 3D 31.91 in [22, 26] BeyondMaximum; approach coverage [5.915, 9.924] = 4.01/10
    approach profile (0 = front position, 10 = end): [0, 1.18] x2 [1.18, 4.176] x1 [4.176, 5.915] x0 [5.915, 9.924] x1 [9.924, 10] x0
    covered by >=1: 8.19/10  by >=2: 1.18/10  OUTSIDE every engagement band: 1.81/10 (partial)
  front of BASE_AIR_023 (352.36, 11.44, -188.26): 2 supporter(s) at the front position
    BASE_HELI_028 L5: 3D 31.64 in [20, 24] BeyondMaximum; approach coverage [7.645, 10] = 2.36/10
    BASE_AIR_071 L5: 3D 22.54 in [20, 24] InBand; approach coverage [0, 4.555] = 4.56/10
    BASE_HELI_054 L6: 3D 22.94 in [22, 26] InBand; approach coverage [0, 1.526] = 1.53/10
    approach profile (0 = front position, 10 = end): [0, 1.526] x2 [1.526, 4.555] x1 [4.555, 7.645] x0 [7.645, 10] x1
    covered by >=1: 6.91/10  by >=2: 1.53/10  OUTSIDE every engagement band: 3.09/10 (partial)
  front of BASE_HELI_054 (333.95, 12.47, -196.84): 2 supporter(s) at the front position
    BASE_HELI_028 L5: 3D 22.26 in [20, 24] InBand; approach coverage [0, 4.611] = 4.61/10
    BASE_AIR_071 L5: 3D 31.91 in [20, 24] BeyondMaximum; approach coverage [7.936, 10] = 2.06/10
    BASE_AIR_023 L6: 3D 22.79 in [22, 26] InBand; approach coverage [0, 1.181] = 1.18/10
    approach profile (0 = front position, 10 = end): [0, 1.181] x2 [1.181, 4.611] x1 [4.611, 7.936] x0 [7.936, 10] x1
    covered by >=1: 6.67/10  by >=2: 1.18/10  OUTSIDE every engagement band: 3.33/10 (partial)
  reserved approach strips: approach BASE_HELI_028 10.00x8 hdg 109.6; approach BASE_AIR_071 10.00x8 hdg 203.2; approach BASE_AIR_023 10.00x8 hdg 290.0; approach BASE_HELI_054 10.00x8 hdg 24.3
  rejected candidate poses: member: outside world bounds x1
reserved corridor strips 28 | ordinary placement attempts rejected by reserved corridors 0
```

### Mode D (pairs + clusters), seed 777

```text
IRONVEIL Stage 2c - Strategic formation report (inward-facing clusters)
seed 777 | S = 1 | front attack 3.3 | extra approach analysed 10 | min supporters per front 2 | approach strip width 8 (placeholder)
requested clusters 2 (members 3/3) | placed 2 | unfilled 0 | eligible L5/L6 land Heli/Air 38
excluded: already a facing-pair member 14, level not eligible 118, no defensive front (Tank) 110, route base (naval/rail) 70

FORM_CLUSTER_001  3 members  centre (79.30, 9.60, -200.66)  radius 15.12 (window 14.32-15.47)  rotation 158.6  shared band [22, 24]  attempts 5  substitutions 0
  [0] BASE_AIR_014   Air  L6  pos (85.03, 9.44, -214.43)  r 14.91  slot 157.4  fwd 338.3  intended 338.3 (jitter +0.8)  facing err 0.00  to centre 0.8
  [1] BASE_HELI_038  Heli L6  pos (64.82, 9.51, -197.90)  r 14.74  slot 280.8  fwd 102.7  intended 102.7 (jitter +1.9)  facing err 0.00  to centre 1.9
  [2] BASE_HELI_022  Heli L5  pos (88.65, 9.85, -189.22)  r 14.77  slot 39.3  fwd 216.9  intended 216.9 (jitter -2.3)  facing err 0.05  to centre 2.3
  front of BASE_AIR_014 (83.81, 9.44, -211.36): 2 supporter(s) at the front position
    BASE_HELI_038 L6: 3D 23.27 in [22, 26] InBand; approach coverage [0, 1.536] = 1.54/10
    BASE_HELI_022 L5: 3D 22.67 in [20, 24] InBand; approach coverage [0, 3.325] = 3.33/10
    approach profile (0 = front position, 10 = end): [0, 1.536] x2 [1.536, 3.325] x1 [3.325, 10] x0
    covered by >=1: 3.33/10  by >=2: 1.54/10  OUTSIDE every engagement band: 6.67/10 (partial)
  front of BASE_HELI_038 (68.04, 9.51, -198.62): 2 supporter(s) at the front position
    BASE_AIR_014 L6: 3D 23.20 in [22, 26] InBand; approach coverage [0, 1.403] = 1.40/10
    BASE_HELI_022 L5: 3D 22.65 in [20, 24] InBand; approach coverage [0, 3.473] = 3.47/10
    approach profile (0 = front position, 10 = end): [0, 1.403] x2 [1.403, 3.473] x1 [3.473, 10] x0
    covered by >=1: 3.47/10  by >=2: 1.40/10  OUTSIDE every engagement band: 6.53/10 (partial)
  front of BASE_HELI_022 (86.67, 9.85, -191.86): 2 supporter(s) at the front position
    BASE_AIR_014 L6: 3D 22.63 in [22, 26] InBand; approach coverage [0, 0.753] = 0.75/10
    BASE_HELI_038 L6: 3D 22.67 in [22, 26] InBand; approach coverage [0, 0.849] = 0.85/10
    approach profile (0 = front position, 10 = end): [0, 0.753] x2 [0.753, 0.849] x1 [0.849, 10] x0
    covered by >=1: 0.85/10  by >=2: 0.75/10  OUTSIDE every engagement band: 9.15/10 (partial)
  reserved approach strips: approach BASE_AIR_014 10.00x8 hdg 338.3; approach BASE_HELI_038 10.00x8 hdg 102.7; approach BASE_HELI_022 10.00x8 hdg 216.9
  rejected candidate poses: front supported by 1 < 2 (BeyondMaximum) x1, front supported by 1 < 2 (InsideMinimum) x1, member: footprint too uneven x1, member: outside world bounds x1

FORM_CLUSTER_002  3 members  centre (250.36, 11.23, 30.35)  radius 15.69 (window 14.32-16.63)  rotation 0.5  shared band [22, 26]  attempts 2  substitutions 0
  [0] BASE_AIR_038   Air  L6  pos (250.07, 10.92, 46.34)  r 15.99  slot 359.0  fwd 181.0  intended 181.0 (jitter +2.1)  facing err 0.00  to centre 2.1
  [1] BASE_AIR_019   Air  L6  pos (263.73, 11.29, 21.64)  r 15.96  slot 123.1  fwd 305.4  intended 305.4 (jitter +2.3)  facing err 0.00  to centre 2.3
  [2] BASE_HELI_086  Heli L6  pos (236.47, 11.48, 23.18)  r 15.63  slot 242.7  fwd 61.1  intended 61.1 (jitter -1.6)  facing err 0.00  to centre 1.6
  front of BASE_AIR_038 (250.02, 10.92, 43.04): 2 supporter(s) at the front position
    BASE_AIR_019 L6: 3D 25.42 in [22, 26] InBand; approach coverage [0, 4.259] = 4.26/10
    BASE_HELI_086 L6: 3D 24.04 in [22, 26] InBand; approach coverage [0, 2.492] = 2.49/10
    approach profile (0 = front position, 10 = end): [0, 2.492] x2 [2.492, 4.259] x1 [4.259, 10] x0
    covered by >=1: 4.26/10  by >=2: 2.49/10  OUTSIDE every engagement band: 5.74/10 (partial)
  front of BASE_AIR_019 (261.04, 11.29, 23.55): 2 supporter(s) at the front position
    BASE_AIR_038 L6: 3D 25.29 in [22, 26] InBand; approach coverage [0, 3.842] = 3.84/10
    BASE_HELI_086 L6: 3D 24.58 in [22, 26] InBand; approach coverage [0, 3.306] = 3.31/10
    approach profile (0 = front position, 10 = end): [0, 3.306] x2 [3.306, 3.842] x1 [3.842, 10] x0
    covered by >=1: 3.84/10  by >=2: 3.31/10  OUTSIDE every engagement band: 6.16/10 (partial)
  front of BASE_HELI_086 (239.36, 11.48, 24.78): 2 supporter(s) at the front position
    BASE_AIR_038 L6: 3D 24.08 in [22, 26] InBand; approach coverage [0, 2.593] = 2.59/10
    BASE_AIR_019 L6: 3D 24.57 in [22, 26] InBand; approach coverage [0, 3.298] = 3.30/10
    approach profile (0 = front position, 10 = end): [0, 2.593] x2 [2.593, 3.298] x1 [3.298, 10] x0
    covered by >=1: 3.30/10  by >=2: 2.59/10  OUTSIDE every engagement band: 6.70/10 (partial)
  reserved approach strips: approach BASE_AIR_038 10.00x8 hdg 181.0; approach BASE_AIR_019 10.00x8 hdg 305.4; approach BASE_HELI_086 10.00x8 hdg 61.1
  rejected candidate poses: member: footprint too uneven x1
reserved corridor strips 27 | ordinary placement attempts rejected by reserved corridors 0
```

### Mode D (pairs + clusters), seed 424242

```text
IRONVEIL Stage 2c - Strategic formation report (inward-facing clusters)
seed 424242 | S = 1 | front attack 3.3 | extra approach analysed 10 | min supporters per front 2 | approach strip width 8 (placeholder)
requested clusters 3 (members 3/4/3) | placed 3 | unfilled 0 | eligible L5/L6 land Heli/Air 46
excluded: already a facing-pair member 14, level not eligible 110, no defensive front (Tank) 110, route base (naval/rail) 70

FORM_CLUSTER_001  3 members  centre (199.40, 12.78, -130.80)  radius 15.13 (window 14.32-15.47)  rotation 356.2  shared band [22, 24]  attempts 1  substitutions 0
  [0] BASE_HELI_059  Heli L6  pos (198.16, 12.37, -115.83)  r 15.02  slot 355.3  fwd 176.2  intended 176.2 (jitter +0.9)  facing err 0.00  to centre 0.9
  [1] BASE_HELI_048  Heli L5  pos (213.36, 12.56, -136.80)  r 15.20  slot 113.3  fwd 292.1  intended 292.1 (jitter -1.2)  facing err 0.00  to centre 1.2
  [2] BASE_HELI_049  Heli L5  pos (186.55, 13.41, -138.35)  r 14.91  slot 239.6  fwd 62.5  intended 62.5 (jitter +2.9)  facing err 0.07  to centre 2.9
  front of BASE_HELI_059 (198.38, 12.37, -119.12): 2 supporter(s) at the front position
    BASE_HELI_048 L5: 3D 23.18 in [20, 24] InBand; approach coverage [0, 4.141] = 4.14/10
    BASE_HELI_049 L5: 3D 22.60 in [20, 24] InBand; approach coverage [0, 3.306] = 3.31/10
    approach profile (0 = front position, 10 = end): [0, 3.306] x2 [3.306, 4.141] x1 [4.141, 10] x0
    covered by >=1: 4.14/10  by >=2: 3.31/10  OUTSIDE every engagement band: 5.86/10 (partial)
  front of BASE_HELI_048 (210.31, 12.56, -135.56): 2 supporter(s) at the front position
    BASE_HELI_059 L6: 3D 23.17 in [22, 26] InBand; approach coverage [0, 1.476] = 1.48/10
    BASE_HELI_049 L5: 3D 23.94 in [20, 24] InBand; approach coverage [0, 4.636] = 4.64/10
    approach profile (0 = front position, 10 = end): [0, 1.476] x2 [1.476, 4.636] x1 [4.636, 10] x0
    covered by >=1: 4.64/10  by >=2: 1.48/10  OUTSIDE every engagement band: 5.36/10 (partial)
  front of BASE_HELI_049 (189.47, 13.41, -136.83): 2 supporter(s) at the front position
    BASE_HELI_059 L6: 3D 22.75 in [22, 26] InBand; approach coverage [0, 0.988] = 0.99/10
    BASE_HELI_048 L5: 3D 23.90 in [20, 24] InBand; approach coverage [0, 4.525] = 4.53/10
    approach profile (0 = front position, 10 = end): [0, 0.988] x2 [0.988, 4.525] x1 [4.525, 10] x0
    covered by >=1: 4.53/10  by >=2: 0.99/10  OUTSIDE every engagement band: 5.47/10 (partial)
  reserved approach strips: approach BASE_HELI_059 10.00x8 hdg 176.2; approach BASE_HELI_048 10.00x8 hdg 292.1; approach BASE_HELI_049 10.00x8 hdg 62.5
  rejected candidate poses: none

FORM_CLUSTER_002  4 members  centre (105.41, 11.77, -73.00)  radius 17.93 (window 17.12-19.96)  rotation 36.8  shared band [22, 26]  attempts 2  substitutions 0
  [0] BASE_AIR_007   Air  L6  pos (115.22, 13.38, -58.07)  r 17.86  slot 33.3  fwd 212.5  intended 212.9 (jitter -0.4)  facing err 0.38  to centre 0.8
  [1] BASE_HELI_084  Heli L6  pos (119.73, 13.10, -83.82)  r 17.95  slot 127.1  fwd 305.5  intended 305.4 (jitter -1.6)  facing err 0.03  to centre 1.6
  [2] BASE_HELI_077  Heli L6  pos (93.73, 10.42, -87.05)  r 18.28  slot 219.7  fwd 39.8  intended 39.9 (jitter +0.1)  facing err 0.10  to centre 0.0
  [3] BASE_AIR_005   Air  L6  pos (91.20, 10.16, -62.11)  r 17.90  slot 307.5  fwd 127.0  intended 126.9 (jitter -0.5)  facing err 0.06  to centre 0.4
  front of BASE_AIR_007 (113.45, 13.38, -60.86): 2 supporter(s) at the front position
    BASE_HELI_084 L6: 3D 23.81 in [22, 26] InBand; approach coverage [0, 2.845] = 2.84/10
    BASE_HELI_077 L6: 3D 32.92 in [22, 26] BeyondMaximum; approach coverage [6.983, 10] = 3.02/10
    BASE_AIR_005 L6: 3D 22.52 in [22, 26] InBand; approach coverage [0, 0.917] = 0.92/10
    approach profile (0 = front position, 10 = end): [0, 0.917] x2 [0.917, 2.845] x1 [2.845, 6.983] x0 [6.983, 10] x1
    covered by >=1: 5.86/10  by >=2: 0.92/10  OUTSIDE every engagement band: 4.14/10 (partial)
  front of BASE_HELI_084 (117.05, 13.10, -81.91): 2 supporter(s) at the front position
    BASE_AIR_007 L6: 3D 23.90 in [22, 26] InBand; approach coverage [0, 3.184] = 3.18/10
    BASE_HELI_077 L6: 3D 24.03 in [22, 26] InBand; approach coverage [0, 3.247] = 3.25/10
    BASE_AIR_005 L6: 3D 32.69 in [22, 26] BeyondMaximum; approach coverage [6.728, 10] = 3.27/10
    approach profile (0 = front position, 10 = end): [0, 3.184] x2 [3.184, 3.247] x1 [3.247, 6.728] x0 [6.728, 10] x1
    covered by >=1: 6.52/10  by >=2: 3.18/10  OUTSIDE every engagement band: 3.48/10 (partial)
  front of BASE_HELI_077 (95.84, 10.42, -84.51): 2 supporter(s) at the front position
    BASE_AIR_007 L6: 3D 32.92 in [22, 26] BeyondMaximum; approach coverage [6.969, 10] = 3.03/10
    BASE_HELI_084 L6: 3D 24.06 in [22, 26] InBand; approach coverage [0, 3.348] = 3.35/10
    BASE_AIR_005 L6: 3D 22.88 in [22, 26] InBand; approach coverage [0, 1.456] = 1.46/10
    approach profile (0 = front position, 10 = end): [0, 1.456] x2 [1.456, 3.348] x1 [3.348, 6.969] x0 [6.969, 10] x1
    covered by >=1: 6.38/10  by >=2: 1.46/10  OUTSIDE every engagement band: 3.62/10 (partial)
  front of BASE_AIR_005 (93.83, 10.16, -64.10): 2 supporter(s) at the front position
    BASE_AIR_007 L6: 3D 22.45 in [22, 26] InBand; approach coverage [0, 0.769] = 0.77/10
    BASE_HELI_084 L6: 3D 32.69 in [22, 26] BeyondMaximum; approach coverage [6.721, 10] = 3.28/10
    BASE_HELI_077 L6: 3D 22.95 in [22, 26] InBand; approach coverage [0, 1.662] = 1.66/10
    approach profile (0 = front position, 10 = end): [0, 0.769] x2 [0.769, 1.662] x1 [1.662, 6.721] x0 [6.721, 10] x1
    covered by >=1: 4.94/10  by >=2: 0.77/10  OUTSIDE every engagement band: 5.06/10 (partial)
  reserved approach strips: approach BASE_AIR_007 10.00x8 hdg 212.5; approach BASE_HELI_084 10.00x8 hdg 305.5; approach BASE_HELI_077 10.00x8 hdg 39.8; approach BASE_AIR_005 10.00x8 hdg 127.0
  rejected candidate poses: front supported by 1 < 2 (BeyondMaximum) x1

FORM_CLUSTER_003  3 members  centre (238.18, 9.57, -404.77)  radius 14.85 (window 14.32-15.47)  rotation 255.6  shared band [22, 24]  attempts 1  substitutions 0
  [0] BASE_AIR_046   Air  L6  pos (224.14, 9.67, -408.68)  r 14.57  slot 254.4  fwd 71.8  intended 71.8 (jitter -2.6)  facing err 0.00  to centre 2.6
  [1] BASE_AIR_031   Air  L5  pos (242.39, 9.52, -390.14)  r 15.23  slot 16.1  fwd 194.1  intended 194.1 (jitter -2.0)  facing err 0.00  to centre 2.0
  [2] BASE_HELI_082  Heli L5  pos (248.61, 9.54, -415.75)  r 15.14  slot 136.5  fwd 317.9  intended 317.9 (jitter +1.5)  facing err 0.00  to centre 1.5
  front of BASE_AIR_046 (227.28, 9.67, -407.65): 2 supporter(s) at the front position
    BASE_AIR_031 L5: 3D 23.14 in [20, 24] InBand; approach coverage [0, 3.77] = 3.77/10
    BASE_HELI_082 L5: 3D 22.81 in [20, 24] InBand; approach coverage [0, 3.802] = 3.80/10
    approach profile (0 = front position, 10 = end): [0, 3.77] x2 [3.77, 3.802] x1 [3.802, 10] x0
    covered by >=1: 3.80/10  by >=2: 3.77/10  OUTSIDE every engagement band: 6.20/10 (partial)
  front of BASE_AIR_031 (241.59, 9.52, -393.34): 2 supporter(s) at the front position
    BASE_AIR_046 L6: 3D 23.24 in [22, 26] InBand; approach coverage [0, 1.522] = 1.52/10
    BASE_HELI_082 L5: 3D 23.48 in [20, 24] InBand; approach coverage [0, 4.223] = 4.22/10
    approach profile (0 = front position, 10 = end): [0, 1.522] x2 [1.522, 4.223] x1 [4.223, 10] x0
    covered by >=1: 4.22/10  by >=2: 1.52/10  OUTSIDE every engagement band: 5.78/10 (partial)
  front of BASE_HELI_082 (246.40, 9.54, -413.30): 2 supporter(s) at the front position
    BASE_AIR_046 L6: 3D 22.73 in [22, 26] InBand; approach coverage [0, 0.91] = 0.91/10
    BASE_AIR_031 L5: 3D 23.50 in [20, 24] InBand; approach coverage [0, 4.298] = 4.30/10
    approach profile (0 = front position, 10 = end): [0, 0.91] x2 [0.91, 4.298] x1 [4.298, 10] x0
    covered by >=1: 4.30/10  by >=2: 0.91/10  OUTSIDE every engagement band: 5.70/10 (partial)
  reserved approach strips: approach BASE_AIR_046 10.00x8 hdg 71.8; approach BASE_AIR_031 10.00x8 hdg 194.1; approach BASE_HELI_082 10.00x8 hdg 317.9
  rejected candidate poses: none
reserved corridor strips 31 | ordinary placement attempts rejected by reserved corridors 0
```

### Mode D (pairs + clusters), seed 1000

```text
IRONVEIL Stage 2c - Strategic formation report (inward-facing clusters)
seed 1000 | S = 1 | front attack 3.3 | extra approach analysed 10 | min supporters per front 2 | approach strip width 8 (placeholder)
requested clusters 3 (members 3/3/3) | placed 3 | unfilled 0 | eligible L5/L6 land Heli/Air 40
excluded: already a facing-pair member 16, level not eligible 114, no defensive front (Tank) 110, route base (naval/rail) 70

FORM_CLUSTER_001  3 members  centre (270.76, 11.93, -7.30)  radius 14.77 (window 14.32-15.47)  rotation 14.7  shared band [22, 24]  attempts 1  substitutions 0
  [0] BASE_AIR_021   Air  L6  pos (274.68, 11.87, 6.73)  r 14.57  slot 15.6  fwd 198.5  intended 198.5 (jitter +2.9)  facing err 0.00  to centre 2.9
  [1] BASE_HELI_042  Heli L5  pos (281.98, 12.14, -17.19)  r 14.96  slot 131.4  fwd 309.3  intended 309.3 (jitter -2.1)  facing err 0.00  to centre 2.1
  [2] BASE_HELI_090  Heli L6  pos (256.73, 11.79, -11.70)  r 14.70  slot 252.6  fwd 75.5  intended 75.5 (jitter +2.9)  facing err 0.00  to centre 2.9
  front of BASE_AIR_021 (273.63, 11.87, 3.60): 2 supporter(s) at the front position
    BASE_HELI_042 L5: 3D 22.40 in [20, 24] InBand; approach coverage [0, 3.303] = 3.30/10
    BASE_HELI_090 L6: 3D 22.80 in [22, 26] InBand; approach coverage [0, 0.924] = 0.92/10
    approach profile (0 = front position, 10 = end): [0, 0.924] x2 [0.924, 3.303] x1 [3.303, 10] x0
    covered by >=1: 3.30/10  by >=2: 0.92/10  OUTSIDE every engagement band: 6.70/10 (partial)
  front of BASE_HELI_042 (279.42, 12.14, -15.10): 2 supporter(s) at the front position
    BASE_AIR_021 L6: 3D 22.34 in [22, 26] InBand; approach coverage [0, 0.433] = 0.43/10
    BASE_HELI_090 L6: 3D 22.95 in [22, 26] InBand; approach coverage [0, 1.114] = 1.11/10
    approach profile (0 = front position, 10 = end): [0, 0.433] x2 [0.433, 1.114] x1 [1.114, 10] x0
    covered by >=1: 1.11/10  by >=2: 0.43/10  OUTSIDE every engagement band: 8.89/10 (partial)
  front of BASE_HELI_090 (259.92, 11.79, -10.88): 2 supporter(s) at the front position
    BASE_AIR_021 L6: 3D 22.97 in [22, 26] InBand; approach coverage [0, 1.208] = 1.21/10
    BASE_HELI_042 L5: 3D 22.94 in [20, 24] InBand; approach coverage [0, 3.506] = 3.51/10
    approach profile (0 = front position, 10 = end): [0, 1.208] x2 [1.208, 3.506] x1 [3.506, 10] x0
    covered by >=1: 3.51/10  by >=2: 1.21/10  OUTSIDE every engagement band: 6.49/10 (partial)
  reserved approach strips: approach BASE_AIR_021 10.00x8 hdg 198.5; approach BASE_HELI_042 10.00x8 hdg 309.3; approach BASE_HELI_090 10.00x8 hdg 75.5
  rejected candidate poses: none

FORM_CLUSTER_002  3 members  centre (-44.02, 11.55, -375.23)  radius 14.88 (window 13.16-15.47)  rotation 48.4  shared band [20, 24]  attempts 2  substitutions 0
  [0] BASE_HELI_081  Heli L5  pos (-33.63, 11.07, -365.04)  r 14.55  slot 45.6  fwd 225.0  intended 225.0 (jitter -0.5)  facing err 0.06  to centre 0.6
  [1] BASE_HELI_044  Heli L5  pos (-40.69, 11.58, -389.74)  r 14.89  slot 167.1  fwd 345.3  intended 345.3 (jitter -1.8)  facing err 0.00  to centre 1.8
  [2] BASE_AIR_035   Air  L5  pos (-57.99, 11.99, -369.80)  r 14.98  slot 291.2  fwd 112.1  intended 112.1 (jitter +0.9)  facing err 0.00  to centre 0.9
  front of BASE_HELI_081 (-35.96, 11.07, -367.37): 2 supporter(s) at the front position
    BASE_HELI_044 L5: 3D 22.87 in [20, 24] InBand; approach coverage [0, 3.535] = 3.54/10
    BASE_AIR_035 L5: 3D 22.17 in [20, 24] InBand; approach coverage [0, 2.897] = 2.90/10
    approach profile (0 = front position, 10 = end): [0, 2.897] x2 [2.897, 3.535] x1 [3.535, 10] x0
    covered by >=1: 3.54/10  by >=2: 2.90/10  OUTSIDE every engagement band: 6.46/10 (partial)
  front of BASE_HELI_044 (-41.53, 11.58, -386.55): 2 supporter(s) at the front position
    BASE_HELI_081 L5: 3D 22.92 in [20, 24] InBand; approach coverage [0, 3.7] = 3.70/10
    BASE_AIR_035 L5: 3D 23.49 in [20, 24] InBand; approach coverage [0, 4.14] = 4.14/10
    approach profile (0 = front position, 10 = end): [0, 3.7] x2 [3.7, 4.14] x1 [4.14, 10] x0
    covered by >=1: 4.14/10  by >=2: 3.70/10  OUTSIDE every engagement band: 5.86/10 (partial)
  front of BASE_AIR_035 (-54.93, 11.99, -371.04): 2 supporter(s) at the front position
    BASE_HELI_081 L5: 3D 22.15 in [20, 24] InBand; approach coverage [0, 2.817] = 2.82/10
    BASE_HELI_044 L5: 3D 23.51 in [20, 24] InBand; approach coverage [0, 4.21] = 4.21/10
    approach profile (0 = front position, 10 = end): [0, 2.817] x2 [2.817, 4.21] x1 [4.21, 10] x0
    covered by >=1: 4.21/10  by >=2: 2.82/10  OUTSIDE every engagement band: 5.79/10 (partial)
  reserved approach strips: approach BASE_HELI_081 10.00x8 hdg 225.0; approach BASE_HELI_044 10.00x8 hdg 345.3; approach BASE_AIR_035 10.00x8 hdg 112.1
  rejected candidate poses: member: outside world bounds x1

FORM_CLUSTER_003  3 members  centre (-420.66, 11.54, 29.89)  radius 14.82 (window 14.32-15.47)  rotation 72.2  shared band [22, 24]  attempts 1  substitutions 0
  [0] BASE_HELI_084  Heli L5  pos (-406.74, 11.98, 34.32)  r 14.61  slot 72.3  fwd 251.0  intended 251.0 (jitter -1.3)  facing err 0.00  to centre 1.4
  [1] BASE_HELI_015  Heli L6  pos (-423.79, 11.34, 15.61)  r 14.61  slot 192.4  fwd 14.8  intended 14.8 (jitter +2.4)  facing err 0.03  to centre 2.4
  [2] BASE_AIR_002   Air  L5  pos (-430.99, 11.28, 40.61)  r 14.89  slot 316.1  fwd 136.5  intended 136.5 (jitter +0.5)  facing err 0.02  to centre 0.5
  front of BASE_HELI_084 (-409.86, 11.98, 33.24): 2 supporter(s) at the front position
    BASE_HELI_015 L6: 3D 22.48 in [22, 26] InBand; approach coverage [0, 0.574] = 0.57/10
    BASE_AIR_002 L5: 3D 22.39 in [20, 24] InBand; approach coverage [0, 3.171] = 3.17/10
    approach profile (0 = front position, 10 = end): [0, 0.574] x2 [0.574, 3.171] x1 [3.171, 10] x0
    covered by >=1: 3.17/10  by >=2: 0.57/10  OUTSIDE every engagement band: 6.83/10 (partial)
  front of BASE_HELI_015 (-422.95, 11.34, 18.80): 2 supporter(s) at the front position
    BASE_HELI_084 L5: 3D 22.45 in [20, 24] InBand; approach coverage [0, 2.94] = 2.94/10
    BASE_AIR_002 L5: 3D 23.24 in [20, 24] InBand; approach coverage [0, 4.132] = 4.13/10
    approach profile (0 = front position, 10 = end): [0, 2.94] x2 [2.94, 4.132] x1 [4.132, 10] x0
    covered by >=1: 4.13/10  by >=2: 2.94/10  OUTSIDE every engagement band: 5.87/10 (partial)
  front of BASE_AIR_002 (-428.72, 11.28, 38.21): 2 supporter(s) at the front position
    BASE_HELI_084 L5: 3D 22.34 in [20, 24] InBand; approach coverage [0, 3.008] = 3.01/10
    BASE_HELI_015 L6: 3D 23.13 in [22, 26] InBand; approach coverage [0, 1.336] = 1.34/10
    approach profile (0 = front position, 10 = end): [0, 1.336] x2 [1.336, 3.008] x1 [3.008, 10] x0
    covered by >=1: 3.01/10  by >=2: 1.34/10  OUTSIDE every engagement band: 6.99/10 (partial)
  reserved approach strips: approach BASE_HELI_084 10.00x8 hdg 251.0; approach BASE_HELI_015 10.00x8 hdg 14.8; approach BASE_AIR_002 10.00x8 hdg 136.5
  rejected candidate poses: none
reserved corridor strips 33 | ordinary placement attempts rejected by reserved corridors 1
```
