# IRONVEIL World-Generation Test Prototype

Open `Ironveil - WorldGeneration Test` and select its `World Generation
Prototype` object. In Edit Mode, **Generate / Refresh Terrain Preview** builds a
transient Scene-view preview and frames the complete world; **Clear Terrain,
Guides, and Generated Bases** removes it. Nothing is written into MainBuild.
Entering Play Mode generates the terrain, visible guide routes, and—when the
scene flag is enabled—the production route generators and BaseSpawner. The
report is logged, displayed in the Game view, stored on the prototype component,
and written under `Application.persistentDataPath` when the platform allows it.

The default recipe is seed `12345`, a 1200 x 1200-unit world, and a 4 x 4 grid
of 300-unit terrain tiles at 513 height samples per tile. Chunk border samples
come from one global, stateless height function. Ridge, peak, river-valley,
rail-valley, plain, and detail features use independent deterministic streams.
Change the configuration asset's seed or tunable controls to explore layouts.

## What the report means

- Terrain edge and collider checks are measured from the generated `TerrainData`
  and `TerrainCollider` instances.
- Land sites use a conservative union envelope of measured renderer bounds among the configured
  Tank/Heli/Air prefabs, the BaseSpawner's nine-point footprint sampling layout,
  the 0.30 m height-variation limit, 30-degree maximum sampled slope, 20-degree
  land tilt limit, 10 m pivot spacing, 8 m world-edge padding, and the current
  3 m river width plus 15 m land-river clearance.
- Naval and Rail numbers are **geometry-screened guide-slot estimates**, using
  measured renderer bounds and mirrored current corridor/spacing limits. They
  are not successful placements produced by the existing `BaseSpawner`. Family
  capacities are screened independently; cross-family request ordering and
  allocation conflicts are not simulated.
- Meeting 322/53/35 means only that the prototype screening thresholds were
  met. It is not certification that the unchanged 350-request BaseSpawner run
  will succeed. Full certification requires a later test with the exact route
  generators and unchanged BaseSpawner validation.

## Integration constraints intentionally left untouched

`BaseSpawner`, `RiverGenerator`, and `RailGenerator` each currently receive a
single serialized `Collider`. This prototype creates multiple TerrainCollider
components, so those systems are not wired to it. A later phase must provide a
tile-aware terrain-sampling API or another explicitly validated collider
strategy, and feed the guide splines into route generation. No existing
spawning, route, scene, or prefab asset is changed here. The guide paths are
available from `RiverGuideControlPoints`, `RailGuideControlPoints`, and
`PotentialGuideCrossings`; the last list identifies candidate future bridge or
grade-separation locations, not built crossings.

The existing mountain FBX prefabs are not used as the macro terrain surface:
they are isolated meshes with colliders rather than editable heightfields.
The prototype's runtime TerrainLayer reuses the project's snowy albedo and
normal textures and the `M_SnowGround` material tint. No detailed vegetation,
rocks, water mesh, rail mesh, or streaming system is generated in this phase.
