# Reusable Components

All shared UI components in `Components/Common/`.

---

## AppCard.razor

Bootstrap card wrapper with RenderFragment slots.

```razor
<div class="card app-card @CssClass">
    @if (Header != null)
    {
        <div class="card-header">
            @Header
        </div>
    }

    <div class="card-body">
        @ChildContent
    </div>

    @if (Footer != null)
    {
        <div class="card-footer">
            @Footer
        </div>
    }
</div>

@code {
    [Parameter] public RenderFragment? Header { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public RenderFragment? Footer { get; set; }
    [Parameter] public string? CssClass { get; set; }
}
```

---

## AppModal.razor

Bootstrap modal with two-way visibility binding, size options, and auto-focus.

```razor
@if (IsVisible)
{
    <div class="modal show d-block" tabindex="-1" role="dialog" aria-modal="true" aria-labelledby="modalTitle" @ref="_modalElement">
        <div class="modal-dialog @GetSizeClass() @(Centered ? "modal-dialog-centered" : "")" role="document">
            <div class="modal-content">
                @if (!string.IsNullOrEmpty(Title) || ShowCloseButton)
                {
                    <div class="modal-header">
                        @if (!string.IsNullOrEmpty(Title))
                        {
                            <h2 class="modal-title h5" id="modalTitle">@Title</h2>
                        }
                        @if (ShowCloseButton)
                        {
                            <button type="button" class="btn-close" @onclick="Close" aria-label="Close modal"></button>
                        }
                    </div>
                }

                <div class="modal-body">
                    @ChildContent
                </div>

                @if (Footer != null)
                {
                    <div class="modal-footer">
                        @Footer
                    </div>
                }
            </div>
        </div>
    </div>
    <div class="modal-backdrop show" @onclick="OnBackdropClick"></div>
}

@code {
    [Parameter] public bool IsVisible { get; set; }
    [Parameter] public EventCallback<bool> IsVisibleChanged { get; set; }
    [Parameter] public string? Title { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public RenderFragment? Footer { get; set; }
    [Parameter] public string Size { get; set; } = "md";
    [Parameter] public bool Centered { get; set; } = true;
    [Parameter] public bool ShowCloseButton { get; set; } = true;
    [Parameter] public bool CloseOnBackdropClick { get; set; } = true;

    private ElementReference _modalElement;

    private string GetSizeClass() => Size.ToLower() switch
    {
        "sm" => "modal-sm",
        "lg" => "modal-lg",
        "xl" => "modal-xl",
        _ => ""
    };

    private async Task Close()
    {
        IsVisible = false;
        await IsVisibleChanged.InvokeAsync(false);
    }

    private async Task OnBackdropClick()
    {
        if (CloseOnBackdropClick)
            await Close();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (IsVisible && firstRender)
        {
            try { await _modalElement.FocusAsync(); }
            catch { /* Ignore focus errors */ }
        }
    }
}
```

---

## AppBadge.razor

Status pill badge with semantic color coding.

```razor
<span class="app-status-pill app-status-@Status.ToLower() @CssClass">
    @Label
</span>

@code {
    [Parameter, EditorRequired] public string Status { get; set; } = "neutral";
    [Parameter, EditorRequired] public string Label { get; set; } = string.Empty;
    [Parameter] public string? CssClass { get; set; }
}
```

Status values: `done`, `working`, `stuck`, `neutral`, `cold`, `followup`, `primary`, `success`, `warning`, `danger`, `info`, `secondary`, `dark`, `light`

---

## ContactTypeBadge.razor

Wraps AppBadge with contact-type-specific color coding.

```razor
@{
    var badgeConfig = GetBadgeConfig(TypeCode);
}

<AppBadge Status="@badgeConfig.Status" Label="@(TypeName ?? badgeConfig.DisplayName)" />

@code {
    [Parameter, EditorRequired] public string TypeCode { get; set; } = string.Empty;
    [Parameter] public string? TypeName { get; set; }

    private record BadgeConfig(string Status, string DisplayName);

    private BadgeConfig GetBadgeConfig(string typeCode)
    {
        return typeCode.ToUpperInvariant() switch
        {
            "CLIENT" => new BadgeConfig("primary", "Client"),
            "FAMILY" => new BadgeConfig("info", "Family Member"),
            "ADJUSTER" => new BadgeConfig("warning", "Adjuster"),
            "MEDICAL" => new BadgeConfig("success", "Medical Provider"),
            "INSURANCE" => new BadgeConfig("secondary", "Insurance Carrier"),
            "LIENHOLDER" => new BadgeConfig("dark", "Lien Holder"),
            "ATTORNEY" => new BadgeConfig("secondary", "Attorney"),
            "JUDGE" => new BadgeConfig("dark", "Judge"),
            "WITNESS" => new BadgeConfig("info", "Witness"),
            "EXPERT" => new BadgeConfig("success", "Expert"),
            "COURT" => new BadgeConfig("dark", "Court"),
            "VENDOR" => new BadgeConfig("secondary", "Vendor"),
            "OTHER" => new BadgeConfig("light", "Other"),
            _ => new BadgeConfig("light", typeCode)
        };
    }
}
```

---

## AppLoadingState.razor

Centered spinner with optional message.

```razor
<div class="app-loading-spinner @CssClass">
    <div class="spinner-border" role="status">
        <span class="visually-hidden">Loading...</span>
    </div>

    @if (!string.IsNullOrEmpty(Message))
    {
        <p>@Message</p>
    }
</div>

@code {
    [Parameter] public string? Message { get; set; }
    [Parameter] public string? CssClass { get; set; }
}
```

---

## AppEmptyState.razor

Empty state placeholder with icon, title, message, and optional action slot.

```razor
<div class="app-empty-state @CssClass">
    @if (!string.IsNullOrEmpty(Icon))
    {
        <i class="bi bi-@Icon"></i>
    }

    @if (!string.IsNullOrEmpty(Title))
    {
        <h5>@Title</h5>
    }

    @if (!string.IsNullOrEmpty(Message))
    {
        <p>@Message</p>
    }

    @if (Actions != null)
    {
        <div class="mt-3">
            @Actions
        </div>
    }
</div>

@code {
    [Parameter] public string? Icon { get; set; } = "inbox";
    [Parameter] public string? Title { get; set; }
    [Parameter] public string? Message { get; set; }
    [Parameter] public RenderFragment? Actions { get; set; }
    [Parameter] public string? CssClass { get; set; }
}
```

---

## ContactAutocomplete.razor

Searchable contact dropdown with debounced API search (300ms).

```razor
@inject IApiClient ApiClient
@inject ILogger<ContactAutocomplete> Logger

<div class="contact-autocomplete">
    <label class="form-label">
        @Label
        @if (Required) { <span class="text-danger">*</span> }
    </label>

    @if (SelectedContact != null)
    {
        <div class="selected-contact p-2 border rounded bg-light d-flex justify-content-between align-items-center">
            <div>
                <div class="fw-bold">@SelectedContact.DisplayName</div>
                <div class="small text-muted">
                    @if (!string.IsNullOrEmpty(SelectedContact.Email))
                    { <span class="me-2">@SelectedContact.Email</span> }
                    @if (!string.IsNullOrEmpty(SelectedContact.Phone))
                    { <span>@SelectedContact.Phone</span> }
                </div>
            </div>
            <button type="button" class="btn btn-sm btn-outline-secondary" @onclick="ClearSelection">
                <i class="bi bi-x"></i>
            </button>
        </div>
    }
    else
    {
        <div class="input-group">
            <input type="text" class="form-control" placeholder="Search by name or email..."
                   value="@_searchQuery" @oninput="HandleSearchInput"
                   @onfocus="() => _showDropdown = true" required="@Required" />
            @if (!string.IsNullOrWhiteSpace(_searchQuery))
            {
                <button type="button" class="btn btn-outline-secondary" @onclick="ClearSearch">
                    <i class="bi bi-x"></i>
                </button>
            }
        </div>

        @if (_showDropdown && _searchResults.Count > 0)
        {
            <div class="autocomplete-dropdown border rounded mt-1 shadow-sm">
                @foreach (var contact in _searchResults)
                {
                    <button type="button" class="dropdown-item d-block w-100 text-start p-2 border-bottom"
                            @onclick="() => SelectContact(contact)">
                        <div class="fw-bold">@contact.DisplayName</div>
                        <div class="small text-muted">
                            @if (!string.IsNullOrEmpty(contact.Email))
                            { <span class="me-2">@contact.Email</span> }
                            <span class="badge bg-secondary ms-2">@contact.ContactTypeCode</span>
                        </div>
                    </button>
                }
            </div>
        }
        else if (_showDropdown && !string.IsNullOrWhiteSpace(_searchQuery) && !_isSearching && _searchResults.Count == 0)
        {
            <div class="autocomplete-dropdown border rounded mt-1 shadow-sm p-2 text-muted">
                No contacts found
            </div>
        }

        @if (_isSearching)
        {
            <div class="small text-muted mt-1">
                <span class="spinner-border spinner-border-sm me-1"></span> Searching...
            </div>
        }
    }
</div>

<style>
    .contact-autocomplete { position: relative; }
    .autocomplete-dropdown { position: absolute; z-index: 1050; background: white; max-height: 300px; overflow-y: auto; width: 100%; }
    .autocomplete-dropdown .dropdown-item { cursor: pointer; background: none; border: none; border-radius: 0; }
    .autocomplete-dropdown .dropdown-item:hover { background-color: #f8f9fa; }
    .selected-contact { min-height: 60px; }
</style>

@code {
    [Parameter] public string Label { get; set; } = "Contact";
    [Parameter] public bool Required { get; set; }
    [Parameter] public ContactSummaryDto? SelectedContact { get; set; }
    [Parameter] public EventCallback<ContactSummaryDto?> SelectedContactChanged { get; set; }
    [Parameter] public Guid? InitialContactId { get; set; }
    [Parameter] public string? ContactTypeCode { get; set; }

    private string _searchQuery = string.Empty;
    private List<ContactSummaryDto> _searchResults = new();
    private bool _showDropdown;
    private bool _isSearching;
    private System.Threading.Timer? _debounceTimer;

    // ... search, select, clear methods with 300ms debounce timer
}
```

---

## UserAutocomplete.razor

Same pattern as ContactAutocomplete but for users. Identical debounce and UI structure.

---

## ContactCard.razor

Reusable contact card with clickable behavior, keyboard support, and optional actions.

```razor
<div class="contact-card @(Clickable ? "contact-card-clickable" : "") @CssClass"
     @onclick="HandleClick"
     tabindex="@(Clickable ? "0" : "-1")"
     @onkeydown="HandleKeyDown"
     role="@(Clickable ? "button" : "article")"
     aria-label="@(Clickable ? $"View contact {Contact.DisplayName}" : Contact.DisplayName)">

    <div class="d-flex align-items-start gap-3">
        <div class="contact-card-icon">
            @if (Contact.IsOrganization)
            { <i class="bi bi-building fs-3 text-primary"></i> }
            else
            { <i class="bi bi-person-circle fs-3 text-primary"></i> }
        </div>

        <div class="flex-grow-1">
            <div class="d-flex align-items-start justify-content-between mb-2">
                <div>
                    <h6 class="contact-card-name mb-1">@Contact.DisplayName</h6>
                    <ContactTypeBadge TypeCode="@Contact.ContactTypeCode" />
                </div>
                @if (ShowActions)
                {
                    <button class="btn btn-sm btn-outline-secondary" @onclick:stopPropagation="true"
                            @onclick="() => OnActionClick.InvokeAsync(Contact)">
                        <i class="bi bi-three-dots-vertical"></i>
                    </button>
                }
            </div>

            <div class="contact-card-details">
                @if (!string.IsNullOrWhiteSpace(Contact.Email))
                {
                    <div class="contact-card-detail">
                        <i class="bi bi-envelope text-muted me-1"></i>
                        <a href="mailto:@Contact.Email" @onclick:stopPropagation="true">@Contact.Email</a>
                    </div>
                }
                @if (!string.IsNullOrWhiteSpace(Contact.Phone))
                {
                    <div class="contact-card-detail">
                        <i class="bi bi-telephone text-muted me-1"></i>
                        <a href="tel:@Contact.Phone" @onclick:stopPropagation="true">@Contact.Phone</a>
                    </div>
                }
            </div>
        </div>
    </div>
</div>

@code {
    [Parameter, EditorRequired] public ContactSummaryDto Contact { get; set; } = null!;
    [Parameter] public bool Clickable { get; set; } = true;
    [Parameter] public bool ShowActions { get; set; } = false;
    [Parameter] public string CssClass { get; set; } = string.Empty;
    [Parameter] public EventCallback<ContactSummaryDto> OnClick { get; set; }
    [Parameter] public EventCallback<ContactSummaryDto> OnActionClick { get; set; }

    private async Task HandleClick() { if (Clickable && OnClick.HasDelegate) await OnClick.InvokeAsync(Contact); }
    private async Task HandleKeyDown(KeyboardEventArgs e) { if (Clickable && (e.Key == "Enter" || e.Key == " ")) await HandleClick(); }
}
```
