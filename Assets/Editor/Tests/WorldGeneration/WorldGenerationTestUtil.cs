using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Ironveil.WorldGeneration.Tests
{
    /// <summary>
    /// Stage 9: helpers for comparing generated worlds in Play Mode.
    ///
    /// World Explorer applies its startup seed after the initial generation: without a seed in the page URL
    /// (always the case in the editor) ExplorerWorldManager regenerates the bases with a NEW RANDOM seed one frame
    /// after GenerationCompleted. Tests must therefore (a) capture the initial world inside the GenerationCompleted
    /// event, and (b) wait until ExplorerWorldManager is Ready before calling RegenerateBases themselves. Otherwise
    /// the startup regeneration replaces the world between a test's regeneration and its capture.
    /// </summary>
    public static class WorldGenerationTestUtil
    {
        public const string WorldExplorerPath = "Assets/Scenes/Ironveil - World Explorer.unity";
        public const string MainBuildPath = "Assets/Scenes/Ironveil - MainBuild.unity";
        private const float TimeoutSeconds = 300f;

        public static readonly FieldInfo SpawnHomeField =
            typeof(BaseSpawner).GetField("spawnPlayerHomeBase", BindingFlags.Instance | BindingFlags.NonPublic);

        public sealed class BaseRecord
        {
            public string id, root, variant, baseName, level;
            public Vector3 position, scale;
            public Quaternion rotation;
            public bool hasHangar;
            public Vector3 hangarPosition, hangarScale;
            public Quaternion hangarRotation;

            public bool SameAs(BaseRecord o) =>
                o != null && id == o.id && root == o.root && variant == o.variant && baseName == o.baseName && level == o.level &&
                Exact(position, o.position) && Exact(scale, o.scale) && Exact(rotation, o.rotation) &&
                hasHangar == o.hasHangar && (!hasHangar ||
                    (Exact(hangarPosition, o.hangarPosition) && Exact(hangarScale, o.hangarScale) && Exact(hangarRotation, o.hangarRotation)));

            public override string ToString() => $"{id} {variant} {level} pos ({position.x:R}, {position.y:R}, {position.z:R}) yaw {rotation.eulerAngles.y:R}" +
                                                 (hasHangar ? $" hangar ({hangarPosition.x:R}, {hangarPosition.y:R}, {hangarPosition.z:R})" : "");
        }

        public sealed class WorldSnapshot
        {
            public int seed;
            public readonly Dictionary<string, BaseRecord> standard = new Dictionary<string, BaseRecord>(StringComparer.Ordinal);
            public BaseRecord home;
            public int totalBases;
            public readonly List<string> rejections = new List<string>();
        }

        private static bool Exact(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        private static bool Exact(Quaternion a, Quaternion b) => a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;

        private static BaseRecord Record(Transform root, BaseController c)
        {
            BaseLevelInfo level = c.GetComponent<BaseLevelInfo>();
            Transform hangar = root.Cast<Transform>().FirstOrDefault(t => t.name == "Naval Hangar" || t.name == "Rail Hangar");
            string rest = root.name.StartsWith("GeneratedBase_") ? root.name.Substring("GeneratedBase_".Length) : root.name;
            int us = rest.LastIndexOf('_');
            return new BaseRecord
            {
                id = c.BaseId,
                root = root.name,
                variant = us > 0 ? rest.Substring(0, us) : rest,
                baseName = c.BaseName,
                level = level != null ? level.Level.ToString() : "-",
                position = c.transform.position,
                rotation = c.transform.rotation,
                scale = c.transform.lossyScale,
                hasHangar = hangar != null,
                hangarPosition = hangar != null ? hangar.position : default,
                hangarRotation = hangar != null ? hangar.rotation : default,
                hangarScale = hangar != null ? hangar.lossyScale : default,
            };
        }

        /// <summary>Records every generated base under the spawner (active roots only; removed roots are inactive and detached).</summary>
        public static WorldSnapshot Capture(BaseSpawner spawner, int seed, IEnumerable<string> rejections = null)
        {
            var snap = new WorldSnapshot { seed = seed };
            if (rejections != null) snap.rejections.AddRange(rejections);
            foreach (Transform root in spawner.transform)
            {
                if (!root.gameObject.activeInHierarchy || !root.name.StartsWith("GeneratedBase_")) continue;
                BaseController c = root.GetComponentInChildren<BaseController>(true);
                if (c == null) continue;
                snap.totalBases++;
                BaseRecord r = Record(root, c);
                if (root.name == BaseSpawner.HomeBaseObjectName)
                {
                    Assert.IsNull(snap.home, "more than one Home Base root");
                    snap.home = r;
                }
                else
                {
                    Assert.IsFalse(snap.standard.ContainsKey(r.id), "duplicate base ID " + r.id);
                    snap.standard.Add(r.id, r);
                }
            }
            return snap;
        }

        /// <summary>
        /// Loads a gameplay scene and captures its INITIAL generation inside GenerationCompleted (before any startup
        /// regeneration), then waits until the scene is idle (World Explorer: ExplorerWorldManager Ready).
        /// </summary>
        public static IEnumerator Load(string path, bool? forceHomeBase, Action<WorldSnapshot> onInitial)
        {
            Scene scene = EditorSceneManager.LoadSceneInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Single));
            float start = Time.realtimeSinceStartup;
            WorldGenerationPrototype proto = null;
            BaseSpawner spawner = null;
            var rejections = new List<string>();
            WorldSnapshot initial = null;
            Action<string, string> onRejected = (variant, reason) => rejections.Add(variant + ": " + reason);
            Action<WorldGenerationPrototype> onCompleted = p => initial = Capture(spawner, p.CurrentBaseSeed, rejections);

            while (proto == null || spawner == null)
            {
                if (scene.isLoaded)
                {
                    proto = Object.FindAnyObjectByType<WorldGenerationPrototype>();
                    spawner = Object.FindAnyObjectByType<BaseSpawner>();
                }
                if (Time.realtimeSinceStartup - start > TimeoutSeconds) Assert.Fail(path + ": generator not found");
                if (proto == null || spawner == null) yield return null;
            }

            Assert.AreNotEqual(WorldGenerationPrototype.WorldGenerationPhase.Completed, proto.Phase, "subscribed before the initial generation completed");
            if (forceHomeBase.HasValue) SpawnHomeField.SetValue(spawner, forceHomeBase.Value);
            spawner.PlacementRejected += onRejected;
            proto.GenerationCompleted += onCompleted;
            try
            {
                while (initial == null)
                {
                    if (proto.Phase == WorldGenerationPrototype.WorldGenerationPhase.Failed) Assert.Fail(path + ": world generation failed");
                    if (Time.realtimeSinceStartup - start > TimeoutSeconds) Assert.Fail(path + ": world generation did not complete");
                    yield return null;
                }
            }
            finally
            {
                spawner.PlacementRejected -= onRejected;
                proto.GenerationCompleted -= onCompleted;
            }

            yield return WaitIdle(path);
            onInitial?.Invoke(initial);
        }

        /// <summary>
        /// Waits for the FINAL world of a freshly loaded gameplay scene: the scene is loaded, the world generation has
        /// finished (Completed, or BasesRegenerated after a startup regeneration), and, in World Explorer, the startup
        /// seed has been applied (ExplorerWorldManager Ready). MainBuild has no startup regeneration, so nothing extra
        /// is awaited there. An explicit readiness condition, no fixed frame delays.
        /// </summary>
        public static IEnumerator WaitForFinalWorld(Scene scene, string label)
        {
            float start = Time.realtimeSinceStartup;
            WorldGenerationPrototype proto = null;
            while (true)
            {
                if (scene.isLoaded && proto == null) proto = Object.FindAnyObjectByType<WorldGenerationPrototype>();
                if (proto != null)
                {
                    WorldGenerationPrototype.WorldGenerationPhase phase = proto.Phase;
                    if (phase == WorldGenerationPrototype.WorldGenerationPhase.Failed) Assert.Fail(label + ": world generation failed");
                    if (phase == WorldGenerationPrototype.WorldGenerationPhase.Completed ||
                        phase == WorldGenerationPrototype.WorldGenerationPhase.BasesRegenerated ||
                        phase == WorldGenerationPrototype.WorldGenerationPhase.BaseRegenerationFailed)
                        break;
                }
                if (Time.realtimeSinceStartup - start > TimeoutSeconds) Assert.Fail(label + ": world generation did not complete");
                yield return null;
            }
            yield return WaitIdle(label);
        }

        /// <summary>Waits until no startup or other regeneration is pending (World Explorer: ExplorerWorldManager Ready).</summary>
        public static IEnumerator WaitIdle(string label)
        {
            float start = Time.realtimeSinceStartup;
            ExplorerWorldManager explorer = Object.FindAnyObjectByType<ExplorerWorldManager>();
            while (explorer != null && explorer.CurrentState != ExplorerWorldManager.State.Ready)
            {
                if (explorer.CurrentState == ExplorerWorldManager.State.Failed) Assert.Fail(label + ": World Explorer failed: " + explorer.LastError);
                if (Time.realtimeSinceStartup - start > TimeoutSeconds) Assert.Fail(label + ": World Explorer did not become ready");
                yield return null;
            }
            ResourceManagerReady();
        }

        private static void ResourceManagerReady()
        {
            Assert.IsNotNull(Ironveil.Economy.ResourceManager.Instance);
            Assert.IsTrue(Ironveil.Economy.ResourceManager.Instance.IsInitialized);
        }

        /// <summary>Regenerates the bases with <paramref name="seed"/> and captures the result synchronously.</summary>
        public static WorldSnapshot Regenerate(WorldGenerationPrototype proto, BaseSpawner spawner, int seed)
        {
            var rejections = new List<string>();
            Action<string, string> onRejected = (variant, reason) => rejections.Add(variant + ": " + reason);
            spawner.PlacementRejected += onRejected;
            try
            {
                Assert.IsTrue(proto.RegenerateBases(seed), $"RegenerateBases({seed}) produced exactly the requested bases");
            }
            finally
            {
                spawner.PlacementRejected -= onRejected;
            }
            return Capture(spawner, proto.CurrentBaseSeed, rejections);
        }

        public static int CountDifferent(WorldSnapshot a, WorldSnapshot b) =>
            a.standard.Count(kv => !b.standard.TryGetValue(kv.Key, out BaseRecord o) || !kv.Value.SameAs(o));

        /// <summary>Exact equality of all standard bases (IDs, types, levels, positions, orientations, scales, hangars) and placement failures.</summary>
        public static void AssertSameStandard(WorldSnapshot a, WorldSnapshot b, string message, bool compareRejections = true)
        {
            Assert.AreEqual(350, a.standard.Count, message + ": standard bases (a)");
            Assert.AreEqual(350, b.standard.Count, message + ": standard bases (b)");
            CollectionAssert.AreEquivalent(a.standard.Keys, b.standard.Keys, message + ": base IDs");
            foreach (var kv in a.standard)
                Assert.IsTrue(kv.Value.SameAs(b.standard[kv.Key]), $"{message}: {kv.Key}\n  a: {kv.Value}\n  b: {b.standard[kv.Key]}");
            if (compareRejections)
                CollectionAssert.AreEqual(a.rejections, b.rejections, message + ": the same failed placement attempts, in the same order");
        }

        public static void AssertSameWorld(WorldSnapshot a, WorldSnapshot b, string message)
        {
            AssertSameStandard(a, b, message);
            Assert.AreEqual(a.home != null, b.home != null, message + ": Home Base presence");
            if (a.home != null) Assert.IsTrue(a.home.SameAs(b.home), $"{message}: Home Base\n  a: {a.home}\n  b: {b.home}");
            Assert.AreEqual(a.totalBases, b.totalBases, message + ": total bases");
        }

        public static void AssertDistribution(WorldSnapshot s)
        {
            var expected = new Dictionary<string, int>
            {
                { "Tank", 110 }, { "Heli", 90 }, { "Air", 80 }, { "Naval", 25 }, { "Rail", 17 },
                { "Heli Naval", 11 }, { "Heli Rail", 7 }, { "Air Naval", 6 }, { "Air Rail", 4 },
            };
            foreach (var kv in expected)
                Assert.AreEqual(kv.Value, s.standard.Values.Count(r => r.variant == kv.Key), kv.Key);
        }
    }
}
