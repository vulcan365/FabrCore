# Design Tokens (CSS Custom Properties)

Complete CSS variables that define the app's visual language. Load as first CSS file.

**File:** `wwwroot/css/design-tokens.css`

```css
/* ============================================================
   App Design Tokens
   ============================================================ */

:root {
  /* ==================== BASE SURFACES ==================== */
  --app-color-bg: #f5f7fb;
  --app-color-surface: #ffffff;
  --app-color-surface-alt: #f0f3ff;

  /* ==================== BRAND CORE ==================== */
  --app-color-navy: #181b34;
  --app-color-primary: #6161ff;
  --app-color-primary-soft: #e6e8ff;

  /* ==================== SEMANTIC STATUS COLORS ==================== */
  --app-status-done: #00ca72;
  --app-status-working: #ffcc00;
  --app-status-stuck: #fb275d;
  --app-status-neutral: #cbd2e1;
  --app-status-cold: #cfd8ff;
  --app-status-followup: #a855f7;

  /* ==================== TEXT COLORS ==================== */
  --app-text-main: #181b34;
  --app-text-muted: #6f7690;
  --app-text-on-dark: #ffffff;

  /* ==================== BORDERS & DIVIDERS ==================== */
  --app-border-subtle: #e3e7f0;
  --app-border-strong: #c0c7d6;

  /* ==================== ELEVATION (SHADOWS) ==================== */
  --app-shadow-sm: 0 1px 2px rgba(0, 0, 0, 0.04);
  --app-shadow-md: 0 8px 24px rgba(0, 0, 0, 0.08);

  /* ==================== BORDER RADII ==================== */
  --app-radius-xs: 4px;
  --app-radius-sm: 6px;
  --app-radius-md: 16px;
  --app-radius-lg: 16px;

  /* ==================== SPACING SCALE (4px grid) ==================== */
  --spc-1: 4px;
  --spc-2: 8px;
  --spc-3: 12px;
  --spc-4: 16px;
  --spc-5: 24px;
  --spc-6: 32px;
  --spc-7: 48px;
  --spc-8: 64px;

  /* ==================== MOTION/TRANSITIONS ==================== */
  --app-transition-fast: 120ms ease-out;
  --app-transition-normal: 180ms ease-out;
  --app-transition-slow: 250ms ease-out;

  /* ==================== FOCUS RING ==================== */
  --app-focus-ring: 0 0 0 2px rgba(97, 97, 255, 0.4);

  /* ==================== Z-INDEX SCALE ==================== */
  --z-base: 1;
  --z-nav: 100;
  --z-header: 200;
  --z-dropdown: 1000;
  --z-sticky: 1020;
  --z-fixed: 1030;
  --z-modal-backdrop: 1040;
  --z-drawer-backdrop: 1040;
  --z-modal: 1050;
  --z-drawer: 1050;
  --z-popover: 1060;
  --z-tooltip: 1070;
  --z-toasts: 1100;
}
```
