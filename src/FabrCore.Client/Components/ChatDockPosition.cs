namespace Fabr.Client.Components;

/// <summary>
/// Specifies where the ChatDock panel appears on the screen.
/// </summary>
public enum ChatDockPosition
{
    /// <summary>
    /// Panel appears in the bottom-right corner, sliding up.
    /// </summary>
    BottomRight,

    /// <summary>
    /// Panel appears in the bottom-left corner, sliding up.
    /// </summary>
    BottomLeft,

    /// <summary>
    /// Panel appears docked to the right edge, full height.
    /// </summary>
    Right,

    /// <summary>
    /// Panel appears docked to the left edge, full height.
    /// </summary>
    Left
}
