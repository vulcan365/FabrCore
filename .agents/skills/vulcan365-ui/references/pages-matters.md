# Matters Pages (List + Detail)

The matters list page with search/filter/pagination and the matter detail page with tab navigation.

## Matters.razor (List Page)

```razor
@page "/matters"
@rendermode InteractiveServer
@using Microsoft.AspNetCore.WebUtilities
@inject IApiClient ApiClient
@inject ILogger<Matters> Logger
@inject NavigationManager Navigation

<PageTitle>Matters - App</PageTitle>

<!-- Page Header -->
<div class="mb-4">
    <div class="d-flex align-items-center justify-content-between mb-2">
        <div>
            <h1 class="app-page-title mb-1">Matters</h1>
            <p class="app-subtitle">Manage and track all legal matters</p>
        </div>
        <button class="btn app-btn-primary" @onclick="() => _showCreateModal = true">
            <i class="bi bi-plus-circle me-1"></i> New Matter
        </button>
    </div>
</div>

<!-- Search and Filters -->
<div class="mb-3">
    <div class="row g-3">
        <div class="col-md-4">
            <div class="input-group">
                <span class="input-group-text"><i class="bi bi-search"></i></span>
                <input type="text" class="form-control" placeholder="Search matters..."
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

        <div class="col-md-3">
            <select class="form-select" @bind="_selectedStatus" @bind:after="OnFiltersChanged">
                <option value="">All Statuses</option>
                <option value="OPEN">Open</option>
                <option value="ACTIVE">Active</option>
                <option value="DISCOVERY">Discovery</option>
                <option value="NEGOTIATION">Negotiation</option>
                <option value="LITIGATION">Litigation</option>
                <option value="SETTLED">Settled</option>
                <option value="CLOSED">Closed</option>
                <option value="DISMISSED">Dismissed</option>
            </select>
        </div>

        <div class="col-md-3">
            <select class="form-select" @bind="_selectedType" @bind:after="OnFiltersChanged">
                <option value="">All Types</option>
                <option value="AUTO">Auto Accident</option>
                <option value="SLIP">Slip & Fall</option>
                <option value="PREM">Premises Liability</option>
                <option value="DOG">Dog Bite</option>
                <option value="MEDMAL">Medical Malpractice</option>
                <option value="PROD">Product Liability</option>
                <option value="WD">Wrongful Death</option>
                <option value="CORP">Corporate</option>
            </select>
        </div>

        <div class="col-md-2">
            <select class="form-select" @bind="_pageSize" @bind:after="OnPageSizeChanged">
                <option value="10">10 per page</option>
                <option value="25">25 per page</option>
                <option value="50">50 per page</option>
            </select>
        </div>
    </div>
</div>

@if (_loading)
{
    <AppLoadingState Message="Loading matters..." />
}
else if (_error != null)
{
    <div class="alert alert-danger app-alert-inline" role="alert">
        <i class="bi bi-exclamation-triangle me-2"></i>
        <strong>Error:</strong> @_error
        <button class="btn btn-sm btn-outline-light ms-2" @onclick="LoadMattersAsync">Retry</button>
    </div>
}
else if (_pagedResult == null || _pagedResult.Items.Count == 0)
{
    <AppEmptyState
        Icon="folder-x"
        Title="No matters found"
        Message="@(_hasActiveFilters ? "Try adjusting your search or filters." : "Get started by creating your first matter.")" />
}
else
{
    <div class="app-board-group">
        <div class="app-board-table">
            <table class="table">
                <thead>
                    <tr>
                        <th @onclick='() => ToggleSort("MatterNumber")' style="cursor: pointer;">
                            Matter # @GetSortIcon("MatterNumber")
                        </th>
                        <th @onclick='() => ToggleSort("Title")' style="cursor: pointer;">
                            Title @GetSortIcon("Title")
                        </th>
                        <th>Client</th>
                        <th>Type</th>
                        <th @onclick='() => ToggleSort("StatusName")' style="cursor: pointer;">
                            Status @GetSortIcon("StatusName")
                        </th>
                        <th @onclick='() => ToggleSort("OpenedOnUtc")' style="cursor: pointer;">
                            Opened @GetSortIcon("OpenedOnUtc")
                        </th>
                        <th @onclick='() => ToggleSort("TrialDate")' style="cursor: pointer;">
                            Trial Date @GetSortIcon("TrialDate")
                        </th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var m in _pagedResult.Items)
                    {
                        <tr class="app-board-row"
                            @onclick="() => NavigateToMatter(m.Id)"
                            tabindex="0"
                            @onkeydown="(e) => HandleKeyDown(e, m.Id)"
                            role="button">
                            <td><code class="text-primary">@m.MatterNumber</code></td>
                            <td><strong>@m.Title</strong></td>
                            <td><span class="text-muted small">@(m.ClientName ?? "—")</span></td>
                            <td><span class="badge bg-light text-dark border">@m.MatterTypeName</span></td>
                            <td><AppBadge Status="@GetStatusClass(m.StatusCode)" Label="@m.StatusName" /></td>
                            <td><span class="text-muted">@m.OpenedOnUtc.ToLocalTime().ToString("MMM dd, yyyy")</span></td>
                            <td>
                                @if (m.TrialDate.HasValue)
                                {
                                    <span class="text-muted">@m.TrialDate.Value.ToLocalTime().ToString("MMM dd, yyyy")</span>
                                }
                                else
                                {
                                    <span class="text-muted">—</span>
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
            Showing @((_pagedResult.Page - 1) * _pagedResult.PageSize + 1)–@Math.Min(_pagedResult.Page * _pagedResult.PageSize, _pagedResult.TotalCount)
            of @_pagedResult.TotalCount matter(s)
        </p>

        @if (_pagedResult.TotalPages > 1)
        {
            <nav aria-label="Matter list pagination">
                <ul class="pagination mb-0">
                    <li class="page-item @(!_pagedResult.HasPreviousPage ? "disabled" : "")">
                        <button class="page-link" @onclick="() => GoToPage(1)" disabled="@(!_pagedResult.HasPreviousPage)">
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
                        <button class="page-link" @onclick="() => GoToPage(_pagedResult.TotalPages)" disabled="@(!_pagedResult.HasNextPage)">
                            <i class="bi bi-chevron-double-right"></i>
                        </button>
                    </li>
                </ul>
            </nav>
        }
    </div>
}

<!-- Create Matter Modal -->
<CreateMatterModal @bind-IsVisible="_showCreateModal" OnMatterCreated="HandleMatterCreated" />

@code {
    private PagedResult<MatterListItemDto>? _pagedResult;
    private bool _loading = true;
    private string? _error;
    private bool _showCreateModal;

    private string _searchQuery = string.Empty;
    private string _selectedStatus = string.Empty;
    private string _selectedType = string.Empty;
    private int _currentPage = 1;
    private int _pageSize = 25;
    private string _sortBy = "OpenedOnUtc";
    private string _sortDirection = "desc";

    private System.Threading.Timer? _searchDebounceTimer;
    private bool _hasActiveFilters => !string.IsNullOrWhiteSpace(_searchQuery) ||
                                      !string.IsNullOrWhiteSpace(_selectedStatus) ||
                                      !string.IsNullOrWhiteSpace(_selectedType);

    protected override async Task OnInitializedAsync()
    {
        var uri = new Uri(Navigation.Uri);
        if (QueryHelpers.ParseQuery(uri.Query).TryGetValue("new", out var newValue) &&
            newValue.ToString().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            _showCreateModal = true;
            Navigation.NavigateTo("/matters", replace: true);
        }
        await LoadMattersAsync();
    }

    private async Task LoadMattersAsync()
    {
        try
        {
            _loading = true;
            _error = null;
            StateHasChanged();

            var queryParams = new MatterQueryParameters
            {
                Search = string.IsNullOrWhiteSpace(_searchQuery) ? null : _searchQuery,
                StatusCodes = string.IsNullOrWhiteSpace(_selectedStatus) ? null : [_selectedStatus],
                TypeCodes = string.IsNullOrWhiteSpace(_selectedType) ? null : [_selectedType],
                Page = _currentPage,
                PageSize = _pageSize,
                SortBy = _sortBy,
                SortDirection = _sortDirection
            };

            _pagedResult = await ApiClient.GetMattersAsync(queryParams);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load matters");
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
            await InvokeAsync(async () => { _currentPage = 1; await LoadMattersAsync(); });
        }, null, 300, Timeout.Infinite);
    }

    private async Task ClearSearch() { _searchQuery = string.Empty; _currentPage = 1; await LoadMattersAsync(); }
    private async Task OnFiltersChanged() { _currentPage = 1; await LoadMattersAsync(); }
    private async Task OnPageSizeChanged() { _currentPage = 1; await LoadMattersAsync(); }

    private async Task ToggleSort(string columnName)
    {
        if (_sortBy == columnName) _sortDirection = _sortDirection == "asc" ? "desc" : "asc";
        else { _sortBy = columnName; _sortDirection = "desc"; }
        await LoadMattersAsync();
    }

    private string GetSortIcon(string columnName) =>
        _sortBy != columnName ? "" : _sortDirection == "asc" ? " ▲" : " ▼";

    private async Task PreviousPage() { if (_pagedResult?.HasPreviousPage == true) { _currentPage--; await LoadMattersAsync(); } }
    private async Task NextPage() { if (_pagedResult?.HasNextPage == true) { _currentPage++; await LoadMattersAsync(); } }

    private async Task GoToPage(int page)
    {
        if (_pagedResult != null && page >= 1 && page <= _pagedResult.TotalPages && page != _currentPage)
        { _currentPage = page; await LoadMattersAsync(); }
    }

    private IEnumerable<int> GetPageNumbers()
    {
        if (_pagedResult == null || _pagedResult.TotalPages == 0) yield break;
        const int maxPages = 5;
        var start = Math.Max(1, _currentPage - maxPages / 2);
        var end = Math.Min(_pagedResult.TotalPages, start + maxPages - 1);
        if (end - start < maxPages - 1) start = Math.Max(1, end - maxPages + 1);
        for (var i = start; i <= end; i++) yield return i;
    }

    private async Task HandleMatterCreated() { _currentPage = 1; await LoadMattersAsync(); }

    private void NavigateToMatter(Guid matterId) => Navigation.NavigateTo($"/matters/{matterId}");

    private void HandleKeyDown(KeyboardEventArgs e, Guid matterId)
    {
        if (e.Key is "Enter" or " ") NavigateToMatter(matterId);
    }

    private static string GetStatusClass(string statusCode) => statusCode.ToUpperInvariant() switch
    {
        "OPEN" or "ACTIVE" or "DISCOVERY" => "working",
        "SETTLED" or "CLOSED" => "done",
        "DISMISSED" => "stuck",
        "NEGOTIATION" or "LITIGATION" => "followup",
        _ => "neutral"
    };

    public void Dispose() { _searchDebounceTimer?.Dispose(); }
}
```

## MatterDetail.razor (Detail Page)

```razor
@page "/matters/{Id:guid}"
@rendermode InteractiveServer
@inject IApiClient ApiClient
@inject MatterStateService MatterState
@inject NavigationManager Navigation
@inject ILogger<MatterDetail> Logger

<PageTitle>@(matterDetail?.MatterNumber ?? "Matter Details") - App</PageTitle>

@if (isLoading)
{
    <AppLoadingState Message="Loading matter details..." />
}
else if (errorMessage != null)
{
    <div class="alert alert-danger app-alert-inline" role="alert">
        <i class="bi bi-exclamation-triangle me-2"></i>
        @errorMessage
        <button class="btn btn-sm btn-outline-light ms-2" @onclick="LoadMatterAsync">Retry</button>
    </div>
}
else if (matterDetail != null)
{
    <!-- Breadcrumb -->
    <nav aria-label="breadcrumb" class="mb-3">
        <ol class="breadcrumb">
            <li class="breadcrumb-item"><a href="/">Home</a></li>
            <li class="breadcrumb-item"><a href="/matters">Matters</a></li>
            <li class="breadcrumb-item active" aria-current="page">@matterDetail.MatterNumber</li>
        </ol>
    </nav>

    <!-- Matter Header -->
    <div class="mb-4">
        <div class="d-flex align-items-start justify-content-between mb-2">
            <div>
                <h1 class="app-page-title mb-1">@matterDetail.MatterNumber • @matterDetail.Title</h1>
                <div class="d-flex align-items-center gap-2">
                    <AppBadge Status="@GetStatusClass(matterDetail.Status.Code)" Label="@matterDetail.Status.Name" />
                    <span class="text-muted">@matterDetail.MatterType.Name</span>
                    @if (!string.IsNullOrEmpty(matterDetail.ClientName))
                    {
                        <span class="text-muted">• Client: @matterDetail.ClientName</span>
                    }
                </div>
            </div>
            <div class="d-flex gap-2">
                <button class="btn btn-sm btn-outline-secondary" @onclick="() => _showEditModal = true">
                    <i class="bi bi-pencil me-1"></i> Edit
                </button>
                <button class="btn btn-sm btn-outline-secondary" @onclick="() => _showStatusChangeModal = true">
                    <i class="bi bi-arrow-repeat me-1"></i> Status
                </button>
            </div>
        </div>
    </div>

    <!-- Tab Navigation -->
    <ul class="nav nav-tabs" role="tablist">
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "overview" ? "active" : "")" type="button" @onclick='() => SetActiveTab("overview")' role="tab">
                <i class="bi bi-grid me-1"></i> Overview
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "parties" ? "active" : "")" type="button" @onclick='() => SetActiveTab("parties")' role="tab">
                <i class="bi bi-people me-1"></i> Parties (@matterDetail.Parties.Count)
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "tasks" ? "active" : "")" type="button" @onclick='() => SetActiveTab("tasks")' role="tab">
                <i class="bi bi-check2-square me-1"></i> Tasks (@matterDetail.Tasks.Count)
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "calendar" ? "active" : "")" type="button" @onclick='() => SetActiveTab("calendar")' role="tab">
                <i class="bi bi-calendar-event me-1"></i> Calendar (@matterDetail.CalendarEntries.Count)
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "communications" ? "active" : "")" type="button" @onclick='() => SetActiveTab("communications")' role="tab">
                <i class="bi bi-chat-dots me-1"></i> Communications (@matterDetail.Communications.Count)
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "evidence" ? "active" : "")" type="button" @onclick='() => SetActiveTab("evidence")' role="tab">
                <i class="bi bi-box-seam me-1"></i> Evidence (@matterDetail.Evidence.Count)
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "litigation" ? "active" : "")" type="button" @onclick='() => SetActiveTab("litigation")' role="tab">
                <i class="bi bi-briefcase me-1"></i> Litigation (@matterDetail.LitigationItems.Count)
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "medical-records" ? "active" : "")" type="button" @onclick='() => SetActiveTab("medical-records")' role="tab">
                <i class="bi bi-hospital me-1"></i> Medical (@matterDetail.MedicalRecords.Count)
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "liens" ? "active" : "")" type="button" @onclick='() => SetActiveTab("liens")' role="tab">
                <i class="bi bi-link-45deg me-1"></i> Liens (@matterDetail.Liens.Count)
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "negotiations" ? "active" : "")" type="button" @onclick='() => SetActiveTab("negotiations")' role="tab">
                <i class="bi bi-currency-exchange me-1"></i> Negotiations (@matterDetail.Negotiations.Count)
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "damages" ? "active" : "")" type="button" @onclick='() => SetActiveTab("damages")' role="tab">
                <i class="bi bi-cash-stack me-1"></i> Damages (@matterDetail.Damages.Count)
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "expenses" ? "active" : "")" type="button" @onclick='() => SetActiveTab("expenses")' role="tab">
                <i class="bi bi-cash-coin me-1"></i> Expenses (@matterDetail.Expenses.Count)
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "settlement" ? "active" : "")" type="button" @onclick='() => SetActiveTab("settlement")' role="tab">
                <i class="bi bi-bank me-1"></i> Settlement @(matterDetail.Settlement != null ? "" : "(None)")
            </button>
        </li>
        <li class="nav-item" role="presentation">
            <button class="nav-link @(activeTab == "documents" ? "active" : "")" type="button" @onclick='() => SetActiveTab("documents")' role="tab">
                <i class="bi bi-folder me-1"></i> Documents
            </button>
        </li>
    </ul>

    <!-- Tab Content -->
    <div class="app-content">
        @switch (activeTab)
        {
            case "overview":
                <MatterOverviewTab Matter="@matterDetail" OnRefresh="LoadMatterAsync" />
                break;
            case "parties":
                <MatterPartiesTab MatterId="@matterDetail.Id" Parties="@matterDetail.Parties" OnRefresh="LoadMatterAsync" />
                break;
            case "tasks":
                <MatterTasksTab @key="@("tasks-" + matterDetail.Id)" MatterId="@matterDetail.Id" Tasks="@matterDetail.Tasks" OnRefresh="LoadMatterAsync" />
                break;
            case "calendar":
                <MatterCalendarTab @key="@("calendar-" + matterDetail.Id)" MatterId="@matterDetail.Id" CalendarEntries="@matterDetail.CalendarEntries" OnRefresh="LoadMatterAsync" />
                break;
            case "communications":
                <MatterCommunicationsTab MatterId="@matterDetail.Id" Communications="@matterDetail.Communications" OnRefresh="LoadMatterAsync" />
                break;
            case "evidence":
                <MatterEvidenceTab MatterId="@matterDetail.Id" Evidence="@matterDetail.Evidence" OnRefresh="LoadMatterAsync" />
                break;
            case "litigation":
                <MatterLitigationTab @key="@("litigation-" + matterDetail.Id)" MatterId="@matterDetail.Id" LitigationItems="@matterDetail.LitigationItems" OnRefresh="LoadMatterAsync" />
                break;
            case "medical-records":
                <MatterMedicalRecordsTab MatterId="@matterDetail.Id" MedicalRecords="@matterDetail.MedicalRecords" PISummary="@matterDetail.PISummary" OnRefresh="LoadMatterAsync" />
                break;
            case "liens":
                <MatterLiensTab MatterId="@matterDetail.Id" Liens="@matterDetail.Liens" PISummary="@matterDetail.PISummary" OnRefresh="LoadMatterAsync" />
                break;
            case "negotiations":
                <MatterNegotiationsTab MatterId="@matterDetail.Id" Negotiations="@matterDetail.Negotiations" PISummary="@matterDetail.PISummary" OnRefresh="LoadMatterAsync" />
                break;
            case "damages":
                <MatterDamagesTab MatterId="@matterDetail.Id" Damages="@matterDetail.Damages" PISummary="@matterDetail.PISummary" OnRefresh="LoadMatterAsync" />
                break;
            case "expenses":
                <MatterExpensesTab @key="@("expenses-" + matterDetail.Id)" MatterId="@matterDetail.Id" ExpenseItems="@matterDetail.Expenses" OnRefresh="LoadMatterAsync" />
                break;
            case "settlement":
                <MatterSettlementTab MatterId="@matterDetail.Id" Settlement="@matterDetail.Settlement" PISummary="@matterDetail.PISummary" OnRefresh="LoadMatterAsync" />
                break;
            case "documents":
                <MatterDocumentsTab @key="@("documents-" + matterDetail.Id)" MatterId="@matterDetail.Id" OnRefresh="LoadMatterAsync" />
                break;
        }
    </div>
}

<!-- Status Change Modal -->
<AppModal Title="Change Matter Status" @bind-IsVisible="_showStatusChangeModal" Size="md">
    <ChildContent>
        @if (matterDetail != null)
        {
            <div class="mb-3">
                <label class="form-label">Current Status</label>
                <div><AppBadge Status="@GetStatusClass(matterDetail.Status.Code)" Label="@matterDetail.Status.Name" /></div>
            </div>
            <div class="mb-3">
                <label for="newStatus" class="form-label">New Status <span class="text-danger">*</span></label>
                <select id="newStatus" class="form-select" @bind="_selectedStatus">
                    <option value="">Select new status...</option>
                    @foreach (var status in GetAvailableStatuses())
                    {
                        <option value="@status.Code">@status.Name</option>
                    }
                </select>
            </div>
            @if (!string.IsNullOrEmpty(_statusChangeError))
            {
                <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_statusChangeError</div>
            }
        }
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showStatusChangeModal = false" disabled="@_isChangingStatus">Cancel</button>
        <button type="button" class="btn btn-primary" @onclick="ChangeStatus" disabled="@(_isChangingStatus || string.IsNullOrEmpty(_selectedStatus))">
            @if (_isChangingStatus) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Changing...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>Change Status</span> }
        </button>
    </Footer>
</AppModal>

<!-- Edit Matter Modal -->
<EditMatterModal @bind-IsVisible="_showEditModal" MatterId="@Id" MatterDetail="@matterDetail" OnMatterSaved="LoadMatterAsync" />

@code {
    [Parameter] public Guid Id { get; set; }

    private MatterDetailDto? matterDetail;
    private bool isLoading = true;
    private string? errorMessage;
    private string activeTab = "overview";
    private bool _showEditModal;
    private bool _showStatusChangeModal;
    private string _selectedStatus = string.Empty;
    private bool _isChangingStatus;
    private string? _statusChangeError;

    protected override async Task OnInitializedAsync() => await LoadMatterAsync();

    protected override async Task OnParametersSetAsync()
    {
        if (Id != MatterState.CurrentMatterId) await LoadMatterAsync();
    }

    private async Task LoadMatterAsync()
    {
        try
        {
            var isInitialLoad = matterDetail == null;
            if (isInitialLoad) { isLoading = true; StateHasChanged(); }
            errorMessage = null;
            await MatterState.LoadMatterAsync(Id, ApiClient);
            matterDetail = MatterState.CurrentMatter;
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to load matter: {ex.Message}";
            matterDetail = null;
        }
        finally { isLoading = false; StateHasChanged(); }
    }

    private void SetActiveTab(string tab) => activeTab = tab;

    private static string GetStatusClass(string statusCode) => statusCode.ToUpperInvariant() switch
    {
        "OPEN" or "ACTIVE" or "DISCOVERY" => "working",
        "SETTLED" or "CLOSED" => "done",
        "DISMISSED" => "stuck",
        "NEGOTIATION" or "LITIGATION" => "followup",
        _ => "neutral"
    };

    private List<(string Code, string Name)> GetAvailableStatuses()
    {
        if (matterDetail == null) return [];
        var allStatuses = new List<(string Code, string Name)>
        {
            ("OPEN", "Open"), ("ACTIVE", "Active"), ("DISCOVERY", "Discovery"),
            ("NEGOTIATION", "Negotiation"), ("LITIGATION", "Litigation"),
            ("SETTLED", "Settled"), ("CLOSED", "Closed"), ("DISMISSED", "Dismissed")
        };
        return allStatuses.Where(s => s.Code != matterDetail.Status.Code).ToList();
    }

    private async Task ChangeStatus()
    {
        if (string.IsNullOrEmpty(_selectedStatus) || matterDetail == null) return;
        try
        {
            _isChangingStatus = true; _statusChangeError = null; StateHasChanged();
            await ApiClient.ChangeMatterStatusAsync(matterDetail.Id, _selectedStatus);
            _showStatusChangeModal = false; _selectedStatus = string.Empty;
            await LoadMatterAsync();
        }
        catch (Exception ex) { _statusChangeError = $"Failed to change status: {ex.Message}"; }
        finally { _isChangingStatus = false; StateHasChanged(); }
    }
}
```
