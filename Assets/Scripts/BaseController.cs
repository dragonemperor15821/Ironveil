
using UnityEngine;

public class BaseController : MonoBehaviour
{
    [Header("Base Identity")]
    [SerializeField] private string baseId = "BASE_001";
    [SerializeField] private string baseName = "Unnamed Outpost";

    [Header("Ownership")]
    [SerializeField] private string ownerId = "NEUTRAL";

    [Header("Base Status")]
    [SerializeField] private float maxHealth = 1000f;
    [SerializeField] private float currentHealth = 1000f;
    [SerializeField, Range(0f, 100f)]
    private float captureProgress = 0f;

    public string BaseId => baseId;
    public string BaseName => baseName;
    public string OwnerId => ownerId;
    public float CurrentHealth => currentHealth;
    public float CaptureProgress => captureProgress;

    public void InitializeIdentity(string newBaseId, string newBaseName)
    {
        if (!string.IsNullOrWhiteSpace(newBaseId))
        {
            baseId = newBaseId;
        }

        if (!string.IsNullOrWhiteSpace(newBaseName))
        {
            baseName = newBaseName;
        }
    }

    public void SetOwner(string newOwnerId)
    {
        if (string.IsNullOrWhiteSpace(newOwnerId))
            return;

        ownerId = newOwnerId;
        Debug.Log($"{baseName} is now controlled by {ownerId}.");
    }

    public void ApplyDamage(float damage)
    {
        if (damage <= 0f || currentHealth <= 0f)
            return;

        currentHealth = Mathf.Max(0f, currentHealth - damage);

        if (currentHealth <= 0f)
            Debug.Log($"{baseName} has been destroyed.");
    }

    public void AddCaptureProgress(float amount)
    {
        if (amount <= 0f || ownerId == "PLAYER")
            return;

        captureProgress = Mathf.Clamp(
            captureProgress + amount, 0f, 100f
        );

        if (captureProgress >= 100f)
        {
            SetOwner("PLAYER");
            captureProgress = 0f;
        }
    }
}
