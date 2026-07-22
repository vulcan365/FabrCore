# MainLayout.razor — App Shell

The primary layout component with sidebar navigation and responsive mobile drawer.

```razor
@inherits LayoutComponentBase

<div class="app-shell">
    @if (_sidebarOpen)
    {
        <div class="app-sidebar-backdrop" @onclick="CloseSidebar"></div>
    }

    <aside class="app-sidebar @(_sidebarOpen ? "show" : "")">
        <div class="app-sidebar-brand">
            <span style="color: white; font-weight: 600; font-size: 1.5rem;">A</span>
        </div>
        <nav class="app-sidebar-nav">
            <NavLink href="" Match="NavLinkMatch.All" class="nav-link" @onclick="CloseSidebar">
                <i class="bi bi-house-door"></i>
                <span>Home</span>
            </NavLink>
            <NavLink href="items" class="nav-link" @onclick="CloseSidebar">
                <i class="bi bi-folder"></i>
                <span>Items</span>
            </NavLink>
            <NavLink href="contacts" class="nav-link" @onclick="CloseSidebar">
                <i class="bi bi-people"></i>
                <span>Contacts</span>
            </NavLink>
            <NavLink href="settings" class="nav-link" @onclick="CloseSidebar">
                <i class="bi bi-gear"></i>
                <span>Settings</span>
            </NavLink>
            <NavLink href="system" class="nav-link" @onclick="CloseSidebar">
                <i class="bi bi-wrench-adjustable"></i>
                <span>System</span>
            </NavLink>
        </nav>
    </aside>

    <div class="app-main">
        <header class="app-header app-mobile-header">
            <button class="app-menu-toggle" @onclick="ToggleSidebar" aria-label="Toggle navigation menu">
                <i class="bi bi-list"></i>
            </button>
            <span class="app-mobile-brand">App</span>
        </header>
        <main class="app-content">
            @Body
        </main>
    </div>
</div>

@code {
    private bool _sidebarOpen = false;

    private void ToggleSidebar()
    {
        _sidebarOpen = !_sidebarOpen;
    }

    private void CloseSidebar()
    {
        _sidebarOpen = false;
    }
}

<div id="blazor-error-ui" data-nosnippet>
    An unhandled error has occurred.
    <a href="." class="reload">Reload</a>
    <span class="dismiss">&#x1f5d9;</span>
</div>
```

## Key Patterns

- **Sidebar toggle**: `_sidebarOpen` bool controls `.show` class and backdrop visibility
- **NavLink**: Built-in Blazor component that adds `.active` class on current route
- **Mobile drawer**: At `<991px`, sidebar becomes fixed overlay with backdrop
- **Desktop**: Sidebar is sticky 72px column, no header shown
- **Icons**: Each nav link has a Bootstrap Icon + text label
