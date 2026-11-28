
using System;
using UnityEngine;

public class BaseController : MonoBehaviour
{
    /// <summary>Owner ID of the player's bases (the value AddCaptureProgress already assigned).</summary>
    public const string PlayerOwnerId = "PLAYER";
    /// <summary>Owner ID every base starts with.</summary>
    public const string NeutralOwnerId = "NEUTRAL";

    /// <summary>Raised after SetOwner actually changes the owner: (base, previous owner, new owner).</summary>
    public static event Action<BaseController, string, string> OwnerChanged;

    /// <summary>Raised when a base object is destroyed (e.g. New World), before it disappears.</summary>
    public static event Action<BaseController> BaseDestroyed;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticEvents()
    {
        OwnerChanged = null;
        BaseDestroyed = null;
    }

    [Header("Base Identity")]
    [SerializeField] private string baseId = "BASE_001";
    [SerializeField] private string baseName = "Unnamed Outpost";

    [Header("Ownership")]
    [SerializeField] private string ownerId = NeutralOwnerId;

    [Header("Base Status")]
    [SerializeField] private float maxHealth = 1000f;
    [SerializeField] private float currentHealth = 1000f;
    [SerializeField, Range(0f, 100f)]
    private float captureProgress = 0f;

    public string BaseId => baseId;
    public string BaseName => baseName;
    public string OwnerId => ownerId;
    public bool IsPlayerOwned => ownerId == PlayerOwnerId;
    /// <summary>True when this base is the player's designated Home Base (PlayerHomeBase).</summary>
    public bool IsHomeBase => PlayerHomeBase.IsHomeBase(this);
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

        // Stage 8B: the Home Base is protected; ordinary ownership changes cannot give it away.
        if (newOwnerId != PlayerOwnerId && IsHomeBase)
        {
            Debug.LogWarning($"{baseName} is the player's Home Base; its owner cannot be changed to {newOwnerId}.", this);
            return;
        }

        string previous = ownerId;
        ownerId = newOwnerId;
        Debug.Log($"{baseName} is now controlled by {ownerId}.");
        if (previous != newOwnerId) Raise(OwnerChanged, this, previous, newOwnerId);
    }

    private void OnDestroy()
    {
        Action<BaseController> handlers = BaseDestroyed;
        if (handlers == null) return;
        foreach (Delegate d in handlers.GetInvocationList())
        {
            try { ((Action<BaseController>)d)(this); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    private static void Raise(Action<BaseController, string, string> handlers, BaseController b, string from, string to)
    {
        if (handlers == null) return;
        foreach (Delegate d in handlers.GetInvocationList())
        {
            try { ((Action<BaseController, string, string>)d)(b, from, to); }
            catch (Exception e) { Debug.LogException(e); }
        }
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
        if (amount <= 0f || ownerId == PlayerOwnerId)
            return;

        captureProgress = Mathf.Clamp(
            captureProgress + amount, 0f, 100f
        );

        if (captureProgress >= 100f)
        {
            SetOwner(PlayerOwnerId);
            captureProgress = 0f;
        }
    }
}
