# Data Display Styles

Tables, boards, lists, activity feeds, tabs, pagination.

**File:** `wwwroot/css/data-display.css`

```css
/* ==================== BOARD/TABLE PATTERN ==================== */
.app-board-toolbar {
  display: flex;
  align-items: center;
  margin-bottom: var(--spc-3);
  padding: var(--spc-3);
  background: var(--app-color-surface);
  border-radius: var(--app-radius-md);
  gap: var(--spc-3);
}

.app-board-view-toggle .btn {
  border-radius: var(--app-radius-sm);
  font-size: 0.85rem;
  padding: 6px 12px;
}

.app-board-view-toggle .btn.active {
  background-color: var(--app-color-primary);
  border-color: var(--app-color-primary);
  color: #fff;
}

.app-board-group { margin-bottom: var(--spc-4); }

.app-board-group-header {
  padding: 8px 16px;
  background: linear-gradient(90deg, var(--app-color-primary-soft), transparent);
  border-radius: var(--app-radius-md) var(--app-radius-md) 0 0;
  border: 1px solid var(--app-border-subtle);
  border-bottom: none;
  display: flex;
  align-items: center;
}

.app-board-group-name {
  font-weight: 600;
  font-size: 0.9rem;
  color: var(--app-text-main);
  text-transform: uppercase;
  letter-spacing: 0.03em;
}

.app-board-group-count {
  margin-left: var(--spc-2);
  font-size: 0.75rem;
  color: var(--app-text-muted);
}

/* Table styling */
.app-board-table {
  background: var(--app-color-surface);
  border-radius: var(--app-radius-md);
  box-shadow: var(--app-shadow-sm);
  border: 1px solid var(--app-border-subtle);
  overflow: hidden;
}

.app-board-group-header+.app-board-table {
  border-radius: 0 0 var(--app-radius-md) var(--app-radius-md);
  border-top: none;
}

.app-board-table table { margin-bottom: 0; width: 100%; }

.app-board-table thead th {
  background-color: #fafbfd;
  border-bottom: 1px solid var(--app-border-subtle);
  font-size: 0.75rem;
  text-transform: uppercase;
  color: var(--app-text-muted);
  font-weight: 600;
  letter-spacing: 0.06em;
  padding: 12px 16px;
  vertical-align: middle;
}

.app-board-table tbody tr {
  border-bottom: 1px solid #eef1f8;
  transition: background-color var(--app-transition-fast);
}

.app-board-table tbody tr:last-child { border-bottom: none; }
.app-board-table tbody tr:hover { background-color: #f7f8ff; }

.app-board-table tbody tr td {
  vertical-align: middle;
  font-size: 0.88rem;
  padding: 12px 16px;
  color: var(--app-text-main);
}

.app-board-row { cursor: pointer; }
.app-board-row:focus-visible { outline: none; box-shadow: inset var(--app-focus-ring); }

/* ==================== SIMPLE TABLE ==================== */
.table { color: var(--app-text-main); }
.table>thead { background-color: #fafbfd; }

.table>thead>tr>th {
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--app-text-muted);
  border-bottom: 1px solid var(--app-border-subtle);
  padding: var(--spc-3);
}

.table>tbody>tr>td {
  padding: var(--spc-3);
  border-bottom: 1px solid #eef1f8;
}

.table>tbody>tr:hover { background-color: var(--app-color-surface-alt); }

/* ==================== RESPONSIVE TABLE ==================== */
.table-responsive {
  border-radius: var(--app-radius-md);
  box-shadow: var(--app-shadow-sm);
}

@media (max-width: 767px) {
  .table-responsive { border-radius: var(--app-radius-sm); }
}

/* ==================== LIST GROUPS ==================== */
.list-group-item {
  background-color: var(--app-color-surface);
  border: 1px solid var(--app-border-subtle);
  color: var(--app-text-main);
  padding: var(--spc-3);
  transition: background-color var(--app-transition-fast);
}

.list-group-item:hover { background-color: var(--app-color-surface-alt); }

.list-group-item.active {
  background-color: var(--app-color-primary);
  border-color: var(--app-color-primary);
  color: #fff;
}

/* ==================== ACTIVITY FEED ==================== */
.activity-feed { display: flex; flex-direction: column; gap: var(--spc-3); }

.activity-item { padding-bottom: var(--spc-3); border-bottom: 1px solid var(--app-border-subtle); }
.activity-item:last-child { padding-bottom: 0; border-bottom: none; }
.activity-item-header { display: flex; gap: var(--spc-3); align-items: flex-start; }

.activity-icon {
  width: 36px;
  height: 36px;
  border-radius: var(--app-radius-md);
  display: flex;
  align-items: center;
  justify-content: center;
  background-color: var(--app-color-primary-soft);
  color: var(--app-color-primary);
  flex-shrink: 0;
}

.activity-content { flex: 1; }
.activity-title { font-weight: 500; font-size: 0.9rem; color: var(--app-text-main); margin-bottom: var(--spc-1); }
.activity-description { font-size: 0.85rem; color: var(--app-text-muted); margin-bottom: var(--spc-1); }
.activity-time { font-size: 0.75rem; color: var(--app-text-muted); }

/* ==================== TABS ==================== */
.nav-tabs {
  border-bottom: 2px solid var(--app-border-subtle);
  margin-bottom: var(--spc-4);
}

.nav-tabs .nav-link {
  border: none;
  color: var(--app-text-muted);
  padding: var(--spc-3) var(--spc-4);
  font-weight: 500;
  font-size: 0.9rem;
  border-bottom: 2px solid transparent;
  margin-bottom: -2px;
  transition: color var(--app-transition-fast), border-color var(--app-transition-fast);
}

.nav-tabs .nav-link:hover { color: var(--app-text-main); border-color: var(--app-border-subtle); }

.nav-tabs .nav-link.active {
  color: var(--app-color-primary);
  border-color: var(--app-color-primary);
  background-color: transparent;
}

@media (max-width: 767px) {
  .nav-tabs {
    display: flex;
    flex-wrap: nowrap;
    overflow-x: auto;
    -webkit-overflow-scrolling: touch;
    scrollbar-width: none;
    -ms-overflow-style: none;
    padding-bottom: 2px;
  }

  .nav-tabs::-webkit-scrollbar { display: none; }
  .nav-tabs .nav-item { flex-shrink: 0; }

  .nav-tabs .nav-link {
    padding: var(--spc-2) var(--spc-3);
    font-size: 0.8rem;
    white-space: nowrap;
  }
}

/* ==================== PAGINATION ==================== */
.pagination { margin: var(--spc-4) 0; }

.page-link {
  color: var(--app-color-primary);
  border-color: var(--app-border-subtle);
  padding: 6px 12px;
  font-size: 0.875rem;
  transition: background-color var(--app-transition-fast), border-color var(--app-transition-fast);
}

.page-link:hover {
  background-color: var(--app-color-primary-soft);
  border-color: var(--app-color-primary);
  color: var(--app-color-primary);
}

.page-item.active .page-link {
  background-color: var(--app-color-primary);
  border-color: var(--app-color-primary);
}
```
