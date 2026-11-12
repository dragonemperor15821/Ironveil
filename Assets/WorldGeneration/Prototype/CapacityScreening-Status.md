# Capacity Screening Status — Seed 12345

**Status: screening executed in Unity Editor batch mode on an isolated project
copy.** This was not Play Mode and did not call the production BaseSpawner.

The prototype component produced the following report for seed `12345`, using
the default 1200 x 1200 map, 4 x 4 chunks, and the existing MainBuild placement
thresholds where mirrored:

| Check | Result | Interpretation |
|---|---:|---|
| Terrain chunks generated | 16/16 | Measured in the isolated editor scene |
| Heightmap resolution | 513 per chunk | Configuration used |
| Seed/data sample check | PASS; max data-vs-seed error 0.00427 m | Measured on 257 samples |
| Shared chunk-edge height difference | 0.000000 m | Measured from adjacent TerrainData borders |
| TerrainCollider/TerrainData matches | 16/16 | Measured component references |
| Potential river/rail crossings | 6 | Guide intersections; bridge geometry not generated |
| Land sites | 4,867 / 322 target | Conservative renderer-envelope, nine-point terrain screen |
| Naval slots | 910 / 53 target | Geometry-screened estimate, not a BaseSpawner placement run |
| Rail slots | 448 / 35 target | Geometry-screened estimate, not a BaseSpawner placement run |

All three **screening** targets were met. This does not certify that BaseSpawner
will place all 350 bases: Naval/Rail footprints use mirrored corridor rules,
the three family capacities are screened independently, and final route
meshes, cross-family allocation, ordering, and the production placement loop
were not run. No actual 350-root spawn result is claimed.

The prototype component also produces a fresh run-time report and saves it to
`Application.persistentDataPath` when the test scene is played. It reports:

- generated chunk count, seed/data reproducibility sample error, maximum shared
  border height difference, and TerrainCollider/TerrainData matches;
- conservative land-site capacity based on measured prefab bounds and the
  current footprint/slope/flatness/spacing/river-clearance thresholds;
- estimated Naval and Rail guide-slot capacities based on measured prefabs,
  continuous guide paths, route spacing, bank/track geometry, and current
  terrain thresholds.

The screening targets remain 322 land candidates, 53 Naval candidates, and 35
Rail candidates. Passing those screens does not prove that the unchanged
BaseSpawner will place all 350 requests.
