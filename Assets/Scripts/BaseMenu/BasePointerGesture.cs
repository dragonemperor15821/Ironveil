using System;
using UnityEngine;

/// <summary>
/// Base Management Menu (Phase 1C): click-versus-drag classification.
///
/// Observes the mouse buttons without consuming anything, so camera
/// controls (MainBuild right-drag, Explorer left/middle/right-drag) and uGUI
/// keep working unchanged. A press becomes a drag as soon as the pointer
/// moves at least <see cref="DragThresholdPixels"/> from where it went down;
/// a primary-button release that never became a drag is a click.
///
/// Keep the threshold at or below the camera's own drag threshold
/// (ExplorerCameraController.dragThresholdPixels = 4 in the Explorer scene):
/// then any press that moved the camera is a drag here too, and can never be
/// mistaken for a menu selection.
/// </summary>
[Serializable]
public sealed class BasePointerGesture
{
    [Tooltip("Pointer travel in pixels after which a press is a drag, not a click. Keep <= the camera drag threshold (Explorer: 4).")]
    [SerializeField, Min(0f)] private float dragThresholdPixels = 4f;

    private bool anyPressed;
    private bool primaryPressed;
    private Vector2 pressPosition;
    private bool pressStartedOverUI;
    private bool primaryPressStartedOverUI;
    private Vector2 primaryPressPosition;
    private bool primaryDragged;

    public float DragThresholdPixels
    {
        get => dragThresholdPixels;
        set => dragThresholdPixels = Mathf.Max(0f, value);
    }

    /// <summary>Any button held and moved past the threshold (camera drag in progress).</summary>
    public bool IsDragging { get; private set; }

    /// <summary>Any mouse button currently held.</summary>
    public bool IsPressed => anyPressed;

    /// <summary>The current/last press started over a UI element.</summary>
    public bool PressStartedOverUI => pressStartedOverUI;

    /// <summary>Set for exactly one update after a primary release that never became a drag.</summary>
    public bool ClickedThisFrame { get; private set; }

    /// <summary>Screen position of the press that produced the click.</summary>
    public Vector2 ClickPressPosition { get; private set; }

    /// <summary>The click's press began over UI (uGUI handles those clicks itself).</summary>
    public bool ClickPressStartedOverUI { get; private set; }

    public bool DragStartedThisFrame { get; private set; }
    public bool DragEndedThisFrame { get; private set; }

    /// <summary>
    /// Feed one frame of pointer state. Allocation-free.
    /// </summary>
    public void Update(Vector2 position, bool primary, bool secondary, bool middle, bool pointerOverUI)
    {
        ClickedThisFrame = false;
        DragStartedThisFrame = false;
        DragEndedThisFrame = false;

        bool nowAny = primary || secondary || middle;
        float thresholdSqr = dragThresholdPixels * dragThresholdPixels;

        // Any-button press tracking (drag detection for every camera button).
        if (nowAny && !anyPressed)
        {
            pressPosition = position;
            pressStartedOverUI = pointerOverUI;
        }

        if (nowAny && !IsDragging && (position - pressPosition).sqrMagnitude >= thresholdSqr && thresholdSqr > 0f)
        {
            IsDragging = true;
            DragStartedThisFrame = true;
        }

        // Primary-button click tracking.
        if (primary && !primaryPressed)
        {
            primaryPressPosition = position;
            primaryPressStartedOverUI = pointerOverUI;
            primaryDragged = false;
        }

        if (primary && (position - primaryPressPosition).sqrMagnitude >= thresholdSqr && thresholdSqr > 0f)
        {
            primaryDragged = true;
        }

        if (!primary && primaryPressed)
        {
            // A release that moved past the threshold since the press is a drag.
            if ((position - primaryPressPosition).sqrMagnitude >= thresholdSqr && thresholdSqr > 0f)
            {
                primaryDragged = true;
            }

            if (!primaryDragged)
            {
                ClickedThisFrame = true;
                ClickPressPosition = primaryPressPosition;
                ClickPressStartedOverUI = primaryPressStartedOverUI;
            }
        }

        if (!nowAny && anyPressed && IsDragging)
        {
            IsDragging = false;
            DragEndedThisFrame = true;
        }

        primaryPressed = primary;
        anyPressed = nowAny;
    }

    /// <summary>Forget any press in progress (e.g. focus lost).</summary>
    public void Reset()
    {
        anyPressed = primaryPressed = primaryDragged = false;
        IsDragging = ClickedThisFrame = DragStartedThisFrame = DragEndedThisFrame = false;
        pressStartedOverUI = primaryPressStartedOverUI = false;
    }
}
