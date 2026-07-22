# Form Control Styles

Form inputs, selects, textareas, checkboxes, file uploads, search inputs.

**File:** `wwwroot/css/form-controls.css`

```css
/* ==================== FORM GENERAL ==================== */
.form-label {
  font-size: 0.875rem;
  font-weight: 500;
  color: var(--app-text-main);
  margin-bottom: var(--spc-2);
}

.form-label .text-danger { color: var(--app-status-stuck); }

.form-text {
  font-size: 0.75rem;
  color: var(--app-text-muted);
  margin-top: var(--spc-1);
}

/* ==================== FORM CONTROLS ==================== */
.form-control,
.form-select {
  border: 1px solid var(--app-border-subtle);
  border-radius: var(--app-radius-sm);
  padding: 6px 12px;
  min-height: 38px;
  font-size: 0.9rem;
  color: var(--app-text-main);
  background-color: var(--app-color-surface);
  transition: border-color var(--app-transition-fast),
    box-shadow var(--app-transition-fast);
}

.form-control::placeholder {
  color: var(--app-text-muted);
  opacity: 0.6;
}

.form-control:focus,
.form-select:focus {
  border-color: var(--app-color-primary);
  box-shadow: 0 0 0 0.2rem rgba(97, 97, 255, 0.15);
  outline: none;
}

.form-control:disabled,
.form-select:disabled {
  background-color: var(--app-color-surface-alt);
  color: var(--app-text-muted);
  cursor: not-allowed;
}

/* ==================== FORM FLOATING ==================== */
.form-floating>.form-control,
.form-floating>.form-select {
  height: calc(3.5rem + 2px);
  padding: 1rem 0.75rem;
}

.form-floating>label {
  padding: 1rem 0.75rem;
}

/* ==================== TEXTAREA ==================== */
textarea.form-control {
  min-height: 100px;
  resize: vertical;
}

/* ==================== CHECKBOXES & RADIOS ==================== */
.form-check-input {
  width: 1.25rem;
  height: 1.25rem;
  border: 1px solid var(--app-border-strong);
  transition: background-color var(--app-transition-fast),
    border-color var(--app-transition-fast);
}

.form-check-input:checked {
  background-color: var(--app-color-primary);
  border-color: var(--app-color-primary);
}

.form-check-input:focus {
  border-color: var(--app-color-primary);
  box-shadow: 0 0 0 0.2rem rgba(97, 97, 255, 0.15);
}

.form-check-label {
  font-size: 0.9rem;
  color: var(--app-text-main);
  margin-left: var(--spc-2);
}

/* ==================== FILE UPLOAD ==================== */
.app-file-upload {
  border: 2px dashed var(--app-border-subtle);
  border-radius: var(--app-radius-md);
  padding: var(--spc-5);
  background-color: var(--app-color-surface-alt);
  transition: all var(--app-transition-normal);
  text-align: center;
  cursor: pointer;
}

.app-file-upload:hover {
  border-color: var(--app-color-primary);
  background-color: #f0f3ff;
}

.app-file-upload.dragging {
  border-color: var(--app-color-primary);
  background-color: var(--app-color-primary-soft);
  border-style: solid;
}

.app-file-upload i { font-size: 2rem; color: var(--app-color-primary); display: block; margin-bottom: var(--spc-2); }
.app-file-upload p { color: var(--app-text-muted); font-size: 0.875rem; margin: 0; }

/* ==================== FORM BUTTONS ==================== */
.form-actions {
  margin-top: var(--spc-4);
  display: flex;
  justify-content: flex-end;
  gap: var(--spc-2);
}

/* ==================== SEARCH INPUT ==================== */
.app-search-input {
  position: relative;
  max-width: 400px;
}

.app-search-input input { padding-left: 2.5rem; }

.app-search-input i {
  position: absolute;
  left: 12px;
  top: 50%;
  transform: translateY(-50%);
  color: var(--app-text-muted);
  pointer-events: none;
}
```
