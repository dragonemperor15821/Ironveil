
using System.Collections.Generic;
using UnityEngine;

public class BaseSpawner : MonoBehaviour
{
    [Header("Base Setup")]
    [SerializeField] private GameObject basePrefab;
    [SerializeField] private int baseCount = 350;

    [Header("Battlefield Bounds")]
    [SerializeField] private float mapSizeX = 400f;
    [SerializeField] private float mapSizeZ = 400f;
    [SerializeField] private float edgePadding = 8f;

    [Header("Organic Distribution")]
    [SerializeField] private float minSpacing = 4f;
    [SerializeField] private int clusterCount = 18;
    [SerializeField] private float clusterRadius = 35f;
    [Range(0f, 1f)]
    [SerializeField] private float clusterChance = 0.7f;

    [Header("Placement")]
    [SerializeField] private float raycastHeight = 200f;
    [SerializeField] private float maxSlopeAngle = 30f;
    [SerializeField] private int seed = 12345;
    [SerializeField] private LayerMask groundMask = ~0;

    private readonly List<Vector3> placedPositions = new();

    public void Start()
    {
        GenerateBases();
    }

    [ContextMenu("Generate Bases")]
    public void GenerateBases()
    {
        if (basePrefab == null)
        {
            Debug.LogError("Assign the Base-Outpost prefab first.", this);
            return;
        }

        // Remove previously generated bases.
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            Transform child = transform.GetChild(i);

            if (child.name.StartsWith("GeneratedBase_"))
                Destroy(child.gameObject);
        }

        placedPositions.Clear();
        Random.InitState(seed);

        Vector2[] clusters = new Vector2[clusterCount];

        for (int i = 0; i < clusterCount; i++)
        {
            clusters[i] = new Vector2(
                Random.Range(-mapSizeX / 2f + edgePadding,
                              mapSizeX / 2f - edgePadding),
                Random.Range(-mapSizeZ / 2f + edgePadding,
                              mapSizeZ / 2f - edgePadding));
        }

        int spawned = 0;
        int attempts = 0;
        int maxAttempts = baseCount * 1000;
        float minSpacingSqr = minSpacing * minSpacing;

        while (spawned < baseCount && attempts < maxAttempts)
        {
            attempts++;

            float x, z;

            if (Random.value < clusterChance)
            {
                Vector2 center = clusters[Random.Range(0, clusterCount)];
                Vector2 offset = Random.insideUnitCircle * clusterRadius;
                x = center.x + offset.x;
                z = center.y + offset.y;
            }
            else
            {
                x = Random.Range(-mapSizeX / 2f + edgePadding,
                                  mapSizeX / 2f - edgePadding);
                z = Random.Range(-mapSizeZ / 2f + edgePadding,
                                  mapSizeZ / 2f - edgePadding);
            }

            if (Mathf.Abs(x) > mapSizeX / 2f - edgePadding ||
                Mathf.Abs(z) > mapSizeZ / 2f - edgePadding)
                continue;

            Vector3 origin = new Vector3(
                transform.position.x + x,
                transform.position.y + raycastHeight,
                transform.position.z + z);

            if (!Physics.Raycast(origin, Vector3.down,
                out RaycastHit hit, raycastHeight * 2f,
                groundMask, QueryTriggerInteraction.Ignore))
                continue;

            if (Vector3.Angle(hit.normal, Vector3.up) > maxSlopeAngle)
                continue;

            Vector2 candidate = new Vector2(hit.point.x, hit.point.z);
            bool tooClose = false;

            foreach (Vector3 existing in placedPositions)
            {
                if ((candidate - new Vector2(existing.x, existing.z))
                    .sqrMagnitude < minSpacingSqr)
                {
                    tooClose = true;
                    break;
                }
            }

            if (tooClose)
                continue;

            int number = spawned + 1;

            GameObject newBase = Instantiate(
                basePrefab,
                hit.point,
                Quaternion.Euler(0f, Random.Range(0f, 360f), 0f),
                transform);

            newBase.name = $"GeneratedBase_{number:000}";
            placedPositions.Add(hit.point);
            spawned++;
        }

        Debug.Log($"IRONVEIL: Generated {spawned}/{baseCount} bases " +
                  $"after {attempts} attempts.", this);
    }
}
