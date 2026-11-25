# IRONVEIL — Stage 2a: Crossfire Geometry Foundation

Status: infrastructure only. **Formation generation is OFF** (script default and MainBuild scene).
With it off, seed 12345 reproduces the approved MainBuild layout bit-for-bit (fingerprint `e71caf6c…`).
No Flak firing, targeting, damage or troop AI exists. No base is moved or rotated.

> The range table below **supersedes** every earlier range table, including the one in
> `BaseScaling-Levels-Stage1.md`.

## 1. Flak range configuration — `FlakRangeTable`

Final approved values, stored as **Strategy Combat (SC) reference units**:

| Level | Min (SC) | Max (SC) | Band |
|---|---:|---:|---:|
| L1 | 12 | 16 | 4 |
| L2 | 14 | 18 | 4 |
| L3 | 16 | 20 | 4 |
| L4 | 18 | 22 | 4 |
| L5 | 20 | 24 | 4 |
| L6 | 22 | 26 | 4 |

* `UnityRange = SCRange × S`. **S = 1.0 is PROVISIONAL** (geometric testing only, not a calibration).
* S is configured in one place: `BaseSpawner ▸ Strategic Formations ▸ crossfireRanges ▸ scToUnity`
  (default `FlakRangeTable.ProvisionalScToUnity`). Changing S needs no terrain regeneration and never
  touches levels, counts or placement.
* The approved SC numbers exist once in code (`FlakRangeTable`), so no other script hard-codes them.
* Units are explicit: `FlakRangeBand.Units` is `StrategyCombatReference` or `UnityWorld`;
  `CrossfireGeometry.Classify` refuses SC bands.
* `GetRangeSC/GetRangeUnity(level)` throw for undefined levels (anything other than L1–L6) or an
  invalid table, and `TryGet…` returns false. `Validate()` checks for six levels, finite values,
  0 ≤ min < max, and S > 0. `WithScToUnity(s)` returns an analysis copy.

## 2. Attack-position model — `CrossfireAttackModel` (Unity units, provisional)

* Front attack position = base centre + horizontal forward × **3.3** (at the centre's height).
  Forward = base local +Z (the spawner's turret-facing convention), flattened onto XZ.
* Analysis may extend the approach by up to **+10** units along the same direction
  (`maxExtraApproach`). This is not an assumed attack distance.
* Ranges are measured from the **supporting base's centre** with **full 3D distance**. Bands are
  inclusive at both ends. Individual Flak muzzles are not used.

## 3. Geometry service — `CrossfireGeometry` (static, pure)

`FrontAttackPosition`, `Distance3D`, `Classify` (InsideMinimum / InBand / BeyondMaximum),
`Evaluate`, `CanEngage`, `CouldBothEngage`, `EngagingBases`, `ApproachCoverage` (the exact
extra-approach intervals where a supporter can engage, solved analytically), `SharedApproachCoverage`.
`CrossfireBase.FromRecord/FromPose` adapt generated bases. It never uses `UnityEngine.Random`.

## 4. Formation planner — `StrategicFormationPlanner`

* Input: `FormationCandidate` (id, type, level, route flag, has-front flag), `FormationPlannerSettings`,
  range table and attack model. Output: a `FormationPlan` of `FormationSpec`s. Each spec carries an id
  (`FORM_PAIR_001`, `FORM_CL3_001`, `FORM_CL4_001`), a kind, members with role (Anchor / Partner /
  Supporter), slot offset, local yaw and intended approach, and `FormationSupportLink`s (which member
  can engage which member's front, with coverage intervals).
* Deterministic: candidates are sorted ordinally and shuffled with a private SplitMix64 stream salted
  with `IRONVEIL.Formations.v1` (the same approach as level assignment). It never touches
  `UnityEngine.Random`.
* Exclusions (Stage 2a decisions):
  * Tank: no "Base Flaks" group, so no defensive front.
  * Route bases (naval/rail): they keep their route-facing orientation and hangar placement.
  * Levels outside `eligibleLevels` (L5/L6 by default).
* Slot templates: facing pair (members at ±d/2, yaw 0/180), `RadialInward`, `RadialOutward`,
  `LineAbreast`. Spacing 0 (the default) means the geometry is unresolved, so no offsets or links are invented.
* Formation counts default to 0. Nothing consumes the plan yet.

## 5. Placement validation refactor (behaviour-preserving)

`TryFindLandPlacement` (signature unchanged) now calls:
1. `TryPassLandSiteChecks`: river clearance, railway clearance and measured footprint (before the yaw draw, as before).
2. `TryEvaluateLandPose(…, worldXZ, yaw, …)`: terrain-aligned footprint sampling, flatness, slope,
   pivot spacing, footprint clearance and corridor clearance. It never touches `UnityEngine.Random`.

On any rejection the caller restores `Random.state` to its post-XZ value, and on success it keeps the
yaw draw. This is exactly the previous stream behaviour.

`BaseSpawner.ProbeLandPose(type, worldXZ, yaw, out result[, level])` tests one proposed land pose
with those same checks against the bases placed so far. It instantiates nothing, records no
diagnostics and leaves `Random.state` untouched. Route bases are refused, because they are positioned
by route sampling (hangar over water / on the track), and that path is unchanged.

## 6. Formation switch

`generateStrategicFormations` (default **false**). The switch is not enabled in any scene.
**Superseded by Stage 2b** (`Crossfire-Formations-Stage2b.md`): when ON, it now places 7–8
L5/L6 facing pairs before ordinary placement, so the layout changes.

## 7. Stage 2a geometry findings (final ranges)

The defended base is at the origin facing +Z, with the attack point at 3.3 (+e). Tables give the
supporter's centre-to-centre distance windows on a flat plane. The physical floor for a land pair (real 6.5×
footprint, 4-unit edge clearance, 10-unit pivot spacing) is **≈10.5 (aligned) to ≈13.9 (worst yaw)**.

Facing pair, e = 0: d ∈ [min·S + 3.3, max·S + 3.3]. Examples: L5 at S=1 is 23.3–27.3, and L6 at S=1 is 25.3–29.3.
A facing supporter can never cover the whole 10-unit approach (band 4·S < 10 for S < 2.5),
because advancing troops close on it 1:1 and enter its minimum ring.

The 90° flank is the only single-supporter geometry that covers the full approach at S ≥ 1. Examples:
L5 at S=1 is 19.73–19.97, at S=1.5 it is 29.82–33.45, and at S=2 it is 39.87–46.12.

Full tables are in the Stage 2a report.
