using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace FabrCore.Client.Services;

/// <summary>
/// Manages the state of multiple ChatDock components on a page.
/// Ensures only one dock is expanded at a time.
/// Thread-safe for use in Blazor Server circuits.
/// </summary>
public class ChatDockManager
{
    private readonly ConcurrentDictionary<string, Action<bool>> _docks = new();
    private readonly object _stateLock = new();
    private readonly ILogger<ChatDockManager> _logger;
    private string? _expandedDockId;

    public event Action? StateChanged;

    public ChatDockManager(ILogger<ChatDockManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Registers a dock with the manager.
    /// </summary>
    /// <param name="dockId">Unique identifier for the dock</param>
    /// <param name="onExpandedChanged">Callback to notify the dock of expansion state changes</param>
    public void Register(string dockId, Action<bool> onExpandedChanged)
    {
        // D4: Warn if overwriting an existing registration
        if (_docks.TryGetValue(dockId, out _))
        {
            _logger.LogWarning("ChatDock with dockId '{DockId}' is already registered. Overwriting. " +
                "This may indicate a DockId collision — ensure each ChatDock has a unique AgentHandle.", dockId);
        }
        _docks[dockId] = onExpandedChanged;
    }

    /// <summary>
    /// Unregisters a dock from the manager.
    /// </summary>
    public void Unregister(string dockId)
    {
        _docks.TryRemove(dockId, out _);
        lock (_stateLock)
        {
            if (_expandedDockId == dockId)
            {
                _expandedDockId = null;
            }
        }
    }

    /// <summary>
    /// Expands the specified dock and collapses all others.
    /// </summary>
    public void Expand(string dockId)
    {
        lock (_stateLock)
        {
            if (_expandedDockId == dockId)
                return;

            // Collapse the currently expanded dock
            if (_expandedDockId != null && _docks.TryGetValue(_expandedDockId, out var collapseCallback))
            {
                SafeInvoke(collapseCallback, false);
            }

            // Expand the new dock
            _expandedDockId = dockId;
            if (_docks.TryGetValue(dockId, out var expandCallback))
            {
                SafeInvoke(expandCallback, true);
            }
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Collapses the specified dock.
    /// </summary>
    public void Collapse(string dockId)
    {
        lock (_stateLock)
        {
            if (_expandedDockId != dockId)
                return;

            _expandedDockId = null;
            if (_docks.TryGetValue(dockId, out var callback))
            {
                SafeInvoke(callback, false);
            }
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Toggles the expansion state of the specified dock.
    /// </summary>
    public void Toggle(string dockId)
    {
        lock (_stateLock)
        {
            if (_expandedDockId == dockId)
            {
                _expandedDockId = null;
                if (_docks.TryGetValue(dockId, out var collapseCallback))
                {
                    SafeInvoke(collapseCallback, false);
                }
            }
            else
            {
                // Collapse current
                if (_expandedDockId != null && _docks.TryGetValue(_expandedDockId, out var prevCallback))
                {
                    SafeInvoke(prevCallback, false);
                }
                // Expand new
                _expandedDockId = dockId;
                if (_docks.TryGetValue(dockId, out var expandCallback))
                {
                    SafeInvoke(expandCallback, true);
                }
            }
        }

        RaiseStateChanged();
    }

    /// <summary>
    /// Gets whether the specified dock is currently expanded.
    /// </summary>
    public bool IsExpanded(string dockId) => _expandedDockId == dockId;

    /// <summary>
    /// Gets the ID of the currently expanded dock, if any.
    /// </summary>
    public string? ExpandedDockId => _expandedDockId;

    // P6: Safe callback invocation with error handling
    private void SafeInvoke(Action<bool> callback, bool expanded)
    {
        try
        {
            callback(expanded);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invoking ChatDock expanded changed callback");
        }
    }

    // P6: Safe event raising
    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invoking ChatDockManager.StateChanged event");
        }
    }
}
