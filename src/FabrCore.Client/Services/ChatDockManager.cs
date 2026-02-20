namespace FabrCore.Client.Services;

/// <summary>
/// Manages the state of multiple ChatDock components on a page.
/// Ensures only one dock is expanded at a time.
/// </summary>
public class ChatDockManager
{
    private readonly Dictionary<string, Action<bool>> _docks = new();
    private string? _expandedDockId;

    public event Action? StateChanged;

    /// <summary>
    /// Registers a dock with the manager.
    /// </summary>
    /// <param name="dockId">Unique identifier for the dock (typically the agent handle)</param>
    /// <param name="onExpandedChanged">Callback to notify the dock of expansion state changes</param>
    public void Register(string dockId, Action<bool> onExpandedChanged)
    {
        _docks[dockId] = onExpandedChanged;
    }

    /// <summary>
    /// Unregisters a dock from the manager.
    /// </summary>
    public void Unregister(string dockId)
    {
        _docks.Remove(dockId);
        if (_expandedDockId == dockId)
        {
            _expandedDockId = null;
        }
    }

    /// <summary>
    /// Expands the specified dock and collapses all others.
    /// </summary>
    public void Expand(string dockId)
    {
        if (_expandedDockId == dockId)
            return;

        // Collapse the currently expanded dock
        if (_expandedDockId != null && _docks.TryGetValue(_expandedDockId, out var collapseCallback))
        {
            collapseCallback(false);
        }

        // Expand the new dock
        _expandedDockId = dockId;
        if (_docks.TryGetValue(dockId, out var expandCallback))
        {
            expandCallback(true);
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Collapses the specified dock.
    /// </summary>
    public void Collapse(string dockId)
    {
        if (_expandedDockId == dockId)
        {
            _expandedDockId = null;
            if (_docks.TryGetValue(dockId, out var callback))
            {
                callback(false);
            }
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Toggles the expansion state of the specified dock.
    /// </summary>
    public void Toggle(string dockId)
    {
        if (_expandedDockId == dockId)
        {
            Collapse(dockId);
        }
        else
        {
            Expand(dockId);
        }
    }

    /// <summary>
    /// Gets whether the specified dock is currently expanded.
    /// </summary>
    public bool IsExpanded(string dockId) => _expandedDockId == dockId;

    /// <summary>
    /// Gets the ID of the currently expanded dock, if any.
    /// </summary>
    public string? ExpandedDockId => _expandedDockId;
}
