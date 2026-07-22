# Contacts Pages (List + Detail)

The contacts list page with search/filter/pagination/bulk operations and the contact detail page.

## Contacts.razor (List Page)

```razor
@page "/contacts"
@rendermode InteractiveServer
@using Microsoft.AspNetCore.WebUtilities
@using System.Text.RegularExpressions
@inject IApiClient ApiClient
@inject ILogger<Contacts> Logger
@inject NavigationManager Navigation

<PageTitle>Contacts - App</PageTitle>

<!-- Keyboard shortcuts listener -->
<div @onkeydown="HandleGlobalKeyDown" @onkeydown:preventDefault="false" tabindex="-1" style="outline: none;">

<!-- Page Header -->
<div class="mb-4">
    <div class="d-flex align-items-center justify-content-between mb-2">
        <div>
            <h1 class="app-page-title mb-1">Contacts</h1>
            <p class="app-subtitle">Manage people and organizations</p>
        </div>
        <button class="btn app-btn-primary" @onclick="() => _showCreateModal = true">
            <i class="bi bi-plus-circle me-1"></i> New Contact
        </button>
    </div>
</div>

<!-- Search and Filters -->
<div class="mb-3">
    <div class="row g-3">
        <!-- Search -->
        <div class="col-md-4">
            <div class="input-group">
                <span class="input-group-text"><i class="bi bi-search"></i></span>
                <input type="text" class="form-control" placeholder="Search contacts..."
                       @bind="_searchQuery"
                       @bind:event="oninput"
                       @bind:after="OnSearchChanged" />
                @if (!string.IsNullOrWhiteSpace(_searchQuery))
                {
                    <button class="btn btn-outline-secondary" @onclick="ClearSearch">
                        <i class="bi bi-x"></i>
                    </button>
                }
            </div>
        </div>

        <!-- Contact Type Filter -->
        <div class="col-md-2">
            <select class="form-select" @bind="_selectedType" @bind:after="OnFiltersChanged">
                <option value="">All Types</option>
                <option value="CLIENT">Client</option>
                <option value="FAMILY">Family Member</option>
                <option value="ADJUSTER">Adjuster</option>
                <option value="MEDICAL">Medical Provider</option>
                <option value="INSURANCE">Insurance Carrier</option>
                <option value="LIENHOLDER">Lien Holder</option>
            </select>
        </div>

        <!-- Status Filter -->
        <div class="col-md-2">
            <select class="form-select" @bind="_selectedStatus" @bind:after="OnFiltersChanged">
                <option value="">All Statuses</option>
                <option value="true">Active</option>
                <option value="false">Inactive</option>
            </select>
        </div>

        <!-- Person/Organization Filter -->
        <div class="col-md-2">
            <select class="form-select" @bind="_selectedEntityType" @bind:after="OnFiltersChanged">
                <option value="">All</option>
                <option value="false">People</option>
                <option value="true">Organizations</option>
            </select>
        </div>

        <!-- Page Size -->
        <div class="col-md-2">
            <select class="form-select" @bind="_pageSize" @bind:after="OnPageSizeChanged">
                <option value="10">10 per page</option>
                <option value="25">25 per page</option>
                <option value="50">50 per page</option>
                <option value="100">100 per page</option>
            </select>
        </div>
    </div>

    @if (_hasActiveFilters)
    {
        <div class="mt-2">
            <button class="btn btn-sm btn-outline-secondary" @onclick="ClearAllFilters">
                <i class="bi bi-x-circle me-1"></i> Clear Filters (@_activeFilterCount)
            </button>
        </div>
    }
</div>

@if (_loading)
{
    <AppLoadingState Message="Loading contacts..." />
}
else if (_error != null)
{
    <div class="alert alert-danger app-alert-inline" role="alert">
        <i class="bi bi-exclamation-triangle me-2"></i>
        <strong>Error:</strong> @_error
        <button class="btn btn-sm btn-outline-light ms-2" @onclick="LoadContactsAsync">
            Retry
        </button>
    </div>
}
else if (_pagedResult == null || _pagedResult.Items.Count == 0)
{
    <AppEmptyState
        Icon="person-x"
        Title="No contacts found"
        Message="@(_hasActiveFilters ? "Try adjusting your search or filters." : "Get started by creating your first contact.")" />
}
else
{
    <div class="app-board-group">
        <div class="app-board-table">
            <table class="table">
                <thead>
                    <tr>
                        <th style="width: 40px;">
                            <input type="checkbox"
                                   class="form-check-input"
                                   checked="@_selectAll"
                                   @onchange="ToggleSelectAll"
                                   aria-label="Select all contacts" />
                        </th>
                        <th @onclick='() => ToggleSort("DisplayName")' style="cursor: pointer;">
                            Name @GetSortIcon("DisplayName")
                        </th>
                        <th>Type</th>
                        <th>Email</th>
                        <th>Phone</th>
                        <th @onclick='() => ToggleSort("CreatedDate")' style="cursor: pointer;">
                            Created @GetSortIcon("CreatedDate")
                        </th>
                        <th>Status</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var contact in _pagedResult.Items)
                    {
                        <tr class="app-board-row"
                            @onclick="() => NavigateToContact(contact.Id)"
                            tabindex="0"
                            @onkeydown="(e) => HandleKeyDown(e, contact.Id)"
                            role="button"
                            aria-label="View contact @contact.DisplayName">
                            <td @onclick:stopPropagation="true">
                                <input type="checkbox"
                                       class="form-check-input"
                                       checked="@_selectedContactIds.Contains(contact.Id)"
                                       @onchange="() => ToggleContactSelection(contact.Id)"
                                       aria-label="Select contact @contact.DisplayName" />
                            </td>
                            <td>
                                <div class="d-flex align-items-center">
                                    @if (contact.IsOrganization)
                                    {
                                        <i class="bi bi-building me-2 text-primary"></i>
                                    }
                                    else
                                    {
                                        <i class="bi bi-person me-2 text-primary"></i>
                                    }
                                    <strong>@((MarkupString)HighlightSearchTerm(contact.DisplayName, _searchQuery))</strong>
                                </div>
                            </td>
                            <td>
                                <ContactTypeBadge TypeCode="@contact.ContactTypeCode" TypeName="@contact.ContactTypeName" />
                            </td>
                            <td>
                                @if (!string.IsNullOrWhiteSpace(contact.Email))
                                {
                                    <a href="mailto:@contact.Email" @onclick:stopPropagation="true" class="text-decoration-none">
                                        @((MarkupString)HighlightSearchTerm(contact.Email, _searchQuery))
                                    </a>
                                }
                                else
                                {
                                    <span class="text-muted">-</span>
                                }
                            </td>
                            <td>
                                @if (!string.IsNullOrWhiteSpace(contact.PrimaryPhone))
                                {
                                    <a href="tel:@contact.PrimaryPhone" @onclick:stopPropagation="true" class="text-decoration-none">
                                        @contact.PrimaryPhone
                                    </a>
                                }
                                else
                                {
                                    <span class="text-muted">-</span>
                                }
                            </td>
                            <td>
                                <span class="text-muted">@contact.CreatedOnUtc.ToLocalTime().ToString("MMM dd, yyyy")</span>
                            </td>
                            <td>
                                @if (contact.IsActive)
                                {
                                    <AppBadge Status="success" Label="Active" />
                                }
                                else
                                {
                                    <AppBadge Status="secondary" Label="Inactive" />
                                }
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>
    </div>

    <!-- Pagination -->
    <div class="d-flex justify-content-between align-items-center mt-3">
        <p class="text-muted mb-0">
            Showing @((_pagedResult.Page - 1) * _pagedResult.PageSize + 1)-@Math.Min(_pagedResult.Page * _pagedResult.PageSize, _pagedResult.TotalCount)
            of @_pagedResult.TotalCount contact(s)
        </p>

        @if (_pagedResult.TotalPages > 1)
        {
            <nav aria-label="Contact list pagination">
                <ul class="pagination mb-0">
                    <li class="page-item @(!_pagedResult.HasPreviousPage ? "disabled" : "")">
                        <button class="page-link" @onclick="FirstPage" disabled="@(!_pagedResult.HasPreviousPage)">
                            <i class="bi bi-chevron-double-left"></i>
                        </button>
                    </li>
                    <li class="page-item @(!_pagedResult.HasPreviousPage ? "disabled" : "")">
                        <button class="page-link" @onclick="PreviousPage" disabled="@(!_pagedResult.HasPreviousPage)">
                            <i class="bi bi-chevron-left"></i>
                        </button>
                    </li>

                    @foreach (var pageNum in GetPageNumbers())
                    {
                        <li class="page-item @(pageNum == _currentPage ? "active" : "")">
                            <button class="page-link" @onclick="() => GoToPage(pageNum)">@pageNum</button>
                        </li>
                    }

                    <li class="page-item @(!_pagedResult.HasNextPage ? "disabled" : "")">
                        <button class="page-link" @onclick="NextPage" disabled="@(!_pagedResult.HasNextPage)">
                            <i class="bi bi-chevron-right"></i>
                        </button>
                    </li>
                    <li class="page-item @(!_pagedResult.HasNextPage ? "disabled" : "")">
                        <button class="page-link" @onclick="LastPage" disabled="@(!_pagedResult.HasNextPage)">
                            <i class="bi bi-chevron-double-right"></i>
                        </button>
                    </li>
                </ul>
            </nav>
        }
    </div>

    <div class="small text-muted mt-2 text-end">
        <i class="bi bi-info-circle me-1"></i>
        Click on any row to view contact details
    </div>
}

</div> <!-- End keyboard shortcuts listener -->

<!-- Bulk Action Bar -->
@if (_selectedContactIds.Any())
{
    <div class="bulk-action-bar position-fixed bottom-0 start-50 translate-middle-x mb-4 px-4 py-3 bg-primary text-white rounded shadow-lg" style="z-index: 1040;">
        <div class="d-flex align-items-center gap-3">
            <span class="fw-bold">@_selectedContactIds.Count selected</span>
            <div class="vr bg-white opacity-50"></div>
            <button class="btn btn-sm btn-light" @onclick="ShowBulkDeactivateConfirmation">
                <i class="bi bi-pause-circle me-1"></i> Deactivate
            </button>
            <button class="btn btn-sm btn-outline-light" @onclick="ClearSelection">
                <i class="bi bi-x-circle me-1"></i> Clear
            </button>
        </div>
    </div>
}

<!-- Bulk Deactivate Confirmation Modal -->
@if (_showBulkDeactivateConfirmation)
{
    <div class="modal show d-block" tabindex="-1" role="dialog">
        <div class="modal-dialog modal-dialog-centered" role="document">
            <div class="modal-content">
                <div class="modal-header">
                    <h5 class="modal-title">Deactivate Contacts</h5>
                    <button type="button" class="btn-close" @onclick="() => _showBulkDeactivateConfirmation = false"></button>
                </div>
                <div class="modal-body">
                    <p>Are you sure you want to deactivate <strong>@_selectedContactIds.Count contact(s)</strong>?</p>
                    <p class="text-muted small mb-0">These contacts will be marked as inactive but can be reactivated later.</p>
                </div>
                <div class="modal-footer">
                    <button type="button" class="btn btn-secondary" @onclick="() => _showBulkDeactivateConfirmation = false" disabled="@_isBulkProcessing">Cancel</button>
                    <button type="button" class="btn btn-warning" @onclick="BulkDeactivateContacts" disabled="@_isBulkProcessing">
                        @if (_isBulkProcessing)
                        {
                            <span class="spinner-border spinner-border-sm me-1"></span>
                            <span>Processing...</span>
                        }
                        else
                        {
                            <span>Deactivate @_selectedContactIds.Count Contact(s)</span>
                        }
                    </button>
                </div>
            </div>
        </div>
    </div>
    <div class="modal-backdrop show"></div>
}

<!-- Create Contact Modal -->
<CreateContactModal IsVisible="_showCreateModal"
                   IsVisibleChanged="OnCreateModalVisibilityChanged"
                   OnContactCreated="OnContactCreated" />

<style>
    mark {
        background-color: #fff3cd;
        color: #000;
        padding: 0.1em 0.2em;
        border-radius: 0.2em;
        font-weight: 600;
    }
</style>

@code {
    private PagedResult<ContactDto>? _pagedResult;
    private bool _loading = true;
    private string? _error;
    private bool _showCreateModal = false;

    // Bulk operations
    private HashSet<Guid> _selectedContactIds = new();
    private bool _selectAll = false;
    private bool _showBulkDeactivateConfirmation = false;
    private bool _isBulkProcessing = false;

    // Query state
    private string _searchQuery = string.Empty;
    private string _selectedType = string.Empty;
    private string _selectedStatus = string.Empty;
    private string _selectedEntityType = string.Empty;
    private int _currentPage = 1;
    private int _pageSize = 25;
    private string _sortBy = "DisplayName";
    private string _sortDirection = "asc";

    private System.Threading.Timer? _searchDebounceTimer;

    private bool _hasActiveFilters => !string.IsNullOrWhiteSpace(_searchQuery) ||
                                      !string.IsNullOrWhiteSpace(_selectedType) ||
                                      !string.IsNullOrWhiteSpace(_selectedStatus) ||
                                      !string.IsNullOrWhiteSpace(_selectedEntityType);

    private int _activeFilterCount
    {
        get
        {
            int count = 0;
            if (!string.IsNullOrWhiteSpace(_searchQuery)) count++;
            if (!string.IsNullOrWhiteSpace(_selectedType)) count++;
            if (!string.IsNullOrWhiteSpace(_selectedStatus)) count++;
            if (!string.IsNullOrWhiteSpace(_selectedEntityType)) count++;
            return count;
        }
    }

    protected override async Task OnInitializedAsync()
    {
        var uri = new Uri(Navigation.Uri);
        if (QueryHelpers.ParseQuery(uri.Query).TryGetValue("new", out var newValue) &&
            newValue.ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            _showCreateModal = true;
            Navigation.NavigateTo("/contacts", replace: true);
        }

        await LoadContactsAsync();
    }

    private async Task LoadContactsAsync()
    {
        try
        {
            _loading = true;
            _error = null;
            StateHasChanged();

            var queryParams = new ContactQueryParameters
            {
                Search = string.IsNullOrWhiteSpace(_searchQuery) ? null : _searchQuery,
                TypeCodes = string.IsNullOrWhiteSpace(_selectedType) ? null : new[] { _selectedType },
                IsActive = string.IsNullOrWhiteSpace(_selectedStatus) ? null : bool.Parse(_selectedStatus),
                IsOrganization = string.IsNullOrWhiteSpace(_selectedEntityType) ? null : bool.Parse(_selectedEntityType),
                Page = _currentPage,
                PageSize = _pageSize,
                SortBy = _sortBy,
                SortDirection = _sortDirection
            };

            _pagedResult = await ApiClient.GetContactsAsync(queryParams);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load contacts");
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
            StateHasChanged();
        }
    }

    private void OnSearchChanged()
    {
        _searchDebounceTimer?.Dispose();
        _searchDebounceTimer = new System.Threading.Timer(async _ =>
        {
            await InvokeAsync(async () =>
            {
                _currentPage = 1;
                await LoadContactsAsync();
            });
        }, null, 300, Timeout.Infinite);
    }

    private async Task ClearSearch()
    {
        _searchQuery = string.Empty;
        _currentPage = 1;
        await LoadContactsAsync();
    }

    private async Task OnFiltersChanged()
    {
        _currentPage = 1;
        await LoadContactsAsync();
    }

    private async Task OnPageSizeChanged()
    {
        _currentPage = 1;
        await LoadContactsAsync();
    }

    private async Task ClearAllFilters()
    {
        _searchQuery = string.Empty;
        _selectedType = string.Empty;
        _selectedStatus = string.Empty;
        _selectedEntityType = string.Empty;
        _currentPage = 1;
        await LoadContactsAsync();
    }

    private async Task ToggleSort(string columnName)
    {
        if (_sortBy == columnName)
        {
            _sortDirection = _sortDirection == "asc" ? "desc" : "asc";
        }
        else
        {
            _sortBy = columnName;
            _sortDirection = columnName == "DisplayName" ? "asc" : "desc";
        }

        await LoadContactsAsync();
    }

    private string GetSortIcon(string columnName)
    {
        if (_sortBy != columnName) return "";
        return _sortDirection == "asc" ? " ▲" : " ▼";
    }

    private async Task FirstPage()
    {
        if (_pagedResult?.HasPreviousPage == true)
        {
            _currentPage = 1;
            await LoadContactsAsync();
        }
    }

    private async Task PreviousPage()
    {
        if (_pagedResult?.HasPreviousPage == true)
        {
            _currentPage--;
            await LoadContactsAsync();
        }
    }

    private async Task NextPage()
    {
        if (_pagedResult?.HasNextPage == true)
        {
            _currentPage++;
            await LoadContactsAsync();
        }
    }

    private async Task LastPage()
    {
        if (_pagedResult?.HasNextPage == true && _pagedResult.TotalPages > 0)
        {
            _currentPage = _pagedResult.TotalPages;
            await LoadContactsAsync();
        }
    }

    private async Task GoToPage(int page)
    {
        if (page != _currentPage && page >= 1 && page <= (_pagedResult?.TotalPages ?? 1))
        {
            _currentPage = page;
            await LoadContactsAsync();
        }
    }

    private IEnumerable<int> GetPageNumbers()
    {
        if (_pagedResult == null || _pagedResult.TotalPages == 0)
            return Enumerable.Empty<int>();

        const int maxPagesToShow = 5;
        int startPage = Math.Max(1, _currentPage - maxPagesToShow / 2);
        int endPage = Math.Min(_pagedResult.TotalPages, startPage + maxPagesToShow - 1);

        if (endPage - startPage + 1 < maxPagesToShow)
        {
            startPage = Math.Max(1, endPage - maxPagesToShow + 1);
        }

        return Enumerable.Range(startPage, endPage - startPage + 1);
    }

    private void NavigateToContact(Guid contactId)
    {
        Navigation.NavigateTo($"/contacts/{contactId}");
    }

    private void HandleKeyDown(KeyboardEventArgs e, Guid contactId)
    {
        if (e.Key == "Enter" || e.Key == " ")
        {
            NavigateToContact(contactId);
        }
    }

    private async Task OnCreateModalVisibilityChanged(bool isVisible)
    {
        _showCreateModal = isVisible;
        StateHasChanged();
    }

    private async Task OnContactCreated(Guid contactId)
    {
        Logger.LogInformation("Contact created with ID: {ContactId}", contactId);
        await LoadContactsAsync();
    }

    private string HighlightSearchTerm(string text, string searchTerm)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(searchTerm))
            return System.Web.HttpUtility.HtmlEncode(text);

        try
        {
            var escapedSearchTerm = Regex.Escape(searchTerm.Trim());
            var regex = new Regex($"({escapedSearchTerm})", RegexOptions.IgnoreCase);
            var encodedText = System.Web.HttpUtility.HtmlEncode(text);
            var highlighted = regex.Replace(encodedText, "<mark>$1</mark>");
            return highlighted;
        }
        catch
        {
            return System.Web.HttpUtility.HtmlEncode(text);
        }
    }

    private void ToggleContactSelection(Guid contactId)
    {
        if (_selectedContactIds.Contains(contactId))
            _selectedContactIds.Remove(contactId);
        else
            _selectedContactIds.Add(contactId);

        _selectAll = _pagedResult != null && _selectedContactIds.Count == _pagedResult.Items.Count;
    }

    private void ToggleSelectAll()
    {
        if (_pagedResult == null) return;

        _selectAll = !_selectAll;

        if (_selectAll)
        {
            foreach (var contact in _pagedResult.Items)
                _selectedContactIds.Add(contact.Id);
        }
        else
        {
            foreach (var contact in _pagedResult.Items)
                _selectedContactIds.Remove(contact.Id);
        }
    }

    private void ClearSelection()
    {
        _selectedContactIds.Clear();
        _selectAll = false;
    }

    private void ShowBulkDeactivateConfirmation()
    {
        _showBulkDeactivateConfirmation = true;
    }

    private async Task BulkDeactivateContacts()
    {
        try
        {
            _isBulkProcessing = true;
            StateHasChanged();

            foreach (var contactId in _selectedContactIds.ToList())
            {
                try
                {
                    await ApiClient.DeactivateContactAsync(contactId);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to deactivate contact {ContactId}", contactId);
                }
            }

            _selectedContactIds.Clear();
            _selectAll = false;
            _showBulkDeactivateConfirmation = false;
            await LoadContactsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during bulk deactivation");
            _error = $"Bulk deactivation failed: {ex.Message}";
        }
        finally
        {
            _isBulkProcessing = false;
            StateHasChanged();
        }
    }

    private void HandleGlobalKeyDown(KeyboardEventArgs e)
    {
        if (_showCreateModal) return;

        switch (e.Key.ToLower())
        {
            case "n":
                if (e.CtrlKey || e.MetaKey)
                {
                    _showCreateModal = true;
                    StateHasChanged();
                }
                break;

            case "escape":
                if (!string.IsNullOrWhiteSpace(_searchQuery))
                {
                    _searchQuery = string.Empty;
                    _currentPage = 1;
                    _ = LoadContactsAsync();
                }
                else if (_selectedContactIds.Any())
                {
                    ClearSelection();
                }
                break;
        }
    }

    public void Dispose()
    {
        _searchDebounceTimer?.Dispose();
    }
}
```

## ContactDetail.razor (Detail Page)

```razor
@page "/contacts/{Id:guid}"
@rendermode InteractiveServer
@using App.Web.Components.Pages.Contact
@inject IApiClient ApiClient
@inject NavigationManager Navigation
@inject ILogger<ContactDetail> Logger

<PageTitle>@(_contact?.DisplayName ?? "Contact Details") - App</PageTitle>

@if (_isLoading)
{
    <AppLoadingState Message="Loading contact details..." />
}
else if (_errorMessage != null)
{
    <div class="alert alert-danger app-alert-inline" role="alert">
        <i class="bi bi-exclamation-triangle me-2"></i>
        @_errorMessage
        <button class="btn btn-sm btn-outline-light ms-2" @onclick="LoadContactAsync">
            Retry
        </button>
    </div>
}
else if (_contact != null)
{
    <!-- Breadcrumb -->
    <nav aria-label="breadcrumb" class="mb-3">
        <ol class="breadcrumb">
            <li class="breadcrumb-item"><a href="/">Home</a></li>
            <li class="breadcrumb-item"><a href="/contacts">Contacts</a></li>
            <li class="breadcrumb-item active" aria-current="page">@_contact.DisplayName</li>
        </ol>
    </nav>

    <!-- Contact Header -->
    <div class="mb-4">
        <div class="d-flex align-items-start justify-content-between mb-2">
            <div class="d-flex align-items-center gap-2">
                @if (_contact.IsOrganization)
                {
                    <i class="bi bi-building fs-1 text-primary"></i>
                }
                else
                {
                    <i class="bi bi-person-circle fs-1 text-primary"></i>
                }
                <div>
                    <h1 class="app-page-title mb-1">@_contact.DisplayName</h1>
                    <div class="d-flex align-items-center gap-2">
                        <ContactTypeBadge TypeCode="@_contact.ContactTypeCode" TypeName="@_contact.ContactTypeName" />
                        @if (_contact.IsActive)
                        {
                            <AppBadge Status="success" Label="Active" />
                        }
                        else
                        {
                            <AppBadge Status="secondary" Label="Inactive" />
                        }
                    </div>
                </div>
            </div>
            <div class="d-flex gap-2">
                <button class="btn btn-sm btn-outline-secondary" @onclick="() => _showEditModal = true">
                    <i class="bi bi-pencil me-1"></i> Edit
                </button>
                <div style="position: relative;">
                    <button class="btn btn-sm btn-outline-secondary" @onclick="ToggleActionsMenu">
                        <i class="bi bi-three-dots-vertical"></i>
                    </button>
                    @if (_showActionsMenu)
                    {
                        <div class="dropdown-menu dropdown-menu-end show" style="position: absolute; right: 0; top: 100%; z-index: 1050;">
                            @if (!string.IsNullOrWhiteSpace(_contact.Email))
                            {
                                <a class="dropdown-item" href="mailto:@_contact.Email" @onclick="() => _showActionsMenu = false">
                                    <i class="bi bi-envelope me-2"></i> Send Email
                                </a>
                            }
                            @if (_contact.IsActive)
                            {
                                <button class="dropdown-item" @onclick="ShowDeactivateConfirmation">
                                    <i class="bi bi-pause-circle me-2"></i> Deactivate
                                </button>
                            }
                            else
                            {
                                <button class="dropdown-item" @onclick="ReactivateContact">
                                    <i class="bi bi-play-circle me-2"></i> Reactivate
                                </button>
                            }
                            <div class="dropdown-divider"></div>
                            <button class="dropdown-item text-danger" @onclick="ShowDeleteConfirmation">
                                <i class="bi bi-trash me-2"></i> Delete
                            </button>
                        </div>
                    }
                </div>
            </div>
        </div>
    </div>

    <div class="row g-3">
        <!-- Left Column: Contact Information -->
        <div class="col-md-8">
            <!-- Contact Information Card -->
            <div class="mb-3">
                <AppCard>
                    <Header>
                        <h5 class="mb-0 app-section-title">Contact Information</h5>
                    </Header>
                    <ChildContent>
                        <div class="row g-4">
                            <!-- Column 1: Identity & Contact Info -->
                            <div class="col-md-6">
                                <div class="mb-4">
                                    <label class="text-secondary fw-semibold mb-1">
                                        @(_contact.IsOrganization ? "Organization Name" : "Full Name")
                                    </label>
                                    <div class="fs-5">
                                        @if (_contact.IsOrganization)
                                        {
                                            @_contact.OrganizationName
                                        }
                                        else
                                        {
                                            @_contact.FirstName
                                            @if (!string.IsNullOrWhiteSpace(_contact.MiddleName))
                                            {
                                                <text> @_contact.MiddleName</text>
                                            }
                                            <text> @_contact.LastName</text>
                                            @if (!string.IsNullOrWhiteSpace(_contact.Suffix))
                                            {
                                                <text>, @_contact.Suffix</text>
                                            }
                                        }
                                    </div>
                                </div>

                                <div class="mb-4">
                                    <label class="text-secondary fw-semibold mb-1">Contact Type</label>
                                    <div>@_contact.ContactTypeName</div>
                                </div>

                                @if (!string.IsNullOrWhiteSpace(_contact.Email))
                                {
                                    <div class="mb-4">
                                        <label class="text-secondary fw-semibold mb-1">Email</label>
                                        <div>
                                            <a href="mailto:@_contact.Email" class="text-decoration-none">
                                                <i class="bi bi-envelope me-1 text-muted"></i>@_contact.Email
                                            </a>
                                        </div>
                                    </div>
                                }

                                <div class="row">
                                    @if (!string.IsNullOrWhiteSpace(_contact.PrimaryPhone))
                                    {
                                        <div class="col-6 mb-3">
                                            <label class="text-secondary fw-semibold mb-1">Primary Phone</label>
                                            <div>
                                                <a href="tel:@_contact.PrimaryPhone" class="text-decoration-none">
                                                    <i class="bi bi-telephone me-1 text-muted"></i>@_contact.PrimaryPhone
                                                </a>
                                            </div>
                                        </div>
                                    }

                                    @if (!string.IsNullOrWhiteSpace(_contact.SecondaryPhone))
                                    {
                                        <div class="col-6 mb-3">
                                            <label class="text-secondary fw-semibold mb-1">Secondary Phone</label>
                                            <div>
                                                <a href="tel:@_contact.SecondaryPhone" class="text-decoration-none">
                                                    <i class="bi bi-telephone me-1 text-muted"></i>@_contact.SecondaryPhone
                                                </a>
                                            </div>
                                        </div>
                                    }
                                </div>

                                @if (!_contact.IsOrganization)
                                {
                                    <div class="row">
                                        @if (_contact.BirthDate.HasValue)
                                        {
                                            <div class="col-6 mb-3">
                                                <label class="text-secondary fw-semibold mb-1">Birth Date</label>
                                                <div>
                                                    <i class="bi bi-calendar-event me-1 text-muted"></i>@_contact.BirthDate.Value.ToString("MMM dd, yyyy")
                                                </div>
                                            </div>
                                        }

                                        @if (!string.IsNullOrWhiteSpace(_contact.SocialSecurityNumberMasked))
                                        {
                                            <div class="col-6 mb-3">
                                                <label class="text-secondary fw-semibold mb-1">SSN</label>
                                                <div class="d-flex align-items-center gap-2">
                                                    <i class="bi bi-shield-lock me-1 text-muted"></i>
                                                    @if (_showFullSsn && !string.IsNullOrWhiteSpace(_fullSsn))
                                                    {
                                                        <span>@_fullSsn</span>
                                                        <button class="btn btn-sm btn-outline-secondary py-0 px-1" @onclick="HideSsn" title="Hide SSN">
                                                            <i class="bi bi-eye-slash"></i>
                                                        </button>
                                                    }
                                                    else
                                                    {
                                                        <span>@_contact.SocialSecurityNumberMasked</span>
                                                        <button class="btn btn-sm btn-outline-secondary py-0 px-1" @onclick="ShowSsnAsync" disabled="@_isLoadingSsn" title="Show full SSN">
                                                            @if (_isLoadingSsn)
                                                            {
                                                                <span class="spinner-border spinner-border-sm"></span>
                                                            }
                                                            else
                                                            {
                                                                <i class="bi bi-eye"></i>
                                                            }
                                                        </button>
                                                    }
                                                </div>
                                            </div>
                                        }
                                    </div>
                                }
                            </div>

                            <!-- Column 2: Address & Metadata -->
                            <div class="col-md-6">
                                @if (_contact.Address != null && !string.IsNullOrWhiteSpace(_contact.Address.Street1))
                                {
                                    <div class="mb-4">
                                        <label class="text-secondary fw-semibold mb-1">Address</label>
                                        <div class="d-flex">
                                            <i class="bi bi-geo-alt me-2 text-muted mt-1"></i>
                                            <div>
                                                <div>@_contact.Address.Street1</div>
                                                @if (!string.IsNullOrWhiteSpace(_contact.Address.Street2))
                                                {
                                                    <div>@_contact.Address.Street2</div>
                                                }
                                                <div>
                                                    @_contact.Address.City@(string.IsNullOrWhiteSpace(_contact.Address.State) ? "" : ", ") @_contact.Address.State @_contact.Address.PostalCode
                                                </div>
                                                @if (!string.IsNullOrWhiteSpace(_contact.Address.Country))
                                                {
                                                    <div>@_contact.Address.Country</div>
                                                }
                                            </div>
                                        </div>
                                    </div>
                                }

                                <div class="row">
                                    <div class="col-6 mb-3">
                                        <label class="text-secondary fw-semibold mb-1">Created</label>
                                        <div>
                                            @_contact.CreatedOnUtc.ToLocalTime().ToString("MMM dd, yyyy")
                                            @if (!string.IsNullOrWhiteSpace(_contact.CreatedBy))
                                            {
                                                <div class="small text-muted">by @_contact.CreatedBy</div>
                                            }
                                        </div>
                                    </div>

                                    @if (_contact.ModifiedOnUtc.HasValue)
                                    {
                                        <div class="col-6 mb-3">
                                            <label class="text-secondary fw-semibold mb-1">Last Modified</label>
                                            <div>
                                                @_contact.ModifiedOnUtc.Value.ToLocalTime().ToString("MMM dd, yyyy")
                                                @if (!string.IsNullOrWhiteSpace(_contact.ModifiedBy))
                                                {
                                                    <div class="small text-muted">by @_contact.ModifiedBy</div>
                                                }
                                            </div>
                                        </div>
                                    }
                                </div>
                            </div>
                        </div>
                    </ChildContent>
                </AppCard>
            </div>

            <!-- Notes Card -->
            @if (!string.IsNullOrWhiteSpace(_contact.Notes) || _isEditingNotes)
            {
                <div class="mb-3">
                    <AppCard>
                        <Header>
                            <div class="d-flex justify-content-between align-items-center">
                                <h5 class="mb-0 app-section-title">Notes</h5>
                                @if (!_isEditingNotes)
                                {
                                    <button class="btn btn-sm btn-outline-secondary" @onclick="StartEditingNotes">
                                        <i class="bi bi-pencil me-1"></i> Edit
                                    </button>
                                }
                            </div>
                        </Header>
                        <ChildContent>
                            @if (_isEditingNotes)
                            {
                                <div>
                                    <textarea class="form-control mb-2"
                                              @bind="_notesEditText"
                                              rows="6"
                                              maxlength="2000"
                                              placeholder="Enter notes about this contact..."></textarea>
                                    <div class="d-flex justify-content-end gap-2">
                                        <button class="btn btn-sm btn-secondary" @onclick="CancelEditingNotes" disabled="@_isSavingNotes">
                                            Cancel
                                        </button>
                                        <button class="btn btn-sm app-btn-primary" @onclick="SaveNotes" disabled="@_isSavingNotes">
                                            @if (_isSavingNotes)
                                            {
                                                <span class="spinner-border spinner-border-sm me-1"></span>
                                                <span>Saving...</span>
                                            }
                                            else
                                            {
                                                <i class="bi bi-check-circle me-1"></i>
                                                <span>Save</span>
                                            }
                                        </button>
                                    </div>
                                </div>
                            }
                            else
                            {
                                <div style="white-space: pre-wrap;">@_contact.Notes</div>
                            }
                        </ChildContent>
                    </AppCard>
                </div>
            }
            else if (!_isEditingNotes)
            {
                <div class="mb-3">
                    <AppCard>
                        <Header>
                            <div class="d-flex justify-content-between align-items-center">
                                <h5 class="mb-0 app-section-title">Notes</h5>
                                <button class="btn btn-sm btn-outline-secondary" @onclick="StartEditingNotes">
                                    <i class="bi bi-plus-circle me-1"></i> Add Notes
                                </button>
                            </div>
                        </Header>
                        <ChildContent>
                            <div class="text-muted text-center py-3">
                                <i class="bi bi-journal-text fs-1 d-block mb-2"></i>
                                No notes yet
                            </div>
                        </ChildContent>
                    </AppCard>
                </div>
            }
        </div>

        <!-- Right Column: Related Information -->
        <div class="col-md-4">
            <!-- Associated Matters -->
            <div class="mb-3">
                <AppCard>
                    <Header>
                        <div class="d-flex justify-content-between align-items-center">
                            <h5 class="mb-0 app-section-title">Associated Matters</h5>
                            <button class="btn btn-sm btn-outline-secondary"
                                    @onclick="() => _showAddToMatterModal = true"
                                    title="Add to Matter">
                                <i class="bi bi-plus-circle"></i>
                            </button>
                        </div>
                    </Header>
                    <ChildContent>
                        @if (_loadingMatters)
                        {
                            <div class="text-center py-3">
                                <div class="spinner-border spinner-border-sm text-primary" role="status">
                                    <span class="visually-hidden">Loading...</span>
                                </div>
                                <p class="text-muted small mt-2">Loading matters...</p>
                            </div>
                        }
                        else if (_associatedMatters.Any())
                        {
                            <div class="list-group list-group-flush">
                                @foreach (var matterItem in _associatedMatters)
                                {
                                    <a href="/matters/@matterItem.Id" class="list-group-item list-group-item-action">
                                        <div class="d-flex justify-content-between align-items-start">
                                            <div class="flex-grow-1">
                                                <div class="fw-bold">@matterItem.MatterNumber</div>
                                                <small class="text-muted d-block">@matterItem.Title</small>
                                                <div class="mt-1">
                                                    <span class="badge bg-info text-dark me-1">@matterItem.PartyRole</span>
                                                    @if (matterItem.IsPrimaryParty)
                                                    {
                                                        <span class="badge bg-primary">Primary</span>
                                                    }
                                                </div>
                                            </div>
                                            <i class="bi bi-chevron-right text-muted"></i>
                                        </div>
                                    </a>
                                }
                            </div>
                        }
                        else
                        {
                            <div class="text-muted text-center py-3">
                                <i class="bi bi-folder-x fs-1 d-block mb-2"></i>
                                <small>No associated matters</small>
                            </div>
                        }
                    </ChildContent>
                </AppCard>
            </div>
        </div>
    </div>
}

<!-- Deactivate Confirmation Modal -->
@if (_showDeactivateConfirmation)
{
    <div class="modal show d-block" tabindex="-1" role="dialog">
        <div class="modal-dialog modal-dialog-centered" role="document">
            <div class="modal-content">
                <div class="modal-header">
                    <h5 class="modal-title">Deactivate Contact</h5>
                    <button type="button" class="btn-close" @onclick="() => _showDeactivateConfirmation = false"></button>
                </div>
                <div class="modal-body">
                    <p>Are you sure you want to deactivate <strong>@_contact?.DisplayName</strong>?</p>
                    <p class="text-muted small mb-0">This contact will be marked as inactive but can be reactivated later.</p>
                </div>
                <div class="modal-footer">
                    <button type="button" class="btn btn-secondary" @onclick="() => _showDeactivateConfirmation = false">Cancel</button>
                    <button type="button" class="btn btn-warning" @onclick="DeactivateContact" disabled="@_isProcessing">
                        @if (_isProcessing)
                        {
                            <span class="spinner-border spinner-border-sm me-1"></span>
                        }
                        Deactivate
                    </button>
                </div>
            </div>
        </div>
    </div>
    <div class="modal-backdrop show"></div>
}

<!-- Delete Confirmation Modal -->
@if (_showDeleteConfirmation)
{
    <div class="modal show d-block" tabindex="-1" role="dialog">
        <div class="modal-dialog modal-dialog-centered" role="document">
            <div class="modal-content">
                <div class="modal-header bg-danger text-white">
                    <h5 class="modal-title">Delete Contact</h5>
                    <button type="button" class="btn-close btn-close-white" @onclick="() => _showDeleteConfirmation = false"></button>
                </div>
                <div class="modal-body">
                    <div class="alert alert-danger" role="alert">
                        <i class="bi bi-exclamation-triangle me-2"></i>
                        <strong>Warning:</strong> This action cannot be undone.
                    </div>
                    <p>Are you sure you want to permanently delete <strong>@_contact?.DisplayName</strong>?</p>
                    <p class="text-muted small mb-0">All associated data will be removed.</p>
                </div>
                <div class="modal-footer">
                    <button type="button" class="btn btn-secondary" @onclick="() => _showDeleteConfirmation = false">Cancel</button>
                    <button type="button" class="btn btn-danger" @onclick="DeleteContact" disabled="@_isProcessing">
                        @if (_isProcessing)
                        {
                            <span class="spinner-border spinner-border-sm me-1"></span>
                        }
                        Delete Permanently
                    </button>
                </div>
            </div>
        </div>
    </div>
    <div class="modal-backdrop show"></div>
}

<!-- Edit Contact Modal -->
<EditContactModal ContactId="@Id"
                 IsVisible="_showEditModal"
                 IsVisibleChanged="OnEditModalVisibilityChanged"
                 OnContactUpdated="OnContactUpdated" />

<!-- Add to Matter Modal -->
@if (_contact != null)
{
    <AddToMatterModal ContactId="@_contact.Id"
                     ContactName="@_contact.DisplayName"
                     IsVisible="_showAddToMatterModal"
                     IsVisibleChanged="OnAddToMatterModalVisibilityChanged"
                     OnContactAddedToMatter="OnContactAddedToMatter" />
}

@code {
    [Parameter]
    public Guid Id { get; set; }

    private ContactDto? _contact;
    private bool _isLoading = true;
    private string? _errorMessage;
    private bool _showEditModal = false;
    private bool _isEditingNotes = false;
    private string _notesEditText = string.Empty;
    private bool _isSavingNotes = false;
    private bool _showDeactivateConfirmation = false;
    private bool _showDeleteConfirmation = false;
    private bool _isProcessing = false;
    private bool _showActionsMenu = false;

    // Associated matters
    private List<AssociatedMatterDto> _associatedMatters = new();
    private bool _loadingMatters = false;
    private bool _showAddToMatterModal = false;

    // SSN reveal
    private bool _showFullSsn = false;
    private string? _fullSsn = null;
    private bool _isLoadingSsn = false;

    protected override async Task OnInitializedAsync()
    {
        await LoadContactAsync();
        await LoadAssociatedMattersAsync();
    }

    private async Task LoadContactAsync()
    {
        try
        {
            _isLoading = true;
            _errorMessage = null;
            StateHasChanged();

            _contact = await ApiClient.GetContactByIdAsync(Id);

            if (_contact == null)
            {
                _errorMessage = "Contact not found.";
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _errorMessage = "Contact not found.";
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load contact {ContactId}", Id);
            _errorMessage = $"Failed to load contact: {ex.Message}";
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    private void StartEditingNotes()
    {
        _notesEditText = _contact?.Notes ?? string.Empty;
        _isEditingNotes = true;
    }

    private void CancelEditingNotes()
    {
        _isEditingNotes = false;
        _notesEditText = string.Empty;
    }

    private async Task SaveNotes()
    {
        if (_contact == null) return;

        try
        {
            _isSavingNotes = true;
            StateHasChanged();

            var request = new UpdateContactDetailsRequest
            {
                Email = _contact.Email,
                PrimaryPhone = _contact.PrimaryPhone,
                SecondaryPhone = _contact.SecondaryPhone,
                Address = _contact.Address,
                Notes = _notesEditText
            };

            await ApiClient.UpdateContactDetailsAsync(_contact.Id, request);

            _contact = _contact with { Notes = _notesEditText };
            _isEditingNotes = false;
            _notesEditText = string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update notes for contact {ContactId}", _contact.Id);
            _errorMessage = $"Failed to update notes: {ex.Message}";
        }
        finally
        {
            _isSavingNotes = false;
            StateHasChanged();
        }
    }

    private void ToggleActionsMenu()
    {
        _showActionsMenu = !_showActionsMenu;
    }

    private void ShowDeactivateConfirmation()
    {
        _showActionsMenu = false;
        _showDeactivateConfirmation = true;
    }

    private async Task DeactivateContact()
    {
        if (_contact == null) return;

        try
        {
            _isProcessing = true;
            StateHasChanged();

            await ApiClient.DeactivateContactAsync(_contact.Id);

            _showDeactivateConfirmation = false;
            await LoadContactAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to deactivate contact {ContactId}", _contact.Id);
            _errorMessage = $"Failed to deactivate contact: {ex.Message}";
            _showDeactivateConfirmation = false;
        }
        finally
        {
            _isProcessing = false;
            StateHasChanged();
        }
    }

    private async Task ReactivateContact()
    {
        if (_contact == null) return;

        try
        {
            _showActionsMenu = false;
            _isProcessing = true;
            StateHasChanged();

            await ApiClient.ReactivateContactAsync(_contact.Id);
            await LoadContactAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to reactivate contact {ContactId}", _contact.Id);
            _errorMessage = $"Failed to reactivate contact: {ex.Message}";
        }
        finally
        {
            _isProcessing = false;
            StateHasChanged();
        }
    }

    private void ShowDeleteConfirmation()
    {
        _showActionsMenu = false;
        _showDeleteConfirmation = true;
    }

    private async Task DeleteContact()
    {
        if (_contact == null) return;

        try
        {
            _isProcessing = true;
            StateHasChanged();

            await ApiClient.DeleteContactAsync(_contact.Id);
            Navigation.NavigateTo("/contacts");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete contact {ContactId}", _contact.Id);
            _errorMessage = $"Failed to delete contact: {ex.Message}";
            _showDeleteConfirmation = false;
        }
        finally
        {
            _isProcessing = false;
            StateHasChanged();
        }
    }

    private async Task OnEditModalVisibilityChanged(bool isVisible)
    {
        _showEditModal = isVisible;
        StateHasChanged();
    }

    private async Task OnContactUpdated()
    {
        await LoadContactAsync();
    }

    private async Task LoadAssociatedMattersAsync()
    {
        try
        {
            _loadingMatters = true;
            StateHasChanged();

            var matters = await ApiClient.GetAssociatedMattersAsync(Id);
            _associatedMatters = matters.ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load associated matters for contact {ContactId}", Id);
        }
        finally
        {
            _loadingMatters = false;
            StateHasChanged();
        }
    }

    private async Task OnAddToMatterModalVisibilityChanged(bool isVisible)
    {
        _showAddToMatterModal = isVisible;
        StateHasChanged();
    }

    private async Task OnContactAddedToMatter()
    {
        await LoadAssociatedMattersAsync();
    }

    private async Task ShowSsnAsync()
    {
        if (_contact == null) return;

        try
        {
            _isLoadingSsn = true;
            StateHasChanged();

            _fullSsn = await ApiClient.GetContactSsnAsync(_contact.Id);
            _showFullSsn = true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to retrieve SSN for contact {ContactId}", _contact.Id);
            _errorMessage = $"Failed to retrieve SSN: {ex.Message}";
        }
        finally
        {
            _isLoadingSsn = false;
            StateHasChanged();
        }
    }

    private void HideSsn()
    {
        _showFullSsn = false;
        _fullSsn = null;
    }
}
```
