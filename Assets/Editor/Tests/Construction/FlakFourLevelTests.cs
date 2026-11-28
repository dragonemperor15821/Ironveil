using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Ironveil.Construction.Tests
{
    /// <summary>
    /// Stage 6: the four-level Flak registration and model mapping, the per-level range bands, and the
    /// crossfire validation of the ORIGINAL strategic formations (OriginalCrossfireFormations) at the new
    /// Flak levels. Edit Mode; nothing in the project is modified.
    /// </summary>
    public class FlakFourLevelTests
    {
        private const string L1PrefabPath = "Assets/Prefabs/Buildings/Level 1/Level 1 Flak.prefab";
        private const string L2PrefabPath = "Assets/Prefabs/Buildings/Level 2/Level 2 Flak.prefab";
        private const string L3PrefabPath = "Assets/Prefabs/Buildings/Level 3/Level 3 Flak.prefab";
        private const string L4ModelPath = "Assets/TripoModels/L6 Base Turret/mecha_tank_3d_model.fbx";
        private const string L2FbxPath = "Assets/TripoModels/Level 2 Base Flak/tripo_convert_11b3dd5d-015b-4120-8a05-f9c624370aa8.fbx";
        private const string L3FbxPath = "Assets/TripoModels/Level 3 Base Flak/tripo_convert_f96e9d7a-a615-4f0d-919f-466ab602db7e.fbx";
        private const string L1PrefabGuid = "a7117640cc68c364c9e22dcab487dbd3";
        private const string L3PrefabGuid = "6aca80d623f285245887de604db6575f"; // the former "Level 2 Flak" prefab
        private const string L4ModelGuid = "39b746d7ee7015d438bccec3b335becf";  // the L6 Base Turret FBX
        private const float ReservedFootprint = 0.05f;

        private BuildingDefinition _flak;

        [SetUp]
        public void SetUp()
        {
            _flak = Resources.Load<ConstructionSettings>(ConstructionSettings.ResourcePath).BuildingCatalog.Get(BuildingType.Flak);
            Assert.IsNotNull(_flak);
        }

        // ------------------------------------------------------------------ registration / mapping

        [Test]
        public void FlakAsset_RegistersExactlyFourLevels_WithTheApprovedModels()
        {
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, _flak.Levels.Select(l => l.level).ToArray());
            Assert.AreEqual(4, _flak.HighestAvailableLevel);
            Assert.IsFalse(_flak.HasLevel(5), "no fifth level");
            Assert.IsFalse(_flak.HasLevel(0));

            Assert.AreEqual(L1PrefabPath, AssetDatabase.GetAssetPath(_flak.GetPrefab(1)));
            Assert.AreEqual(L1PrefabGuid, AssetDatabase.AssetPathToGUID(L1PrefabPath), "L1 prefab not replaced");
            Assert.AreEqual(L2PrefabPath, AssetDatabase.GetAssetPath(_flak.GetPrefab(2)));
            Assert.AreEqual(L3PrefabPath, AssetDatabase.GetAssetPath(_flak.GetPrefab(3)));
            Assert.AreEqual(L3PrefabGuid, AssetDatabase.AssetPathToGUID(L3PrefabPath), "L3 = the existing former-L2 prefab, moved, not duplicated");
            Assert.AreEqual(L4ModelPath, AssetDatabase.GetAssetPath(_flak.GetPrefab(4)));
            Assert.AreEqual(L4ModelGuid, AssetDatabase.AssetPathToGUID(L4ModelPath), "L4 = the existing L6 Base Turret model");

            // Each level's prefab is a variant of the expected source model.
            Assert.AreEqual(L2FbxPath, AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromOriginalSource(_flak.GetPrefab(2))), "L2 = the NEW Tripo export");
            Assert.AreEqual(L3FbxPath, AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromOriginalSource(_flak.GetPrefab(3))));

            Assert.AreEqual(-90f, _flak.GetFacingYawOffset(1));
            Assert.AreEqual(0f, _flak.GetFacingYawOffset(2), "new L2 barrels run along +Z");
            Assert.AreEqual(0f, _flak.GetFacingYawOffset(3));
            Assert.AreEqual(0f, _flak.GetFacingYawOffset(4));
            Assert.AreEqual(0.05f, _flak.InstanceScale);
            Assert.AreEqual(28, _flak.MaxCount);
            StringAssert.Contains("four-level", _flak.MissingLevelsNote);
            StringAssert.DoesNotContain("three-level", _flak.MissingLevelsNote);
        }

        [Test]
        public void EveryLevelFits_TheReservedFlakFootprint_AtItsFacing()
        {
            foreach (BuildingDefinition.LevelModel lv in _flak.Levels)
            {
                Assert.IsTrue(ModularFoundationGeometry.TryModelFootprint(lv.prefab, _flak.InstanceScale, out Vector2 size, out Vector2 off,
                    out _, out float minY, lv.facingYawOffset), "L" + lv.level);
                Vector2 effective = size + 2f * new Vector2(Mathf.Abs(off.x), Mathf.Abs(off.y));
                Assert.LessOrEqual(effective.x, ReservedFootprint + 1e-5f, "L" + lv.level + " width");
                Assert.LessOrEqual(effective.y, ReservedFootprint + 1e-5f, "L" + lv.level + " depth");
                Assert.AreEqual(0f, minY, 1e-4f, "L" + lv.level + " pivot at the model bottom");
            }
        }

        [Test]
        public void NewL2Prefab_FollowsTheL1Recipe()
        {
            GameObject l1 = _flak.GetPrefab(1), l2 = _flak.GetPrefab(2);
            Assert.AreEqual(PrefabAssetType.Variant, PrefabUtility.GetPrefabAssetType(l2));
            Assert.AreEqual(l1.transform.localRotation, l2.transform.localRotation, "imported root rotation (X 270)");
            Assert.AreEqual(Vector3.one, l2.transform.localScale, "no baked scaling");
            Assert.AreEqual("Level 2 Flak", l2.name);
            Assert.AreEqual("Level 3 Flak", _flak.GetPrefab(3).name, "former L2 prefab renamed with its level");
            Material m = l2.GetComponentInChildren<Renderer>().sharedMaterial;
            Assert.AreEqual("Level_2_Base_Flak", m.name);
            Assert.IsNotNull(m.GetTexture("_BaseMap"), "BaseMap set (Tripo import pitfall)");
            Assert.IsNotNull(m.GetTexture("_MetallicGlossMap"));
            ModelImporter imp = (ModelImporter)AssetImporter.GetAtPath(L2FbxPath);
            Assert.AreEqual(1f, imp.globalScale);
            Assert.IsTrue(imp.useFileScale);
        }

        [Test]
        public void BuilderSpec_MatchesTheAsset_SoARebuildKeepsFourLevels()
        {
            ModularFoundationBuilder.BuildingSpec spec = ModularFoundationBuilder.Buildings.Single(s => s.type == BuildingType.Flak);
            CollectionAssert.AreEqual(new[] { 2, 3, 4 }, spec.higherLevels);
            CollectionAssert.AreEqual(new[] { L2PrefabPath, L3PrefabPath, L4ModelPath }, spec.higherModels);
            Assert.AreEqual(-90f, spec.level1FacingYawOffset);
            StringAssert.EndsWith(spec.note, _flak.MissingLevelsNote, "asset note = builder-generated note");
            StringAssert.StartsWith("Models present for building levels 1, 2, 3, 4.", _flak.MissingLevelsNote);
        }

        // ------------------------------------------------------------------ ranges

        [TestCase(1, 16f, 20f)]
        [TestCase(2, 18f, 22f)]
        [TestCase(3, 20f, 24f)]
        [TestCase(4, 22f, 26f)]
        public void Ranges_AreTheApprovedBands(int level, float min, float max)
        {
            FlakRangeBand sc = FlakLevelRanges.GetRangeSC(level);
            Assert.AreEqual(min, sc.Minimum);
            Assert.AreEqual(max, sc.Maximum);
            Assert.AreEqual(RangeUnits.StrategyCombatReference, sc.Units);
            Assert.AreEqual(4f, sc.Width, "every band is 4 wide");

            FlakRangeBand unity = FlakLevelRanges.GetRangeUnity(level);
            Assert.AreEqual(RangeUnits.UnityWorld, unity.Units);
            Assert.AreEqual(min * FlakRangeTable.ProvisionalScToUnity, unity.Minimum);
            Assert.AreEqual(max * FlakRangeTable.ProvisionalScToUnity, unity.Maximum);
            FlakRangeBand scaled = FlakLevelRanges.GetRangeUnity(level, 1.5f);
            Assert.AreEqual(min * 1.5f, scaled.Minimum, 1e-5f);
            Assert.AreEqual(max * 1.5f, scaled.Maximum, 1e-5f);
        }

        [Test]
        public void Ranges_ShiftOutwardByTwoPerLevel_AndOverlap()
        {
            for (int level = 2; level <= FlakUpgradeRules.MaxLevel; level++)
            {
                FlakRangeBand lo = FlakLevelRanges.GetRangeSC(level - 1), hi = FlakLevelRanges.GetRangeSC(level);
                Assert.AreEqual(FlakLevelRanges.LevelStepSC, hi.Minimum - lo.Minimum);
                Assert.AreEqual(FlakLevelRanges.LevelStepSC, hi.Maximum - lo.Maximum);
                Assert.Less(hi.Minimum, lo.Maximum, "successive bands overlap");
            }

            Assert.AreEqual(2f, FlakLevelRanges.LevelStepSC);
            Assert.AreEqual(4f, FlakLevelRanges.BandWidthSC);
        }

        [TestCase(0)]
        [TestCase(5)]
        [TestCase(-1)]
        public void Ranges_RejectLevelsOutsideL1ToL4(int level)
        {
            Assert.IsFalse(FlakLevelRanges.TryGetRangeSC(level, out _));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => FlakLevelRanges.GetRangeSC(level));
            Assert.Throws<System.ArgumentOutOfRangeException>(() => FlakLevelRanges.GetRangeUnity(level));
        }

        [Test]
        public void Ranges_FlakLevelN_EqualsFormationLevelNPlus2_AndTheFormationTableIsUnchanged()
        {
            for (int level = 1; level <= 4; level++)
            {
                BaseLevel formation = FlakLevelRanges.EquivalentFormationLevel(level);
                Assert.AreEqual(level + 2, (int)formation);
                FlakRangeBand a = FlakLevelRanges.GetRangeSC(level), b = FlakRangeTable.ApprovedRangeSC(formation);
                Assert.AreEqual(b.Minimum, a.Minimum, "L" + level);
                Assert.AreEqual(b.Maximum, a.Maximum, "L" + level);
            }

            Assert.AreEqual(22f, FlakRangeTable.ApprovedRangeSC(BaseLevel.L6).Minimum, "Flak L4 inherits the original L6 band");
            Assert.AreEqual(26f, FlakRangeTable.ApprovedRangeSC(BaseLevel.L6).Maximum);
            float[] expMin = { 12f, 14f, 16f, 18f, 20f, 22f }, expMax = { 16f, 18f, 20f, 22f, 24f, 26f };
            for (int lv = 1; lv <= 6; lv++)
            {
                Assert.AreEqual(expMin[lv - 1], FlakRangeTable.ApprovedRangeSC((BaseLevel)lv).Minimum, "formation table L" + lv);
                Assert.AreEqual(expMax[lv - 1], FlakRangeTable.ApprovedRangeSC((BaseLevel)lv).Maximum, "formation table L" + lv);
            }

            Assert.AreEqual(1f, FlakRangeTable.ProvisionalScToUnity, "S still the provisional 1.0");
        }

        [Test]
        public void Ranges_AreFull3D_FromTheBaseCentre_InclusiveAtBothEnds()
        {
            Vector3 centre = new Vector3(10f, 5f, -3f);
            // L4: 22..26
            Assert.AreEqual(RangeBandStatus.InBand, FlakLevelRanges.Classify(4, centre, centre + new Vector3(0f, 0f, 22f)));
            Assert.AreEqual(RangeBandStatus.InBand, FlakLevelRanges.Classify(4, centre, centre + new Vector3(26f, 0f, 0f)));
            Assert.AreEqual(RangeBandStatus.InsideMinimum, FlakLevelRanges.Classify(4, centre, centre + new Vector3(0f, 0f, 21.99f)));
            Assert.AreEqual(RangeBandStatus.BeyondMaximum, FlakLevelRanges.Classify(4, centre, centre + new Vector3(0f, 0f, 26.01f)));
            // Horizontal 25 (in band) but 10 higher: 3D 26.93 > 26.
            Assert.AreEqual(RangeBandStatus.BeyondMaximum, FlakLevelRanges.Classify(4, centre, centre + new Vector3(0f, 10f, 25f)));
            // Horizontal 21 (inside minimum) but 8 higher: 3D 22.47 -> in band.
            Assert.IsTrue(FlakLevelRanges.CanEngage(4, centre, centre + new Vector3(21f, 8f, 0f)));
            // The same point by level: 19 is L1 and L2 only.
            Vector3 p19 = centre + new Vector3(0f, 0f, 19f);
            CollectionAssert.AreEqual(new[] { true, true, false, false },
                Enumerable.Range(1, 4).Select(l => FlakLevelRanges.CanEngage(l, centre, p19)).ToArray());
        }

        [Test]
        public void BaseFlakDefence_ReportsTheBandOfItsCurrentLevel_FromItsRootPosition()
        {
            var go = new GameObject("range probe");
            try
            {
                go.transform.position = new Vector3(100f, 2f, 50f);
                BaseFlakDefence d = go.AddComponent<BaseFlakDefence>();
                Assert.AreEqual(1, d.FlakLevel);
                Assert.AreEqual(go.transform.position, d.BaseCentre);
                Assert.AreEqual(16f, d.RangeSC.Minimum);
                Assert.AreEqual(20f, d.RangeUnity.Maximum);
                Assert.IsTrue(d.CanEngage(go.transform.position + new Vector3(0f, 0f, 18f)));
                Assert.IsFalse(d.CanEngage(go.transform.position + new Vector3(0f, 0f, 21f)));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ------------------------------------------------------------------ crossfire (original geometry)

        private struct Member
        {
            public string mode, kind, formation, role, id;
            public int seed, level;
            public Vector3 centre;
            public float heading;
        }

        private static List<Member> LoadFormations()
        {
            var list = new List<Member>();
            foreach (string row in OriginalCrossfireFormations.Rows)
            {
                string[] f = row.Split(',');
                list.Add(new Member
                {
                    mode = f[0], seed = int.Parse(f[1], CultureInfo.InvariantCulture), kind = f[2], formation = f[3], role = f[4], id = f[5],
                    level = int.Parse(f[6], CultureInfo.InvariantCulture),
                    centre = new Vector3(float.Parse(f[7], CultureInfo.InvariantCulture), float.Parse(f[8], CultureInfo.InvariantCulture), float.Parse(f[9], CultureInfo.InvariantCulture)),
                    heading = float.Parse(f[10], CultureInfo.InvariantCulture),
                });
            }

            return list;
        }

        private static Vector3 Front(Member m) => CrossfireGeometry.FrontAttackPosition(
            m.centre, CrossfireGeometry.ForwardFromYaw(m.heading), CrossfireAttackModel.CreateProvisional().FrontAttackDistance);

        /// <summary>Original design band of a formation member (formation table, base level L5/L6, S = 1).</summary>
        private static FlakRangeBand Original(Member m) => FlakRangeTable.CreateApproved().GetRangeUnity((BaseLevel)m.level);

        private static bool InBand(float d, FlakRangeBand b) => CrossfireGeometry.Classify(d, b) == RangeBandStatus.InBand;

        [Test]
        public void Crossfire_Dataset_IsTheOriginalSet()
        {
            List<Member> all = LoadFormations();
            var pairs = all.Where(m => m.kind == "pair").GroupBy(m => m.seed + m.formation).ToList();
            var clusters = all.Where(m => m.kind == "cluster").GroupBy(m => m.mode + m.seed + m.formation).ToList();
            Assert.AreEqual(29, pairs.Count, "7 + 7 + 7 + 8 facing pairs");
            Assert.IsTrue(pairs.All(p => p.Count() == 2));
            Assert.AreEqual(20, clusters.Count, "2 + 2 + 3 + 3 clusters in Modes C and D");
            Assert.AreEqual(64, clusters.Sum(c => c.Count()), "64 cluster fronts");
            Assert.AreEqual(3.3f, CrossfireAttackModel.CreateProvisional().FrontAttackDistance, "front attack position 3.3 ahead");
            Assert.AreEqual(10f, CrossfireAttackModel.CreateProvisional().MaxExtraApproach, "10-unit approach analysis");
            Assert.IsTrue(all.All(m => m.level == 5 || m.level == 6), "formations are L5/L6 bases");
        }

        [Test]
        public void Crossfire_ReproducesTheOriginalVerdicts_WithTheOriginalBands()
        {
            List<Member> all = LoadFormations();
            foreach (var pair in all.Where(m => m.kind == "pair").GroupBy(m => m.seed + " " + m.formation))
            {
                Member a = pair.First(m => m.role == "A"), b = pair.First(m => m.role == "B");
                float minO = Mathf.Max(Original(a).Minimum, Original(b).Minimum), maxO = Mathf.Min(Original(a).Maximum, Original(b).Maximum);
                float dAtA = CrossfireGeometry.Distance3D(b.centre, Front(a)), dAtB = CrossfireGeometry.Distance3D(a.centre, Front(b));
                Assert.IsTrue(dAtA >= minO && dAtA <= maxO && dAtB >= minO && dAtB <= maxO, $"{pair.Key}: {dAtA:F3} / {dAtB:F3} in [{minO}, {maxO}]");
                Assert.GreaterOrEqual(Vector2.Distance(new Vector2(a.centre.x, a.centre.z), new Vector2(b.centre.x, b.centre.z)), 10f, pair.Key + " pivot spacing 10");
            }

            foreach (var cluster in all.Where(m => m.kind == "cluster").GroupBy(m => m.mode + " " + m.seed + " " + m.formation))
            {
                foreach (Member target in cluster)
                {
                    int supporters = cluster.Count(s => s.id != target.id && InBand(CrossfireGeometry.Distance3D(s.centre, Front(target)), Original(s)));
                    Assert.GreaterOrEqual(supporters, 2, $"{cluster.Key} front of {target.id}");
                    foreach (Member other in cluster.Where(o => o.id != target.id))
                        Assert.GreaterOrEqual(Vector2.Distance(new Vector2(target.centre.x, target.centre.z), new Vector2(other.centre.x, other.centre.z)), 10f, "pivot spacing 10");
                }
            }
        }

        [Test]
        public void Crossfire_Clusters_KeepTwoSupportersAtEveryFront_WithEveryMemberAtL4()
        {
            FlakRangeBand l4 = FlakLevelRanges.GetRangeUnity(4);
            int fronts = 0;
            foreach (var cluster in LoadFormations().Where(m => m.kind == "cluster").GroupBy(m => m.mode + " " + m.seed + " " + m.formation))
            {
                foreach (Member target in cluster)
                {
                    fronts++;
                    int supporters = cluster.Count(s => s.id != target.id && InBand(CrossfireGeometry.Distance3D(s.centre, Front(target)), l4));
                    Assert.GreaterOrEqual(supporters, 2, $"{cluster.Key} front of {target.id} at L4");
                }
            }

            Assert.AreEqual(64, fronts);
        }

        [Test]
        public void Crossfire_PairsWithAnL6Member_KeepTheirCrossfireAtL4()
        {
            FlakRangeBand l4 = FlakLevelRanges.GetRangeUnity(4);
            int checkedPairs = 0;
            foreach (var pair in LoadFormations().Where(m => m.kind == "pair").GroupBy(m => m.seed + " " + m.formation))
            {
                Member a = pair.First(m => m.role == "A"), b = pair.First(m => m.role == "B");
                if (a.level == 5 && b.level == 5) continue;
                checkedPairs++;
                Assert.IsTrue(InBand(CrossfireGeometry.Distance3D(b.centre, Front(a)), l4), pair.Key + " B at A's front");
                Assert.IsTrue(InBand(CrossfireGeometry.Distance3D(a.centre, Front(b)), l4), pair.Key + " A at B's front");
            }

            Assert.AreEqual(24, checkedPairs, "8 L6/L6 + 16 mixed L5/L6 pairs");
        }

        [Test]
        public void Crossfire_L5L5Pairs_ExactlyThreeLoseTheirCrossfireAtL4_AllHoldAtTheMappedL3()
        {
            FlakRangeBand l3 = FlakLevelRanges.GetRangeUnity(3), l4 = FlakLevelRanges.GetRangeUnity(4);
            var failingAtL4 = new List<string>();
            int l5Pairs = 0;
            foreach (var pair in LoadFormations().Where(m => m.kind == "pair").GroupBy(m => m.seed + " " + m.formation))
            {
                Member a = pair.First(m => m.role == "A"), b = pair.First(m => m.role == "B");
                if (a.level != 5 || b.level != 5) continue;
                l5Pairs++;
                float dA = CrossfireGeometry.Distance3D(b.centre, Front(a)), dB = CrossfireGeometry.Distance3D(a.centre, Front(b));
                Assert.IsTrue(InBand(dA, l3) && InBand(dB, l3), pair.Key + ": original L5 geometry holds at the mapped Flak L3 (20-24)");
                if (!(InBand(dA, l4) && InBand(dB, l4)))
                {
                    failingAtL4.Add(pair.Key);
                    Assert.AreEqual(RangeBandStatus.InsideMinimum, CrossfireGeometry.Classify(Mathf.Min(dA, dB), l4), pair.Key + " fails by the L4 minimum");
                    Assert.GreaterOrEqual(Mathf.Min(dA, dB), 20f);
                }
            }

            Assert.AreEqual(5, l5Pairs);
            CollectionAssert.AreEquivalent(new[] { "12345 FORM_PAIR_003", "777 FORM_PAIR_002", "424242 FORM_PAIR_006" }, failingAtL4);
        }

        [Test]
        public void Crossfire_MappedLevels_ReproduceEveryOriginalVerdict()
        {
            // old L5 -> Flak L3 (20-24), old L6 -> Flak L4 (22-26): identical bands, identical results.
            foreach (Member m in LoadFormations())
            {
                FlakRangeBand mapped = FlakLevelRanges.GetRangeUnity(m.level - 2);
                Assert.AreEqual(Original(m).Minimum, mapped.Minimum, m.id);
                Assert.AreEqual(Original(m).Maximum, mapped.Maximum, m.id);
            }
        }
    }
}
