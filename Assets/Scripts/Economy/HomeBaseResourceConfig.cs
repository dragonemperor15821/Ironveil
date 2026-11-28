using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Ironveil.Economy
{
    /// <summary>
    /// Resource configuration for the player's single Home Base, stored as a ScriptableObject asset
    /// (Assets/Data/Economy/HomeBaseResourceConfig.asset). MainBuild and World Explorer represent the same
    /// player's world, so both should read this one asset.
    ///
    /// The finalized values come from <see cref="HomeBaseResourceProfile"/>: the asset is generated from it by
    /// Tools/IRONVEIL/Economy/Create Home Base Resource Config, and an Edit Mode test fails if the two differ.
    /// Validation is delegated to <see cref="ResourceConfigEntry.ToConfig"/> and <see cref="ResourceConfig"/>,
    /// so the rules are defined in one place.
    /// </summary>
    [CreateAssetMenu(menuName = "IRONVEIL/Economy/Home Base Resource Config", fileName = "HomeBaseResourceConfig")]
    public sealed class HomeBaseResourceConfig : ScriptableObject
    {
        /// <summary>
        /// Resources whose starting values are finalized and must appear in the configuration.
        /// GroupLimit is not listed because its starting value and capacity are not decided yet.
        /// </summary>
        public static readonly IReadOnlyList<ResourceType> RequiredResources = new[]
        {
            ResourceType.Concrete,
            ResourceType.Iron,
            ResourceType.Aluminium,
            ResourceType.Fuel,
            ResourceType.Gold,
            ResourceType.Economy,
            ResourceType.Military,
        };

        [SerializeField] private List<ResourceConfigEntry> entries = new List<ResourceConfigEntry>();

        public IReadOnlyList<ResourceConfigEntry> Entries => entries;

        /// <summary>A runtime (non-asset) instance holding the finalized <see cref="HomeBaseResourceProfile"/> values.</summary>
        public static HomeBaseResourceConfig CreateFromProfile()
        {
            var list = new List<ResourceConfigEntry>();
            foreach (ResourceConfig config in HomeBaseResourceProfile.CreateSinglePlayerConfigs())
                list.Add(ResourceConfigEntry.FromConfig(config));
            return CreateRuntime(list);
        }

        /// <summary>A runtime (non-asset) instance with the given entries. It is not validated until used.</summary>
        public static HomeBaseResourceConfig CreateRuntime(IEnumerable<ResourceConfigEntry> configEntries)
        {
            if (configEntries == null) throw new ArgumentNullException(nameof(configEntries));
            var instance = CreateInstance<HomeBaseResourceConfig>();
            instance.entries = new List<ResourceConfigEntry>(configEntries);
            return instance;
        }

        /// <summary>Checks the whole configuration and adds one message per problem to <paramref name="errors"/>. Returns true when valid.</summary>
        public bool Validate(List<string> errors)
        {
            if (errors == null) throw new ArgumentNullException(nameof(errors));
            TryBuild(errors, out _);
            return errors.Count == 0;
        }

        public bool IsValid() => Validate(new List<string>());

        /// <summary>Validated runtime configs, one per entry. Throws <see cref="InvalidOperationException"/> listing every problem.</summary>
        public IReadOnlyList<ResourceConfig> BuildResourceConfigs()
        {
            var errors = new List<string>();
            if (!TryBuild(errors, out List<ResourceConfig> configs))
                throw new InvalidOperationException(FormatErrors(errors));
            return configs;
        }

        /// <summary>A new, independent ledger initialized from this configuration. Each call returns a fresh ledger.</summary>
        public ResourceLedger CreateLedger() => new ResourceLedger(BuildResourceConfigs());

        private bool TryBuild(List<string> errors, out List<ResourceConfig> configs)
        {
            int errorsBefore = errors.Count;
            configs = new List<ResourceConfig>();
            var seen = new bool[ResourceTypeRules.Count];

            if (entries == null || entries.Count == 0)
            {
                errors.Add("No resources are configured.");
                return false;
            }

            for (int e = 0; e < entries.Count; e++)
            {
                ResourceConfigEntry entry = entries[e];
                if (!ResourceTypeRules.IsDefined(entry.Resource))
                {
                    errors.Add($"Entry {e}: undefined resource type {(int)entry.Resource}.");
                    continue;
                }

                int index = (int)entry.Resource;
                if (seen[index])
                {
                    errors.Add($"Entry {e}: {entry.Resource} is configured more than once.");
                    continue;
                }
                seen[index] = true;

                try
                {
                    configs.Add(entry.ToConfig());
                }
                catch (ArgumentException ex)
                {
                    errors.Add($"Entry {e} ({entry.Resource}): {FirstLine(ex.Message)}");
                }
            }

            foreach (ResourceType required in RequiredResources)
            {
                if (!seen[(int)required])
                    errors.Add($"{required} is missing; its starting value is finalized and must be configured.");
            }

            return errors.Count == errorsBefore;
        }

        private static string FirstLine(string message)
        {
            // ArgumentException appends "Parameter name: ..." / "Actual value was ..." on extra lines.
            int newline = message.IndexOf('\n');
            return (newline < 0 ? message : message.Substring(0, newline)).Trim();
        }

        private static string FormatErrors(List<string> errors)
        {
            var sb = new StringBuilder("Home Base resource configuration is invalid:");
            foreach (string error in errors) sb.Append("\n - ").Append(error);
            return sb.ToString();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            var errors = new List<string>();
            if (!Validate(errors))
                Debug.LogWarning($"{FormatErrors(errors)}\n({name})", this);
        }
#endif
    }
}
