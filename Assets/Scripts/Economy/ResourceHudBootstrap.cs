using System.Collections.Generic;
using UnityEngine;

namespace Ironveil.Economy
{
    /// <summary>
    /// Scene entry point for the resource economy. One instance is placed in MainBuild and one in World Explorer,
    /// both referencing the same Assets/Data/Economy/HomeBaseResourceConfig.asset.
    ///
    /// Unlike BaseMenuBootstrap and BaseConstructionBootstrap, which create themselves through
    /// RuntimeInitializeOnLoadMethod and need no assets, this bootstrap must supply a serialized asset reference.
    /// Without Resources.Load, a serialized field on a scene object is the supported way to do that. The bootstrap
    /// itself stays a plain scene object. The state it guards lives on <see cref="ResourceManager"/>'s
    /// DontDestroyOnLoad host, and it follows the same Instance and duplicate rules.
    ///
    /// On Awake it validates the config, then gets or creates the session's single manager and initializes it.
    /// When the second scene loads, its bootstrap finds the existing manager already initialized from the same
    /// asset and reuses it: balances, cycles and subscriptions are untouched.
    ///
    /// Stage 4 will attach the resource HUD here. Stage 3 has no visuals.
    /// </summary>
    [DefaultExecutionOrder(-1000)] // initialize before other scene scripts' Awake/Start may query resources
    [DisallowMultipleComponent]
    public sealed class ResourceHudBootstrap : MonoBehaviour
    {
        [Tooltip("Must be Assets/Data/Economy/HomeBaseResourceConfig.asset, the same asset in every scene.")]
        [SerializeField] private HomeBaseResourceConfig homeBaseConfig;

        public HomeBaseResourceConfig HomeBaseConfig => homeBaseConfig;

        /// <summary>Result of the most recent <see cref="Run"/>.</summary>
        public bool LastRunSucceeded { get; private set; }

        private void Awake()
        {
            Run();
        }

        /// <summary>
        /// Idempotent: ensures the session's manager exists and is initialized from <see cref="HomeBaseConfig"/>.
        /// No manager is created when the configuration is missing or invalid.
        /// </summary>
        public bool Run()
        {
            LastRunSucceeded = false;

            if (homeBaseConfig == null)
            {
                Debug.LogError($"ResourceHudBootstrap on '{name}': no HomeBaseResourceConfig assigned; resources are unavailable.", this);
                return false;
            }

            ResourceManager existing = ResourceManager.Instance;
            if (existing == null || !existing.IsInitialized)
            {
                var errors = new List<string>();
                if (!homeBaseConfig.Validate(errors))
                {
                    Debug.LogError($"ResourceHudBootstrap on '{name}': '{homeBaseConfig.name}' is invalid; resources are unavailable:\n - " +
                                   string.Join("\n - ", errors), this);
                    return false;
                }
            }

            LastRunSucceeded = ResourceManager.GetOrCreate().Initialize(homeBaseConfig);
            return LastRunSucceeded;
        }

#if UNITY_EDITOR
        /// <summary>Editor and test use only: assigns the serialized reference.</summary>
        public void EditorSetConfig(HomeBaseResourceConfig config) => homeBaseConfig = config;
#endif
    }
}
