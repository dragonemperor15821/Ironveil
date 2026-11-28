using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using static Ironveil.WorldGeneration.Tests.WorldGenerationTestUtil;

namespace Ironveil.WorldGeneration.Tests
{
    /// <summary>
    /// Stage 9: world generation determinism in the REAL gameplay scenes, without warm-up generations. Compares
    /// actual generated bases (IDs, variants, levels, positions, orientations, scales, hangars) and the sequence of
    /// failed placement attempts.
    /// </summary>
    public class WorldGenerationDeterminismPlayModeTests
    {
        private const int OtherSeed = 777;

        // A + B + F: the initial generation and five consecutive same-seed regenerations are identical, including the
        // Home Base and every failed placement attempt; a fresh scene load reproduces the same initial world.
        [UnityTest]
        public IEnumerator WorldExplorer_FirstGenerationAndEveryRegeneration_AreIdentical_FromACleanStart()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;

            WorldSnapshot initial = null;
            yield return Load(WorldExplorerPath, true, s => initial = s);
            var proto = Object.FindAnyObjectByType<WorldGenerationPrototype>();
            var spawner = Object.FindAnyObjectByType<BaseSpawner>();
            int seed = initial.seed;
            Assert.AreEqual(proto.BaseSeed, seed, "the initial generation used the world's base seed");
            Assert.AreEqual(350, initial.standard.Count);
            AssertDistribution(initial);
            Assert.IsNotNull(initial.home, "Home Base in the initial generation");
            Assert.AreEqual(351, initial.totalBases);
            Assert.Greater(initial.rejections.Count, 0, "the world has failed placement attempts (they must reproduce too)");

            // World Explorer's startup has meanwhile regenerated a random-seed world (no URL seed in the editor).
            WorldSnapshot startupWorld = Capture(spawner, proto.CurrentBaseSeed);
            Assert.AreNotEqual(seed, startupWorld.seed, "startup applied a different (random) seed");

            for (int k = 1; k <= 5; k++)
            {
                WorldSnapshot again = Regenerate(proto, spawner, seed);
                AssertSameWorld(initial, again, $"regeneration #{k} of seed {seed}");
                yield return null;
                yield return null;
                AssertSameWorld(initial, Capture(spawner, seed, again.rejections), $"regeneration #{k}, two frames later");
            }

            // The startup world's seed reproduces its own first generation too.
            AssertSameStandard(startupWorld, Regenerate(proto, spawner, startupWorld.seed), $"seed {startupWorld.seed} regenerated", compareRejections: false);

            // Clean initial state: reload the scene; its first generation equals the first one.
            WorldSnapshot reloaded = null;
            yield return Load(WorldExplorerPath, true, s => reloaded = s);
            AssertSameWorld(initial, reloaded, "fresh scene load");

            yield return new ExitPlayMode();
        }

        // C + E: different seeds give different layouts; seed A -> seed B -> seed A reproduces A, with the Home Base
        // enabled and disabled; no stale Home Base or previous-world state influences the result.
        [UnityTest]
        public IEnumerator WorldExplorer_DifferentSeeds_AndPreviousWorldCleanup()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            WorldSnapshot initial = null;
            yield return Load(WorldExplorerPath, true, s => initial = s);
            var proto = Object.FindAnyObjectByType<WorldGenerationPrototype>();
            var spawner = Object.FindAnyObjectByType<BaseSpawner>();
            int seed = initial.seed;
            try
            {
                WorldSnapshot other = Regenerate(proto, spawner, OtherSeed);
                Assert.AreEqual(350, other.standard.Count);
                AssertDistribution(other);
                Assert.Greater(CountDifferent(initial, other), 300, "a different seed gives a different layout");
                Assert.IsNotNull(other.home);
                Assert.IsFalse(initial.home.SameAs(other.home), "the Home Base follows the seed too");

                AssertSameWorld(initial, Regenerate(proto, spawner, seed), "A -> B -> A (Home Base on)");
                AssertSameWorld(other, Regenerate(proto, spawner, OtherSeed), "B again (Home Base on)");
                Assert.AreSame(spawner.LastHomeBase, PlayerHomeBase.Current, "exactly the new Home Base is designated");
                Assert.AreEqual(1, spawner.GetComponentsInChildren<BaseController>(false).Count(b => b.IsHomeBase));

                SpawnHomeField.SetValue(spawner, false);
                WorldSnapshot aOff = Regenerate(proto, spawner, seed);
                Assert.IsNull(aOff.home);
                Assert.IsFalse(PlayerHomeBase.HasHomeBase, "no stale Home Base");
                Assert.AreEqual(350, aOff.totalBases);
                AssertSameStandard(initial, aOff, "A without Home Base after a Home Base world");
                WorldSnapshot bOff = Regenerate(proto, spawner, OtherSeed);
                AssertSameStandard(other, bOff, "B without Home Base");
                AssertSameStandard(initial, Regenerate(proto, spawner, seed), "A -> B -> A (Home Base off)");

                SpawnHomeField.SetValue(spawner, true);
                AssertSameWorld(initial, Regenerate(proto, spawner, seed), "Home Base on again after Home-Base-off worlds");
                yield return null;
            }
            finally
            {
                SpawnHomeField.SetValue(spawner, true);
            }
            yield return new ExitPlayMode();
        }

        // D: the 350 standard bases are identical with the Home Base disabled and enabled (initial generations from
        // clean scene loads); the enabled world has exactly one additional Tank Base marked HOME BASE.
        [UnityTest]
        public IEnumerator WorldExplorer_HomeBaseIndependence_CleanLoads()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;

            WorldSnapshot off = null, on = null;
            yield return Load(WorldExplorerPath, false, s => off = s);
            Assert.IsNull(off.home);
            Assert.AreEqual(350, off.totalBases);
            Assert.IsFalse(PlayerHomeBase.HasHomeBase);

            yield return Load(WorldExplorerPath, true, s => on = s);
            AssertSameStandard(off, on, "Home Base disabled vs enabled");
            Assert.AreEqual(351, on.totalBases, "exactly one additional base");
            Assert.IsNotNull(on.home);
            Assert.AreEqual(PlayerHomeBase.HomeBaseId, on.home.id);

            // The current (startup) world: one Home Base, an ordinary Tank Base with the HOME BASE marking.
            BaseController home = PlayerHomeBase.Current;
            Assert.IsNotNull(home);
            Assert.AreEqual("Tank", home.GetComponent<BaseFoundation>().BaseType);
            Assert.AreEqual("HOME BASE", home.GetComponentInChildren<HomeBaseLabel>(true).Text.text);
            Assert.AreEqual(1, Object.FindObjectsByType<HomeBaseLabel>(FindObjectsInactive.Exclude).Length);

            yield return new ExitPlayMode();
        }

        // MainBuild has no startup regeneration; its initial generation and regenerations are identical as well.
        [UnityTest]
        public IEnumerator MainBuild_FirstGenerationAndRegenerations_AreIdentical()
        {
            yield return new EnterPlayMode();
            LogAssert.ignoreFailingMessages = true;
            WorldSnapshot initial = null;
            yield return Load(MainBuildPath, null, s => initial = s);
            var proto = Object.FindAnyObjectByType<WorldGenerationPrototype>();
            var spawner = Object.FindAnyObjectByType<BaseSpawner>();
            Assert.AreEqual(350, initial.standard.Count);
            AssertDistribution(initial);
            Assert.IsNotNull(initial.home);
            AssertSameWorld(initial, Capture(spawner, proto.CurrentBaseSeed, initial.rejections), "MainBuild keeps its initial world (no startup regeneration)");
            for (int k = 1; k <= 3; k++)
                AssertSameWorld(initial, Regenerate(proto, spawner, initial.seed), $"MainBuild regeneration #{k}");
            yield return new ExitPlayMode();
        }
    }
}
