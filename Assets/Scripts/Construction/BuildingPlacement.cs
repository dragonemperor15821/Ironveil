using UnityEngine;

/// <summary>
/// Stage 3A: low-level placement of a building model into a slot. No costs,
/// timers, menu actions or validation of game rules; callers decide when to
/// place. Scale is applied exactly once, on the instance:
///   instance.localScale = prefabRootScale * slot.InstanceScale
/// with the slot and every transform between slot and base root at unit scale,
/// so the instance's scale relative to the base root is exactly the slot scale
/// (0.2 buildings, 0.05 Flaks) whatever the base root's runtime scale is.
/// Rotation: slot yaw (facing) x model facing offset x prefab root rotation.
/// </summary>
public static class BuildingPlacement
{
    public static BuildingInstance Place(BuildingSlot slot, BuildingDefinition definition, int buildingLevel)
    {
        if (slot == null || definition == null || slot.IsOccupied || !slot.Accepts(definition.Type))
        {
            return null;
        }

        GameObject prefab = definition.GetPrefab(buildingLevel);
        if (prefab == null)
        {
            return null;
        }

        GameObject go = Object.Instantiate(prefab, slot.transform, false);
        go.name = prefab.name;
        go.transform.localPosition = Vector3.zero;
        // Model forward onto the slot's +Z facing, then the model's own import rotation (X 270).
        go.transform.localRotation = Quaternion.Euler(0f, definition.GetFacingYawOffset(buildingLevel), 0f) *
                                     prefab.transform.localRotation;
        go.transform.localScale = prefab.transform.localScale * slot.InstanceScale;

        BuildingInstance instance = go.AddComponent<BuildingInstance>();
        instance.Initialize(definition, buildingLevel, slot);
        slot.SetOccupant(instance);
        return instance;
    }

    public static void Remove(BuildingSlot slot)
    {
        if (slot == null || slot.Occupant == null)
        {
            return;
        }

        GameObject go = slot.Occupant.gameObject;
        slot.ClearOccupant();
        if (Application.isPlaying)
        {
            Object.Destroy(go);
        }
        else
        {
            Object.DestroyImmediate(go);
        }
    }
}
