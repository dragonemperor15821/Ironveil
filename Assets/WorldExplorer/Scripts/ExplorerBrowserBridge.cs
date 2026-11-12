using System;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// C# side of ExplorerBrowserBridge.jslib. In Web builds it uses the browser
/// (history.replaceState, Clipboard API, crypto.getRandomValues, an HTML link
/// field). Elsewhere (Editor, desktop players) it uses safe local
/// equivalents so the Explorer still runs.
/// </summary>
public static class ExplorerBrowserBridge
{
    public enum CopyResult { Failed = 0, ClipboardApi = 1, ExecCommand = 2 }

    private static System.Security.Cryptography.RandomNumberGenerator localRandom;
#if !(UNITY_WEBGL && !UNITY_EDITOR)
    private static int localCopyStatus = -1;
#endif

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")] private static extern int ExplorerBridge_ReplaceQuery(string query);
    [DllImport("__Internal")] private static extern string ExplorerBridge_GetHref();
    [DllImport("__Internal")] private static extern void ExplorerBridge_CopyText(string text);
    [DllImport("__Internal")] private static extern int ExplorerBridge_CopyStatus();
    [DllImport("__Internal")] private static extern int ExplorerBridge_RandomBits();
    [DllImport("__Internal")] private static extern void ExplorerBridge_ShowLinkField(string url, int topPx, int leftPx);
    [DllImport("__Internal")] private static extern void ExplorerBridge_HideLinkField();
#endif

    /// <summary>True when running as a Web build with the browser bridge.</summary>
    public static bool IsBrowser
    {
        get
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>The current page address (the live browser URL in Web builds).</summary>
    public static string CurrentPageUrl()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        try { return ExplorerBridge_GetHref(); }
        catch (Exception) { return Application.absoluteURL ?? string.Empty; }
#else
        return Application.absoluteURL ?? string.Empty;
#endif
    }

    /// <summary>Replaces the page query without adding a history entry.</summary>
    public static bool ReplaceQuery(string query)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        return ExplorerBridge_ReplaceQuery(query) == 1;
#else
        return false;
#endif
    }

    /// <summary>
    /// Starts copying text to the system clipboard. Poll
    /// <see cref="TryGetCopyResult"/> for the browser's real outcome (it
    /// arrives asynchronously in Web builds). Must be started from a user
    /// click: browsers refuse clipboard writes without user activation.
    /// </summary>
    public static void CopyText(string text)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        ExplorerBridge_CopyText(text);
#else
        // Editor / desktop: Unity's copy buffer is the system clipboard here.
        GUIUtility.systemCopyBuffer = text;
        localCopyStatus = GUIUtility.systemCopyBuffer == text ? (int)CopyResult.ClipboardApi : (int)CopyResult.Failed;
#endif
    }

    /// <summary>False while the copy started by <see cref="CopyText"/> is still pending.</summary>
    public static bool TryGetCopyResult(out CopyResult result)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        int status = ExplorerBridge_CopyStatus();
#else
        int status = localCopyStatus;
#endif
        result = status == 1 ? CopyResult.ClipboardApi : status == 2 ? CopyResult.ExecCommand : CopyResult.Failed;
        return status >= 0;
    }

    /// <summary>
    /// A new seed in 1..2147483647 from a cryptographic random source
    /// (browser crypto.getRandomValues in Web builds), never equal to
    /// <paramref name="exclude"/>.
    /// </summary>
    public static int NewRandomSeed(int exclude)
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            if (ExplorerSeedUrl.TrySeedFromRandomBits(RandomBits(), out int seed) && seed != exclude)
                return seed;
        }
        // Practically unreachable (needs 64 consecutive rejected draws).
        int fallback = UnityEngine.Random.Range(ExplorerSeedUrl.MinSeed, ExplorerSeedUrl.MaxSeed);
        return fallback != exclude ? fallback : (fallback == ExplorerSeedUrl.MaxSeed ? fallback - 1 : fallback + 1);
    }

    private static uint RandomBits()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        uint bits = unchecked((uint)ExplorerBridge_RandomBits());
        if (bits != 0u) return bits;
#endif
        if (localRandom == null) localRandom = System.Security.Cryptography.RandomNumberGenerator.Create();
        var buffer = new byte[4];
        localRandom.GetBytes(buffer);
        return BitConverter.ToUInt32(buffer, 0);
    }

    /// <summary>Shows a native, selectable link field (Web builds). Returns false elsewhere.</summary>
    public static bool ShowLinkField(string url, int topCssPx, int leftCssPx)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        ExplorerBridge_ShowLinkField(url, topCssPx, leftCssPx);
        return true;
#else
        return false;
#endif
    }

    public static void HideLinkField()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        ExplorerBridge_HideLinkField();
#endif
    }
}
