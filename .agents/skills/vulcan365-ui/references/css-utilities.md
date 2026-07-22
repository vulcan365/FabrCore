# Utility Styles

Buttons, cards, badges, avatars, modals, drawers, empty states, loading, toasts, stat cards, alerts.

**File:** `wwwroot/css/utilities.css`

```css
/* ==================== BUTTONS ==================== */
.btn {
  white-space: nowrap;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.25rem;
}

.btn .bi {
  flex-shrink: 0;
  line-height: 1;
  color: inherit;
  margin-right: 0 !important;
}

.btn-sm .bi {
  font-size: 0.875em;
}

.app-btn-primary {
  background-color: var(--app-color-primary);
  border-color: var(--app-color-primary);
  border-radius: 999px;
  color: #fff;
  transition: background-color var(--app-transition-fast),
              box-shadow var(--app-transition-fast);
}

.app-btn-primary:hover,
.app-btn-primary:focus-visible {
  background-color: #4b4bff;
  border-color: #4b4bff;
  box-shadow: var(--app-shadow-sm);
  color: #fff;
}

.app-btn-primary:active {
  background-color: #3b3bef;
  border-color: #3b3bef;
  color: #fff;
}

.btn-outline-secondary,
.btn-outline-danger {
  border-radius: 999px;
}

@media (max-width: 767px) {
  .btn {
    min-height: 44px;
    padding: 12px 16px;
  }

  .btn-sm {
    min-height: 36px;
    padding: 6px 12px;
  }

  .app-sidebar-nav .nav-link {
    padding: 12px 16px;
    min-height: 44px;
  }
}

/* ==================== CARDS ==================== */
.app-card {
  background: var(--app-color-surface);
  border-radius: var(--app-radius-lg);
  border: none;
  box-shadow: var(--app-shadow-md);
  margin-bottom: var(--spc-4);
}

.app-card .card-header {
  background-color: transparent;
  border-bottom: 1px solid var(--app-border-subtle);
  padding: var(--spc-3) var(--spc-4);
  font-weight: 600;
}

.app-card .card-body {
  padding: var(--spc-4);
}

.app-card .card-footer {
  background-color: transparent;
  border-top: 1px solid var(--app-border-subtle);
  padding: var(--spc-3) var(--spc-4);
}

/* ==================== STATUS BADGES/PILLS ==================== */
.app-status-pill {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  padding: 2px 10px;
  border-radius: 999px;
  font-size: 0.75rem;
  font-weight: 600;
  letter-spacing: 0.03em;
  text-transform: uppercase;
}

.app-status-done { background-color: var(--app-status-done); color: #fff; }
.app-status-working { background-color: var(--app-status-working); color: #4a3b00; }
.app-status-stuck { background-color: var(--app-status-stuck); color: #fff; }
.app-status-neutral { background-color: var(--app-status-neutral); color: #1f2633; }
.app-status-cold { background-color: var(--app-status-cold); color: #3730a3; }
.app-status-followup { background-color: var(--app-status-followup); color: #fff; }

/* ==================== AVATARS ==================== */
.app-avatar-xs,
.app-avatar-sm,
.app-avatar-md {
  border-radius: 999px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  font-weight: 600;
  background-color: var(--app-color-primary);
  color: var(--app-text-on-dark);
}

.app-avatar-xs { width: 24px; height: 24px; font-size: 0.65rem; }
.app-avatar-sm { width: 32px; height: 32px; font-size: 0.7rem; }
.app-avatar-md { width: 48px; height: 48px; font-size: 0.9rem; }

/* ==================== MODALS ==================== */
.modal-content {
  border-radius: var(--app-radius-lg);
  border: none;
  box-shadow: var(--app-shadow-md);
}

.modal-header { border-bottom: 1px solid var(--app-border-subtle); padding: var(--spc-4); }
.modal-body { padding: var(--spc-4); }
.modal-footer { border-top: 1px solid var(--app-border-subtle); padding: var(--spc-4); }
.modal-backdrop.show { opacity: 0.5; }

/* ==================== DRAWERS (SIDE PANELS) ==================== */
.app-drawer-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(14, 18, 35, 0.28);
  z-index: var(--z-drawer-backdrop);
  animation: fadeIn var(--app-transition-normal);
}

.app-drawer {
  position: fixed;
  top: 0;
  right: 0;
  width: min(420px, 100%);
  height: 100%;
  background: var(--app-color-surface);
  box-shadow: -6px 0 24px rgba(0, 0, 0, 0.15);
  padding: var(--spc-5);
  z-index: var(--z-drawer);
  overflow-y: auto;
  border-radius: var(--app-radius-lg) 0 0 var(--app-radius-lg);
  animation: slideInRight var(--app-transition-normal);
}

@keyframes slideInRight {
  from { transform: translateX(100%); }
  to { transform: translateX(0); }
}

.app-drawer-header { display: flex; align-items: flex-start; margin-bottom: var(--spc-4); }
.app-drawer-close {
  margin-left: auto;
  background: transparent;
  border: none;
  font-size: 1.2rem;
  color: var(--app-text-muted);
  cursor: pointer;
  padding: var(--spc-2);
  transition: color var(--app-transition-fast);
}
.app-drawer-close:hover { color: var(--app-text-main); }

/* ==================== EMPTY STATES ==================== */
.app-empty-state {
  text-align: center;
  padding: var(--spc-6) var(--spc-4);
  max-width: 420px;
  margin: 0 auto;
}

.app-empty-state i { font-size: 3rem; color: var(--app-text-muted); display: block; margin-bottom: var(--spc-3); }
.app-empty-state h5 { font-weight: 600; margin-bottom: var(--spc-3); color: var(--app-text-main); }
.app-empty-state p { color: var(--app-text-muted); margin-bottom: var(--spc-4); }

/* ==================== LOADING STATES ==================== */
.app-loading-spinner {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: var(--spc-6);
}

.app-loading-spinner .spinner-border { color: var(--app-color-primary); }
.app-loading-spinner p { margin-top: var(--spc-3); color: var(--app-text-muted); }

/* ==================== TOASTS ==================== */
.app-toasts {
  position: fixed;
  bottom: var(--spc-4);
  right: var(--spc-4);
  z-index: var(--z-toasts);
}

.app-toast {
  min-width: 260px;
  border-radius: var(--app-radius-md);
  box-shadow: var(--app-shadow-md);
  margin-top: var(--spc-2);
  animation: slideInUp 0.3s ease-out;
}

@keyframes slideInUp {
  from { transform: translateY(100%); opacity: 0; }
  to { transform: translateY(0); opacity: 1; }
}

/* ==================== DETAIL GRID ==================== */
.app-detail-grid .app-detail-label {
  font-size: 0.75rem;
  text-transform: uppercase;
  color: var(--app-text-muted);
  font-weight: 600;
  letter-spacing: 0.03em;
}

.app-detail-grid .app-detail-value {
  color: var(--app-text-main);
}

/* ==================== ALERTS ==================== */
.app-alert-inline {
  border-radius: var(--app-radius-md);
  padding: var(--spc-3);
  margin-bottom: var(--spc-3);
}

/* ==================== STAT CARDS (KPI DISPLAYS) ==================== */
.stat-card {
  background: var(--app-color-surface);
  border-radius: var(--app-radius-lg);
  padding: var(--spc-4);
  box-shadow: var(--app-shadow-md);
  display: flex;
  gap: var(--spc-3);
}

.stat-card-icon {
  width: 48px;
  height: 48px;
  border-radius: var(--app-radius-md);
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 1.5rem;
  flex-shrink: 0;
}

.stat-card-body { flex: 1; }

.stat-card-value {
  font-size: 1.75rem;
  font-weight: 700;
  line-height: 1;
  margin-bottom: var(--spc-1);
  color: var(--app-text-main);
}

.stat-card-label {
  font-size: 0.85rem;
  color: var(--app-text-muted);
  margin-bottom: var(--spc-2);
}

.stat-card-detail {
  font-size: 0.75rem;
  display: flex;
  align-items: center;
  gap: 2px;
}
```
