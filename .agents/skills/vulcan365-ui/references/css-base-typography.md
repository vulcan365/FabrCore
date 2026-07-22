# Base Styles & Typography

Base typography, links, focus states, validation styles, and accessibility.

**File:** `wwwroot/css/app.css`

```css
/* ==================== BASE STYLES ==================== */
html, body {
  font-family: "Figtree", system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
  font-size: 16px;
  line-height: 1.6;
  color: var(--app-text-main);
  background-color: var(--app-color-bg);
  -webkit-font-smoothing: antialiased;
  -moz-osx-font-smoothing: grayscale;
}

body {
  font-size: 0.9rem;
  margin: 0;
  padding: 0;
}

/* ==================== TYPOGRAPHY ==================== */
h1, h2, h3, h4, h5, h6, .app-heading {
  font-family: "Poppins", "Figtree", system-ui, sans-serif;
  font-weight: 600;
  letter-spacing: 0.01em;
  color: var(--app-text-main);
  line-height: 1.3;
}

.app-page-title {
  font-size: 1.35rem;
  font-weight: 600;
  margin: 0;
  font-family: "Poppins", "Figtree", system-ui, sans-serif;
}

.app-subtitle {
  font-size: 0.875rem;
  color: var(--app-text-muted);
  margin: 0;
  line-height: 1.4;
}

.app-section-title {
  font-size: 1.1rem;
  font-weight: 600;
  margin-bottom: var(--spc-3);
  font-family: "Poppins", "Figtree", system-ui, sans-serif;
}

.small, small {
  font-size: 0.85rem;
}

.text-meta {
  font-size: 0.75rem;
  color: var(--app-text-muted);
}

/* ==================== LINKS ==================== */
a {
  color: var(--app-color-primary);
  text-decoration: none;
  transition: color var(--app-transition-fast);
}

a:hover {
  color: #4b4bff;
  text-decoration: underline;
}

/* ==================== FOCUS STATES ==================== */
.app-focusable:focus-visible {
  outline: none;
  box-shadow: var(--app-focus-ring);
}

button:focus-visible,
.btn:focus-visible {
  outline: 2px solid var(--app-color-primary);
  outline-offset: 2px;
}

/* ==================== VALIDATION STYLES ==================== */
.valid.modified:not([type=checkbox]) {
  border-color: var(--app-status-done);
}

.invalid {
  border-color: var(--app-status-stuck);
}

.invalid .form-control {
  border-color: var(--app-status-stuck);
}

.invalid .form-control:focus {
  border-color: var(--app-status-stuck);
  box-shadow: 0 0 0 0.2rem rgba(251, 39, 93, 0.15);
}

.valid .form-control {
  border-color: var(--app-status-done);
}

.validation-message {
  color: var(--app-status-stuck);
  font-size: 0.8rem;
  margin-top: var(--spc-1);
  display: block;
}

/* ==================== ERROR BOUNDARY ==================== */
.blazor-error-boundary {
  background: url(data:image/svg+xml;base64,...) no-repeat 1rem/1.8rem, #b32121;
  padding: 1rem 1rem 1rem 3.7rem;
  color: white;
}

.blazor-error-boundary::after {
  content: "An error has occurred.";
}

/* ==================== ACCESSIBILITY ==================== */
@media (prefers-reduced-motion: reduce) {
  * {
    animation-duration: 0.01ms !important;
    animation-iteration-count: 1 !important;
    transition-duration: 0.01ms !important;
  }
}

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  padding: 0;
  margin: -1px;
  overflow: hidden;
  clip: rect(0, 0, 0, 0);
  white-space: nowrap;
  border-width: 0;
}

/* ==================== UTILITY CLASSES ==================== */
.app-clickable {
  cursor: pointer;
}

.text-muted {
  color: var(--app-text-muted);
}
```
