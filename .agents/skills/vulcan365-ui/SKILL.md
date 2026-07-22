---
name: vulced365-ui
description: >
  Blazor Server (interactive) UI framework using Bootstrap 5.3.3 CDN for CSS, JS, fonts, and icons.
  Covers app shell layout, design tokens, reusable components (AppCard, AppModal, AppBadge, AppLoadingState, AppEmptyState),
  page archetypes (dashboard, list, detail with tabs), form patterns, data tables, and accessibility.
  Use when building new pages, components, or features in a Blazor Server app with Bootstrap CDN.
  Triggers on: "new page", "new component", "add UI", "Blazor page", "Bootstrap", "dashboard", "list page",
  "detail page", "modal", "form", "table", "sidebar", "layout", "design tokens", "CSS", "status badge",
  "empty state", "loading state", "stat card", "pagination", "tabs", "drawer", "toast",
  or any Blazor Server UI development task.
invocable: false
---

# Blazor Server + Bootstrap CDN UI Framework

A complete UI framework for building Blazor Server (interactive) applications using Bootstrap 5.3.3 via CDN, custom CSS design tokens, Google Fonts, and Bootstrap Icons. No NuGet UI component libraries needed.

## Quick Reference

| Concept | Pattern | Reference |
|---------|---------|-----------|
| App Shell | Sidebar + header + content flex layout | `references/main-layout.md` |
| Design Tokens | CSS custom properties for colors, spacing, shadows | `references/design-tokens.md` |
| Card | `<AppCard>` with Header/Body/Footer slots | `references/components.md` |
| Modal | `<AppModal>` with two-way IsVisible binding | `references/components.md` |
| Badge | `<AppBadge>` status pill (done/working/stuck/etc.) | `references/components.md` |
| Loading | `<AppLoadingState>` spinner + message | `references/components.md` |
| Empty | `<AppEmptyState>` icon + title + message + actions | `references/components.md` |
| Dashboard | Stat cards + activity feed + responsive grid | `references/pages-dashboard.md` |
| List Page | Search + filters + sortable table + pagination | `references/pages-matters.md` |
| Detail Page | Breadcrumb + tabs + tab content components | `references/pages-matters.md` |
| Create/Edit | Modal with EditForm + DataAnnotationsValidator | `references/pages-matter-modals.md` |

---

## CDN Dependencies

Load these in `App.razor` `<head>` in this exact order:

```html
<!-- Google Fonts: Figtree (body) and Poppins (headings) -->
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Figtree:wght@400;500;600;700&family=Poppins:wght@500;600;700&display=swap" />

<!-- Bootstrap 5.3.3 CSS -->
<link href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" rel="stylesheet" integrity="sha384-QWTKZyjpPEjISv5WaRU9OFeRpok6YctnYmDr5pNlyT2bRjXh0JMhjY6hW+ALEwIH" crossorigin="anonymous">

<!-- Bootstrap Icons 1.11.3 -->
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11.3/font/bootstrap-icons.min.css">

<!-- App Design System (load in this order) -->
<link rel="stylesheet" href="@Assets["css/design-tokens.css"]" />
<link rel="stylesheet" href="@Assets["css/app.css"]" />
<link rel="stylesheet" href="@Assets["css/layout.css"]" />
<link rel="stylesheet" href="@Assets["css/utilities.css"]" />
<link rel="stylesheet" href="@Assets["css/form-controls.css"]" />
<link rel="stylesheet" href="@Assets["css/data-display.css"]" />

<!-- Scoped component CSS (auto-generated) -->
<link rel="stylesheet" href="@Assets["YourApp.Web.styles.css"]" />
```

In `<body>` at the end:
```html
<!-- Bootstrap 5.3.3 JS Bundle (includes Popper) -->
<script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js" integrity="sha384-YvpcrYf0tY3lHB60NNkmXc5s9fDVZLESaAA55NDzOxhy9GkcIdslK1eN7N6jIeHz" crossorigin="anonymous"></script>
<script src="@Assets["_framework/blazor.web.js"]"></script>
```

---

## CSS Architecture

Six CSS files in `wwwroot/css/`, loaded in dependency order:

| File | Purpose | Reference |
|------|---------|-----------|
| `design-tokens.css` | CSS custom properties (colors, spacing, shadows, radii, z-index, transitions) | `references/design-tokens.md` |
| `app.css` | Base typography (Figtree body, Poppins headings), links, focus states, validation, accessibility | `references/css-base-typography.md` |
| `layout.css` | App shell, sidebar (72px), header (64px), breadcrumbs, responsive breakpoints | `references/css-layout.md` |
| `utilities.css` | Buttons, cards, badges, avatars, modals, drawers, empty states, loading, toasts, stat cards, alerts | `references/css-utilities.md` |
| `form-controls.css` | Form inputs, selects, textareas, checkboxes, file uploads, search inputs | `references/css-form-controls.md` |
| `data-display.css` | Tables, boards, lists, activity feeds, tabs, pagination | `references/css-data-display.md` |

### Key Design Tokens

```css
/* Brand */
--app-color-navy: #181b34;        /* Sidebar background */
--app-color-primary: #6161ff;     /* Primary accent */
--app-color-primary-soft: #e6e8ff; /* Light primary background */

/* Status Colors */
--app-status-done: #00ca72;       /* Green - completed */
--app-status-working: #ffcc00;    /* Yellow - in progress */
--app-status-stuck: #fb275d;      /* Red - blocked */
--app-status-neutral: #cbd2e1;    /* Gray - inactive */
--app-status-cold: #cfd8ff;       /* Light blue - cold */
--app-status-followup: #a855f7;   /* Purple - follow up */

/* Spacing (4px grid) */
--spc-1: 4px; --spc-2: 8px; --spc-3: 12px; --spc-4: 16px;
--spc-5: 24px; --spc-6: 32px; --spc-7: 48px; --spc-8: 64px;

/* Transitions */
--app-transition-fast: 120ms ease-out;
--app-transition-normal: 180ms ease-out;
--app-transition-slow: 250ms ease-out;
```

---

## Layout Architecture

The app uses a flex-based shell with sticky sidebar and responsive mobile drawer:

```
┌──────────┬─────────────────────────────────┐
│          │  Header (mobile only, 64px)     │
│ Sidebar  ├─────────────────────────────────┤
│  (72px)  │                                 │
│          │  Content Area                   │
│  Icons   │  (padding: 32px desktop,        │
│  +Labels │   16px tablet, 12px mobile)     │
│          │                                 │
└──────────┴─────────────────────────────────┘
```

**MainLayout.razor** structure:
```razor
<div class="app-shell">
    <aside class="app-sidebar">
        <div class="app-sidebar-brand">...</div>
        <nav class="app-sidebar-nav">
            <NavLink href="" class="nav-link"><i class="bi bi-house-door"></i><span>Home</span></NavLink>
            <!-- More nav links -->
        </nav>
    </aside>
    <div class="app-main">
        <header class="app-header app-mobile-header">
            <button class="app-menu-toggle">...</button>
        </header>
        <main class="app-content">@Body</main>
    </div>
</div>
```

See `references/main-layout.md` for full source.

---

## Component Catalog

### AppCard
Bootstrap card wrapper with RenderFragment slots.

```razor
<AppCard CssClass="optional-class">
    <Header>Card Title</Header>
    <ChildContent>Card body content here</ChildContent>
    <Footer>Footer actions</Footer>
</AppCard>
```

### AppModal
Bootstrap modal with two-way visibility binding.

```razor
<AppModal @bind-IsVisible="_showModal" Title="Create Item" Size="lg" Centered="true">
    <ChildContent>
        <!-- Form content -->
    </ChildContent>
    <Footer>
        <button class="btn btn-outline-secondary" @onclick="() => _showModal = false">Cancel</button>
        <button class="btn app-btn-primary" @onclick="Save">Save</button>
    </Footer>
</AppModal>
```

Parameters: `IsVisible` (two-way), `Title`, `Size` (sm/md/lg/xl), `Centered`, `ShowCloseButton`, `CloseOnBackdropClick`

### AppBadge
Status pill badge with semantic colors.

```razor
<AppBadge Status="done" Label="Complete" />
<AppBadge Status="working" Label="In Progress" />
<AppBadge Status="stuck" Label="Blocked" />
```

Status values: `done`, `working`, `stuck`, `neutral`, `cold`, `followup`, `primary`, `success`, `warning`, `danger`, `info`, `secondary`, `dark`, `light`

### AppLoadingState
Centered spinner with optional message.

```razor
<AppLoadingState Message="Loading records..." />
```

### AppEmptyState
Empty state placeholder with icon, title, message, and optional actions.

```razor
<AppEmptyState Icon="inbox" Title="No Items" Message="Create your first item to get started.">
    <Actions>
        <button class="btn app-btn-primary btn-sm">
            <i class="bi bi-plus-circle"></i> Create Item
        </button>
    </Actions>
</AppEmptyState>
```

See `references/components.md` for full source of all components.

---

## Page Archetypes

### 1. Dashboard Page
Stat cards in responsive grid + content cards.

```razor
@page "/"
<div class="d-flex align-items-center justify-content-between mb-4">
    <div>
        <h1 class="app-page-title">Dashboard</h1>
        <p class="app-subtitle">Welcome</p>
    </div>
</div>

<div class="row g-4 mb-4">
    <div class="col-sm-6 col-lg-3">
        <div class="stat-card">
            <div class="stat-card-icon" style="background-color: var(--app-color-primary-soft); color: var(--app-color-primary);">
                <i class="bi bi-folder"></i>
            </div>
            <div class="stat-card-body">
                <div class="stat-card-value">42</div>
                <div class="stat-card-label">Active Items</div>
            </div>
        </div>
    </div>
    <!-- More stat cards... -->
</div>
```

### 2. List Page
Search + filters + sortable table + pagination + modal create.

Key patterns:
- Page header with title + "Create" button
- Search input with `@bind:event="oninput"` and debounced `@bind:after`
- Filter dropdowns (page size, status, type)
- `app-board-table` wrapping a `<table>` with sortable `<th>` headers
- Clickable `app-board-row` rows with `role="button"` and keyboard handlers
- Pagination with first/prev/pages/next/last
- Three states: loading (`AppLoadingState`), empty (`AppEmptyState`), error (inline alert)

### 3. Detail Page with Tabs
Breadcrumb + header + Bootstrap nav-tabs + tab content components.

```razor
<nav aria-label="breadcrumb">
    <ol class="breadcrumb">
        <li class="breadcrumb-item"><a href="/items">Items</a></li>
        <li class="breadcrumb-item active" aria-current="page">@_item.Title</li>
    </ol>
</nav>

<ul class="nav nav-tabs">
    <li class="nav-item">
        <button class="nav-link @(_activeTab == "overview" ? "active" : "")"
                @onclick='() => _activeTab = "overview"'>Overview</button>
    </li>
    <!-- More tabs -->
</ul>

@switch (_activeTab)
{
    case "overview": <OverviewTab Item="_item" /> break;
    case "tasks": <TasksTab ItemId="_item.Id" /> break;
}
```

### 4. Create/Edit Modal
EditForm with DataAnnotationsValidator inside AppModal.

```razor
<AppModal @bind-IsVisible="_showModal" Title="Create Item" Size="lg">
    <ChildContent>
        <EditForm Model="@_model" OnValidSubmit="HandleSubmitAsync">
            <DataAnnotationsValidator />
            <div class="mb-3">
                <label class="form-label">Title <span class="text-danger">*</span></label>
                <InputText class="form-control" @bind-Value="_model.Title" />
                <ValidationMessage For="() => _model.Title" />
            </div>
            <!-- More fields -->
        </EditForm>
    </ChildContent>
    <Footer>
        <button class="btn btn-outline-secondary" @onclick="Close">Cancel</button>
        <button class="btn app-btn-primary" @onclick="HandleSubmitAsync" disabled="@_saving">
            @if (_saving) { <span class="spinner-border spinner-border-sm"></span> }
            Save
        </button>
    </Footer>
</AppModal>
```

See `references/pages-*.md` files for complete page implementations.

---

## Bootstrap Class Conventions

### Layout & Spacing
- Grid: `row`, `col-sm-6`, `col-md-4`, `col-lg-3`, `g-3`, `g-4`
- Flex: `d-flex`, `align-items-center`, `justify-content-between`, `gap-2`
- Margins: `mb-4`, `mb-2`, `mt-3`, `me-1`, `ms-2`

### Buttons
- Primary action: `btn app-btn-primary` (pill-shaped, brand purple)
- Secondary: `btn btn-outline-secondary` (pill-shaped)
- Small: add `btn-sm`
- Icon + text: `<i class="bi bi-plus-circle"></i> Create`
- Loading state: replace text with `spinner-border spinner-border-sm`

### Icons
Always use Bootstrap Icons: `<i class="bi bi-{icon-name}"></i>`

Common icons: `bi-house-door`, `bi-folder`, `bi-people`, `bi-gear`, `bi-plus-circle`, `bi-search`, `bi-pencil`, `bi-trash`, `bi-eye`, `bi-check-circle`, `bi-exclamation-triangle`, `bi-x-lg`, `bi-chevron-left`, `bi-chevron-right`, `bi-chevron-double-left`, `bi-chevron-double-right`

### Forms
- Labels: `form-label` with `<span class="text-danger">*</span>` for required
- Inputs: `form-control`
- Selects: `form-select`
- Checkboxes: `form-check-input` + `form-check-label`
- Actions footer: `form-actions` (flex, justify-end, gap)

### Tables
```
app-board-group > app-board-table > table > thead/tbody
```
- Headers: uppercase, muted, 0.75rem
- Rows: `app-board-row` with `cursor: pointer` and hover highlight
- Status column: `<AppBadge Status="..." Label="..." />`

---

## Accessibility Requirements

1. **ARIA** — `aria-label` on buttons, `aria-modal="true"` on modals, `role="button"` on clickable rows
2. **Keyboard** — `tabindex="0"` on interactive elements, handle `@onkeydown` for Enter/Space navigation
3. **Focus** — `focus-visible` outlines using `--app-focus-ring` variable
4. **Motion** — `@media (prefers-reduced-motion: reduce)` disables animations
5. **Screen readers** — `visually-hidden` class for off-screen text
6. **Semantic HTML** — `<nav>`, `<main>`, `<aside>`, `<header>` elements

---

## Data State Pattern

Every data-driven component should handle three states:

```razor
@if (_loading)
{
    <AppLoadingState Message="Loading..." />
}
else if (!string.IsNullOrEmpty(_error))
{
    <div class="alert alert-danger app-alert-inline">
        <i class="bi bi-exclamation-triangle me-1"></i> @_error
        <button class="btn btn-sm btn-outline-danger ms-2" @onclick="LoadDataAsync">Retry</button>
    </div>
}
else if (_items == null || !_items.Any())
{
    <AppEmptyState Icon="inbox" Title="No Items" Message="Nothing to display." />
}
else
{
    <!-- Render data table/cards/content -->
}
```

---

## Render Mode

All pages use interactive server rendering:

```razor
@rendermode InteractiveServer
```

Configure in `Program.cs`:
```csharp
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
```
