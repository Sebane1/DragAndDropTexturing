namespace DragAndDropTexturing.Windows;

/// <summary>
/// Optional live bind-palette experiment for overlay skinning.
/// ReferencePose (0) is the safe default; other modes use .sklb InverseBoneMatrix with different conventions.
/// </summary>
public enum OverlayBindExperiment
{
    ReferencePose = 0,
    SklbInvTimesCurrent = 1,
    SklbCurrentTimesInv = 2,
    SklbTransposeInvTimesCurrent = 3,
    SklbCurrentTimesTransposeInv = 4,
}
