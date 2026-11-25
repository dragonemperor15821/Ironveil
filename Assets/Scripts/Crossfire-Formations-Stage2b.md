# IRONVEIL — Stage 2b: Deterministic L5/L6 Facing Pairs

Status: implemented, **OFF by default** (`BaseSpawner ▸ generateStrategicFormations`, script default
and MainBuild scene). With it off, every seed reproduces the pre-2b layout bit for bit (seed 12345 =
`e71caf6c…`). There are no clusters, Flak firing, targeting, combat or troop AI.

Builds on `Crossfire-Formations-Stage2a.md` (ranges, S, attack model, geometry, probe).

## What ON does

1. Levels are assigned first (unchanged `BaseLevelAssignment`).
2. **Selection** (`FacingPairFormations.SelectPool`): eligible = land + defensive front + level in
   `eligibleLevels` (L5/L6). This means only Heli and Air. Tank, naval/rail and L1–L4 are excluded.
   The pair count is 7 or 8, drawn from a private SplitMix64 stream (`IRONVEIL.FacingPairs.Select.v1`).
   The pool is sorted by ID and then shuffled, so input order never matters. Each base is used at most once.
3. **Pose sampling** (private stream `IRONVEIL.FacingPairs.Place.v1`, fixed draw order per attempt):
   * spacing d, uniform inside the overlapping window shrunk by `windowEdgeMargin` (0.3);
   * pair centre, uniform in the battlefield;
   * pair axis heading;
   * per-member yaw jitter of up to ±`maxYawJitterDegrees` (4°).

   Members sit at centre ± axis·d/2, each facing its partner plus jitter.
4. **Validation** (every condition must hold, otherwise the candidate is resampled):
   * Both members pass the real land checks (`TryPassLandSiteChecks` + `TryEvaluateLandPose`: bounds,
     river/rail clearance, terrain flatness, slope, pivot spacing (10), footprint clearance (4),
     corridor clearance). B is checked against everything already placed **plus A**.
   * After terrain alignment, the horizontal forward is within 1.5° of the intended heading and within
     jitter + 1.5° of the partner.
   * Each member's centre is within its own band of the partner's front attack position (3.3 ahead), using full 3D
     distance with both min and max inclusive. Both distances must also lie in the **overlapping band**
     [max(minA, minB), min(maxA, maxB)]. For a mixed L5/L6 pair that is [22, 24] at S = 1.
   * The extended approach (+10) is analysed exactly (`CrossfireGeometry.ApproachCoverage`) and reported.
     Full coverage is not required, and partial coverage is reported as partial.
5. **Reservation**: accepted members are instantiated like ordinary land bases. Their positions and
   footprints are reserved, and every other base is then placed by the unchanged rules
   (still 350 bases, same type counts, same level per base ID).
   `UnityEngine.Random` is saved and restored around formation placement.

Facing window (flat, colinear, at S = 1): L5–L5 23.3–27.3, L6–L6 25.3–29.3, mixed 25.3–27.3.

## Editor-readable output

* Each member carries a `FormationMemberInfo` component (inspector view). It shows the formation ID,
  role, partner, distances, front position, the partner's distance to this base's front, its band
  status and approach coverage. Selecting the component draws gizmos.
* `BaseSpawner ▸ context menu ▸ Log Strategic Formation Report` gives the full text report. It is also
  logged after each ON generation and exposed as `LastFormationReport`.
  The report lists IDs, levels, positions, yaws, intended and actual headings, pair distances
  (horizontal / 3D / dy), the overlapping band, distances to the front, and coverage intervals.
* `LastFacingPairs` (records) and `LastFormationPlan` (generic `FormationSpec`s with support links).
* `FacingPairMember.placementInputXZ` is the exact XZ given to the checks.
  `ProbeLandPose(type, placementInputXZ, yaw)` reproduces the pose.

## Settings — `BaseSpawner ▸ facingPairSettings`

`minPairs` 7, `maxPairs` 8, `maxYawJitterDegrees` 4, `windowEdgeMargin` 0.3, `facingToleranceDegrees` 1.5,
`attemptsPerPair` 4000, `maxMemberSubstitutions` 3. If a pair cannot be placed, its members fall back to
ordinary placement and the report counts the pair as unfilled.

## Known geometry facts (S = 1)

A facing supporter covers only 0.3–3.8 units of the 10-unit extended approach, because advancing troops
close on it and enter its minimum ring. No pair covers the full approach.
Before Stage 2b.1, ordinary placement could put an unrelated base between two pair members. Stage 2b.1
reserves the pair corridor; see `Crossfire-Formations-Stage2b1.md`.
