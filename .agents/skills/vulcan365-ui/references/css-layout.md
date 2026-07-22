# Layout Styles

App shell, sidebar, header, breadcrumbs, and responsive breakpoints.

**File:** `wwwroot/css/layout.css`

```css
/* ==================== APPLICATION SHELL ==================== */
.app-shell {
  display: flex;
  min-height: 100vh;
}

.app-main {
  flex: 1;
  display: flex;
  flex-direction: column;
  width: 100%;
  overflow-x: hidden;
}

.app-content {
  flex: 1;
  padding: var(--spc-6);
  width: 100%;
}

@media (max-width: 991px) {
  .app-content {
    padding: var(--spc-4);
  }
}

@media (max-width: 767px) {
  .app-content {
    padding: var(--spc-3);
  }
}

/* ==================== SIDEBAR NAVIGATION ==================== */
.app-sidebar {
  width: 72px;
  background: var(--app-color-navy);
  color: var(--app-text-on-dark);
  padding: var(--spc-4) var(--spc-2);
  position: sticky;
  top: 0;
  height: 100vh;
  z-index: var(--z-nav);
  display: flex;
  flex-direction: column;
  transition: transform var(--app-transition-normal);
}

.app-sidebar-brand {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--spc-3) 0;
  margin-bottom: var(--spc-4);
}

.app-sidebar-brand img {
  max-width: 40px;
  height: auto;
}

.app-sidebar-nav {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: var(--spc-1);
}

.app-sidebar-nav .nav-link {
  color: rgba(255, 255, 255, 0.7);
  font-size: 0.8rem;
  border-radius: var(--app-radius-md);
  padding: 10px 8px;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  transition: background-color var(--app-transition-fast), color var(--app-transition-fast);
  text-decoration: none;
  border: none;
  background: transparent;
}

.app-sidebar-nav .nav-link i {
  font-size: 1.1rem;
}

.app-sidebar-nav .nav-link span {
  font-size: 0.65rem;
  line-height: 1;
  text-align: center;
}

.app-sidebar-nav .nav-link.active,
.app-sidebar-nav .nav-link:hover {
  background-color: rgba(255, 255, 255, 0.12);
  color: #fff;
}

/* Mobile sidebar */
@media (max-width: 991px) {
  .app-sidebar {
    position: fixed;
    width: 250px;
    max-width: 80vw;
    transform: translateX(-100%);
    z-index: var(--z-drawer);
  }

  .app-sidebar.show {
    transform: translateX(0);
  }

  .app-sidebar-nav .nav-link {
    flex-direction: row;
    justify-content: flex-start;
    gap: var(--spc-3);
    padding: 12px 16px;
  }

  .app-sidebar-nav .nav-link span {
    font-size: 0.9rem;
    text-align: left;
  }
}

.app-sidebar-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(14, 18, 35, 0.5);
  z-index: calc(var(--z-drawer) - 1);
  animation: fadeIn var(--app-transition-normal);
}

@keyframes fadeIn {
  from { opacity: 0; }
  to { opacity: 1; }
}

/* ==================== TOP HEADER ==================== */
.app-header {
  height: 64px;
  padding: 0 var(--spc-5);
  border-bottom: 1px solid var(--app-border-subtle);
  background: var(--app-color-surface);
  position: sticky;
  top: 0;
  z-index: var(--z-header);
  display: flex;
  align-items: center;
  gap: var(--spc-4);
}

.app-header-title {
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.app-header-actions {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: var(--spc-3);
}

.app-menu-toggle {
  display: none;
  background: transparent;
  border: none;
  color: var(--app-text-main);
  font-size: 1.5rem;
  padding: var(--spc-2);
  cursor: pointer;
  margin-right: var(--spc-2);
}

.app-mobile-header {
  display: none;
}

.app-mobile-brand {
  font-family: "Poppins", "Figtree", system-ui, sans-serif;
  font-weight: 600;
  font-size: 1.25rem;
  color: var(--app-text-main);
}

@media (max-width: 991px) {
  .app-menu-toggle {
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .app-mobile-header {
    display: flex;
  }

  .app-header {
    padding: 0 var(--spc-3);
  }
}

@media (max-width: 767px) {
  .app-header {
    padding: 0 var(--spc-2);
    height: auto;
    min-height: 56px;
    flex-wrap: wrap;
  }

  .app-menu-toggle {
    min-height: 44px;
    min-width: 44px;
  }
}

/* ==================== BREADCRUMBS ==================== */
.breadcrumb {
  background: transparent;
  padding: 0;
  margin: 0 0 var(--spc-2) 0;
  font-size: 0.85rem;
}

.breadcrumb-item+.breadcrumb-item::before {
  content: "\203A";
  color: var(--app-text-muted);
  padding: 0 var(--spc-2);
}

.breadcrumb-item a {
  color: var(--app-text-muted);
  text-decoration: none;
  transition: color var(--app-transition-fast);
}

.breadcrumb-item a:hover {
  color: var(--app-color-primary);
  text-decoration: none;
}

.breadcrumb-item.active {
  color: var(--app-text-main);
}
```
