# Matter Tab Components

Individual tab components used within the matter detail page. Each tab handles CRUD for its domain area.

## MatterOverviewTab.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<MatterOverviewTab> Logger
@inject NavigationManager Navigation

<div class="row mt-3">
    <!-- Main Info Column -->
    <div class="col-lg-8">
        <AppCard>
            <Header>
                <div class="d-flex justify-content-between align-items-center">
                    <h5 class="mb-0">Matter Information</h5>
                    <div class="d-flex align-items-center gap-2">
                        <AppBadge Status="@GetStatusClass(Matter.Status.Code)" Label="@Matter.Status.Name" />
                        <button class="btn btn-sm btn-outline-secondary" @onclick="OpenStatusChangeModal" title="Change Status">
                            <i class="bi bi-arrow-repeat"></i>
                        </button>
                        <button class="btn btn-sm btn-outline-primary" @onclick="OpenEditMatterModal" title="Edit Details">
                            <i class="bi bi-pencil-square"></i>
                        </button>
                    </div>
                </div>
            </Header>
            <ChildContent>
                <div class="row g-3">
                    <div class="col-md-6">
                        <div class="mb-3">
                            <label class="form-label text-muted small mb-0">Client</label>
                            <div class="fw-semibold">@(Matter.ClientName ?? "N/A")</div>
                        </div>
                    </div>
                    <div class="col-md-6">
                        <div class="mb-3">
                            <label class="form-label text-muted small mb-0">Matter Type</label>
                            <div class="fw-semibold">@Matter.MatterType.Name</div>
                        </div>
                    </div>
                    <div class="col-md-6">
                        <div class="mb-3">
                            <label class="form-label text-muted small mb-0">Jurisdiction</label>
                            <div class="fw-semibold">@(Matter.Jurisdiction ?? "N/A")</div>
                        </div>
                    </div>
                    <div class="col-md-6">
                        <div class="mb-3">
                            <label class="form-label text-muted small mb-0">Court #</label>
                            <div class="fw-semibold">@(Matter.CourtMatterNumber ?? "N/A")</div>
                        </div>
                    </div>
                    <div class="col-md-6">
                        <div class="mb-3">
                            <label class="form-label text-muted small mb-0">Assigned Attorney</label>
                            <div class="d-flex align-items-center gap-2">
                                <span class="fw-semibold">@(Matter.AssignedAttorneyName ?? "Unassigned")</span>
                                <button class="btn btn-sm btn-outline-secondary" @onclick="OpenAttorneyModal">
                                    <i class="bi bi-pencil"></i>
                                </button>
                            </div>
                        </div>
                    </div>
                    <div class="col-md-6">
                        <div class="mb-3">
                            <label class="form-label text-muted small mb-0">Estimated Value</label>
                            <div class="fw-semibold">@(Matter.EstimatedValue?.ToString("C") ?? "N/A")</div>
                        </div>
                    </div>
                    <div class="col-md-6">
                        <div class="mb-3">
                            <label class="form-label text-muted small mb-0">Opened On</label>
                            <div class="fw-semibold">@Matter.OpenedOnUtc.ToLocalTime().ToString("MMM dd, yyyy")</div>
                        </div>
                    </div>
                    <div class="col-md-6">
                        <div class="mb-3">
                            <label class="form-label text-muted small mb-0">Closed On</label>
                            <div class="fw-semibold">@(Matter.ClosedOnUtc?.ToLocalTime().ToString("MMM dd, yyyy") ?? "Open")</div>
                        </div>
                    </div>
                    <div class="col-md-6">
                        <div class="mb-3">
                            <label class="form-label text-muted small mb-0">Trial Date</label>
                            <div class="fw-semibold">@(Matter.TrialDate?.ToLocalTime().ToString("MMM dd, yyyy") ?? "Not set")</div>
                        </div>
                    </div>
                    <div class="col-md-6">
                        <div class="mb-3">
                            <label class="form-label text-muted small mb-0">Date of Incident</label>
                            <div class="fw-semibold">@(Matter.DateOfIncidentUtc?.ToLocalTime().ToString("MMM dd, yyyy") ?? "N/A")</div>
                        </div>
                    </div>
                    <div class="col-md-6">
                        <div class="mb-3">
                            <label class="form-label text-muted small mb-0">Accident Type</label>
                            <div>
                                @if (!string.IsNullOrWhiteSpace(Matter.AccidentType))
                                {
                                    <span class="badge bg-info text-dark">@Matter.AccidentType</span>
                                }
                                else
                                {
                                    <span class="text-muted">N/A</span>
                                }
                            </div>
                        </div>
                    </div>
                    @if (!string.IsNullOrWhiteSpace(Matter.Description))
                    {
                        <div class="col-12">
                            <label class="form-label text-muted small mb-0">Description</label>
                            <div>@Matter.Description</div>
                        </div>
                    }
                    @if (!string.IsNullOrWhiteSpace(Matter.IncidentDescription))
                    {
                        <div class="col-12">
                            <label class="form-label text-muted small mb-0">Incident Description</label>
                            <div>@Matter.IncidentDescription</div>
                        </div>
                    }
                </div>
            </ChildContent>
        </AppCard>

        <!-- Insurance Claims -->
        <AppCard CssClass="mt-3">
            <Header>
                <div class="d-flex justify-content-between align-items-center">
                    <h6 class="mb-0"><i class="bi bi-shield-check me-2"></i>Insurance Claims</h6>
                    <button class="btn btn-sm app-btn-primary" @onclick="StartAddClaim">
                        <i class="bi bi-plus-circle me-1"></i> Add Claim
                    </button>
                </div>
            </Header>
            <ChildContent>
                @if (Matter.InsuranceClaims.Count == 0)
                {
                    <div class="text-center text-muted py-4">
                        <i class="bi bi-shield fs-1 d-block mb-2"></i>
                        <p class="mb-0">No insurance claims recorded.</p>
                        <button class="btn btn-link btn-sm" @onclick="StartAddClaim">Add the first claim</button>
                    </div>
                }
                else
                {
                    <div class="table-responsive">
                        <table class="table table-sm table-hover mb-0">
                            <thead class="table-light">
                                <tr>
                                    <th>Claim #</th>
                                    <th>Carrier</th>
                                    <th>Adjuster</th>
                                    <th>Policy #</th>
                                    <th>Coverage</th>
                                    <th>Notes</th>
                                    <th class="text-end" style="width:100px;"></th>
                                </tr>
                            </thead>
                            <tbody>
                                @foreach (var claim in Matter.InsuranceClaims)
                                {
                                    <tr>
                                        <td class="fw-bold">@claim.ClaimNumber</td>
                                        <td>
                                            @if (claim.CarrierContactId.HasValue && !string.IsNullOrWhiteSpace(claim.CarrierName))
                                            {
                                                <a href="/contacts/@claim.CarrierContactId" class="text-decoration-none">
                                                    <i class="bi bi-person-circle me-1"></i>@claim.CarrierName
                                                </a>
                                            }
                                            else
                                            {
                                                <span class="text-muted">@(claim.CarrierName ?? "N/A")</span>
                                            }
                                        </td>
                                        <td>
                                            @if (claim.AdjusterContactId.HasValue && !string.IsNullOrWhiteSpace(claim.AdjusterName))
                                            {
                                                <a href="/contacts/@claim.AdjusterContactId" class="text-decoration-none">
                                                    <i class="bi bi-person-circle me-1"></i>@claim.AdjusterName
                                                </a>
                                            }
                                            else
                                            {
                                                <span class="text-muted">@(claim.AdjusterName ?? "N/A")</span>
                                            }
                                        </td>
                                        <td>@(claim.PolicyNumber ?? "N/A")</td>
                                        <td>
                                            @if (!string.IsNullOrWhiteSpace(claim.CoverageType))
                                            {
                                                <span class="badge bg-secondary">@claim.CoverageType</span>
                                            }
                                            else
                                            {
                                                <span class="text-muted">N/A</span>
                                            }
                                        </td>
                                        <td class="text-truncate" style="max-width: 150px;" title="@claim.Notes">@(claim.Notes ?? "")</td>
                                        <td class="text-end">
                                            <button class="btn btn-sm btn-outline-secondary me-1" @onclick="() => StartEditClaim(claim)" title="Edit">
                                                <i class="bi bi-pencil"></i>
                                            </button>
                                            <button class="btn btn-sm btn-outline-danger" @onclick="() => ConfirmDeleteClaim(claim)" title="Delete">
                                                <i class="bi bi-trash"></i>
                                            </button>
                                        </td>
                                    </tr>
                                }
                            </tbody>
                        </table>
                    </div>
                }
            </ChildContent>
        </AppCard>

        <!-- Status History Timeline -->
        @if (Matter.StatusHistory.Count > 0)
        {
            <AppCard CssClass="mt-3">
                <Header>
                    <h5 class="mb-0">Status History</h5>
                </Header>
                <ChildContent>
                    <div class="timeline">
                        @foreach (var entry in Matter.StatusHistory.OrderByDescending(h => h.ChangedOnUtc))
                        {
                            <div class="timeline-item d-flex mb-3">
                                <div class="timeline-marker me-3">
                                    <i class="bi bi-circle-fill text-primary small"></i>
                                </div>
                                <div class="flex-grow-1">
                                    <div class="d-flex align-items-center gap-2 mb-1">
                                        <AppBadge Status="@GetStatusClass(entry.FromStatus.Code)" Label="@entry.FromStatus.Name" />
                                        <i class="bi bi-arrow-right text-muted"></i>
                                        <AppBadge Status="@GetStatusClass(entry.ToStatus.Code)" Label="@entry.ToStatus.Name" />
                                    </div>
                                    <div class="small text-muted">
                                        @entry.ChangedOnUtc.ToLocalTime().ToString("MMM dd, yyyy h:mm tt")
                                        @if (!string.IsNullOrEmpty(entry.ChangedByUserName))
                                        {
                                            <span> by @entry.ChangedByUserName</span>
                                        }
                                    </div>
                                    @if (!string.IsNullOrEmpty(entry.Notes))
                                    {
                                        <div class="small mt-1">@entry.Notes</div>
                                    }
                                </div>
                            </div>
                        }
                    </div>
                </ChildContent>
            </AppCard>
        }
    </div>

    <!-- Key Dates Sidebar -->
    <div class="col-lg-4">
        <AppCard>
            <Header>
                <h5 class="mb-0">Key Dates</h5>
            </Header>
            <ChildContent>
                <div class="mb-3">
                    <div class="text-muted small">Opened</div>
                    <div class="fw-semibold">@Matter.OpenedOnUtc.ToLocalTime().ToString("MMM dd, yyyy")</div>
                </div>
                @if (Matter.DateOfIncidentUtc.HasValue)
                {
                    <div class="mb-3">
                        <div class="text-muted small">Date of Incident</div>
                        <div class="fw-semibold">@Matter.DateOfIncidentUtc.Value.ToLocalTime().ToString("MMM dd, yyyy")</div>
                    </div>
                }
                @if (Matter.TrialDate.HasValue)
                {
                    <div class="mb-3">
                        <div class="text-muted small">Trial Date</div>
                        <div class="fw-semibold">@Matter.TrialDate.Value.ToLocalTime().ToString("MMM dd, yyyy")</div>
                    </div>
                }
                @if (Matter.ClosedOnUtc.HasValue)
                {
                    <div class="mb-3">
                        <div class="text-muted small">Closed</div>
                        <div class="fw-semibold">@Matter.ClosedOnUtc.Value.ToLocalTime().ToString("MMM dd, yyyy")</div>
                    </div>
                }
                @{
                    var upcomingEvents = Matter.CalendarEntries
                        .Where(e => !e.IsCancelled && !e.IsPast)
                        .OrderBy(e => e.EntryDateTimeUtc)
                        .Take(3)
                        .ToList();
                }
                @if (upcomingEvents.Count > 0)
                {
                    <hr />
                    <div class="text-muted small fw-semibold mb-2">Upcoming Events</div>
                    @foreach (var entry in upcomingEvents)
                    {
                        <div class="mb-2">
                            <div class="small fw-semibold">@entry.Title</div>
                            <div class="small text-muted">@entry.EntryDateTimeUtc.ToLocalTime().ToString("MMM dd, yyyy h:mm tt")</div>
                        </div>
                    }
                }
            </ChildContent>
        </AppCard>
    </div>
</div>

<!-- Status Change Modal -->
<AppModal Title="Change Status" @bind-IsVisible="_showStatusModal" Size="md">
    <ChildContent>
        <div class="mb-3">
            <label class="form-label">Current Status</label>
            <div><AppBadge Status="@GetStatusClass(Matter.Status.Code)" Label="@Matter.Status.Name" /></div>
        </div>
        <div class="mb-3">
            <label for="overviewNewStatus" class="form-label">New Status <span class="text-danger">*</span></label>
            <select id="overviewNewStatus" class="form-select" @bind="_selectedStatusCode">
                <option value="">Select new status...</option>
                @foreach (var status in GetAvailableStatuses())
                {
                    <option value="@status.Code">@status.Name</option>
                }
            </select>
        </div>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showStatusModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn btn-primary" @onclick="ChangeStatusAsync" disabled="@(_isSaving || string.IsNullOrEmpty(_selectedStatusCode))">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>Change Status</span> }
        </button>
    </Footer>
</AppModal>

<!-- Attorney Assignment Modal -->
<AppModal Title="Assign Attorney" @bind-IsVisible="_showAttorneyModal" Size="md">
    <ChildContent>
        <UserAutocomplete Label="Attorney" @bind-SelectedUser="_selectedAttorney" InitialUserId="@Matter.AssignedAttorneyId" />
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger mt-3"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showAttorneyModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn btn-primary" @onclick="AssignAttorneyAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>Assign</span> }
        </button>
    </Footer>
</AppModal>

<!-- Edit Matter Details Modal -->
<AppModal Title="Edit Matter Details" @bind-IsVisible="_showEditMatterModal" Size="lg">
    <ChildContent>
        <div class="row g-3">
            <div class="col-12">
                <label for="editTitle" class="form-label">Title <span class="text-danger">*</span></label>
                <input id="editTitle" type="text" class="form-control" @bind="_editTitle" />
            </div>
            <div class="col-12">
                <label for="editDescription" class="form-label">Description</label>
                <textarea id="editDescription" class="form-control" rows="2" @bind="_editDescription"></textarea>
            </div>
            <div class="col-md-6">
                <label for="editJurisdiction" class="form-label">Jurisdiction</label>
                <input id="editJurisdiction" type="text" class="form-control" @bind="_editJurisdiction" />
            </div>
            <div class="col-md-6">
                <label for="editCourtNumber" class="form-label">Court Matter Number</label>
                <input id="editCourtNumber" type="text" class="form-control" @bind="_editCourtMatterNumber" />
            </div>
            <div class="col-md-6">
                <label for="editEstimatedValue" class="form-label">Estimated Value</label>
                <input id="editEstimatedValue" type="number" step="0.01" class="form-control" @bind="_editEstimatedValue" />
            </div>
            <div class="col-md-6">
                <label for="editTrialDate" class="form-label">Trial Date</label>
                <input id="editTrialDate" type="date" class="form-control" @bind="_editTrialDate" />
            </div>
            <div class="col-md-6">
                <label for="editDateOfIncident" class="form-label">Date of Incident</label>
                <input id="editDateOfIncident" type="date" class="form-control" @bind="_editDateOfIncident" />
            </div>
            <div class="col-md-6">
                <label for="editAccidentType" class="form-label">Accident Type</label>
                <select id="editAccidentType" class="form-select" @bind="_editAccidentType">
                    <option value="">Select...</option>
                    <option value="Auto">Auto</option>
                    <option value="Premises">Premises</option>
                    <option value="Slip & Fall">Slip &amp; Fall</option>
                    <option value="Medical Malpractice">Medical Malpractice</option>
                    <option value="Product Liability">Product Liability</option>
                    <option value="Wrongful Death">Wrongful Death</option>
                    <option value="Workers Comp">Workers Comp</option>
                    <option value="Other">Other</option>
                </select>
            </div>
            <div class="col-12">
                <label for="editIncidentDescription" class="form-label">Incident Description</label>
                <textarea id="editIncidentDescription" class="form-control" rows="3" @bind="_editIncidentDescription"></textarea>
            </div>
        </div>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger mt-3"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showEditMatterModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn btn-primary" @onclick="SaveMatterDetailsAsync" disabled="@(_isSaving || string.IsNullOrWhiteSpace(_editTitle))">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>Save Changes</span> }
        </button>
    </Footer>
</AppModal>

<!-- Add/Edit Insurance Claim Modal -->
<AppModal Title="@(_isEditingClaim ? "Edit Insurance Claim" : "Add Insurance Claim")" @bind-IsVisible="_showClaimModal" Size="lg">
    <ChildContent>
        <div class="row g-3">
            <div class="col-md-6">
                <label for="claimNumber" class="form-label">Claim Number <span class="text-danger">*</span></label>
                <input id="claimNumber" type="text" class="form-control" @bind="_claimNumber" />
            </div>
            <div class="col-md-6">
                <label for="claimPolicyNumber" class="form-label">Policy Number</label>
                <input id="claimPolicyNumber" type="text" class="form-control" @bind="_claimPolicyNumber" />
            </div>
            <div class="col-md-6">
                <ContactAutocomplete Label="Carrier" @bind-SelectedContact="_claimCarrierContact" InitialContactId="@_claimCarrierContactId" />
            </div>
            <div class="col-md-6">
                <ContactAutocomplete Label="Adjuster" @bind-SelectedContact="_claimAdjusterContact" InitialContactId="@_claimAdjusterContactId" />
            </div>
            <div class="col-md-6">
                <label for="claimCoverageType" class="form-label">Coverage Type</label>
                <select id="claimCoverageType" class="form-select" @bind="_claimCoverageType">
                    <option value="">Select...</option>
                    <option value="Liability">Liability</option>
                    <option value="UM/UIM">UM/UIM</option>
                    <option value="PIP">PIP</option>
                    <option value="MedPay">MedPay</option>
                    <option value="Property Damage">Property Damage</option>
                    <option value="Other">Other</option>
                </select>
            </div>
            <div class="col-12">
                <label for="claimNotes" class="form-label">Notes</label>
                <textarea id="claimNotes" class="form-control" rows="3" @bind="_claimNotes"></textarea>
            </div>
        </div>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger mt-3"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showClaimModal = false" disabled="@_isSavingClaim">Cancel</button>
        <button type="button" class="btn btn-primary" @onclick="SaveClaimAsync" disabled="@(_isSavingClaim || string.IsNullOrWhiteSpace(_claimNumber))">
            @if (_isSavingClaim) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>@(_isEditingClaim ? "Update Claim" : "Add Claim")</span> }
        </button>
    </Footer>
</AppModal>

<!-- Delete Claim Confirmation Modal -->
<AppModal Title="Delete Insurance Claim" @bind-IsVisible="_showDeleteClaimModal" Size="sm">
    <ChildContent>
        @if (_claimToDelete != null)
        {
            <p>Are you sure you want to delete claim <strong>@_claimToDelete.ClaimNumber</strong>?</p>
            <p class="text-muted small">This action cannot be undone.</p>
        }
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showDeleteClaimModal = false" disabled="@_isDeletingClaim">Cancel</button>
        <button type="button" class="btn btn-danger" @onclick="DeleteClaimAsync" disabled="@_isDeletingClaim">
            @if (_isDeletingClaim) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Deleting...</span> }
            else { <i class="bi bi-trash me-1"></i> <span>Delete</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter, EditorRequired]
    public MatterDetailDto Matter { get; set; } = default!;

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    // Status modal
    private bool _showStatusModal;
    private string _selectedStatusCode = string.Empty;

    // Attorney modal
    private bool _showAttorneyModal;
    private UserSummaryDto? _selectedAttorney;

    // Edit matter modal
    private bool _showEditMatterModal;
    private string _editTitle = "";
    private string? _editDescription;
    private string? _editJurisdiction;
    private string? _editCourtMatterNumber;
    private decimal? _editEstimatedValue;
    private DateTime? _editTrialDate;
    private DateTime? _editDateOfIncident;
    private string? _editAccidentType;
    private string? _editIncidentDescription;

    // Insurance Claims
    private bool _showClaimModal;
    private bool _isEditingClaim;
    private bool _isSavingClaim;
    private Guid? _editingClaimId;
    private string _claimNumber = "";
    private Guid? _claimCarrierContactId;
    private ContactSummaryDto? _claimCarrierContact;
    private Guid? _claimAdjusterContactId;
    private ContactSummaryDto? _claimAdjusterContact;
    private string _claimPolicyNumber = "";
    private string _claimCoverageType = "";
    private string _claimNotes = "";
    private bool _showDeleteClaimModal;
    private InsuranceClaimDto? _claimToDelete;
    private bool _isDeletingClaim;

    // Shared
    private bool _isSaving;
    private string? _errorMessage;

    // ==================== STATUS ====================

    private void OpenStatusChangeModal()
    {
        _selectedStatusCode = string.Empty;
        _errorMessage = null;
        _showStatusModal = true;
    }

    private async Task ChangeStatusAsync()
    {
        if (string.IsNullOrEmpty(_selectedStatusCode)) return;
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            await ApiClient.ChangeMatterStatusAsync(Matter.Id, _selectedStatusCode);
            _showStatusModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to change status for matter {MatterId}", Matter.Id);
            _errorMessage = $"Failed to change status: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    // ==================== ATTORNEY ====================

    private void OpenAttorneyModal()
    {
        _selectedAttorney = null;
        _errorMessage = null;
        _showAttorneyModal = true;
    }

    private async Task AssignAttorneyAsync()
    {
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            var command = new UpdateMatterCommand
            {
                Title = Matter.Title,
                Description = Matter.Description,
                MatterTypeCode = Matter.MatterType.Code,
                AssignedAttorneyId = _selectedAttorney?.Id,
                Jurisdiction = Matter.Jurisdiction,
                EstimatedValue = Matter.EstimatedValue,
                CourtMatterNumber = Matter.CourtMatterNumber,
                TrialDate = Matter.TrialDate,
                DateOfIncidentUtc = Matter.DateOfIncidentUtc,
                AccidentType = Matter.AccidentType,
                IncidentDescription = Matter.IncidentDescription
            };
            await ApiClient.UpdateMatterAsync(Matter.Id, command);
            _showAttorneyModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to assign attorney for matter {MatterId}", Matter.Id);
            _errorMessage = $"Failed to assign attorney: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    // ==================== EDIT MATTER ====================

    private void OpenEditMatterModal()
    {
        _editTitle = Matter.Title;
        _editDescription = Matter.Description;
        _editJurisdiction = Matter.Jurisdiction;
        _editCourtMatterNumber = Matter.CourtMatterNumber;
        _editEstimatedValue = Matter.EstimatedValue;
        _editTrialDate = Matter.TrialDate?.ToLocalTime();
        _editDateOfIncident = Matter.DateOfIncidentUtc?.ToLocalTime();
        _editAccidentType = Matter.AccidentType ?? "";
        _editIncidentDescription = Matter.IncidentDescription;
        _errorMessage = null;
        _showEditMatterModal = true;
    }

    private async Task SaveMatterDetailsAsync()
    {
        if (string.IsNullOrWhiteSpace(_editTitle)) return;
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            var command = new UpdateMatterCommand
            {
                Title = _editTitle,
                Description = _editDescription,
                MatterTypeCode = Matter.MatterType.Code,
                AssignedAttorneyId = Matter.AssignedAttorneyId,
                Jurisdiction = _editJurisdiction,
                EstimatedValue = _editEstimatedValue,
                CourtMatterNumber = _editCourtMatterNumber,
                TrialDate = _editTrialDate?.ToUniversalTime(),
                DateOfIncidentUtc = _editDateOfIncident?.ToUniversalTime(),
                AccidentType = string.IsNullOrWhiteSpace(_editAccidentType) ? null : _editAccidentType,
                IncidentDescription = _editIncidentDescription
            };
            await ApiClient.UpdateMatterAsync(Matter.Id, command);
            _showEditMatterModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update matter {MatterId}", Matter.Id);
            _errorMessage = $"Failed to save changes: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    // ==================== INSURANCE CLAIMS ====================

    private void StartAddClaim()
    {
        _isEditingClaim = false;
        _editingClaimId = null;
        _claimNumber = "";
        _claimCarrierContactId = null;
        _claimCarrierContact = null;
        _claimAdjusterContactId = null;
        _claimAdjusterContact = null;
        _claimPolicyNumber = "";
        _claimCoverageType = "";
        _claimNotes = "";
        _errorMessage = null;
        _showClaimModal = true;
    }

    private void StartEditClaim(InsuranceClaimDto claim)
    {
        _isEditingClaim = true;
        _editingClaimId = claim.Id;
        _claimNumber = claim.ClaimNumber;
        _claimCarrierContactId = claim.CarrierContactId;
        _claimCarrierContact = null; // ContactAutocomplete will load from InitialContactId
        _claimAdjusterContactId = claim.AdjusterContactId;
        _claimAdjusterContact = null;
        _claimPolicyNumber = claim.PolicyNumber ?? "";
        _claimCoverageType = claim.CoverageType ?? "";
        _claimNotes = claim.Notes ?? "";
        _errorMessage = null;
        _showClaimModal = true;
    }

    private async Task SaveClaimAsync()
    {
        if (string.IsNullOrWhiteSpace(_claimNumber)) return;
        try
        {
            _isSavingClaim = true; _errorMessage = null; StateHasChanged();

            if (_isEditingClaim && _editingClaimId.HasValue)
            {
                var command = new UpdateInsuranceClaimCommand
                {
                    ClaimNumber = _claimNumber,
                    CarrierContactId = _claimCarrierContact?.Id ?? _claimCarrierContactId,
                    AdjusterContactId = _claimAdjusterContact?.Id ?? _claimAdjusterContactId,
                    PolicyNumber = string.IsNullOrWhiteSpace(_claimPolicyNumber) ? null : _claimPolicyNumber,
                    CoverageType = string.IsNullOrWhiteSpace(_claimCoverageType) ? null : _claimCoverageType,
                    Notes = string.IsNullOrWhiteSpace(_claimNotes) ? null : _claimNotes
                };
                await ApiClient.UpdateInsuranceClaimAsync(Matter.Id, _editingClaimId.Value, command);
            }
            else
            {
                var command = new CreateInsuranceClaimCommand
                {
                    ClaimNumber = _claimNumber,
                    CarrierContactId = _claimCarrierContact?.Id,
                    AdjusterContactId = _claimAdjusterContact?.Id,
                    PolicyNumber = string.IsNullOrWhiteSpace(_claimPolicyNumber) ? null : _claimPolicyNumber,
                    CoverageType = string.IsNullOrWhiteSpace(_claimCoverageType) ? null : _claimCoverageType,
                    Notes = string.IsNullOrWhiteSpace(_claimNotes) ? null : _claimNotes
                };
                await ApiClient.CreateInsuranceClaimAsync(Matter.Id, command);
            }

            _showClaimModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save insurance claim for matter {MatterId}", Matter.Id);
            _errorMessage = $"Failed to save claim: {ex.Message}";
        }
        finally { _isSavingClaim = false; StateHasChanged(); }
    }

    private void ConfirmDeleteClaim(InsuranceClaimDto claim)
    {
        _claimToDelete = claim;
        _errorMessage = null;
        _showDeleteClaimModal = true;
    }

    private async Task DeleteClaimAsync()
    {
        if (_claimToDelete == null) return;
        try
        {
            _isDeletingClaim = true; _errorMessage = null; StateHasChanged();
            await ApiClient.DeleteInsuranceClaimAsync(Matter.Id, _claimToDelete.Id);
            _showDeleteClaimModal = false;
            _claimToDelete = null;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete insurance claim {ClaimId}", _claimToDelete.Id);
            _errorMessage = $"Failed to delete claim: {ex.Message}";
        }
        finally { _isDeletingClaim = false; StateHasChanged(); }
    }

    // ==================== HELPERS ====================

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
        var allStatuses = new List<(string Code, string Name)>
        {
            ("OPEN", "Open"), ("ACTIVE", "Active"), ("DISCOVERY", "Discovery"),
            ("NEGOTIATION", "Negotiation"), ("LITIGATION", "Litigation"),
            ("SETTLED", "Settled"), ("CLOSED", "Closed"), ("DISMISSED", "Dismissed")
        };
        return allStatuses.Where(s => s.Code != Matter.Status.Code).ToList();
    }
}
```

## MatterPartiesTab.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<MatterPartiesTab> Logger

<div class="mt-3">
    <div class="d-flex justify-content-between align-items-center mb-3">
        <h5 class="mb-0">Parties</h5>
        <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
            <i class="bi bi-plus-circle me-1"></i> Add Party
        </button>
    </div>

    @if (Parties.Count == 0)
    {
        <AppEmptyState Icon="people" Title="No Parties" Message="No parties have been added to this matter yet.">
            <Actions>
                <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
                    <i class="bi bi-plus-circle me-1"></i> Add Party
                </button>
            </Actions>
        </AppEmptyState>
    }
    else
    {
        <div class="table-responsive">
            <table class="table table-hover align-middle">
                <thead>
                    <tr>
                        <th>Contact Name</th>
                        <th>Role</th>
                        <th>Primary</th>
                        <th>Added On</th>
                        <th class="text-end">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var party in Parties)
                    {
                        <tr>
                            <td>@party.ContactName</td>
                            <td>@party.Role.Name</td>
                            <td>
                                @if (party.IsPrimary)
                                {
                                    <AppBadge Status="done" Label="Primary" />
                                }
                            </td>
                            <td>@party.AddedOnUtc.ToLocalTime().ToString("MMM dd, yyyy")</td>
                            <td class="text-end">
                                <button class="btn btn-sm btn-outline-secondary me-1" @onclick="() => OpenEditModal(party)" title="Edit">
                                    <i class="bi bi-pencil"></i>
                                </button>
                                <button class="btn btn-sm btn-outline-danger" @onclick="() => RemovePartyAsync(party)" title="Remove">
                                    <i class="bi bi-trash"></i>
                                </button>
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>
    }
</div>

<!-- Add Party Modal -->
<AppModal Title="Add Party" @bind-IsVisible="_showAddModal" Size="md" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
        <div class="mb-3">
            <ContactAutocomplete Label="Contact" Required="true" @bind-SelectedContact="_selectedContact" />
        </div>
        <div class="mb-3">
            <label for="partyRole" class="form-label">Role <span class="text-danger">*</span></label>
            <select id="partyRole" class="form-select" @bind="_addCommand.RoleCode">
                <option value="">Select role...</option>
                @foreach (var role in _roles)
                {
                    <option value="@role.Code">@role.Name</option>
                }
            </select>
        </div>
        <div class="form-check mb-3">
            <input type="checkbox" class="form-check-input" id="addPartyPrimary" @bind="_addCommand.IsPrimary" />
            <label class="form-check-label" for="addPartyPrimary">Primary Party</label>
        </div>
        <div class="mb-3">
            <label for="addPartyNotes" class="form-label">Notes</label>
            <textarea class="form-control" id="addPartyNotes" @bind="_addCommand.Notes" rows="2"></textarea>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="CloseAddModal" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="AddPartyAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Adding...</span> }
            else { <i class="bi bi-plus-circle me-1"></i> <span>Add Party</span> }
        </button>
    </Footer>
</AppModal>

<!-- Edit Party Modal -->
<AppModal Title="Edit Party" @bind-IsVisible="_showEditModal" Size="md" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
        @if (_editingParty != null)
        {
            <div class="mb-3">
                <label class="form-label">Contact</label>
                <div class="fw-semibold">@_editingParty.ContactName</div>
            </div>
        }
        <div class="mb-3">
            <label for="editPartyRole" class="form-label">Role <span class="text-danger">*</span></label>
            <select id="editPartyRole" class="form-select" @bind="_editCommand.RoleCode">
                <option value="">Select role...</option>
                @foreach (var role in _roles)
                {
                    <option value="@role.Code">@role.Name</option>
                }
            </select>
        </div>
        <div class="form-check mb-3">
            <input type="checkbox" class="form-check-input" id="editPartyPrimary" @bind="_editCommand.IsPrimary" />
            <label class="form-check-label" for="editPartyPrimary">Primary Party</label>
        </div>
        <div class="mb-3">
            <label for="editPartyNotes" class="form-label">Notes</label>
            <textarea class="form-control" id="editPartyNotes" @bind="_editCommand.Notes" rows="2"></textarea>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showEditModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="UpdatePartyAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>Save Changes</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter, EditorRequired]
    public Guid MatterId { get; set; }

    [Parameter, EditorRequired]
    public IReadOnlyList<PartyDto> Parties { get; set; } = [];

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    private bool _showAddModal;
    private bool _showEditModal;
    private bool _isSaving;
    private string? _errorMessage;

    private CreatePartyCommand _addCommand = new();
    private UpdatePartyCommand _editCommand = new();
    private ContactSummaryDto? _selectedContact;
    private PartyDto? _editingParty;

    private static readonly List<(string Code, string Name)> _roles =
    [
        ("CLIENT", "Client"), ("FAMILY", "Family Member"), ("ADJUSTER", "Adjuster"),
        ("MEDICAL", "Medical Provider"), ("INSURANCE", "Insurance Carrier"), ("LIENHOLDER", "Lien Holder")
    ];

    private void OpenAddModal()
    {
        _addCommand = new();
        _selectedContact = null;
        _errorMessage = null;
        _showAddModal = true;
    }

    private void CloseAddModal()
    {
        _showAddModal = false;
        _errorMessage = null;
    }

    private void OpenEditModal(PartyDto party)
    {
        _editingParty = party;
        _editCommand = new UpdatePartyCommand
        {
            RoleCode = party.Role.Code,
            IsPrimary = party.IsPrimary,
            Notes = party.Notes
        };
        _errorMessage = null;
        _showEditModal = true;
    }

    private async Task AddPartyAsync()
    {
        if (_selectedContact == null) { _errorMessage = "Please select a contact."; return; }
        if (string.IsNullOrEmpty(_addCommand.RoleCode)) { _errorMessage = "Please select a role."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            _addCommand.ContactId = _selectedContact.Id;
            await ApiClient.AddPartyToMatterAsync(MatterId, _addCommand);
            _showAddModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to add party to matter {MatterId}", MatterId);
            _errorMessage = $"Failed to add party: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task UpdatePartyAsync()
    {
        if (_editingParty == null) return;
        if (string.IsNullOrEmpty(_editCommand.RoleCode)) { _errorMessage = "Please select a role."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            await ApiClient.UpdatePartyAsync(MatterId, _editingParty.Id, _editCommand);
            _showEditModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update party {PartyId}", _editingParty.Id);
            _errorMessage = $"Failed to update party: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task RemovePartyAsync(PartyDto party)
    {
        try
        {
            await ApiClient.RemovePartyAsync(MatterId, party.Id);
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to remove party {PartyId}", party.Id);
        }
    }
}
```

## MatterTasksTab.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<MatterTasksTab> Logger

<div class="mt-3">
    <div class="d-flex justify-content-between align-items-center mb-3">
        <div class="d-flex align-items-center gap-2">
            <h5 class="mb-0">Tasks</h5>
            <select class="form-select form-select-sm" style="width: auto;" @bind="_statusFilter">
                <option value="">All Statuses</option>
                <option value="Open">Open</option>
                <option value="InProgress">In Progress</option>
                <option value="Completed">Completed</option>
                <option value="Cancelled">Cancelled</option>
            </select>
        </div>
        <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
            <i class="bi bi-plus-circle me-1"></i> Add Task
        </button>
    </div>

    @if (FilteredTasks.Count == 0)
    {
        <AppEmptyState Icon="check2-square" Title="No Tasks" Message="No tasks found for the selected filter.">
            <Actions>
                <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
                    <i class="bi bi-plus-circle me-1"></i> Add Task
                </button>
            </Actions>
        </AppEmptyState>
    }
    else
    {
        <div class="table-responsive">
            <table class="table table-hover align-middle">
                <thead>
                    <tr>
                        <th>Title</th>
                        <th>Status</th>
                        <th>Priority</th>
                        <th>Assigned To</th>
                        <th>Due Date</th>
                        <th class="text-end">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var task in FilteredTasks)
                    {
                        <tr class="@(task.IsOverdue ? "table-warning" : "")">
                            <td>
                                @task.Title
                                @if (task.IsOverdue)
                                {
                                    <span class="badge bg-danger ms-1">Overdue</span>
                                }
                            </td>
                            <td><AppBadge Status="@GetTaskStatusClass(task.Status)" Label="@task.Status" /></td>
                            <td><AppBadge Status="@GetPriorityClass(task.Priority)" Label="@task.Priority" /></td>
                            <td>@(task.AssignedToUserName ?? "Unassigned")</td>
                            <td>@(task.DueDateUtc?.ToLocalTime().ToString("MMM dd, yyyy") ?? "No due date")</td>
                            <td class="text-end">
                                @if (task.Status != "Completed")
                                {
                                    <button class="btn btn-sm btn-outline-success me-1" @onclick="() => CompleteTaskAsync(task)" title="Complete">
                                        <i class="bi bi-check-lg"></i>
                                    </button>
                                }
                                <button class="btn btn-sm btn-outline-secondary me-1" @onclick="() => OpenEditModal(task)" title="Edit">
                                    <i class="bi bi-pencil"></i>
                                </button>
                                <button class="btn btn-sm btn-outline-danger" @onclick="() => DeleteTaskAsync(task)" title="Delete">
                                    <i class="bi bi-trash"></i>
                                </button>
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>
    }
</div>

<!-- Add/Edit Task Modal -->
<AppModal Title="@(_isEditing ? "Edit Task" : "Add Task")" @bind-IsVisible="_showModal" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
        <div class="row g-3">
            <div class="col-12">
                <label for="taskTitle" class="form-label">Title <span class="text-danger">*</span></label>
                <input type="text" class="form-control" id="taskTitle" @bind="_title" required maxlength="200" />
            </div>
            <div class="col-12">
                <label for="taskDescription" class="form-label">Description</label>
                <textarea class="form-control" id="taskDescription" @bind="_description" rows="3"></textarea>
            </div>
            <div class="col-md-6">
                <label for="taskDueDate" class="form-label">Due Date</label>
                <input type="date" class="form-control" id="taskDueDate" value="@_dueDateText" @onchange="@((ChangeEventArgs e) => _dueDateText = e.Value?.ToString())" />
            </div>
            <div class="col-md-6">
                <label for="taskPriority" class="form-label">Priority</label>
                <select id="taskPriority" class="form-select" @bind="_priority">
                    <option value="Normal">Normal</option>
                    <option value="High">High</option>
                    <option value="Low">Low</option>
                </select>
            </div>
            <div class="col-12">
                <UserAutocomplete Label="Assigned To" @bind-SelectedUser="_selectedUser" InitialUserId="@_assignedToUserId" />
            </div>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="SaveTaskAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>@(_isEditing ? "Save Changes" : "Add Task")</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter, EditorRequired]
    public Guid MatterId { get; set; }

    [Parameter, EditorRequired]
    public IReadOnlyList<TaskDto> Tasks { get; set; } = [];

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    private string _statusFilter = string.Empty;
    private bool _showModal;
    private bool _isEditing;
    private bool _isSaving;
    private string? _errorMessage;
    private Guid? _editingTaskId;

    // Form fields
    private string _title = string.Empty;
    private string? _description;
    private string? _dueDateText;
    private string _priority = "Normal";
    private string _status = "Open";
    private UserSummaryDto? _selectedUser;
    private Guid? _assignedToUserId;

    private IReadOnlyList<TaskDto> FilteredTasks => string.IsNullOrEmpty(_statusFilter)
        ? Tasks
        : Tasks.Where(t => t.Status == _statusFilter).ToList();

    private void OpenAddModal()
    {
        _isEditing = false;
        _editingTaskId = null;
        _title = string.Empty;
        _description = null;
        _dueDateText = null;
        _priority = "Normal";
        _status = "Open";
        _selectedUser = null;
        _assignedToUserId = null;
        _errorMessage = null;
        _showModal = true;
    }

    private void OpenEditModal(TaskDto task)
    {
        _isEditing = true;
        _editingTaskId = task.Id;
        _title = task.Title;
        _description = task.Description;
        _dueDateText = task.DueDateUtc?.ToLocalTime().ToString("yyyy-MM-dd");
        _priority = task.Priority;
        _status = task.Status;
        _selectedUser = null;
        _assignedToUserId = task.AssignedToUserId;
        _errorMessage = null;
        _showModal = true;
    }

    private async Task SaveTaskAsync()
    {
        if (string.IsNullOrWhiteSpace(_title)) { _errorMessage = "Title is required."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            DateTime? dueDate = null;
            if (!string.IsNullOrWhiteSpace(_dueDateText) && DateTime.TryParse(_dueDateText, out var parsed))
                dueDate = parsed.ToUniversalTime();

            if (_isEditing && _editingTaskId.HasValue)
            {
                var cmd = new UpdateTaskCommand
                {
                    Title = _title, Description = _description, DueDateUtc = dueDate,
                    Priority = _priority, Status = _status, AssignedToUserId = _selectedUser?.Id ?? _assignedToUserId
                };
                await ApiClient.UpdateTaskAsync(MatterId, _editingTaskId.Value, cmd);
            }
            else
            {
                var cmd = new CreateTaskCommand
                {
                    Title = _title, Description = _description, DueDateUtc = dueDate,
                    Priority = _priority, AssignedToUserId = _selectedUser?.Id
                };
                await ApiClient.CreateTaskAsync(MatterId, cmd);
            }
            _showModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save task for matter {MatterId}", MatterId);
            _errorMessage = $"Failed to save task: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task CompleteTaskAsync(TaskDto task)
    {
        try
        {
            await ApiClient.ChangeTaskStatusAsync(MatterId, task.Id, "Completed");
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to complete task {TaskId}", task.Id); }
    }

    private async Task DeleteTaskAsync(TaskDto task)
    {
        try
        {
            await ApiClient.DeleteTaskAsync(MatterId, task.Id);
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to delete task {TaskId}", task.Id); }
    }

    private static string GetTaskStatusClass(string status) => status switch
    {
        "Open" or "InProgress" => "working",
        "Completed" => "done",
        "Cancelled" => "stuck",
        _ => "neutral"
    };

    private static string GetPriorityClass(string priority) => priority switch
    {
        "High" => "stuck",
        "Normal" => "neutral",
        "Low" => "cold",
        _ => "neutral"
    };
}
```

## MatterCalendarTab.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<MatterCalendarTab> Logger

<div class="mt-3">
    <div class="d-flex justify-content-between align-items-center mb-3">
        <h5 class="mb-0">Calendar Entries</h5>
        <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
            <i class="bi bi-plus-circle me-1"></i> Add Entry
        </button>
    </div>

    @if (CalendarEntries.Count == 0)
    {
        <AppEmptyState Icon="calendar-event" Title="No Calendar Entries" Message="No calendar entries have been added to this matter yet.">
            <Actions>
                <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
                    <i class="bi bi-plus-circle me-1"></i> Add Entry
                </button>
            </Actions>
        </AppEmptyState>
    }
    else
    {
        <div class="table-responsive">
            <table class="table table-hover align-middle">
                <thead>
                    <tr>
                        <th>Title</th>
                        <th>Date/Time</th>
                        <th>Duration</th>
                        <th>Type</th>
                        <th>Location</th>
                        <th>Status</th>
                        <th class="text-end">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var entry in CalendarEntries.OrderBy(e => e.EntryDateTimeUtc))
                    {
                        <tr class="@(entry.IsCancelled ? "text-decoration-line-through text-muted" : entry.IsPast ? "text-muted" : "")">
                            <td>@entry.Title</td>
                            <td>@entry.EntryDateTimeUtc.ToLocalTime().ToString("MMM dd, yyyy h:mm tt")</td>
                            <td>@(entry.DurationMinutes.HasValue ? $"{entry.DurationMinutes} min" : "N/A")</td>
                            <td>@FormatEntryType(entry.EntryType)</td>
                            <td>@(entry.Location ?? "N/A")</td>
                            <td>
                                @if (entry.IsCancelled)
                                {
                                    <AppBadge Status="stuck" Label="Cancelled" />
                                }
                                else if (entry.IsPast)
                                {
                                    <AppBadge Status="neutral" Label="Past" />
                                }
                                else
                                {
                                    <AppBadge Status="working" Label="Upcoming" />
                                }
                            </td>
                            <td class="text-end">
                                <button class="btn btn-sm btn-outline-secondary me-1" @onclick="() => OpenEditModal(entry)" title="Edit">
                                    <i class="bi bi-pencil"></i>
                                </button>
                                @if (!entry.IsCancelled)
                                {
                                    <button class="btn btn-sm btn-outline-warning me-1" @onclick="() => CancelEntryAsync(entry)" title="Cancel">
                                        <i class="bi bi-x-circle"></i>
                                    </button>
                                }
                                <button class="btn btn-sm btn-outline-danger" @onclick="() => DeleteEntryAsync(entry)" title="Delete">
                                    <i class="bi bi-trash"></i>
                                </button>
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>
    }
</div>

<!-- Add/Edit Calendar Entry Modal -->
<AppModal Title="@(_isEditing ? "Edit Calendar Entry" : "Add Calendar Entry")" @bind-IsVisible="_showModal" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
        <div class="row g-3">
            <div class="col-12">
                <label for="calTitle" class="form-label">Title <span class="text-danger">*</span></label>
                <input type="text" class="form-control" id="calTitle" @bind="_title" required maxlength="200" />
            </div>
            <div class="col-md-6">
                <label for="calDateTime" class="form-label">Date & Time <span class="text-danger">*</span></label>
                <input type="datetime-local" class="form-control" id="calDateTime" @bind="_dateTime" />
            </div>
            <div class="col-md-6">
                <label for="calDuration" class="form-label">Duration (minutes)</label>
                <input type="number" class="form-control" id="calDuration" @bind="_durationMinutes" min="0" />
            </div>
            <div class="col-md-6">
                <label for="calType" class="form-label">Type <span class="text-danger">*</span></label>
                <select id="calType" class="form-select" @bind="_entryType">
                    <option value="">Select type...</option>
                    @foreach (var type in _entryTypes)
                    {
                        <option value="@type.Code">@type.Name</option>
                    }
                </select>
            </div>
            <div class="col-md-6">
                <label for="calLocation" class="form-label">Location</label>
                <input type="text" class="form-control" id="calLocation" @bind="_location" maxlength="200" />
            </div>
            <div class="col-12">
                <label for="calDescription" class="form-label">Description</label>
                <textarea class="form-control" id="calDescription" @bind="_description" rows="3"></textarea>
            </div>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="SaveEntryAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>@(_isEditing ? "Save Changes" : "Add Entry")</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter, EditorRequired]
    public Guid MatterId { get; set; }

    [Parameter, EditorRequired]
    public IReadOnlyList<CalendarEntryDto> CalendarEntries { get; set; } = [];

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    private bool _showModal;
    private bool _isEditing;
    private bool _isSaving;
    private string? _errorMessage;
    private Guid? _editingEntryId;

    // Form fields
    private string _title = string.Empty;
    private DateTime _dateTime = DateTime.Now;
    private int? _durationMinutes;
    private string _entryType = string.Empty;
    private string? _location;
    private string? _description;

    private static readonly List<(string Code, string Name)> _entryTypes =
    [
        ("CourtHearing", "Court Hearing"), ("Trial", "Trial"), ("Deposition", "Deposition"),
        ("ClientMeeting", "Client Meeting"), ("InternalMeeting", "Internal Meeting"),
        ("Mediation", "Mediation"), ("Arbitration", "Arbitration"),
        ("FilingDeadline", "Filing Deadline"), ("DiscoveryDeadline", "Discovery Deadline"),
        ("StatuteDeadline", "Statute Deadline"), ("Other", "Other")
    ];

    private void OpenAddModal()
    {
        _isEditing = false;
        _editingEntryId = null;
        _title = string.Empty;
        _dateTime = DateTime.Now;
        _durationMinutes = null;
        _entryType = string.Empty;
        _location = null;
        _description = null;
        _errorMessage = null;
        _showModal = true;
    }

    private void OpenEditModal(CalendarEntryDto entry)
    {
        _isEditing = true;
        _editingEntryId = entry.Id;
        _title = entry.Title;
        _dateTime = entry.EntryDateTimeUtc.ToLocalTime();
        _durationMinutes = entry.DurationMinutes;
        _entryType = entry.EntryType;
        _location = entry.Location;
        _description = entry.Description;
        _errorMessage = null;
        _showModal = true;
    }

    private async Task SaveEntryAsync()
    {
        if (string.IsNullOrWhiteSpace(_title)) { _errorMessage = "Title is required."; return; }
        if (string.IsNullOrEmpty(_entryType)) { _errorMessage = "Type is required."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            if (_isEditing && _editingEntryId.HasValue)
            {
                var cmd = new UpdateCalendarEntryCommand
                {
                    Title = _title, EntryDateTimeUtc = _dateTime.ToUniversalTime(),
                    DurationMinutes = _durationMinutes, EntryType = _entryType,
                    Location = _location, Description = _description
                };
                await ApiClient.UpdateCalendarEntryAsync(MatterId, _editingEntryId.Value, cmd);
            }
            else
            {
                var cmd = new CreateCalendarEntryCommand
                {
                    Title = _title, EntryDateTimeUtc = _dateTime.ToUniversalTime(),
                    DurationMinutes = _durationMinutes, EntryType = _entryType,
                    Location = _location, Description = _description
                };
                await ApiClient.CreateCalendarEntryAsync(MatterId, cmd);
            }
            _showModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save calendar entry for matter {MatterId}", MatterId);
            _errorMessage = $"Failed to save entry: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task CancelEntryAsync(CalendarEntryDto entry)
    {
        try
        {
            await ApiClient.CancelCalendarEntryAsync(MatterId, entry.Id);
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to cancel calendar entry {EntryId}", entry.Id); }
    }

    private async Task DeleteEntryAsync(CalendarEntryDto entry)
    {
        try
        {
            await ApiClient.DeleteCalendarEntryAsync(MatterId, entry.Id);
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to delete calendar entry {EntryId}", entry.Id); }
    }

    private static string FormatEntryType(string type) => type switch
    {
        "CourtHearing" => "Court Hearing",
        "ClientMeeting" => "Client Meeting",
        "InternalMeeting" => "Internal Meeting",
        "FilingDeadline" => "Filing Deadline",
        "DiscoveryDeadline" => "Discovery Deadline",
        "StatuteDeadline" => "Statute Deadline",
        _ => type
    };
}
```

## MatterCommunicationsTab.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<MatterCommunicationsTab> Logger

<div class="mt-3">
    <div class="d-flex justify-content-between align-items-center mb-3">
        <h5 class="mb-0">Communications</h5>
        <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
            <i class="bi bi-plus-circle me-1"></i> Add Communication
        </button>
    </div>

    @if (Communications.Count == 0)
    {
        <AppEmptyState Icon="chat-dots" Title="No Communications" Message="No communications or notes have been added yet.">
            <Actions>
                <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
                    <i class="bi bi-plus-circle me-1"></i> Add Communication
                </button>
            </Actions>
        </AppEmptyState>
    }
    else
    {
        @foreach (var comm in SortedCommunications)
        {
            <AppCard CssClass="@($"mb-3 {(comm.IsPinned ? "border-warning" : "")}")">
                <Header>
                    <div class="d-flex justify-content-between align-items-center">
                        <div class="d-flex align-items-center gap-2">
                            @if (comm.IsPinned)
                            {
                                <i class="bi bi-pin-fill text-warning" title="Pinned"></i>
                            }
                            <span class="fw-semibold">@(comm.Title ?? "Untitled")</span>
                            <AppBadge Status="@GetCategoryClass(comm.Category)" Label="@FormatCategory(comm.Category)" />
                            @if (comm.IsPrivate)
                            {
                                <AppBadge Status="stuck" Label="Private" />
                            }
                        </div>
                        <div class="d-flex gap-1">
                            <button class="btn btn-sm btn-outline-warning" @onclick="() => TogglePinAsync(comm)" title="@(comm.IsPinned ? "Unpin" : "Pin")">
                                <i class="bi @(comm.IsPinned ? "bi-pin-fill" : "bi-pin")"></i>
                            </button>
                            <button class="btn btn-sm btn-outline-secondary" @onclick="() => OpenEditModal(comm)" title="Edit">
                                <i class="bi bi-pencil"></i>
                            </button>
                            <button class="btn btn-sm btn-outline-danger" @onclick="() => DeleteCommunicationAsync(comm)" title="Delete">
                                <i class="bi bi-trash"></i>
                            </button>
                        </div>
                    </div>
                </Header>
                <ChildContent>
                    <div class="mb-2" style="white-space: pre-wrap;">@TruncateContent(comm.Content)</div>
                    <div class="small text-muted">
                        @if (!string.IsNullOrEmpty(comm.CreatedByUserName))
                        {
                            <span class="me-2"><i class="bi bi-person me-1"></i>@comm.CreatedByUserName</span>
                        }
                        <span><i class="bi bi-clock me-1"></i>@comm.CreatedAtUtc.ToLocalTime().ToString("MMM dd, yyyy h:mm tt")</span>
                        @if (comm.ModifiedAtUtc != comm.CreatedAtUtc)
                        {
                            <span class="ms-2">(edited @comm.ModifiedAtUtc.ToLocalTime().ToString("MMM dd, yyyy h:mm tt"))</span>
                        }
                    </div>
                </ChildContent>
            </AppCard>
        }
    }
</div>

<!-- Add/Edit Communication Modal -->
<AppModal Title="@(_isEditing ? "Edit Communication" : "Add Communication")" @bind-IsVisible="_showModal" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
        <div class="row g-3">
            <div class="col-12">
                <label for="commTitle" class="form-label">Title</label>
                <input type="text" class="form-control" id="commTitle" @bind="_title" maxlength="200" />
            </div>
            <div class="col-12">
                <label for="commContent" class="form-label">Content <span class="text-danger">*</span></label>
                <textarea class="form-control" id="commContent" @bind="_content" rows="6" required></textarea>
            </div>
            <div class="col-md-6">
                <label for="commCategory" class="form-label">Category <span class="text-danger">*</span></label>
                <select id="commCategory" class="form-select" @bind="_category">
                    <option value="">Select category...</option>
                    @foreach (var cat in _categories)
                    {
                        <option value="@cat.Code">@cat.Name</option>
                    }
                </select>
            </div>
            <div class="col-md-6 d-flex align-items-end gap-3">
                <div class="form-check">
                    <input type="checkbox" class="form-check-input" id="commPinned" @bind="_isPinned" />
                    <label class="form-check-label" for="commPinned">Pinned</label>
                </div>
                <div class="form-check">
                    <input type="checkbox" class="form-check-input" id="commPrivate" @bind="_isPrivate" />
                    <label class="form-check-label" for="commPrivate">Private</label>
                </div>
            </div>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="SaveCommunicationAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>@(_isEditing ? "Save Changes" : "Add Communication")</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter, EditorRequired]
    public Guid MatterId { get; set; }

    [Parameter, EditorRequired]
    public IReadOnlyList<CommunicationDto> Communications { get; set; } = [];

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    private bool _showModal;
    private bool _isEditing;
    private bool _isSaving;
    private string? _errorMessage;
    private Guid? _editingId;

    // Form fields
    private string? _title;
    private string _content = string.Empty;
    private string _category = string.Empty;
    private bool _isPinned;
    private bool _isPrivate;

    private static readonly (string Code, string Name)[] _categories = [
        ("General", "General"), ("ClientCommunication", "Client Communication"),
        ("LegalResearch", "Legal Research"), ("CourtNotes", "Court Notes"),
        ("Strategy", "Strategy"), ("Discovery", "Discovery"), ("Settlement", "Settlement"),
        ("WitnessInterview", "Witness Interview"), ("ExpertConsultation", "Expert Consultation"),
        ("FollowUp", "Follow Up"), ("Alert", "Alert")
    ];

    private IEnumerable<CommunicationDto> SortedCommunications =>
        Communications.OrderByDescending(c => c.IsPinned).ThenByDescending(c => c.CreatedAtUtc);

    private void OpenAddModal()
    {
        _isEditing = false;
        _editingId = null;
        _title = null;
        _content = string.Empty;
        _category = string.Empty;
        _isPinned = false;
        _isPrivate = false;
        _errorMessage = null;
        _showModal = true;
    }

    private void OpenEditModal(CommunicationDto comm)
    {
        _isEditing = true;
        _editingId = comm.Id;
        _title = comm.Title;
        _content = comm.Content;
        _category = comm.Category;
        _isPinned = comm.IsPinned;
        _isPrivate = comm.IsPrivate;
        _errorMessage = null;
        _showModal = true;
    }

    private async Task SaveCommunicationAsync()
    {
        if (string.IsNullOrWhiteSpace(_content)) { _errorMessage = "Content is required."; return; }
        if (string.IsNullOrEmpty(_category)) { _errorMessage = "Category is required."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            if (_isEditing && _editingId.HasValue)
            {
                var cmd = new UpdateCommunicationCommand
                {
                    Title = _title, Content = _content, Category = _category,
                    IsPinned = _isPinned, IsPrivate = _isPrivate
                };
                await ApiClient.UpdateCommunicationAsync(MatterId, _editingId.Value, cmd);
            }
            else
            {
                var cmd = new CreateCommunicationCommand
                {
                    Title = _title, Content = _content, Category = _category,
                    IsPinned = _isPinned, IsPrivate = _isPrivate
                };
                await ApiClient.CreateCommunicationAsync(MatterId, cmd);
            }
            _showModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save communication for matter {MatterId}", MatterId);
            _errorMessage = $"Failed to save communication: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task TogglePinAsync(CommunicationDto comm)
    {
        try
        {
            await ApiClient.ToggleCommunicationPinAsync(MatterId, comm.Id);
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to toggle pin for communication {CommId}", comm.Id); }
    }

    private async Task DeleteCommunicationAsync(CommunicationDto comm)
    {
        try
        {
            await ApiClient.DeleteCommunicationAsync(MatterId, comm.Id);
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to delete communication {CommId}", comm.Id); }
    }

    private static string GetCategoryClass(string category) => category switch
    {
        "General" => "neutral",
        "ClientCommunication" => "working",
        "LegalResearch" => "followup",
        "CourtNotes" => "done",
        "Strategy" => "cold",
        "Discovery" => "followup",
        "Settlement" => "done",
        "WitnessInterview" => "working",
        "ExpertConsultation" => "working",
        "FollowUp" => "neutral",
        "Alert" => "cold",
        _ => "neutral"
    };

    private static string FormatCategory(string category) => category switch
    {
        "ClientCommunication" => "Client Communication",
        "LegalResearch" => "Legal Research",
        "CourtNotes" => "Court Notes",
        "WitnessInterview" => "Witness Interview",
        "ExpertConsultation" => "Expert Consultation",
        "FollowUp" => "Follow Up",
        _ => category
    };

    private static string TruncateContent(string content) =>
        content.Length > 500 ? content[..500] + "..." : content;
}
```

## MatterEvidenceTab.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<MatterEvidenceTab> Logger

<div class="mt-3">
    <div class="d-flex justify-content-between align-items-center mb-3">
        <h5 class="mb-0">Evidence</h5>
        <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
            <i class="bi bi-plus-circle me-1"></i> Add Evidence
        </button>
    </div>

    @if (Evidence.Count == 0)
    {
        <AppEmptyState Icon="box-seam" Title="No Evidence" Message="No evidence items have been added to this matter yet.">
            <Actions>
                <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
                    <i class="bi bi-plus-circle me-1"></i> Add Evidence
                </button>
            </Actions>
        </AppEmptyState>
    }
    else
    {
        <div class="table-responsive">
            <table class="table table-hover align-middle">
                <thead>
                    <tr>
                        <th>Evidence #</th>
                        <th>Title</th>
                        <th>Type</th>
                        <th>Source</th>
                        <th>Admissible</th>
                        <th>Received Date</th>
                        <th class="text-end">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var item in Evidence)
                    {
                        <tr>
                            <td><span class="fw-semibold">@item.EvidenceNumber</span></td>
                            <td>@item.Title</td>
                            <td>@FormatEvidenceType(item.EvidenceType)</td>
                            <td>@(item.Source ?? "N/A")</td>
                            <td>
                                @if (item.IsAdmissible == true)
                                {
                                    <AppBadge Status="done" Label="Yes" />
                                }
                                else if (item.IsAdmissible == false)
                                {
                                    <AppBadge Status="stuck" Label="No" />
                                }
                                else
                                {
                                    <AppBadge Status="neutral" Label="Pending" />
                                }
                            </td>
                            <td>@(item.ReceivedDateUtc?.ToLocalTime().ToString("MMM dd, yyyy") ?? "N/A")</td>
                            <td class="text-end">
                                <div class="btn-group btn-group-sm">
                                    @if (item.IsAdmissible != true)
                                    {
                                        <button class="btn btn-outline-success" @onclick="() => SetAdmissibilityAsync(item, true)" title="Mark Admissible">
                                            <i class="bi bi-check-lg"></i>
                                        </button>
                                    }
                                    @if (item.IsAdmissible != false)
                                    {
                                        <button class="btn btn-outline-danger" @onclick="() => SetAdmissibilityAsync(item, false)" title="Mark Inadmissible">
                                            <i class="bi bi-x-lg"></i>
                                        </button>
                                    }
                                    <button class="btn btn-outline-secondary" @onclick="() => OpenEditModal(item)" title="Edit">
                                        <i class="bi bi-pencil"></i>
                                    </button>
                                    <button class="btn btn-outline-danger" @onclick="() => DeleteEvidenceAsync(item)" title="Delete">
                                        <i class="bi bi-trash"></i>
                                    </button>
                                </div>
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>
    }
</div>

<!-- Add/Edit Evidence Modal -->
<AppModal Title="@(_isEditing ? "Edit Evidence" : "Add Evidence")" @bind-IsVisible="_showModal" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
        <div class="row g-3">
            <div class="col-12">
                <label for="evidenceTitle" class="form-label">Title <span class="text-danger">*</span></label>
                <input type="text" class="form-control" id="evidenceTitle" @bind="_title" required maxlength="200" />
            </div>
            <div class="col-12">
                <label for="evidenceDescription" class="form-label">Description</label>
                <textarea class="form-control" id="evidenceDescription" @bind="_description" rows="3"></textarea>
            </div>
            <div class="col-md-6">
                <label for="evidenceType" class="form-label">Type <span class="text-danger">*</span></label>
                <select id="evidenceType" class="form-select" @bind="_evidenceType">
                    <option value="">Select type...</option>
                    @foreach (var type in _evidenceTypes)
                    {
                        <option value="@type.Code">@type.Name</option>
                    }
                </select>
            </div>
            <div class="col-md-6">
                <label for="evidenceSource" class="form-label">Source</label>
                <input type="text" class="form-control" id="evidenceSource" @bind="_source" maxlength="200" />
            </div>
            <div class="col-12">
                <label for="chainOfCustody" class="form-label">Chain of Custody</label>
                <textarea class="form-control" id="chainOfCustody" @bind="_chainOfCustody" rows="2"></textarea>
            </div>
            <div class="col-md-6">
                <label for="receivedDate" class="form-label">Received Date</label>
                <input type="date" class="form-control" id="receivedDate" value="@_receivedDateText" @onchange="@((ChangeEventArgs e) => _receivedDateText = e.Value?.ToString())" />
            </div>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="SaveEvidenceAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>@(_isEditing ? "Save Changes" : "Add Evidence")</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter, EditorRequired]
    public Guid MatterId { get; set; }

    [Parameter, EditorRequired]
    public IReadOnlyList<EvidenceDto> Evidence { get; set; } = [];

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    private bool _showModal;
    private bool _isEditing;
    private bool _isSaving;
    private string? _errorMessage;
    private Guid? _editingId;

    // Form fields
    private string _title = string.Empty;
    private string? _description;
    private string _evidenceType = string.Empty;
    private string? _source;
    private string? _chainOfCustody;
    private string? _receivedDateText;

    private static readonly (string Code, string Name)[] _evidenceTypes = [
        ("PhysicalDocument", "Physical Document"), ("DigitalDocument", "Digital Document"),
        ("Photograph", "Photograph"), ("Video", "Video"), ("Audio", "Audio"),
        ("PhysicalObject", "Physical Object"), ("Testimony", "Testimony"),
        ("ExpertReport", "Expert Report"), ("MedicalRecord", "Medical Record"),
        ("FinancialRecord", "Financial Record"), ("Email", "Email"),
        ("TextMessage", "Text Message"), ("SocialMedia", "Social Media"),
        ("Contract", "Contract"), ("PoliceReport", "Police Report"), ("Other", "Other")
    ];

    private void OpenAddModal()
    {
        _isEditing = false;
        _editingId = null;
        _title = string.Empty;
        _description = null;
        _evidenceType = string.Empty;
        _source = null;
        _chainOfCustody = null;
        _receivedDateText = null;
        _errorMessage = null;
        _showModal = true;
    }

    private void OpenEditModal(EvidenceDto item)
    {
        _isEditing = true;
        _editingId = item.Id;
        _title = item.Title;
        _description = item.Description;
        _evidenceType = item.EvidenceType;
        _source = item.Source;
        _chainOfCustody = item.ChainOfCustody;
        _receivedDateText = item.ReceivedDateUtc?.ToLocalTime().ToString("yyyy-MM-dd");
        _errorMessage = null;
        _showModal = true;
    }

    private async Task SaveEvidenceAsync()
    {
        if (string.IsNullOrWhiteSpace(_title)) { _errorMessage = "Title is required."; return; }
        if (string.IsNullOrEmpty(_evidenceType)) { _errorMessage = "Type is required."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            DateTime? receivedDate = null;
            if (!string.IsNullOrWhiteSpace(_receivedDateText) && DateTime.TryParse(_receivedDateText, out var parsed))
                receivedDate = parsed.ToUniversalTime();

            if (_isEditing && _editingId.HasValue)
            {
                var cmd = new UpdateEvidenceCommand
                {
                    Title = _title, Description = _description, EvidenceType = _evidenceType,
                    Source = _source, ChainOfCustody = _chainOfCustody, ReceivedDateUtc = receivedDate
                };
                await ApiClient.UpdateEvidenceAsync(MatterId, _editingId.Value, cmd);
            }
            else
            {
                var cmd = new CreateEvidenceCommand
                {
                    Title = _title, Description = _description, EvidenceType = _evidenceType,
                    Source = _source, ChainOfCustody = _chainOfCustody, ReceivedDateUtc = receivedDate
                };
                await ApiClient.CreateEvidenceAsync(MatterId, cmd);
            }
            _showModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save evidence for matter {MatterId}", MatterId);
            _errorMessage = $"Failed to save evidence: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task SetAdmissibilityAsync(EvidenceDto item, bool isAdmissible)
    {
        try
        {
            await ApiClient.SetEvidenceAdmissibilityAsync(MatterId, item.Id, isAdmissible, null);
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to set admissibility for evidence {EvidenceId}", item.Id); }
    }

    private async Task DeleteEvidenceAsync(EvidenceDto item)
    {
        try
        {
            await ApiClient.DeleteEvidenceAsync(MatterId, item.Id);
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to delete evidence {EvidenceId}", item.Id); }
    }

    private static string FormatEvidenceType(string type) => type switch
    {
        "PhysicalDocument" => "Physical Document",
        "DigitalDocument" => "Digital Document",
        "PhysicalObject" => "Physical Object",
        "ExpertReport" => "Expert Report",
        "MedicalRecord" => "Medical Record",
        "FinancialRecord" => "Financial Record",
        "TextMessage" => "Text Message",
        "SocialMedia" => "Social Media",
        "PoliceReport" => "Police Report",
        _ => type
    };
}
```

## MatterLitigationTab.razor

```razor
@inject IApiClient ApiClient
@inject NavigationManager Navigation
@inject IJSRuntime JS
@inject ILogger<MatterLitigationTab> Logger

<div class="mt-3">
    <!-- Summary Cards -->
    <div class="row g-3 mb-3">
        <div class="col-md-3">
            <AppCard>
                <ChildContent>
                    <div class="text-muted small">Total Items</div>
                    <div class="fs-4 fw-bold">@LitigationItems.Count</div>
                </ChildContent>
            </AppCard>
        </div>
        <div class="col-md-3">
            <AppCard>
                <ChildContent>
                    <div class="text-muted small">Pending Responses</div>
                    <div class="fs-4 fw-bold text-warning">@LitigationItems.Count(i => i.Status == LitigationStatusDto.PendingResponse)</div>
                </ChildContent>
            </AppCard>
        </div>
        <div class="col-md-3">
            <AppCard>
                <ChildContent>
                    <div class="text-muted small">Overdue</div>
                    <div class="fs-4 fw-bold text-danger">@LitigationItems.Count(i => i.IsOverdue)</div>
                </ChildContent>
            </AppCard>
        </div>
        <div class="col-md-3">
            <AppCard>
                <ChildContent>
                    <div class="text-muted small">Next Deadline</div>
                    <div class="fs-4 fw-bold">@GetNextDeadline()</div>
                </ChildContent>
            </AppCard>
        </div>
    </div>

    <div class="d-flex justify-content-between align-items-center mb-3">
        <h5 class="mb-0">Litigation</h5>
        <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
            <i class="bi bi-plus-circle me-1"></i> Add Item
        </button>
    </div>

    @if (_errorMessage != null)
    {
        <div class="alert alert-danger app-alert-inline" role="alert">
            <i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage
            <button class="btn-close float-end" @onclick="() => _errorMessage = null"></button>
        </div>
    }

    @if (LitigationItems.Count == 0)
    {
        <AppEmptyState Icon="briefcase" Title="No Litigation Items" Message="No litigation items have been added to this matter yet.">
            <Actions>
                <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
                    <i class="bi bi-plus-circle me-1"></i> Add Item
                </button>
            </Actions>
        </AppEmptyState>
    }
    else
    {
        <div class="table-responsive">
            <table class="table table-hover align-middle">
                <thead>
                    <tr>
                        <th>Type</th>
                        <th>Title</th>
                        <th>Filed</th>
                        <th>Due</th>
                        <th>Status</th>
                        <th>Docs</th>
                        <th class="text-end">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var item in LitigationItems)
                    {
                        <tr style="cursor: pointer;" @onclick="() => ToggleItemExpand(item)">
                            <td>
                                <i class="bi @GetTypeIcon(item.Type) me-1 text-muted"></i>
                                <span>@FormatType(item.Type)</span>
                            </td>
                            <td><span class="text-primary fw-semibold">@item.Title</span></td>
                            <td class="text-muted">@(item.FiledDateUtc?.ToLocalTime().ToString("MMM dd, yyyy") ?? "—")</td>
                            <td class="text-muted">@(item.DueDateUtc?.ToLocalTime().ToString("MMM dd, yyyy") ?? "—")</td>
                            <td>
                                <span class="badge bg-@GetStatusBadgeClass(item.Status)">@FormatStatus(item.Status)</span>
                            </td>
                            <td>
                                @if (item.DocumentCount > 0)
                                {
                                    <span class="badge bg-secondary"><i class="bi bi-paperclip me-1"></i>@item.DocumentCount</span>
                                }
                                else
                                {
                                    <span class="text-muted">—</span>
                                }
                            </td>
                            <td class="text-end" @onclick:stopPropagation>
                                <div class="btn-group btn-group-sm">
                                    <button class="btn btn-outline-secondary" @onclick="() => OpenEditModal(item)" title="Edit">
                                        <i class="bi bi-pencil"></i>
                                    </button>
                                    <label class="btn btn-outline-secondary mb-0" for="@($"litUpload_{item.Id}")" title="Attach File">
                                        <i class="bi bi-paperclip"></i>
                                    </label>
                                    <InputFile OnChange="(e) => HandleFileUpload(e, item.Id)" class="d-none" id="@($"litUpload_{item.Id}")" multiple />
                                    <button class="btn btn-outline-danger" @onclick="() => ConfirmDelete(item)" title="Delete">
                                        <i class="bi bi-trash"></i>
                                    </button>
                                </div>
                            </td>
                        </tr>

                        @if (_expandedItemId == item.Id)
                        {
                            <tr>
                                <td colspan="7" class="bg-light border-0 px-4 py-3">
                                    @if (_isLoadingDocuments)
                                    {
                                        <div class="d-flex align-items-center gap-2">
                                            <span class="spinner-border spinner-border-sm text-primary"></span>
                                            <span class="text-muted small">Loading documents...</span>
                                        </div>
                                    }
                                    else if (_expandedDocuments.Count == 0)
                                    {
                                        <div class="text-muted small">
                                            <i class="bi bi-folder2-open me-1"></i>No documents attached.
                                            <label class="btn btn-sm btn-outline-secondary ms-2 mb-0" for="@($"litUploadExpand_{item.Id}")">
                                                <i class="bi bi-cloud-upload me-1"></i> Upload
                                            </label>
                                            <InputFile OnChange="(e) => HandleFileUpload(e, item.Id)" class="d-none" id="@($"litUploadExpand_{item.Id}")" multiple />
                                        </div>
                                    }
                                    else
                                    {
                                        <div class="d-flex justify-content-between align-items-center mb-2">
                                            <span class="fw-semibold small">Attached Documents</span>
                                            <label class="btn btn-sm btn-outline-secondary mb-0" for="@($"litUploadExpand_{item.Id}")">
                                                <i class="bi bi-cloud-upload me-1"></i> Upload
                                            </label>
                                            <InputFile OnChange="(e) => HandleFileUpload(e, item.Id)" class="d-none" id="@($"litUploadExpand_{item.Id}")" multiple />
                                        </div>
                                        <ul class="list-group list-group-flush">
                                            @foreach (var doc in _expandedDocuments)
                                            {
                                                <li class="list-group-item bg-transparent d-flex justify-content-between align-items-center px-0 py-1"
                                                    style="cursor: pointer;" @onclick="() => OpenDocumentAsync(doc)">
                                                    <span class="text-primary small">
                                                        <i class="bi @GetFileIcon(doc.MimeType) me-1"></i>@doc.Name
                                                    </span>
                                                    <span class="text-muted small">@FormatFileSize(doc.Size)</span>
                                                </li>
                                            }
                                        </ul>
                                    }
                                </td>
                            </tr>
                        }
                    }
                </tbody>
            </table>
        </div>
    }
</div>

<!-- Add/Edit Litigation Item Modal -->
<AppModal Title="@(_isEditing ? "Edit Litigation Item" : "Add Litigation Item")" @bind-IsVisible="_showModal" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_modalError != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_modalError</div>
        }
        <div class="row g-3">
            <div class="col-md-6">
                <label for="litType" class="form-label">Type <span class="text-danger">*</span></label>
                <select id="litType" class="form-select" @bind="_formType">
                    @foreach (var type in Enum.GetValues<LitigationTypeDto>())
                    {
                        <option value="@type">@FormatType(type)</option>
                    }
                </select>
            </div>
            <div class="col-md-6">
                <label for="litStatus" class="form-label">Status</label>
                <select id="litStatus" class="form-select" @bind="_formStatus">
                    @foreach (var status in Enum.GetValues<LitigationStatusDto>())
                    {
                        <option value="@status">@FormatStatus(status)</option>
                    }
                </select>
            </div>
            <div class="col-12">
                <label for="litTitle" class="form-label">Title <span class="text-danger">*</span></label>
                <input type="text" class="form-control" id="litTitle" @bind="_formTitle" required maxlength="300" />
            </div>
            <div class="col-md-6">
                <label for="litFiledDate" class="form-label">Filed Date</label>
                <input type="date" class="form-control" id="litFiledDate" value="@_formFiledDateText" @onchange="@((ChangeEventArgs e) => _formFiledDateText = e.Value?.ToString())" />
            </div>
            <div class="col-md-6">
                <label for="litDueDate" class="form-label">Due Date</label>
                <input type="date" class="form-control" id="litDueDate" value="@_formDueDateText" @onchange="@((ChangeEventArgs e) => _formDueDateText = e.Value?.ToString())" />
            </div>
            <div class="col-12">
                <label for="litFiledBy" class="form-label">Filed By</label>
                <input type="text" class="form-control" id="litFiledBy" @bind="_formFiledBy" maxlength="200" />
            </div>
            <div class="col-12">
                <label for="litNotes" class="form-label">Notes</label>
                <textarea class="form-control" id="litNotes" @bind="_formNotes" rows="3"></textarea>
            </div>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="SaveItemAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>@(_isEditing ? "Save Changes" : "Add Item")</span> }
        </button>
    </Footer>
</AppModal>

<!-- Delete Confirmation Modal -->
<AppModal Title="Delete Litigation Item" @bind-IsVisible="_showDeleteModal" Size="sm">
    <ChildContent>
        <p>Are you sure you want to delete <strong>@_itemToDelete?.Title</strong>?</p>
        <div class="alert alert-warning mb-0">
            <i class="bi bi-exclamation-triangle me-2"></i>This will also delete all attached documents for this item.
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showDeleteModal = false" disabled="@_isDeleting">Cancel</button>
        <button type="button" class="btn btn-danger" @onclick="DeleteItemAsync" disabled="@_isDeleting">
            @if (_isDeleting) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Deleting...</span> }
            else { <i class="bi bi-trash me-1"></i> <span>Delete</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter, EditorRequired]
    public Guid MatterId { get; set; }

    [Parameter, EditorRequired]
    public IReadOnlyList<LitigationItemDto> LitigationItems { get; set; } = [];

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    // General state
    private string? _errorMessage;

    // Add/Edit modal
    private bool _showModal;
    private bool _isEditing;
    private bool _isSaving;
    private string? _modalError;
    private Guid? _editingId;

    // Form fields
    private LitigationTypeDto _formType = LitigationTypeDto.Motion;
    private string _formTitle = string.Empty;
    private string? _formFiledDateText;
    private string? _formDueDateText;
    private LitigationStatusDto _formStatus = LitigationStatusDto.Draft;
    private string? _formFiledBy;
    private string? _formNotes;

    // Delete modal
    private bool _showDeleteModal;
    private bool _isDeleting;
    private LitigationItemDto? _itemToDelete;

    // Expanded row / documents
    private Guid? _expandedItemId;
    private List<DocumentItemDto> _expandedDocuments = [];
    private bool _isLoadingDocuments;

    private string GetNextDeadline()
    {
        var next = LitigationItems
            .Where(i => i.DueDateUtc.HasValue && i.DueDateUtc.Value > DateTime.UtcNow)
            .OrderBy(i => i.DueDateUtc)
            .FirstOrDefault();
        return next?.DueDateUtc?.ToLocalTime().ToString("MMM dd, yyyy") ?? "—";
    }

    // --- Expand / Documents ---

    private async Task ToggleItemExpand(LitigationItemDto item)
    {
        if (_expandedItemId == item.Id)
        {
            _expandedItemId = null;
            _expandedDocuments = [];
            return;
        }

        _expandedItemId = item.Id;
        _expandedDocuments = [];
        await LoadItemDocumentsAsync(item.Id);
    }

    private async Task LoadItemDocumentsAsync(Guid itemId)
    {
        try
        {
            _isLoadingDocuments = true;
            StateHasChanged();
            _expandedDocuments = (await ApiClient.GetLitigationDocumentsAsync(MatterId, itemId)).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load documents for litigation item {ItemId}", itemId);
            _expandedDocuments = [];
        }
        finally
        {
            _isLoadingDocuments = false;
            StateHasChanged();
        }
    }

    private async Task OpenDocumentAsync(DocumentItemDto doc)
    {
        var isOfficeFile = GetOfficeProtocol(doc.Name) != null;

        if (isOfficeFile && !string.IsNullOrEmpty(doc.WebUrl))
        {
            await JS.InvokeVoidAsync("open", doc.WebUrl, "_blank");
            return;
        }

        // Non-Office files: use preview endpoint
        try
        {
            var preview = await ApiClient.GetDocumentPreviewAsync(MatterId, doc.Id);
            var url = preview.ViewUrl ?? preview.EditUrl;
            if (!string.IsNullOrEmpty(url))
            {
                await JS.InvokeVoidAsync("open", url, "_blank");
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get preview URL for document {DocId}", doc.Id);
        }

        // Fallback: download
        var downloadUrl = $"/download/matters/{MatterId}/documents/{Uri.EscapeDataString(doc.Id)}";
        Navigation.NavigateTo(downloadUrl, forceLoad: true);
    }

    // --- File Upload ---

    private async Task HandleFileUpload(InputFileChangeEventArgs e, Guid itemId)
    {
        _errorMessage = null;

        // Buffer all files into memory BEFORE re-rendering.
        // Re-rendering removes the InputFile from the DOM which destroys
        // Blazor's JS-side file reference (_blazorFilesById), causing
        // "Cannot read properties of null" errors on OpenReadStream().
        var bufferedFiles = new List<(string Name, MemoryStream Data)>();
        foreach (var file in e.GetMultipleFiles(10))
        {
            try
            {
                var ms = new MemoryStream();
                await using var browserStream = file.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024); // 100 MB
                await browserStream.CopyToAsync(ms);
                ms.Position = 0;
                bufferedFiles.Add((file.Name, ms));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to buffer file {FileName}", file.Name);
            }
        }

        if (bufferedFiles.Count == 0)
            return;

        StateHasChanged();

        var uploadErrors = new List<string>();
        foreach (var (name, data) in bufferedFiles)
        {
            try
            {
                await ApiClient.UploadLitigationDocumentAsync(MatterId, itemId, name, data);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to upload file {FileName} to litigation item {ItemId}", name, itemId);
                uploadErrors.Add(name);
            }
            finally
            {
                await data.DisposeAsync();
            }
        }

        if (uploadErrors.Count > 0)
            _errorMessage = $"Failed to upload: {string.Join(", ", uploadErrors)}";

        // Refresh the parent to update document counts
        await OnRefresh.InvokeAsync();

        // Reload expanded documents if this item is currently expanded
        if (_expandedItemId == itemId)
            await LoadItemDocumentsAsync(itemId);
    }

    // --- Add / Edit ---

    private void OpenAddModal()
    {
        _isEditing = false;
        _editingId = null;
        _formType = LitigationTypeDto.Motion;
        _formTitle = string.Empty;
        _formFiledDateText = null;
        _formDueDateText = null;
        _formStatus = LitigationStatusDto.Draft;
        _formFiledBy = null;
        _formNotes = null;
        _modalError = null;
        _showModal = true;
    }

    private void OpenEditModal(LitigationItemDto item)
    {
        _isEditing = true;
        _editingId = item.Id;
        _formType = item.Type;
        _formTitle = item.Title;
        _formFiledDateText = item.FiledDateUtc?.ToLocalTime().ToString("yyyy-MM-dd");
        _formDueDateText = item.DueDateUtc?.ToLocalTime().ToString("yyyy-MM-dd");
        _formStatus = item.Status;
        _formFiledBy = item.FiledBy;
        _formNotes = item.Notes;
        _modalError = null;
        _showModal = true;
    }

    private async Task SaveItemAsync()
    {
        if (string.IsNullOrWhiteSpace(_formTitle)) { _modalError = "Title is required."; return; }

        try
        {
            _isSaving = true; _modalError = null; StateHasChanged();

            DateTime? ParseDate(string? text) =>
                !string.IsNullOrWhiteSpace(text) && DateTime.TryParse(text, out var d) ? d.ToUniversalTime() : null;

            if (_isEditing && _editingId.HasValue)
            {
                var cmd = new UpdateLitigationItemCommand
                {
                    Id = _editingId.Value,
                    Type = _formType,
                    Title = _formTitle,
                    FiledDateUtc = ParseDate(_formFiledDateText),
                    DueDateUtc = ParseDate(_formDueDateText),
                    Status = _formStatus,
                    FiledBy = _formFiledBy,
                    Notes = _formNotes
                };
                await ApiClient.UpdateLitigationItemAsync(MatterId, cmd);
            }
            else
            {
                var cmd = new CreateLitigationItemCommand
                {
                    Type = _formType,
                    Title = _formTitle,
                    FiledDateUtc = ParseDate(_formFiledDateText),
                    DueDateUtc = ParseDate(_formDueDateText),
                    Status = _formStatus,
                    FiledBy = _formFiledBy,
                    Notes = _formNotes
                };
                await ApiClient.CreateLitigationItemAsync(MatterId, cmd);
            }
            _showModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save litigation item for matter {MatterId}", MatterId);
            _modalError = $"Failed to save litigation item: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    // --- Delete ---

    private void ConfirmDelete(LitigationItemDto item)
    {
        _itemToDelete = item;
        _showDeleteModal = true;
    }

    private async Task DeleteItemAsync()
    {
        if (_itemToDelete == null) return;

        try
        {
            _isDeleting = true;
            _errorMessage = null;
            StateHasChanged();

            await ApiClient.DeleteLitigationItemAsync(MatterId, _itemToDelete.Id);
            _showDeleteModal = false;

            // Collapse if we deleted the expanded item
            if (_expandedItemId == _itemToDelete.Id)
            {
                _expandedItemId = null;
                _expandedDocuments = [];
            }

            _itemToDelete = null;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete litigation item {ItemId}", _itemToDelete.Id);
            _errorMessage = $"Failed to delete: {ex.Message}";
        }
        finally
        {
            _isDeleting = false;
            StateHasChanged();
        }
    }

    // --- Formatting Helpers ---

    private static string FormatType(LitigationTypeDto type) => type switch
    {
        LitigationTypeDto.Complaint => "Complaint",
        LitigationTypeDto.Answer => "Answer",
        LitigationTypeDto.Motion => "Motion",
        LitigationTypeDto.DiscoveryRequest => "Discovery Request",
        LitigationTypeDto.DiscoveryResponse => "Discovery Response",
        LitigationTypeDto.Deposition => "Deposition",
        LitigationTypeDto.CourtOrder => "Court Order",
        LitigationTypeDto.Subpoena => "Subpoena",
        LitigationTypeDto.Brief => "Brief",
        LitigationTypeDto.Other => "Other",
        _ => type.ToString()
    };

    private static string GetTypeIcon(LitigationTypeDto type) => type switch
    {
        LitigationTypeDto.Complaint => "bi-exclamation-circle",
        LitigationTypeDto.Answer => "bi-reply",
        LitigationTypeDto.Motion => "bi-file-earmark-text",
        LitigationTypeDto.DiscoveryRequest => "bi-search",
        LitigationTypeDto.DiscoveryResponse => "bi-file-earmark-check",
        LitigationTypeDto.Deposition => "bi-mic",
        LitigationTypeDto.CourtOrder => "bi-hammer",
        LitigationTypeDto.Subpoena => "bi-envelope-paper",
        LitigationTypeDto.Brief => "bi-journal-text",
        LitigationTypeDto.Other => "bi-file-earmark",
        _ => "bi-file-earmark"
    };

    private static string FormatStatus(LitigationStatusDto status) => status switch
    {
        LitigationStatusDto.Draft => "Draft",
        LitigationStatusDto.Filed => "Filed",
        LitigationStatusDto.Served => "Served",
        LitigationStatusDto.PendingResponse => "Pending Response",
        LitigationStatusDto.Completed => "Completed",
        LitigationStatusDto.Overdue => "Overdue",
        _ => status.ToString()
    };

    private static string GetStatusBadgeClass(LitigationStatusDto status) => status switch
    {
        LitigationStatusDto.Draft => "secondary",
        LitigationStatusDto.Filed => "primary",
        LitigationStatusDto.Served => "info",
        LitigationStatusDto.PendingResponse => "warning",
        LitigationStatusDto.Completed => "success",
        LitigationStatusDto.Overdue => "danger",
        _ => "secondary"
    };

    private static string? GetOfficeProtocol(string fileName)
    {
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        return ext switch
        {
            ".doc" or ".docx" or ".docm" or ".dotx" or ".rtf" => "ms-word",
            ".xls" or ".xlsx" or ".xlsm" or ".xltx" or ".csv" => "ms-excel",
            ".ppt" or ".pptx" or ".pptm" or ".potx" => "ms-powerpoint",
            _ => null
        };
    }

    private static string GetFileIcon(string? mimeType) => mimeType switch
    {
        not null when mimeType.StartsWith("image/") => "bi-file-image",
        not null when mimeType.StartsWith("video/") => "bi-file-play",
        not null when mimeType.StartsWith("audio/") => "bi-file-music",
        "application/pdf" => "bi-file-pdf",
        not null when mimeType.Contains("spreadsheet") || mimeType.Contains("excel") => "bi-file-earmark-spreadsheet",
        not null when mimeType.Contains("document") || mimeType.Contains("word") => "bi-file-earmark-word",
        not null when mimeType.Contains("presentation") || mimeType.Contains("powerpoint") => "bi-file-earmark-slides",
        not null when mimeType.Contains("zip") || mimeType.Contains("archive") || mimeType.Contains("compressed") => "bi-file-zip",
        not null when mimeType.StartsWith("text/") => "bi-file-text",
        _ => "bi-file-earmark"
    };

    private const long KB = 1024;
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * 1024 * 1024;

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= GB) return $"{bytes / (double)GB:F2} GB";
        if (bytes >= MB) return $"{bytes / (double)MB:F1} MB";
        if (bytes >= KB) return $"{bytes / (double)KB:F1} KB";
        return $"{bytes} B";
    }
}
```

## MatterMedicalRecordsTab.razor

```razor
@inject IApiClient ApiClient
@inject NavigationManager Navigation
@inject IJSRuntime JS
@inject ILogger<MatterMedicalRecordsTab> Logger

<div class="mt-3">
    <!-- Summary Card -->
    @if (PISummary != null)
    {
        <AppCard CssClass="mb-3">
            <ChildContent>
                <div class="d-flex align-items-center gap-4">
                    <div>
                        <div class="text-muted small">Total Medical Bills</div>
                        <div class="fs-4 fw-bold">@PISummary.TotalMedicalBills.ToString("C")</div>
                    </div>
                </div>
            </ChildContent>
        </AppCard>
    }

    <div class="d-flex justify-content-between align-items-center mb-3">
        <h5 class="mb-0">Medical Records</h5>
        <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
            <i class="bi bi-plus-circle me-1"></i> Add Record
        </button>
    </div>

    @if (_errorMessage != null)
    {
        <div class="alert alert-danger app-alert-inline" role="alert">
            <i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage
            <button class="btn-close float-end" @onclick="() => _errorMessage = null"></button>
        </div>
    }

    @if (MedicalRecords.Count == 0)
    {
        <AppEmptyState Icon="hospital" Title="No Medical Records" Message="No medical records have been added to this matter yet.">
            <Actions>
                <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
                    <i class="bi bi-plus-circle me-1"></i> Add Record
                </button>
            </Actions>
        </AppEmptyState>
    }
    else
    {
        <div class="table-responsive">
            <table class="table table-hover align-middle">
                <thead>
                    <tr>
                        <th>Provider</th>
                        <th>Subject</th>
                        <th>Treatment Dates</th>
                        <th class="text-end">Bill Amount</th>
                        <th class="text-end">Charge Amount</th>
                        <th>Records Status</th>
                        <th>Docs</th>
                        <th class="text-end">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var record in MedicalRecords)
                    {
                        <tr style="cursor: pointer;" @onclick="() => ToggleRecordExpand(record.Id)">
                            <td>@record.ProviderName</td>
                            <td>@record.Subject</td>
                            <td>
                                @(record.TreatmentFromDate?.ToLocalTime().ToString("MM/dd/yyyy") ?? "N/A")
                                @if (record.TreatmentToDate.HasValue)
                                {
                                    <span> - @record.TreatmentToDate.Value.ToLocalTime().ToString("MM/dd/yyyy")</span>
                                }
                            </td>
                            <td class="text-end">@(record.BillAmount?.ToString("C") ?? "N/A")</td>
                            <td class="text-end">@(record.ChargeAmount?.ToString("C") ?? "N/A")</td>
                            <td>
                                @if (record.HasRecordsReceived)
                                {
                                    <AppBadge Status="done" Label="Received" />
                                }
                                else if (record.HasRecordsRequested)
                                {
                                    <AppBadge Status="working" Label="Requested" />
                                    @if (record.DaysWaitingForRecords.HasValue)
                                    {
                                        <span class="small text-muted ms-1">(@record.DaysWaitingForRecords days)</span>
                                    }
                                }
                                else
                                {
                                    <AppBadge Status="neutral" Label="Not Requested" />
                                }
                            </td>
                            <td>
                                @if (record.DocumentCount > 0)
                                {
                                    <span class="badge bg-secondary"><i class="bi bi-paperclip me-1"></i>@record.DocumentCount</span>
                                }
                                else
                                {
                                    <span class="text-muted">—</span>
                                }
                            </td>
                            <td class="text-end" @onclick:stopPropagation>
                                <div class="btn-group btn-group-sm">
                                    <button class="btn btn-outline-secondary" @onclick="() => OpenEditModal(record)" title="Edit">
                                        <i class="bi bi-pencil"></i>
                                    </button>
                                    <label class="btn btn-outline-secondary mb-0" for="@($"mrUpload_{record.Id}")" title="Attach File">
                                        <i class="bi bi-paperclip"></i>
                                    </label>
                                    <InputFile OnChange="(e) => HandleDocumentUpload(e, record.Id)" class="d-none" id="@($"mrUpload_{record.Id}")" multiple />
                                    <button class="btn btn-outline-danger" @onclick="() => DeleteRecordAsync(record)" title="Delete">
                                        <i class="bi bi-trash"></i>
                                    </button>
                                </div>
                            </td>
                        </tr>

                        @if (_expandedRecordId == record.Id)
                        {
                            <tr>
                                <td colspan="8" class="bg-light border-0 px-4 py-3">
                                    @if (_isLoadingDocs)
                                    {
                                        <div class="d-flex align-items-center gap-2">
                                            <span class="spinner-border spinner-border-sm text-primary"></span>
                                            <span class="text-muted small">Loading documents...</span>
                                        </div>
                                    }
                                    else if (_expandedDocuments.Count == 0)
                                    {
                                        <div class="text-muted small">
                                            <i class="bi bi-folder2-open me-1"></i>No documents attached.
                                            <label class="btn btn-sm btn-outline-secondary ms-2 mb-0" for="@($"mrUploadExpand_{record.Id}")">
                                                <i class="bi bi-cloud-upload me-1"></i> Upload
                                            </label>
                                            <InputFile OnChange="(e) => HandleDocumentUpload(e, record.Id)" class="d-none" id="@($"mrUploadExpand_{record.Id}")" multiple />
                                        </div>
                                    }
                                    else
                                    {
                                        <div class="d-flex justify-content-between align-items-center mb-2">
                                            <span class="fw-semibold small">Attached Documents</span>
                                            <label class="btn btn-sm btn-outline-secondary mb-0" for="@($"mrUploadExpand_{record.Id}")">
                                                <i class="bi bi-cloud-upload me-1"></i> Upload
                                            </label>
                                            <InputFile OnChange="(e) => HandleDocumentUpload(e, record.Id)" class="d-none" id="@($"mrUploadExpand_{record.Id}")" multiple />
                                        </div>
                                        <ul class="list-group list-group-flush">
                                            @foreach (var doc in _expandedDocuments)
                                            {
                                                <li class="list-group-item bg-transparent d-flex justify-content-between align-items-center px-0 py-1"
                                                    style="cursor: pointer;" @onclick="() => OpenDocumentAsync(doc)">
                                                    <span class="text-primary small">
                                                        <i class="bi @GetFileIcon(doc.MimeType) me-1"></i>@doc.Name
                                                    </span>
                                                    <span class="text-muted small">@FormatFileSize(doc.Size)</span>
                                                </li>
                                            }
                                        </ul>
                                    }
                                </td>
                            </tr>
                        }
                    }
                </tbody>
            </table>
        </div>
    }
</div>

<!-- Add/Edit Medical Record Modal -->
<AppModal Title="@(_isEditing ? "Edit Medical Record" : "Add Medical Record")" @bind-IsVisible="_showModal" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
        <div class="row g-3">
            <div class="col-12">
                <ContactAutocomplete Label="Provider" Required="true" ContactTypeCode="MEDICAL"
                                     @bind-SelectedContact="_selectedProvider"
                                     InitialContactId="@_initialProviderId" />
            </div>
            <div class="col-12">
                <label for="mrSubject" class="form-label">Subject <span class="text-danger">*</span></label>
                <input type="text" class="form-control" id="mrSubject" @bind="_subject" required maxlength="200" />
            </div>
            <div class="col-md-6">
                <label for="mrTreatmentFrom" class="form-label">Treatment From</label>
                <input type="date" class="form-control" id="mrTreatmentFrom" value="@_treatmentFromText" @onchange="@((ChangeEventArgs e) => _treatmentFromText = e.Value?.ToString())" />
            </div>
            <div class="col-md-6">
                <label for="mrTreatmentTo" class="form-label">Treatment To</label>
                <input type="date" class="form-control" id="mrTreatmentTo" value="@_treatmentToText" @onchange="@((ChangeEventArgs e) => _treatmentToText = e.Value?.ToString())" />
            </div>
            <div class="col-md-6">
                <label for="mrRequestedDate" class="form-label">Records Requested Date</label>
                <input type="date" class="form-control" id="mrRequestedDate" value="@_requestedDateText" @onchange="@((ChangeEventArgs e) => _requestedDateText = e.Value?.ToString())" />
            </div>
            <div class="col-md-6">
                <label for="mrReceivedDate" class="form-label">Records Received Date</label>
                <input type="date" class="form-control" id="mrReceivedDate" value="@_receivedDateText" @onchange="@((ChangeEventArgs e) => _receivedDateText = e.Value?.ToString())" />
            </div>
            <div class="col-md-6">
                <label for="mrBillAmount" class="form-label">Bill Amount</label>
                <input type="number" class="form-control" id="mrBillAmount" @bind="_billAmount" step="0.01" />
            </div>
            <div class="col-md-6">
                <label for="mrChargeAmount" class="form-label">Charge Amount</label>
                <input type="number" class="form-control" id="mrChargeAmount" @bind="_chargeAmount" step="0.01" />
            </div>
            <div class="col-md-6">
                <label for="mrDamageCategory" class="form-label">Damage Category</label>
                <input type="text" class="form-control" id="mrDamageCategory" @bind="_damageCategory" maxlength="100" />
            </div>
            <div class="col-12">
                <label for="mrNotes" class="form-label">Notes</label>
                <textarea class="form-control" id="mrNotes" @bind="_notes" rows="2"></textarea>
            </div>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="SaveRecordAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>@(_isEditing ? "Save Changes" : "Add Record")</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter, EditorRequired]
    public Guid MatterId { get; set; }

    [Parameter, EditorRequired]
    public IReadOnlyList<MedicalRecordDto> MedicalRecords { get; set; } = [];

    [Parameter]
    public PIMattertSummaryDto? PISummary { get; set; }

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    private bool _showModal;
    private bool _isEditing;
    private bool _isSaving;
    private string? _errorMessage;
    private Guid? _editingId;

    // Document expansion state
    private Guid? _expandedRecordId;
    private List<DocumentItemDto> _expandedDocuments = [];
    private bool _isLoadingDocs;

    // Form fields
    private ContactSummaryDto? _selectedProvider;
    private Guid? _initialProviderId;
    private string _subject = string.Empty;
    private string? _treatmentFromText;
    private string? _treatmentToText;
    private string? _requestedDateText;
    private string? _receivedDateText;
    private decimal? _billAmount;
    private decimal? _chargeAmount;
    private string? _damageCategory;
    private string? _notes;

    private void OpenAddModal()
    {
        _isEditing = false;
        _editingId = null;
        _selectedProvider = null;
        _initialProviderId = null;
        _subject = string.Empty;
        _treatmentFromText = null;
        _treatmentToText = null;
        _requestedDateText = null;
        _receivedDateText = null;
        _billAmount = null;
        _chargeAmount = null;
        _damageCategory = null;
        _notes = null;
        _errorMessage = null;
        _showModal = true;
    }

    private void OpenEditModal(MedicalRecordDto record)
    {
        _isEditing = true;
        _editingId = record.Id;
        _selectedProvider = null;
        _initialProviderId = record.ProviderId;
        _subject = record.Subject;
        _treatmentFromText = record.TreatmentFromDate?.ToLocalTime().ToString("yyyy-MM-dd");
        _treatmentToText = record.TreatmentToDate?.ToLocalTime().ToString("yyyy-MM-dd");
        _requestedDateText = record.RecordsRequestedDate?.ToLocalTime().ToString("yyyy-MM-dd");
        _receivedDateText = record.RecordsReceivedDate?.ToLocalTime().ToString("yyyy-MM-dd");
        _billAmount = record.BillAmount;
        _chargeAmount = record.ChargeAmount;
        _damageCategory = record.DamageCategory;
        _notes = record.Notes;
        _errorMessage = null;
        _showModal = true;
    }

    private async Task SaveRecordAsync()
    {
        if (_selectedProvider == null && !_initialProviderId.HasValue) { _errorMessage = "Provider is required."; return; }
        if (string.IsNullOrWhiteSpace(_subject)) { _errorMessage = "Subject is required."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            var providerId = _selectedProvider?.Id ?? _initialProviderId!.Value;

            DateTime? ParseDate(string? text) =>
                !string.IsNullOrWhiteSpace(text) && DateTime.TryParse(text, out var d) ? d.ToUniversalTime() : null;

            if (_isEditing && _editingId.HasValue)
            {
                var cmd = new UpdateMedicalRecordCommand
                {
                    ProviderId = providerId, Subject = _subject,
                    TreatmentFromDate = ParseDate(_treatmentFromText), TreatmentToDate = ParseDate(_treatmentToText),
                    RecordsRequestedDate = ParseDate(_requestedDateText), RecordsReceivedDate = ParseDate(_receivedDateText),
                    BillAmount = _billAmount, ChargeAmount = _chargeAmount,
                    DamageCategory = _damageCategory, Notes = _notes
                };
                await ApiClient.UpdateMedicalRecordAsync(MatterId, _editingId.Value, cmd);
            }
            else
            {
                var cmd = new CreateMedicalRecordCommand
                {
                    ProviderId = providerId, Subject = _subject,
                    TreatmentFromDate = ParseDate(_treatmentFromText), TreatmentToDate = ParseDate(_treatmentToText),
                    RecordsRequestedDate = ParseDate(_requestedDateText), RecordsReceivedDate = ParseDate(_receivedDateText),
                    BillAmount = _billAmount, ChargeAmount = _chargeAmount,
                    DamageCategory = _damageCategory, Notes = _notes
                };
                await ApiClient.CreateMedicalRecordAsync(MatterId, cmd);
            }
            _showModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save medical record for matter {MatterId}", MatterId);
            _errorMessage = $"Failed to save record: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task DeleteRecordAsync(MedicalRecordDto record)
    {
        try
        {
            await ApiClient.DeleteMedicalRecordAsync(MatterId, record.Id);
            if (_expandedRecordId == record.Id)
            {
                _expandedRecordId = null;
                _expandedDocuments = [];
            }
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to delete medical record {RecordId}", record.Id); }
    }

    // --- Expand / Documents ---

    private async Task ToggleRecordExpand(Guid recordId)
    {
        if (_expandedRecordId == recordId)
        {
            _expandedRecordId = null;
            _expandedDocuments = [];
            return;
        }

        _expandedRecordId = recordId;
        _expandedDocuments = [];
        await LoadRecordDocumentsAsync(recordId);
    }

    private async Task LoadRecordDocumentsAsync(Guid recordId)
    {
        try
        {
            _isLoadingDocs = true;
            StateHasChanged();
            _expandedDocuments = (await ApiClient.GetMedicalRecordDocumentsAsync(MatterId, recordId)).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load documents for medical record {RecordId}", recordId);
            _expandedDocuments = [];
        }
        finally
        {
            _isLoadingDocs = false;
            StateHasChanged();
        }
    }

    private async Task HandleDocumentUpload(InputFileChangeEventArgs e, Guid recordId)
    {
        _errorMessage = null;

        // Buffer all files into memory BEFORE re-rendering to avoid _blazorFilesById errors
        var bufferedFiles = new List<(string Name, MemoryStream Data)>();
        foreach (var file in e.GetMultipleFiles(10))
        {
            try
            {
                var ms = new MemoryStream();
                await using var browserStream = file.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024);
                await browserStream.CopyToAsync(ms);
                ms.Position = 0;
                bufferedFiles.Add((file.Name, ms));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to buffer file {FileName}", file.Name);
            }
        }

        if (bufferedFiles.Count == 0)
            return;

        StateHasChanged();

        var uploadErrors = new List<string>();
        foreach (var (name, data) in bufferedFiles)
        {
            try
            {
                await ApiClient.UploadMedicalRecordDocumentAsync(MatterId, recordId, name, data);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to upload file {FileName} to medical record {RecordId}", name, recordId);
                uploadErrors.Add(name);
            }
            finally
            {
                await data.DisposeAsync();
            }
        }

        if (uploadErrors.Count > 0)
            _errorMessage = $"Failed to upload: {string.Join(", ", uploadErrors)}";

        await OnRefresh.InvokeAsync();

        if (_expandedRecordId == recordId)
            await LoadRecordDocumentsAsync(recordId);
    }

    private async Task OpenDocumentAsync(DocumentItemDto doc)
    {
        var isOfficeFile = GetOfficeProtocol(doc.Name) != null;

        if (isOfficeFile && !string.IsNullOrEmpty(doc.WebUrl))
        {
            await JS.InvokeVoidAsync("open", doc.WebUrl, "_blank");
            return;
        }

        try
        {
            var preview = await ApiClient.GetDocumentPreviewAsync(MatterId, doc.Id);
            var url = preview.ViewUrl ?? preview.EditUrl;
            if (!string.IsNullOrEmpty(url))
            {
                await JS.InvokeVoidAsync("open", url, "_blank");
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get preview URL for document {DocId}", doc.Id);
        }

        var downloadUrl = $"/download/matters/{MatterId}/documents/{Uri.EscapeDataString(doc.Id)}";
        Navigation.NavigateTo(downloadUrl, forceLoad: true);
    }

    // --- File/Document Helpers ---

    private static string? GetOfficeProtocol(string fileName)
    {
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        return ext switch
        {
            ".doc" or ".docx" or ".docm" or ".dotx" or ".rtf" => "ms-word",
            ".xls" or ".xlsx" or ".xlsm" or ".xltx" or ".csv" => "ms-excel",
            ".ppt" or ".pptx" or ".pptm" or ".potx" => "ms-powerpoint",
            _ => null
        };
    }

    private static string GetFileIcon(string? mimeType) => mimeType switch
    {
        not null when mimeType.StartsWith("image/") => "bi-file-image",
        not null when mimeType.StartsWith("video/") => "bi-file-play",
        not null when mimeType.StartsWith("audio/") => "bi-file-music",
        "application/pdf" => "bi-file-pdf",
        not null when mimeType.Contains("spreadsheet") || mimeType.Contains("excel") => "bi-file-earmark-spreadsheet",
        not null when mimeType.Contains("document") || mimeType.Contains("word") => "bi-file-earmark-word",
        not null when mimeType.Contains("presentation") || mimeType.Contains("powerpoint") => "bi-file-earmark-slides",
        not null when mimeType.Contains("zip") || mimeType.Contains("archive") || mimeType.Contains("compressed") => "bi-file-zip",
        not null when mimeType.StartsWith("text/") => "bi-file-text",
        _ => "bi-file-earmark"
    };

    private const long KB = 1024;
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * 1024 * 1024;

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= GB) return $"{bytes / (double)GB:F2} GB";
        if (bytes >= MB) return $"{bytes / (double)MB:F1} MB";
        if (bytes >= KB) return $"{bytes / (double)KB:F1} KB";
        return $"{bytes} B";
    }
}
```

## MatterExpensesTab.razor

```razor
@inject IApiClient ApiClient
@inject NavigationManager Navigation
@inject IJSRuntime JS
@inject ILogger<MatterExpensesTab> Logger

<div class="mt-3">
    <!-- Summary Cards -->
    @if (ExpenseItems.Count > 0)
    {
        <AppCard CssClass="mb-3">
            <ChildContent>
                <div class="d-flex align-items-center gap-4 flex-wrap">
                    <div>
                        <div class="text-muted small">Total Expenses</div>
                        <div class="fs-4 fw-bold">@ExpenseItems.Count</div>
                    </div>
                    <div>
                        <div class="text-muted small">Total Amount</div>
                        <div class="fs-4 fw-bold">@ExpenseItems.Sum(e => e.Amount).ToString("C")</div>
                    </div>
                    <div>
                        <div class="text-muted small">Reimbursable</div>
                        <div class="fs-4 fw-bold text-success">@ExpenseItems.Where(e => e.IsReimbursable).Sum(e => e.Amount).ToString("C")</div>
                    </div>
                    <div>
                        <div class="text-muted small">Unbilled</div>
                        <div class="fs-4 fw-bold text-warning">@ExpenseItems.Where(e => !e.IsBilled).Sum(e => e.Amount).ToString("C")</div>
                    </div>
                </div>
            </ChildContent>
        </AppCard>
    }

    <div class="d-flex justify-content-between align-items-center mb-3">
        <h5 class="mb-0">Expenses</h5>
        <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
            <i class="bi bi-plus-circle me-1"></i> Add Expense
        </button>
    </div>

    @if (_errorMessage != null)
    {
        <div class="alert alert-danger app-alert-inline" role="alert">
            <i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage
            <button class="btn-close float-end" @onclick="() => _errorMessage = null"></button>
        </div>
    }

    @if (ExpenseItems.Count == 0)
    {
        <AppEmptyState Icon="cash-coin" Title="No Expenses" Message="No expenses have been added to this matter yet.">
            <Actions>
                <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
                    <i class="bi bi-plus-circle me-1"></i> Add Expense
                </button>
            </Actions>
        </AppEmptyState>
    }
    else
    {
        <div class="table-responsive">
            <table class="table table-hover align-middle">
                <thead>
                    <tr>
                        <th>Date</th>
                        <th>Category</th>
                        <th>Description</th>
                        <th class="text-end">Amount</th>
                        <th>Paid By</th>
                        <th>Vendor</th>
                        <th class="text-center">Reimb.</th>
                        <th class="text-center">Billed</th>
                        <th>Docs</th>
                        <th class="text-end">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var expense in ExpenseItems.OrderByDescending(e => e.DateIncurred ?? e.CreatedOnUtc))
                    {
                        <tr style="cursor: pointer;" @onclick="() => ToggleExpenseExpand(expense.Id)">
                            <td>@(expense.DateIncurred?.ToLocalTime().ToString("MM/dd/yyyy") ?? "N/A")</td>
                            <td>@expense.Category</td>
                            <td>@expense.Description</td>
                            <td class="text-end fw-semibold">@expense.Amount.ToString("C")</td>
                            <td>@(expense.PaidBy ?? "—")</td>
                            <td>@(expense.Vendor ?? "—")</td>
                            <td class="text-center">
                                @if (expense.IsReimbursable)
                                {
                                    <i class="bi bi-check-circle-fill text-success"></i>
                                }
                                else
                                {
                                    <span class="text-muted">—</span>
                                }
                            </td>
                            <td class="text-center">
                                @if (expense.IsBilled)
                                {
                                    <i class="bi bi-check-circle-fill text-primary"></i>
                                }
                                else
                                {
                                    <span class="text-muted">—</span>
                                }
                            </td>
                            <td>
                                @if (expense.DocumentCount > 0)
                                {
                                    <span class="badge bg-secondary"><i class="bi bi-paperclip me-1"></i>@expense.DocumentCount</span>
                                }
                                else
                                {
                                    <span class="text-muted">—</span>
                                }
                            </td>
                            <td class="text-end" @onclick:stopPropagation>
                                <div class="btn-group btn-group-sm">
                                    <button class="btn btn-outline-secondary" @onclick="() => OpenEditModal(expense)" title="Edit">
                                        <i class="bi bi-pencil"></i>
                                    </button>
                                    <label class="btn btn-outline-secondary mb-0" for="@($"expUpload_{expense.Id}")" title="Attach File">
                                        <i class="bi bi-paperclip"></i>
                                    </label>
                                    <InputFile OnChange="(e) => HandleDocumentUpload(e, expense.Id)" class="d-none" id="@($"expUpload_{expense.Id}")" multiple />
                                    <button class="btn btn-outline-danger" @onclick="() => DeleteExpenseAsync(expense)" title="Delete">
                                        <i class="bi bi-trash"></i>
                                    </button>
                                </div>
                            </td>
                        </tr>

                        @if (_expandedExpenseId == expense.Id)
                        {
                            <tr>
                                <td colspan="10" class="bg-light border-0 px-4 py-3">
                                    @if (_isLoadingDocs)
                                    {
                                        <div class="d-flex align-items-center gap-2">
                                            <span class="spinner-border spinner-border-sm text-primary"></span>
                                            <span class="text-muted small">Loading documents...</span>
                                        </div>
                                    }
                                    else if (_expandedDocuments.Count == 0)
                                    {
                                        <div class="text-muted small">
                                            <i class="bi bi-folder2-open me-1"></i>No documents attached.
                                            <label class="btn btn-sm btn-outline-secondary ms-2 mb-0" for="@($"expUploadExpand_{expense.Id}")">
                                                <i class="bi bi-cloud-upload me-1"></i> Upload
                                            </label>
                                            <InputFile OnChange="(e) => HandleDocumentUpload(e, expense.Id)" class="d-none" id="@($"expUploadExpand_{expense.Id}")" multiple />
                                        </div>
                                    }
                                    else
                                    {
                                        <div class="d-flex justify-content-between align-items-center mb-2">
                                            <span class="fw-semibold small">Attached Documents</span>
                                            <label class="btn btn-sm btn-outline-secondary mb-0" for="@($"expUploadExpand_{expense.Id}")">
                                                <i class="bi bi-cloud-upload me-1"></i> Upload
                                            </label>
                                            <InputFile OnChange="(e) => HandleDocumentUpload(e, expense.Id)" class="d-none" id="@($"expUploadExpand_{expense.Id}")" multiple />
                                        </div>
                                        <ul class="list-group list-group-flush">
                                            @foreach (var doc in _expandedDocuments)
                                            {
                                                <li class="list-group-item bg-transparent d-flex justify-content-between align-items-center px-0 py-1"
                                                    style="cursor: pointer;" @onclick="() => OpenDocumentAsync(doc)">
                                                    <span class="text-primary small">
                                                        <i class="bi @GetFileIcon(doc.MimeType) me-1"></i>@doc.Name
                                                    </span>
                                                    <span class="text-muted small">@FormatFileSize(doc.Size)</span>
                                                </li>
                                            }
                                        </ul>
                                    }

                                    @* Show additional details *@
                                    <div class="mt-2 small text-muted">
                                        @if (!string.IsNullOrEmpty(expense.CheckNumber))
                                        {
                                            <span class="me-3"><strong>Check #:</strong> @expense.CheckNumber</span>
                                        }
                                        @if (!string.IsNullOrEmpty(expense.Notes))
                                        {
                                            <div class="mt-1"><strong>Notes:</strong> @expense.Notes</div>
                                        }
                                    </div>
                                </td>
                            </tr>
                        }
                    }
                </tbody>
            </table>
        </div>
    }
</div>

<!-- Add/Edit Expense Modal -->
<AppModal Title="@(_isEditing ? "Edit Expense" : "Add Expense")" @bind-IsVisible="_showModal" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
        <div class="row g-3">
            <div class="col-md-6">
                <label for="expCategory" class="form-label">Category <span class="text-danger">*</span></label>
                <select class="form-select" id="expCategory" @bind="_category">
                    <option value="">Select category...</option>
                    <option value="Filing Fee">Filing Fee</option>
                    <option value="Service Fee">Service Fee</option>
                    <option value="Expert Witness">Expert Witness</option>
                    <option value="Court Reporter">Court Reporter</option>
                    <option value="Medical Records">Medical Records</option>
                    <option value="Travel">Travel</option>
                    <option value="Postage">Postage</option>
                    <option value="Copying">Copying</option>
                    <option value="Deposition">Deposition</option>
                    <option value="Investigation">Investigation</option>
                    <option value="Other">Other</option>
                </select>
            </div>
            <div class="col-md-6">
                <label for="expDate" class="form-label">Date Incurred</label>
                <input type="date" class="form-control" id="expDate" value="@_dateIncurredText" @onchange="@((ChangeEventArgs e) => _dateIncurredText = e.Value?.ToString())" />
            </div>
            <div class="col-12">
                <label for="expDescription" class="form-label">Description <span class="text-danger">*</span></label>
                <input type="text" class="form-control" id="expDescription" @bind="_description" required maxlength="500" />
            </div>
            <div class="col-md-6">
                <label for="expAmount" class="form-label">Amount <span class="text-danger">*</span></label>
                <div class="input-group">
                    <span class="input-group-text">$</span>
                    <input type="number" class="form-control" id="expAmount" @bind="_amount" step="0.01" min="0" />
                </div>
            </div>
            <div class="col-md-6">
                <label for="expPaidBy" class="form-label">Paid By</label>
                <select class="form-select" id="expPaidBy" @bind="_paidBy">
                    <option value="">Select...</option>
                    <option value="Firm">Firm</option>
                    <option value="Client">Client</option>
                    <option value="Insurance">Insurance</option>
                    <option value="Other">Other</option>
                </select>
            </div>
            <div class="col-md-6">
                <label for="expVendor" class="form-label">Vendor</label>
                <input type="text" class="form-control" id="expVendor" @bind="_vendor" maxlength="500" />
            </div>
            <div class="col-md-6">
                <label for="expCheckNumber" class="form-label">Check / Reference #</label>
                <input type="text" class="form-control" id="expCheckNumber" @bind="_checkNumber" maxlength="100" />
            </div>
            <div class="col-md-6">
                <div class="form-check mt-4">
                    <input class="form-check-input" type="checkbox" id="expReimbursable" @bind="_isReimbursable" />
                    <label class="form-check-label" for="expReimbursable">Reimbursable</label>
                </div>
            </div>
            <div class="col-md-6">
                <div class="form-check mt-4">
                    <input class="form-check-input" type="checkbox" id="expBilled" @bind="_isBilled" />
                    <label class="form-check-label" for="expBilled">Billed</label>
                </div>
            </div>
            <div class="col-12">
                <label for="expNotes" class="form-label">Notes</label>
                <textarea class="form-control" id="expNotes" @bind="_notes" rows="2"></textarea>
            </div>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="SaveExpenseAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>@(_isEditing ? "Save Changes" : "Add Expense")</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter, EditorRequired]
    public Guid MatterId { get; set; }

    [Parameter, EditorRequired]
    public IReadOnlyList<ExpenseDto> ExpenseItems { get; set; } = [];

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    private bool _showModal;
    private bool _isEditing;
    private bool _isSaving;
    private string? _errorMessage;
    private Guid? _editingId;

    // Document expansion state
    private Guid? _expandedExpenseId;
    private List<DocumentItemDto> _expandedDocuments = [];
    private bool _isLoadingDocs;

    // Form fields
    private string _category = string.Empty;
    private string _description = string.Empty;
    private decimal _amount;
    private string? _dateIncurredText;
    private string? _paidBy;
    private bool _isReimbursable;
    private bool _isBilled;
    private string? _vendor;
    private string? _checkNumber;
    private string? _notes;

    private void OpenAddModal()
    {
        _isEditing = false;
        _editingId = null;
        _category = string.Empty;
        _description = string.Empty;
        _amount = 0;
        _dateIncurredText = null;
        _paidBy = null;
        _isReimbursable = false;
        _isBilled = false;
        _vendor = null;
        _checkNumber = null;
        _notes = null;
        _errorMessage = null;
        _showModal = true;
    }

    private void OpenEditModal(ExpenseDto expense)
    {
        _isEditing = true;
        _editingId = expense.Id;
        _category = expense.Category;
        _description = expense.Description;
        _amount = expense.Amount;
        _dateIncurredText = expense.DateIncurred?.ToLocalTime().ToString("yyyy-MM-dd");
        _paidBy = expense.PaidBy;
        _isReimbursable = expense.IsReimbursable;
        _isBilled = expense.IsBilled;
        _vendor = expense.Vendor;
        _checkNumber = expense.CheckNumber;
        _notes = expense.Notes;
        _errorMessage = null;
        _showModal = true;
    }

    private async Task SaveExpenseAsync()
    {
        if (string.IsNullOrWhiteSpace(_category)) { _errorMessage = "Category is required."; return; }
        if (string.IsNullOrWhiteSpace(_description)) { _errorMessage = "Description is required."; return; }
        if (_amount < 0) { _errorMessage = "Amount cannot be negative."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();

            DateTime? ParseDate(string? text) =>
                !string.IsNullOrWhiteSpace(text) && DateTime.TryParse(text, out var d) ? d.ToUniversalTime() : null;

            if (_isEditing && _editingId.HasValue)
            {
                var cmd = new UpdateExpenseCommand
                {
                    Category = _category, Description = _description, Amount = _amount,
                    DateIncurred = ParseDate(_dateIncurredText), PaidBy = _paidBy,
                    IsReimbursable = _isReimbursable, IsBilled = _isBilled,
                    Vendor = _vendor, CheckNumber = _checkNumber, Notes = _notes
                };
                await ApiClient.UpdateExpenseAsync(MatterId, _editingId.Value, cmd);
            }
            else
            {
                var cmd = new CreateExpenseCommand
                {
                    Category = _category, Description = _description, Amount = _amount,
                    DateIncurred = ParseDate(_dateIncurredText), PaidBy = _paidBy,
                    IsReimbursable = _isReimbursable, IsBilled = _isBilled,
                    Vendor = _vendor, CheckNumber = _checkNumber, Notes = _notes
                };
                await ApiClient.CreateExpenseAsync(MatterId, cmd);
            }
            _showModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save expense for matter {MatterId}", MatterId);
            _errorMessage = $"Failed to save expense: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task DeleteExpenseAsync(ExpenseDto expense)
    {
        try
        {
            await ApiClient.DeleteExpenseAsync(MatterId, expense.Id);
            if (_expandedExpenseId == expense.Id)
            {
                _expandedExpenseId = null;
                _expandedDocuments = [];
            }
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to delete expense {ExpenseId}", expense.Id); }
    }

    // --- Expand / Documents ---

    private async Task ToggleExpenseExpand(Guid expenseId)
    {
        if (_expandedExpenseId == expenseId)
        {
            _expandedExpenseId = null;
            _expandedDocuments = [];
            return;
        }

        _expandedExpenseId = expenseId;
        _expandedDocuments = [];
        await LoadExpenseDocumentsAsync(expenseId);
    }

    private async Task LoadExpenseDocumentsAsync(Guid expenseId)
    {
        try
        {
            _isLoadingDocs = true;
            StateHasChanged();
            _expandedDocuments = (await ApiClient.GetExpenseDocumentsAsync(MatterId, expenseId)).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load documents for expense {ExpenseId}", expenseId);
            _expandedDocuments = [];
        }
        finally
        {
            _isLoadingDocs = false;
            StateHasChanged();
        }
    }

    private async Task HandleDocumentUpload(InputFileChangeEventArgs e, Guid expenseId)
    {
        _errorMessage = null;

        // Buffer all files into memory BEFORE re-rendering to avoid _blazorFilesById errors
        var bufferedFiles = new List<(string Name, MemoryStream Data)>();
        foreach (var file in e.GetMultipleFiles(10))
        {
            try
            {
                var ms = new MemoryStream();
                await using var browserStream = file.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024);
                await browserStream.CopyToAsync(ms);
                ms.Position = 0;
                bufferedFiles.Add((file.Name, ms));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to buffer file {FileName}", file.Name);
            }
        }

        if (bufferedFiles.Count == 0)
            return;

        StateHasChanged();

        var uploadErrors = new List<string>();
        foreach (var (name, data) in bufferedFiles)
        {
            try
            {
                await ApiClient.UploadExpenseDocumentAsync(MatterId, expenseId, name, data);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to upload file {FileName} to expense {ExpenseId}", name, expenseId);
                uploadErrors.Add(name);
            }
            finally
            {
                await data.DisposeAsync();
            }
        }

        if (uploadErrors.Count > 0)
            _errorMessage = $"Failed to upload: {string.Join(", ", uploadErrors)}";

        await OnRefresh.InvokeAsync();

        if (_expandedExpenseId == expenseId)
            await LoadExpenseDocumentsAsync(expenseId);
    }

    private async Task OpenDocumentAsync(DocumentItemDto doc)
    {
        var isOfficeFile = GetOfficeProtocol(doc.Name) != null;

        if (isOfficeFile && !string.IsNullOrEmpty(doc.WebUrl))
        {
            await JS.InvokeVoidAsync("open", doc.WebUrl, "_blank");
            return;
        }

        try
        {
            var preview = await ApiClient.GetDocumentPreviewAsync(MatterId, doc.Id);
            var url = preview.ViewUrl ?? preview.EditUrl;
            if (!string.IsNullOrEmpty(url))
            {
                await JS.InvokeVoidAsync("open", url, "_blank");
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get preview URL for document {DocId}", doc.Id);
        }

        var downloadUrl = $"/download/matters/{MatterId}/documents/{Uri.EscapeDataString(doc.Id)}";
        Navigation.NavigateTo(downloadUrl, forceLoad: true);
    }

    // --- File/Document Helpers ---

    private static string? GetOfficeProtocol(string fileName)
    {
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        return ext switch
        {
            ".doc" or ".docx" or ".docm" or ".dotx" or ".rtf" => "ms-word",
            ".xls" or ".xlsx" or ".xlsm" or ".xltx" or ".csv" => "ms-excel",
            ".ppt" or ".pptx" or ".pptm" or ".potx" => "ms-powerpoint",
            _ => null
        };
    }

    private static string GetFileIcon(string? mimeType) => mimeType switch
    {
        not null when mimeType.StartsWith("image/") => "bi-file-image",
        not null when mimeType.StartsWith("video/") => "bi-file-play",
        not null when mimeType.StartsWith("audio/") => "bi-file-music",
        "application/pdf" => "bi-file-pdf",
        not null when mimeType.Contains("spreadsheet") || mimeType.Contains("excel") => "bi-file-earmark-spreadsheet",
        not null when mimeType.Contains("document") || mimeType.Contains("word") => "bi-file-earmark-word",
        not null when mimeType.Contains("presentation") || mimeType.Contains("powerpoint") => "bi-file-earmark-slides",
        not null when mimeType.Contains("zip") || mimeType.Contains("archive") || mimeType.Contains("compressed") => "bi-file-zip",
        not null when mimeType.StartsWith("text/") => "bi-file-text",
        _ => "bi-file-earmark"
    };

    private const long KB = 1024;
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * 1024 * 1024;

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= GB) return $"{bytes / (double)GB:F2} GB";
        if (bytes >= MB) return $"{bytes / (double)MB:F1} MB";
        if (bytes >= KB) return $"{bytes / (double)KB:F1} KB";
        return $"{bytes} B";
    }
}
```

## MatterDamagesTab.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<MatterDamagesTab> Logger

<div class="mt-3">
    <!-- Summary Cards -->
    @if (PISummary != null)
    {
        <div class="row g-3 mb-3">
            <div class="col-md-4">
                <AppCard>
                    <ChildContent>
                        <div class="text-muted small">Total Special Damages</div>
                        <div class="fs-4 fw-bold">@PISummary.TotalSpecialDamages.ToString("C")</div>
                    </ChildContent>
                </AppCard>
            </div>
            <div class="col-md-4">
                <AppCard>
                    <ChildContent>
                        <div class="text-muted small">Total General Damages</div>
                        <div class="fs-4 fw-bold">@PISummary.TotalGeneralDamages.ToString("C")</div>
                    </ChildContent>
                </AppCard>
            </div>
            <div class="col-md-4">
                <AppCard>
                    <ChildContent>
                        <div class="text-muted small">Total Damages</div>
                        <div class="fs-4 fw-bold">@PISummary.TotalDamages.ToString("C")</div>
                    </ChildContent>
                </AppCard>
            </div>
        </div>
    }

    <div class="d-flex justify-content-between align-items-center mb-3">
        <h5 class="mb-0">Damages</h5>
        <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
            <i class="bi bi-plus-circle me-1"></i> Add Damage
        </button>
    </div>

    @if (Damages.Count == 0)
    {
        <AppEmptyState Icon="cash-stack" Title="No Damages" Message="No damage items have been added to this matter yet.">
            <Actions>
                <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
                    <i class="bi bi-plus-circle me-1"></i> Add Damage
                </button>
            </Actions>
        </AppEmptyState>
    }
    else
    {
        <div class="table-responsive">
            <table class="table table-hover align-middle">
                <thead>
                    <tr>
                        <th>Category</th>
                        <th>Description</th>
                        <th class="text-end">Amount</th>
                        <th class="text-end">Claimed Amount</th>
                        <th>Type</th>
                        <th>Speculative</th>
                        <th class="text-end">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var item in Damages)
                    {
                        <tr>
                            <td><AppBadge Status="@GetCategoryClass(item.Category)" Label="@FormatCategory(item.Category)" /></td>
                            <td>@item.Description</td>
                            <td class="text-end">@item.Amount.ToString("C")</td>
                            <td class="text-end">@(item.ClaimedAmount?.ToString("C") ?? "-")</td>
                            <td>
                                @if (item.IsSpecialDamage)
                                {
                                    <AppBadge Status="working" Label="Special" />
                                }
                                else if (item.IsGeneralDamage)
                                {
                                    <AppBadge Status="neutral" Label="General" />
                                }
                            </td>
                            <td>
                                @if (item.IsSpeculative)
                                {
                                    <AppBadge Status="followup" Label="Speculative" />
                                }
                            </td>
                            <td class="text-end">
                                <button class="btn btn-sm btn-outline-secondary me-1" @onclick="() => OpenEditModal(item)" title="Edit">
                                    <i class="bi bi-pencil"></i>
                                </button>
                                <button class="btn btn-sm btn-outline-danger" @onclick="() => DeleteDamageAsync(item)" title="Delete">
                                    <i class="bi bi-trash"></i>
                                </button>
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>
    }
</div>

<!-- Add/Edit Damage Modal -->
<AppModal Title="@(_isEditing ? "Edit Damage" : "Add Damage")" @bind-IsVisible="_showModal" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
        <div class="row g-3">
            <div class="col-md-6">
                <label for="dmgCategory" class="form-label">Category <span class="text-danger">*</span></label>
                <select id="dmgCategory" class="form-select" @bind="_category">
                    @foreach (var cat in Enum.GetValues<DamageCategoryDto>())
                    {
                        <option value="@cat">@FormatCategory(cat)</option>
                    }
                </select>
            </div>
            <div class="col-md-6">
                <div class="form-check mt-4">
                    <input type="checkbox" class="form-check-input" id="dmgSpeculative" @bind="_isSpeculative" />
                    <label class="form-check-label" for="dmgSpeculative">Speculative</label>
                </div>
            </div>
            <div class="col-12">
                <label for="dmgDescription" class="form-label">Description <span class="text-danger">*</span></label>
                <textarea class="form-control" id="dmgDescription" @bind="_description" rows="3" required></textarea>
            </div>
            <div class="col-md-6">
                <label for="dmgAmount" class="form-label">Amount <span class="text-danger">*</span></label>
                <input type="number" class="form-control" id="dmgAmount" @bind="_amount" step="0.01" />
            </div>
            <div class="col-md-6">
                <label for="dmgClaimedAmount" class="form-label">Claimed Amount</label>
                <input type="number" class="form-control" id="dmgClaimedAmount" @bind="_claimedAmount" step="0.01" />
            </div>
            <div class="col-md-6">
                <label for="dmgStartDate" class="form-label">Start Date</label>
                <input type="date" class="form-control" id="dmgStartDate" @bind="_startDate" />
            </div>
            <div class="col-md-6">
                <label for="dmgEndDate" class="form-label">End Date</label>
                <input type="date" class="form-control" id="dmgEndDate" @bind="_endDate" />
            </div>
            <div class="col-12">
                <label for="dmgNotes" class="form-label">Notes</label>
                <textarea class="form-control" id="dmgNotes" @bind="_notes" rows="2"></textarea>
            </div>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="SaveDamageAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>@(_isEditing ? "Save Changes" : "Add Damage")</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter, EditorRequired]
    public Guid MatterId { get; set; }

    [Parameter, EditorRequired]
    public IReadOnlyList<DamageItemDto> Damages { get; set; } = [];

    [Parameter]
    public PIMattertSummaryDto? PISummary { get; set; }

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    private bool _showModal;
    private bool _isEditing;
    private bool _isSaving;
    private string? _errorMessage;
    private Guid? _editingId;

    // Form fields
    private DamageCategoryDto _category = DamageCategoryDto.MedicalExpenses;
    private string _description = string.Empty;
    private decimal _amount;
    private decimal? _claimedAmount;
    private bool _isSpeculative;
    private DateOnly? _startDate;
    private DateOnly? _endDate;
    private string? _notes;

    private void OpenAddModal()
    {
        _isEditing = false;
        _editingId = null;
        _category = DamageCategoryDto.MedicalExpenses;
        _description = string.Empty;
        _amount = 0;
        _claimedAmount = null;
        _isSpeculative = false;
        _startDate = null;
        _endDate = null;
        _notes = null;
        _errorMessage = null;
        _showModal = true;
    }

    private void OpenEditModal(DamageItemDto item)
    {
        _isEditing = true;
        _editingId = item.Id;
        _category = item.Category;
        _description = item.Description;
        _amount = item.Amount;
        _claimedAmount = item.ClaimedAmount;
        _isSpeculative = item.IsSpeculative;
        _startDate = item.StartDate;
        _endDate = item.EndDate;
        _notes = item.Notes;
        _errorMessage = null;
        _showModal = true;
    }

    private async Task SaveDamageAsync()
    {
        if (string.IsNullOrWhiteSpace(_description)) { _errorMessage = "Description is required."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            if (_isEditing && _editingId.HasValue)
            {
                var cmd = new UpdateDamageItemCommand
                {
                    Category = _category, Description = _description,
                    Amount = _amount, ClaimedAmount = _claimedAmount,
                    IsSpeculative = _isSpeculative, StartDate = _startDate,
                    EndDate = _endDate, Notes = _notes
                };
                await ApiClient.UpdateDamageAsync(MatterId, _editingId.Value, cmd);
            }
            else
            {
                var cmd = new CreateDamageItemCommand
                {
                    Category = _category, Description = _description,
                    Amount = _amount, ClaimedAmount = _claimedAmount,
                    IsSpeculative = _isSpeculative, StartDate = _startDate,
                    EndDate = _endDate, Notes = _notes
                };
                await ApiClient.CreateDamageAsync(MatterId, cmd);
            }
            _showModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save damage for matter {MatterId}", MatterId);
            _errorMessage = $"Failed to save damage: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task DeleteDamageAsync(DamageItemDto item)
    {
        try
        {
            await ApiClient.DeleteDamageAsync(MatterId, item.Id);
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to delete damage {DamageId}", item.Id); }
    }

    private static string GetCategoryClass(DamageCategoryDto category) => category switch
    {
        DamageCategoryDto.MedicalExpenses or DamageCategoryDto.FutureMedical => "followup",
        DamageCategoryDto.LostWages or DamageCategoryDto.FutureLostWages => "working",
        DamageCategoryDto.PainAndSuffering or DamageCategoryDto.EmotionalDistress => "neutral",
        DamageCategoryDto.PropertyDamage => "stuck",
        DamageCategoryDto.Other => "cold",
        _ => "neutral"
    };

    private static string FormatCategory(DamageCategoryDto category) => category switch
    {
        DamageCategoryDto.MedicalExpenses => "Medical Expenses",
        DamageCategoryDto.LostWages => "Lost Wages",
        DamageCategoryDto.FutureMedical => "Future Medical",
        DamageCategoryDto.FutureLostWages => "Future Lost Wages",
        DamageCategoryDto.PainAndSuffering => "Pain & Suffering",
        DamageCategoryDto.PropertyDamage => "Property Damage",
        DamageCategoryDto.LossOfConsortium => "Loss of Consortium",
        DamageCategoryDto.EmotionalDistress => "Emotional Distress",
        DamageCategoryDto.Disfigurement => "Disfigurement",
        DamageCategoryDto.Other => "Other",
        _ => category.ToString()
    };
}
```

## MatterLiensTab.razor

```razor
@inject IApiClient ApiClient
@inject NavigationManager Navigation
@inject IJSRuntime JS
@inject ILogger<MatterLiensTab> Logger

<div class="mt-3">
    <!-- Summary Card -->
    @if (PISummary != null)
    {
        <AppCard CssClass="mb-3">
            <ChildContent>
                <div class="d-flex align-items-center gap-4">
                    <div>
                        <div class="text-muted small">Total Lien Amount</div>
                        <div class="fs-4 fw-bold">@PISummary.TotalLienAmount.ToString("C")</div>
                    </div>
                </div>
            </ChildContent>
        </AppCard>
    }

    <div class="d-flex justify-content-between align-items-center mb-3">
        <h5 class="mb-0">Liens</h5>
        <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
            <i class="bi bi-plus-circle me-1"></i> Add Lien
        </button>
    </div>

    @if (_errorMessage != null)
    {
        <div class="alert alert-danger app-alert-inline" role="alert">
            <i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage
            <button class="btn-close float-end" @onclick="() => _errorMessage = null"></button>
        </div>
    }

    @if (Liens.Count == 0)
    {
        <AppEmptyState Icon="link-45deg" Title="No Liens" Message="No liens have been added to this matter yet.">
            <Actions>
                <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
                    <i class="bi bi-plus-circle me-1"></i> Add Lien
                </button>
            </Actions>
        </AppEmptyState>
    }
    else
    {
        <div class="table-responsive">
            <table class="table table-hover align-middle">
                <thead>
                    <tr>
                        <th>Lienholder</th>
                        <th>Subject</th>
                        <th>Status</th>
                        <th class="text-end">Original Amount</th>
                        <th class="text-end">Negotiated Amount</th>
                        <th class="text-end">Effective Amount</th>
                        <th>Docs</th>
                        <th class="text-end">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var lien in Liens)
                    {
                        <tr style="cursor: pointer;" @onclick="() => ToggleLienExpand(lien.Id)">
                            <td>@lien.LienholderName</td>
                            <td>@lien.Subject</td>
                            <td><AppBadge Status="@GetLienStatusClass(lien.Status)" Label="@lien.Status.ToString()" /></td>
                            <td class="text-end">@lien.OriginalAmount.ToString("C")</td>
                            <td class="text-end">@(lien.NegotiatedAmount?.ToString("C") ?? "N/A")</td>
                            <td class="text-end fw-semibold">@lien.EffectiveAmount.ToString("C")</td>
                            <td>
                                @if (lien.DocumentCount > 0)
                                {
                                    <span class="badge bg-secondary"><i class="bi bi-paperclip me-1"></i>@lien.DocumentCount</span>
                                }
                                else
                                {
                                    <span class="text-muted">—</span>
                                }
                            </td>
                            <td class="text-end" @onclick:stopPropagation>
                                <div class="btn-group btn-group-sm">
                                    <button class="btn btn-outline-secondary" @onclick="() => OpenEditModal(lien)" title="Edit">
                                        <i class="bi bi-pencil"></i>
                                    </button>
                                    <label class="btn btn-outline-secondary mb-0" for="@($"lienUpload_{lien.Id}")" title="Attach File">
                                        <i class="bi bi-paperclip"></i>
                                    </label>
                                    <InputFile OnChange="(e) => HandleDocumentUpload(e, lien.Id)" class="d-none" id="@($"lienUpload_{lien.Id}")" multiple />
                                    <button class="btn btn-outline-danger" @onclick="() => DeleteLienAsync(lien)" title="Delete">
                                        <i class="bi bi-trash"></i>
                                    </button>
                                </div>
                            </td>
                        </tr>

                        @if (_expandedLienId == lien.Id)
                        {
                            <tr>
                                <td colspan="8" class="bg-light border-0 px-4 py-3">
                                    @if (_isLoadingDocs)
                                    {
                                        <div class="d-flex align-items-center gap-2">
                                            <span class="spinner-border spinner-border-sm text-primary"></span>
                                            <span class="text-muted small">Loading documents...</span>
                                        </div>
                                    }
                                    else if (_expandedDocuments.Count == 0)
                                    {
                                        <div class="text-muted small">
                                            <i class="bi bi-folder2-open me-1"></i>No documents attached.
                                            <label class="btn btn-sm btn-outline-secondary ms-2 mb-0" for="@($"lienUploadExpand_{lien.Id}")">
                                                <i class="bi bi-cloud-upload me-1"></i> Upload
                                            </label>
                                            <InputFile OnChange="(e) => HandleDocumentUpload(e, lien.Id)" class="d-none" id="@($"lienUploadExpand_{lien.Id}")" multiple />
                                        </div>
                                    }
                                    else
                                    {
                                        <div class="d-flex justify-content-between align-items-center mb-2">
                                            <span class="fw-semibold small">Attached Documents</span>
                                            <label class="btn btn-sm btn-outline-secondary mb-0" for="@($"lienUploadExpand_{lien.Id}")">
                                                <i class="bi bi-cloud-upload me-1"></i> Upload
                                            </label>
                                            <InputFile OnChange="(e) => HandleDocumentUpload(e, lien.Id)" class="d-none" id="@($"lienUploadExpand_{lien.Id}")" multiple />
                                        </div>
                                        <ul class="list-group list-group-flush">
                                            @foreach (var doc in _expandedDocuments)
                                            {
                                                <li class="list-group-item bg-transparent d-flex justify-content-between align-items-center px-0 py-1"
                                                    style="cursor: pointer;" @onclick="() => OpenDocumentAsync(doc)">
                                                    <span class="text-primary small">
                                                        <i class="bi @GetFileIcon(doc.MimeType) me-1"></i>@doc.Name
                                                    </span>
                                                    <span class="text-muted small">@FormatFileSize(doc.Size)</span>
                                                </li>
                                            }
                                        </ul>
                                    }
                                </td>
                            </tr>
                        }
                    }
                </tbody>
            </table>
        </div>
    }
</div>

<!-- Add Lien Modal -->
<AppModal Title="Add Lien" @bind-IsVisible="_showAddModal" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
        <div class="row g-3">
            <div class="col-12">
                <ContactAutocomplete Label="Lienholder" Required="true" @bind-SelectedContact="_selectedContact" />
            </div>
            <div class="col-12">
                <label for="addLienSubject" class="form-label">Subject <span class="text-danger">*</span></label>
                <input type="text" class="form-control" id="addLienSubject" @bind="_addCommand.Subject" required maxlength="200" />
            </div>
            <div class="col-md-6">
                <label for="addLienAmount" class="form-label">Original Amount <span class="text-danger">*</span></label>
                <input type="number" class="form-control" id="addLienAmount" @bind="_addCommand.OriginalAmount" step="0.01" />
            </div>
            <div class="col-md-6">
                <label for="addLienDueDate" class="form-label">Due Date</label>
                <input type="date" class="form-control" id="addLienDueDate" value="@_addDueDateText" @onchange="@((ChangeEventArgs e) => _addDueDateText = e.Value?.ToString())" />
            </div>
            <div class="col-md-6">
                <label for="addLienRefNumber" class="form-label">Reference Number</label>
                <input type="text" class="form-control" id="addLienRefNumber" @bind="_addCommand.ReferenceNumber" maxlength="100" />
            </div>
            <div class="col-12">
                <label for="addLienNotes" class="form-label">Notes</label>
                <textarea class="form-control" id="addLienNotes" @bind="_addCommand.Notes" rows="2"></textarea>
            </div>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showAddModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="AddLienAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Adding...</span> }
            else { <i class="bi bi-plus-circle me-1"></i> <span>Add Lien</span> }
        </button>
    </Footer>
</AppModal>

<!-- Edit Lien Modal -->
<AppModal Title="Edit Lien" @bind-IsVisible="_showEditModal" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
        <div class="row g-3">
            <div class="col-12">
                <ContactAutocomplete Label="Lienholder" Required="true"
                                     @bind-SelectedContact="_editSelectedContact"
                                     InitialContactId="@_editInitialContactId" />
            </div>
            <div class="col-12">
                <label for="editLienSubject" class="form-label">Subject <span class="text-danger">*</span></label>
                <input type="text" class="form-control" id="editLienSubject" @bind="_editCommand.Subject" required maxlength="200" />
            </div>
            <div class="col-md-6">
                <label for="editLienStatus" class="form-label">Status <span class="text-danger">*</span></label>
                <select id="editLienStatus" class="form-select" @bind="_editStatusValue">
                    @foreach (var status in Enum.GetValues<LienStatusDto>())
                    {
                        <option value="@status">@status</option>
                    }
                </select>
            </div>
            <div class="col-md-6">
                <label for="editLienOriginal" class="form-label">Original Amount <span class="text-danger">*</span></label>
                <input type="number" class="form-control" id="editLienOriginal" @bind="_editCommand.OriginalAmount" step="0.01" />
            </div>
            <div class="col-md-6">
                <label for="editLienNegotiated" class="form-label">Negotiated Amount</label>
                <input type="number" class="form-control" id="editLienNegotiated" @bind="_editCommand.NegotiatedAmount" step="0.01" />
            </div>
            <div class="col-md-6">
                <label for="editLienPaid" class="form-label">Paid Amount</label>
                <input type="number" class="form-control" id="editLienPaid" @bind="_editCommand.PaidAmount" step="0.01" />
            </div>
            <div class="col-md-6">
                <label for="editLienDueDate" class="form-label">Due Date</label>
                <input type="date" class="form-control" id="editLienDueDate" value="@_editDueDateText" @onchange="@((ChangeEventArgs e) => _editDueDateText = e.Value?.ToString())" />
            </div>
            <div class="col-md-6">
                <label for="editLienRefNumber" class="form-label">Reference Number</label>
                <input type="text" class="form-control" id="editLienRefNumber" @bind="_editCommand.ReferenceNumber" maxlength="100" />
            </div>
            <div class="col-12">
                <label for="editLienNotes" class="form-label">Notes</label>
                <textarea class="form-control" id="editLienNotes" @bind="_editCommand.Notes" rows="2"></textarea>
            </div>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showEditModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="UpdateLienAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>Save Changes</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter, EditorRequired]
    public Guid MatterId { get; set; }

    [Parameter, EditorRequired]
    public IReadOnlyList<LienDto> Liens { get; set; } = [];

    [Parameter]
    public PIMattertSummaryDto? PISummary { get; set; }

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    private bool _showAddModal;
    private bool _showEditModal;
    private bool _isSaving;
    private string? _errorMessage;
    private Guid? _editingId;

    // Document expansion state
    private Guid? _expandedLienId;
    private List<DocumentItemDto> _expandedDocuments = [];
    private bool _isLoadingDocs;

    // Add form
    private CreateLienCommand _addCommand = new();
    private ContactSummaryDto? _selectedContact;
    private string? _addDueDateText;

    // Edit form
    private UpdateLienCommand _editCommand = new();
    private ContactSummaryDto? _editSelectedContact;
    private Guid? _editInitialContactId;
    private LienStatusDto _editStatusValue;
    private string? _editDueDateText;

    private void OpenAddModal()
    {
        _addCommand = new();
        _selectedContact = null;
        _addDueDateText = null;
        _errorMessage = null;
        _showAddModal = true;
    }

    private void OpenEditModal(LienDto lien)
    {
        _editingId = lien.Id;
        _editSelectedContact = null;
        _editInitialContactId = lien.LienholderContactId;
        _editStatusValue = lien.Status;
        _editCommand = new UpdateLienCommand
        {
            LienholderContactId = lien.LienholderContactId,
            Subject = lien.Subject,
            Status = lien.Status,
            OriginalAmount = lien.OriginalAmount,
            NegotiatedAmount = lien.NegotiatedAmount,
            PaidAmount = lien.PaidAmount,
            ReferenceNumber = lien.ReferenceNumber,
            Notes = lien.Notes
        };
        _editDueDateText = lien.DueDate?.ToString("yyyy-MM-dd");
        _errorMessage = null;
        _showEditModal = true;
    }

    private async Task AddLienAsync()
    {
        if (_selectedContact == null) { _errorMessage = "Please select a lienholder."; return; }
        if (string.IsNullOrWhiteSpace(_addCommand.Subject)) { _errorMessage = "Subject is required."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            _addCommand.LienholderContactId = _selectedContact.Id;
            if (!string.IsNullOrWhiteSpace(_addDueDateText) && DateTime.TryParse(_addDueDateText, out var dueDate))
                _addCommand.DueDate = dueDate;
            await ApiClient.CreateLienAsync(MatterId, _addCommand);
            _showAddModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to add lien to matter {MatterId}", MatterId);
            _errorMessage = $"Failed to add lien: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task UpdateLienAsync()
    {
        if (_editingId == null) return;
        if (string.IsNullOrWhiteSpace(_editCommand.Subject)) { _errorMessage = "Subject is required."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            _editCommand.LienholderContactId = _editSelectedContact?.Id ?? _editInitialContactId ?? _editCommand.LienholderContactId;
            _editCommand.Status = _editStatusValue;
            if (!string.IsNullOrWhiteSpace(_editDueDateText) && DateTime.TryParse(_editDueDateText, out var dueDate))
                _editCommand.DueDate = dueDate;
            else
                _editCommand.DueDate = null;
            await ApiClient.UpdateLienAsync(MatterId, _editingId.Value, _editCommand);
            _showEditModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update lien {LienId}", _editingId);
            _errorMessage = $"Failed to update lien: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task DeleteLienAsync(LienDto lien)
    {
        try
        {
            await ApiClient.DeleteLienAsync(MatterId, lien.Id);
            if (_expandedLienId == lien.Id)
            {
                _expandedLienId = null;
                _expandedDocuments = [];
            }
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to delete lien {LienId}", lien.Id); }
    }

    // --- Expand / Documents ---

    private async Task ToggleLienExpand(Guid lienId)
    {
        if (_expandedLienId == lienId)
        {
            _expandedLienId = null;
            _expandedDocuments = [];
            return;
        }

        _expandedLienId = lienId;
        _expandedDocuments = [];
        await LoadLienDocumentsAsync(lienId);
    }

    private async Task LoadLienDocumentsAsync(Guid lienId)
    {
        try
        {
            _isLoadingDocs = true;
            StateHasChanged();
            _expandedDocuments = (await ApiClient.GetLienDocumentsAsync(MatterId, lienId)).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load documents for lien {LienId}", lienId);
            _expandedDocuments = [];
        }
        finally
        {
            _isLoadingDocs = false;
            StateHasChanged();
        }
    }

    private async Task HandleDocumentUpload(InputFileChangeEventArgs e, Guid lienId)
    {
        _errorMessage = null;

        // Buffer all files into memory BEFORE re-rendering to avoid _blazorFilesById errors
        var bufferedFiles = new List<(string Name, MemoryStream Data)>();
        foreach (var file in e.GetMultipleFiles(10))
        {
            try
            {
                var ms = new MemoryStream();
                await using var browserStream = file.OpenReadStream(maxAllowedSize: 100 * 1024 * 1024);
                await browserStream.CopyToAsync(ms);
                ms.Position = 0;
                bufferedFiles.Add((file.Name, ms));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to buffer file {FileName}", file.Name);
            }
        }

        if (bufferedFiles.Count == 0)
            return;

        StateHasChanged();

        var uploadErrors = new List<string>();
        foreach (var (name, data) in bufferedFiles)
        {
            try
            {
                await ApiClient.UploadLienDocumentAsync(MatterId, lienId, name, data);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to upload file {FileName} to lien {LienId}", name, lienId);
                uploadErrors.Add(name);
            }
            finally
            {
                await data.DisposeAsync();
            }
        }

        if (uploadErrors.Count > 0)
            _errorMessage = $"Failed to upload: {string.Join(", ", uploadErrors)}";

        await OnRefresh.InvokeAsync();

        if (_expandedLienId == lienId)
            await LoadLienDocumentsAsync(lienId);
    }

    private async Task OpenDocumentAsync(DocumentItemDto doc)
    {
        var isOfficeFile = GetOfficeProtocol(doc.Name) != null;

        if (isOfficeFile && !string.IsNullOrEmpty(doc.WebUrl))
        {
            await JS.InvokeVoidAsync("open", doc.WebUrl, "_blank");
            return;
        }

        try
        {
            var preview = await ApiClient.GetDocumentPreviewAsync(MatterId, doc.Id);
            var url = preview.ViewUrl ?? preview.EditUrl;
            if (!string.IsNullOrEmpty(url))
            {
                await JS.InvokeVoidAsync("open", url, "_blank");
                return;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get preview URL for document {DocId}", doc.Id);
        }

        var downloadUrl = $"/download/matters/{MatterId}/documents/{Uri.EscapeDataString(doc.Id)}";
        Navigation.NavigateTo(downloadUrl, forceLoad: true);
    }

    // --- File/Document Helpers ---

    private static string? GetOfficeProtocol(string fileName)
    {
        var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
        return ext switch
        {
            ".doc" or ".docx" or ".docm" or ".dotx" or ".rtf" => "ms-word",
            ".xls" or ".xlsx" or ".xlsm" or ".xltx" or ".csv" => "ms-excel",
            ".ppt" or ".pptx" or ".pptm" or ".potx" => "ms-powerpoint",
            _ => null
        };
    }

    private static string GetFileIcon(string? mimeType) => mimeType switch
    {
        not null when mimeType.StartsWith("image/") => "bi-file-image",
        not null when mimeType.StartsWith("video/") => "bi-file-play",
        not null when mimeType.StartsWith("audio/") => "bi-file-music",
        "application/pdf" => "bi-file-pdf",
        not null when mimeType.Contains("spreadsheet") || mimeType.Contains("excel") => "bi-file-earmark-spreadsheet",
        not null when mimeType.Contains("document") || mimeType.Contains("word") => "bi-file-earmark-word",
        not null when mimeType.Contains("presentation") || mimeType.Contains("powerpoint") => "bi-file-earmark-slides",
        not null when mimeType.Contains("zip") || mimeType.Contains("archive") || mimeType.Contains("compressed") => "bi-file-zip",
        not null when mimeType.StartsWith("text/") => "bi-file-text",
        _ => "bi-file-earmark"
    };

    private const long KB = 1024;
    private const long MB = 1024 * 1024;
    private const long GB = 1024 * 1024 * 1024;

    private static string FormatFileSize(long bytes)
    {
        if (bytes >= GB) return $"{bytes / (double)GB:F2} GB";
        if (bytes >= MB) return $"{bytes / (double)MB:F1} MB";
        if (bytes >= KB) return $"{bytes / (double)KB:F1} KB";
        return $"{bytes} B";
    }

    private static string GetLienStatusClass(LienStatusDto status) => status switch
    {
        LienStatusDto.Pending => "working",
        LienStatusDto.Negotiating => "followup",
        LienStatusDto.Resolved or LienStatusDto.Paid => "done",
        LienStatusDto.Disputed => "stuck",
        LienStatusDto.Waived => "neutral",
        _ => "neutral"
    };
}
```

## MatterNegotiationsTab.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<MatterNegotiationsTab> Logger

<div class="mt-3">
    <!-- Summary Card -->
    @if (PISummary != null)
    {
        <AppCard CssClass="mb-3">
            <ChildContent>
                <div class="d-flex align-items-center gap-4">
                    <div>
                        <div class="text-muted small">Latest Demand Amount</div>
                        <div class="fs-4 fw-bold">@(PISummary.LatestDemandAmount?.ToString("C") ?? "N/A")</div>
                    </div>
                    <div class="vr"></div>
                    <div>
                        <div class="text-muted small">Latest Offer Amount</div>
                        <div class="fs-4 fw-bold">@(PISummary.LatestOfferAmount?.ToString("C") ?? "N/A")</div>
                    </div>
                </div>
            </ChildContent>
        </AppCard>
    }

    <div class="d-flex justify-content-between align-items-center mb-3">
        <h5 class="mb-0">Negotiations</h5>
        <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
            <i class="bi bi-plus-circle me-1"></i> Add Entry
        </button>
    </div>

    @if (Negotiations.Count == 0)
    {
        <AppEmptyState Icon="currency-exchange" Title="No Negotiations" Message="No negotiation entries have been added to this matter yet.">
            <Actions>
                <button class="btn btn-sm app-btn-primary" @onclick="OpenAddModal">
                    <i class="bi bi-plus-circle me-1"></i> Add Entry
                </button>
            </Actions>
        </AppEmptyState>
    }
    else
    {
        <div class="table-responsive">
            <table class="table table-hover align-middle">
                <thead>
                    <tr>
                        <th>Date</th>
                        <th>Type</th>
                        <th>Subject</th>
                        <th class="text-end">Demand Amount</th>
                        <th class="text-end">Offer Amount</th>
                        <th>Author</th>
                        <th class="text-end">Actions</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach (var entry in Negotiations.OrderByDescending(n => n.EntryDate))
                    {
                        <tr>
                            <td>@entry.EntryDate.ToLocalTime().ToString("MMM dd, yyyy")</td>
                            <td><AppBadge Status="@GetNegotiationTypeClass(entry.EntryType)" Label="@FormatEntryType(entry.EntryType)" /></td>
                            <td>@entry.Subject</td>
                            <td class="text-end">@(entry.DemandAmount?.ToString("C") ?? "-")</td>
                            <td class="text-end">@(entry.OfferAmount?.ToString("C") ?? "-")</td>
                            <td>@(entry.CreatedByUserName ?? "N/A")</td>
                            <td class="text-end">
                                <button class="btn btn-sm btn-outline-secondary me-1" @onclick="() => OpenEditModal(entry)" title="Edit">
                                    <i class="bi bi-pencil"></i>
                                </button>
                                <button class="btn btn-sm btn-outline-danger" @onclick="() => DeleteEntryAsync(entry)" title="Delete">
                                    <i class="bi bi-trash"></i>
                                </button>
                            </td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>
    }
</div>

<!-- Add/Edit Negotiation Entry Modal -->
<AppModal Title="@(_isEditing ? "Edit Negotiation Entry" : "Add Negotiation Entry")" @bind-IsVisible="_showModal" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }
        <div class="row g-3">
            <div class="col-md-6">
                <label for="negDate" class="form-label">Date <span class="text-danger">*</span></label>
                <input type="date" class="form-control" id="negDate" @bind="_entryDate" />
            </div>
            <div class="col-md-6">
                <label for="negType" class="form-label">Type <span class="text-danger">*</span></label>
                <select id="negType" class="form-select" @bind="_entryType">
                    @foreach (var type in Enum.GetValues<NegotiationTypeDto>())
                    {
                        <option value="@type">@FormatEntryType(type)</option>
                    }
                </select>
            </div>
            <div class="col-12">
                <label for="negSubject" class="form-label">Subject <span class="text-danger">*</span></label>
                <input type="text" class="form-control" id="negSubject" @bind="_subject" required maxlength="200" />
            </div>
            <div class="col-md-6">
                <label for="negDemand" class="form-label">Demand Amount</label>
                <input type="number" class="form-control" id="negDemand" @bind="_demandAmount" step="0.01" />
            </div>
            <div class="col-md-6">
                <label for="negOffer" class="form-label">Offer Amount</label>
                <input type="number" class="form-control" id="negOffer" @bind="_offerAmount" step="0.01" />
            </div>
            <div class="col-12">
                <label for="negNotes" class="form-label">Notes</label>
                <textarea class="form-control" id="negNotes" @bind="_notes" rows="3"></textarea>
            </div>
        </div>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="() => _showModal = false" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="SaveEntryAsync" disabled="@_isSaving">
            @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>@(_isEditing ? "Save Changes" : "Add Entry")</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter, EditorRequired]
    public Guid MatterId { get; set; }

    [Parameter, EditorRequired]
    public IReadOnlyList<NegotiationEntryDto> Negotiations { get; set; } = [];

    [Parameter]
    public PIMattertSummaryDto? PISummary { get; set; }

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    private bool _showModal;
    private bool _isEditing;
    private bool _isSaving;
    private string? _errorMessage;
    private Guid? _editingId;

    // Form fields
    private DateTime _entryDate = DateTime.Today;
    private NegotiationTypeDto _entryType = NegotiationTypeDto.Demand;
    private string _subject = string.Empty;
    private decimal? _demandAmount;
    private decimal? _offerAmount;
    private string? _notes;

    private void OpenAddModal()
    {
        _isEditing = false;
        _editingId = null;
        _entryDate = DateTime.Today;
        _entryType = NegotiationTypeDto.Demand;
        _subject = string.Empty;
        _demandAmount = null;
        _offerAmount = null;
        _notes = null;
        _errorMessage = null;
        _showModal = true;
    }

    private void OpenEditModal(NegotiationEntryDto entry)
    {
        _isEditing = true;
        _editingId = entry.Id;
        _entryDate = entry.EntryDate.ToLocalTime();
        _entryType = entry.EntryType;
        _subject = entry.Subject;
        _demandAmount = entry.DemandAmount;
        _offerAmount = entry.OfferAmount;
        _notes = entry.Notes;
        _errorMessage = null;
        _showModal = true;
    }

    private async Task SaveEntryAsync()
    {
        if (string.IsNullOrWhiteSpace(_subject)) { _errorMessage = "Subject is required."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();
            if (_isEditing && _editingId.HasValue)
            {
                var cmd = new UpdateNegotiationEntryCommand
                {
                    EntryDate = _entryDate.ToUniversalTime(), EntryType = _entryType,
                    Subject = _subject, DemandAmount = _demandAmount,
                    OfferAmount = _offerAmount, Notes = _notes
                };
                await ApiClient.UpdateNegotiationAsync(MatterId, _editingId.Value, cmd);
            }
            else
            {
                var cmd = new CreateNegotiationEntryCommand
                {
                    EntryDate = _entryDate.ToUniversalTime(), EntryType = _entryType,
                    Subject = _subject, DemandAmount = _demandAmount,
                    OfferAmount = _offerAmount, Notes = _notes
                };
                await ApiClient.CreateNegotiationAsync(MatterId, cmd);
            }
            _showModal = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save negotiation entry for matter {MatterId}", MatterId);
            _errorMessage = $"Failed to save entry: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task DeleteEntryAsync(NegotiationEntryDto entry)
    {
        try
        {
            await ApiClient.DeleteNegotiationAsync(MatterId, entry.Id);
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex) { Logger.LogError(ex, "Failed to delete negotiation entry {EntryId}", entry.Id); }
    }

    private static string GetNegotiationTypeClass(NegotiationTypeDto type) => type switch
    {
        NegotiationTypeDto.InitialDemand or NegotiationTypeDto.Demand => "stuck",
        NegotiationTypeDto.Offer or NegotiationTypeDto.CounterOffer or NegotiationTypeDto.FinalOffer => "working",
        NegotiationTypeDto.Acceptance => "done",
        NegotiationTypeDto.Rejection => "stuck",
        _ => "neutral"
    };

    private static string FormatEntryType(NegotiationTypeDto type) => type switch
    {
        NegotiationTypeDto.InitialDemand => "Initial Demand",
        NegotiationTypeDto.CounterOffer => "Counter Offer",
        NegotiationTypeDto.FinalOffer => "Final Offer",
        _ => type.ToString()
    };
}
```

## MatterSettlementTab.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<MatterSettlementTab> Logger

<div class="mt-3">
    @if (Settlement == null && !_isEditing)
    {
        <AppEmptyState Icon="bank" Title="No Settlement" Message="No settlement has been recorded for this matter yet.">
            <Actions>
                <button class="btn btn-sm app-btn-primary" @onclick="OpenEditForm">
                    <i class="bi bi-plus-circle me-1"></i> Record Settlement
                </button>
            </Actions>
        </AppEmptyState>
    }
    else if (_isEditing)
    {
        <!-- Edit / Create Form -->
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger"><i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage</div>
        }

        <div class="d-flex justify-content-between align-items-center mb-3">
            <h5 class="mb-0">@(Settlement != null ? "Edit Settlement" : "Record Settlement")</h5>
        </div>

        <div class="row g-3">
            <!-- Settlement Info -->
            <div class="col-12">
                <h6 class="text-muted border-bottom pb-2">Settlement Information</h6>
            </div>
            <div class="col-md-4">
                <label for="settlementDate" class="form-label">Settlement Date <span class="text-danger">*</span></label>
                <input type="date" class="form-control" id="settlementDate" @bind="_settlementDate" />
            </div>
            <div class="col-md-4">
                <label for="grossAmount" class="form-label">Gross Settlement Amount <span class="text-danger">*</span></label>
                <div class="input-group">
                    <span class="input-group-text">$</span>
                    <input type="number" class="form-control" id="grossAmount" @bind="_grossAmount" step="0.01" min="0" />
                </div>
            </div>
            <div class="col-md-4">
                <label for="settlementType" class="form-label">Settlement Type <span class="text-danger">*</span></label>
                <select id="settlementType" class="form-select" @bind="_settlementType">
                    <option value="@SettlementTypeDto.LumpSum">Lump Sum</option>
                    <option value="@SettlementTypeDto.Structured">Structured</option>
                    <option value="@SettlementTypeDto.Partial">Partial</option>
                </select>
            </div>
            <div class="col-md-4">
                <label for="settlementStatus" class="form-label">Status</label>
                <select id="settlementStatus" class="form-select" @bind="_settlementStatus">
                    <option value="@SettlementStatusDto.Draft">Draft</option>
                    <option value="@SettlementStatusDto.Pending">Pending</option>
                    <option value="@SettlementStatusDto.Approved">Approved</option>
                    <option value="@SettlementStatusDto.FundsReceived">Funds Received</option>
                    <option value="@SettlementStatusDto.Disbursed">Disbursed</option>
                    <option value="@SettlementStatusDto.Voided">Voided</option>
                </select>
            </div>

            <!-- Insurance / Paying Party -->
            <div class="col-12 mt-4">
                <h6 class="text-muted border-bottom pb-2">Paying Party / Insurance</h6>
            </div>
            <div class="col-md-6">
                <label for="insuranceClaimNumber" class="form-label">Insurance Claim Number</label>
                <input type="text" class="form-control" id="insuranceClaimNumber" @bind="_insuranceClaimNumber" maxlength="200" />
            </div>
            <div class="col-md-6">
                <label for="insurancePolicyNumber" class="form-label">Insurance Policy Number</label>
                <input type="text" class="form-control" id="insurancePolicyNumber" @bind="_insurancePolicyNumber" maxlength="200" />
            </div>
            <div class="col-md-6">
                <label for="paymentRef" class="form-label">Payment Reference / Check Number</label>
                <input type="text" class="form-control" id="paymentRef" @bind="_paymentReferenceNumber" maxlength="200" />
            </div>

            <!-- Attorney Fees -->
            <div class="col-12 mt-4">
                <h6 class="text-muted border-bottom pb-2">Attorney Fees</h6>
            </div>
            <div class="col-md-4">
                <label for="attorneyFeePct" class="form-label">Attorney Fee % <span class="text-danger">*</span></label>
                <div class="input-group">
                    <input type="number" class="form-control" id="attorneyFeePct" @bind="_attorneyFeePercentage" step="0.01" min="0" max="100" />
                    <span class="input-group-text">%</span>
                </div>
            </div>
            <div class="col-md-4">
                <label for="attorneyFeeOverride" class="form-label">Attorney Fee Override</label>
                <div class="input-group">
                    <span class="input-group-text">$</span>
                    <input type="number" class="form-control" id="attorneyFeeOverride" @bind="_attorneyFeeOverride" step="0.01" min="0" placeholder="Leave blank to calculate" />
                </div>
            </div>
            <div class="col-md-4">
                <label class="form-label">Calculated Attorney Fee</label>
                <div class="form-control-plaintext fw-bold">@CalculatedAttorneyFee.ToString("C")</div>
            </div>

            <!-- Referral Fee -->
            <div class="col-12 mt-4">
                <h6 class="text-muted border-bottom pb-2">Referral / Co-Counsel Fee</h6>
            </div>
            <div class="col-md-4">
                <label for="referralFeePct" class="form-label">Referral Fee %</label>
                <div class="input-group">
                    <input type="number" class="form-control" id="referralFeePct" @bind="_referralFeePercentage" step="0.01" min="0" max="100" />
                    <span class="input-group-text">%</span>
                </div>
            </div>
            <div class="col-md-4">
                <label for="referralAttorney" class="form-label">Referral Attorney Name</label>
                <input type="text" class="form-control" id="referralAttorney" @bind="_referralAttorneyName" maxlength="300" />
            </div>
            <div class="col-md-4">
                <label class="form-label">Calculated Referral Fee</label>
                <div class="form-control-plaintext fw-bold">@CalculatedReferralFee.ToString("C")</div>
            </div>

            <!-- Case Costs -->
            <div class="col-12 mt-4">
                <h6 class="text-muted border-bottom pb-2">Case Costs & Expenses</h6>
            </div>
            <div class="col-md-4">
                <label for="caseCosts" class="form-label">Total Case Costs</label>
                <div class="input-group">
                    <span class="input-group-text">$</span>
                    <input type="number" class="form-control" id="caseCosts" @bind="_caseCosts" step="0.01" min="0" />
                </div>
            </div>
            <div class="col-md-8">
                <label for="caseCostsDescription" class="form-label">Cost Description</label>
                <input type="text" class="form-control" id="caseCostsDescription" @bind="_caseCostsDescription" placeholder="Filing fees, expert fees, deposition costs, etc." />
            </div>

            <!-- Liens -->
            <div class="col-12 mt-4">
                <h6 class="text-muted border-bottom pb-2">Liens & Subrogation</h6>
            </div>
            <div class="col-md-4">
                <label for="totalLienAmount" class="form-label">Total Lien Amount</label>
                <div class="input-group">
                    <span class="input-group-text">$</span>
                    <input type="number" class="form-control" id="totalLienAmount" @bind="_totalLienAmount" step="0.01" min="0" />
                </div>
            </div>
            <div class="col-md-4">
                <label for="lienReductions" class="form-label">Lien Reductions (Negotiated)</label>
                <div class="input-group">
                    <span class="input-group-text">$</span>
                    <input type="number" class="form-control" id="lienReductions" @bind="_lienReductions" step="0.01" min="0" />
                </div>
            </div>
            <div class="col-md-4">
                <label for="medicalReductions" class="form-label">Medical Provider Reductions</label>
                <div class="input-group">
                    <span class="input-group-text">$</span>
                    <input type="number" class="form-control" id="medicalReductions" @bind="_medicalProviderReductions" step="0.01" min="0" />
                </div>
            </div>

            <!-- Disbursement -->
            <div class="col-12 mt-4">
                <h6 class="text-muted border-bottom pb-2">Disbursement</h6>
            </div>
            <div class="col-md-4">
                <label for="disbursementDate" class="form-label">Disbursement Date</label>
                <input type="date" class="form-control" id="disbursementDate" @bind="_disbursementDate" />
            </div>
            <div class="col-md-4">
                <label for="disbursementMethod" class="form-label">Disbursement Method</label>
                <select id="disbursementMethod" class="form-select" @bind="_disbursementMethod">
                    <option value="">-- Select --</option>
                    <option value="@DisbursementMethodDto.Check">Check</option>
                    <option value="@DisbursementMethodDto.Wire">Wire Transfer</option>
                    <option value="@DisbursementMethodDto.ACH">ACH</option>
                    <option value="@DisbursementMethodDto.Trust">Trust Account</option>
                </select>
            </div>

            <!-- Notes -->
            <div class="col-12 mt-4">
                <label for="settlementNotes" class="form-label">Notes</label>
                <textarea class="form-control" id="settlementNotes" @bind="_notes" rows="3"></textarea>
            </div>

            <!-- Settlement Sheet Preview -->
            <div class="col-12 mt-4">
                <h6 class="text-muted border-bottom pb-2">Settlement Sheet Preview</h6>
            </div>
            <div class="col-12">
                @RenderSettlementSheet(CalculatedAttorneyFee, CalculatedReferralFee)
            </div>

            <!-- Actions -->
            <div class="col-12 mt-3 d-flex gap-2 justify-content-end">
                <button type="button" class="btn btn-secondary" @onclick="CancelEdit" disabled="@_isSaving">Cancel</button>
                <button type="button" class="btn app-btn-primary" @onclick="SaveAsync" disabled="@_isSaving">
                    @if (_isSaving) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
                    else { <i class="bi bi-check-circle me-1"></i> <span>Save Settlement</span> }
                </button>
            </div>
        </div>
    }
    else if (Settlement != null)
    {
        <!-- Read-only Settlement View -->
        <div class="d-flex justify-content-between align-items-center mb-3">
            <div class="d-flex align-items-center gap-2">
                <h5 class="mb-0">Settlement</h5>
                <AppBadge Status="@GetStatusClass(Settlement.Status)" Label="@FormatStatus(Settlement.Status)" />
            </div>
            <div class="d-flex gap-2">
                <button class="btn btn-sm btn-outline-secondary" @onclick="OpenEditForm">
                    <i class="bi bi-pencil me-1"></i> Edit
                </button>
                <button class="btn btn-sm btn-outline-danger" @onclick="DeleteAsync">
                    <i class="bi bi-trash me-1"></i> Delete
                </button>
            </div>
        </div>

        <div class="row g-4">
            <!-- Settlement Info Card -->
            <div class="col-md-6">
                <AppCard CssClass="h-100">
                    <ChildContent>
                        <h6 class="text-muted mb-3">Settlement Information</h6>
                        <div class="row g-2">
                            <div class="col-6"><span class="text-muted small">Settlement Date</span><div>@Settlement.SettlementDate.ToLocalTime().ToString("MMM dd, yyyy")</div></div>
                            <div class="col-6"><span class="text-muted small">Type</span><div>@FormatType(Settlement.Type)</div></div>
                            <div class="col-6"><span class="text-muted small">Gross Amount</span><div class="fs-5 fw-bold text-success">@Settlement.GrossAmount.ToString("C")</div></div>
                            @if (!string.IsNullOrEmpty(Settlement.PayingPartyName))
                            {
                                <div class="col-6"><span class="text-muted small">Paying Party</span><div>@Settlement.PayingPartyName</div></div>
                            }
                            @if (!string.IsNullOrEmpty(Settlement.InsuranceClaimNumber))
                            {
                                <div class="col-6"><span class="text-muted small">Claim #</span><div>@Settlement.InsuranceClaimNumber</div></div>
                            }
                            @if (!string.IsNullOrEmpty(Settlement.InsurancePolicyNumber))
                            {
                                <div class="col-6"><span class="text-muted small">Policy #</span><div>@Settlement.InsurancePolicyNumber</div></div>
                            }
                            @if (!string.IsNullOrEmpty(Settlement.PaymentReferenceNumber))
                            {
                                <div class="col-6"><span class="text-muted small">Payment Ref / Check #</span><div>@Settlement.PaymentReferenceNumber</div></div>
                            }
                        </div>
                    </ChildContent>
                </AppCard>
            </div>

            <!-- Attorney Fees Card -->
            <div class="col-md-6">
                <AppCard CssClass="h-100">
                    <ChildContent>
                        <h6 class="text-muted mb-3">Attorney Fees</h6>
                        <div class="row g-2">
                            <div class="col-6"><span class="text-muted small">Fee Percentage</span><div>@Settlement.AttorneyFeePercentage.ToString("0.##")%</div></div>
                            <div class="col-6"><span class="text-muted small">Attorney Fee</span><div class="fw-bold">@Settlement.AttorneyFeeAmount.ToString("C")</div></div>
                            @if (Settlement.AttorneyFeeOverride.HasValue)
                            {
                                <div class="col-6"><span class="text-muted small">Fee Override</span><div>@Settlement.AttorneyFeeOverride.Value.ToString("C")</div></div>
                            }
                            @if (Settlement.ReferralFeePercentage.HasValue)
                            {
                                <div class="col-6"><span class="text-muted small">Referral Fee %</span><div>@Settlement.ReferralFeePercentage.Value.ToString("0.##")%</div></div>
                                <div class="col-6"><span class="text-muted small">Referral Fee</span><div>@Settlement.ReferralFeeAmount.ToString("C")</div></div>
                            }
                            @if (!string.IsNullOrEmpty(Settlement.ReferralAttorneyName))
                            {
                                <div class="col-6"><span class="text-muted small">Referral Attorney</span><div>@Settlement.ReferralAttorneyName</div></div>
                            }
                            <div class="col-6"><span class="text-muted small">Net Attorney Fee</span><div class="fw-bold">@Settlement.NetAttorneyFee.ToString("C")</div></div>
                        </div>
                    </ChildContent>
                </AppCard>
            </div>

            <!-- Settlement Sheet Card (full width) -->
            <div class="col-12">
                <AppCard>
                    <ChildContent>
                        <h6 class="text-muted mb-3">Settlement Sheet</h6>
                        @RenderSettlementSheet(Settlement.AttorneyFeeAmount, Settlement.ReferralFeeAmount)
                    </ChildContent>
                </AppCard>
            </div>

            <!-- Disbursement Card -->
            @if (Settlement.DisbursementDate.HasValue || Settlement.DisbursementMethodType.HasValue)
            {
                <div class="col-md-6">
                    <AppCard>
                        <ChildContent>
                            <h6 class="text-muted mb-3">Disbursement</h6>
                            <div class="row g-2">
                                @if (Settlement.DisbursementDate.HasValue)
                                {
                                    <div class="col-6"><span class="text-muted small">Disbursement Date</span><div>@Settlement.DisbursementDate.Value.ToLocalTime().ToString("MMM dd, yyyy")</div></div>
                                }
                                @if (Settlement.DisbursementMethodType.HasValue)
                                {
                                    <div class="col-6"><span class="text-muted small">Method</span><div>@FormatDisbursementMethod(Settlement.DisbursementMethodType.Value)</div></div>
                                }
                            </div>
                        </ChildContent>
                    </AppCard>
                </div>
            }

            <!-- Notes -->
            @if (!string.IsNullOrEmpty(Settlement.Notes))
            {
                <div class="col-md-6">
                    <AppCard>
                        <ChildContent>
                            <h6 class="text-muted mb-3">Notes</h6>
                            <p class="mb-0">@Settlement.Notes</p>
                        </ChildContent>
                    </AppCard>
                </div>
            }
        </div>
    }
</div>

@code {
    [Parameter, EditorRequired]
    public Guid MatterId { get; set; }

    [Parameter]
    public SettlementDto? Settlement { get; set; }

    [Parameter]
    public PIMattertSummaryDto? PISummary { get; set; }

    [Parameter]
    public EventCallback OnRefresh { get; set; }

    private bool _isEditing;
    private bool _isSaving;
    private string? _errorMessage;

    // Form fields
    private DateTime _settlementDate = DateTime.Today;
    private decimal _grossAmount;
    private SettlementTypeDto _settlementType = SettlementTypeDto.LumpSum;
    private SettlementStatusDto _settlementStatus = SettlementStatusDto.Draft;
    private Guid? _payingPartyContactId;
    private string? _insuranceClaimNumber;
    private string? _insurancePolicyNumber;
    private string? _paymentReferenceNumber;
    private decimal _attorneyFeePercentage = 33.33m;
    private decimal? _attorneyFeeOverride;
    private decimal? _referralFeePercentage;
    private string? _referralAttorneyName;
    private decimal _caseCosts;
    private string? _caseCostsDescription;
    private decimal _totalLienAmount;
    private decimal _lienReductions;
    private decimal _medicalProviderReductions;
    private DateTime? _disbursementDate;
    private DisbursementMethodDto? _disbursementMethod;
    private string? _notes;

    // Computed preview values
    private decimal CalculatedAttorneyFee => _attorneyFeeOverride ?? (_grossAmount * _attorneyFeePercentage / 100m);
    private decimal CalculatedReferralFee => _referralFeePercentage.HasValue ? (CalculatedAttorneyFee * _referralFeePercentage.Value / 100m) : 0m;
    private decimal CalculatedEffectiveLienAmount => _totalLienAmount - _lienReductions;
    private decimal CalculatedTotalDeductions => CalculatedAttorneyFee + _caseCosts + CalculatedEffectiveLienAmount + _medicalProviderReductions;
    private decimal CalculatedNetToClient => _grossAmount - CalculatedTotalDeductions;

    private void OpenEditForm()
    {
        if (Settlement != null)
        {
            _settlementDate = Settlement.SettlementDate.ToLocalTime();
            _grossAmount = Settlement.GrossAmount;
            _settlementType = Settlement.Type;
            _settlementStatus = Settlement.Status;
            _payingPartyContactId = Settlement.PayingPartyContactId;
            _insuranceClaimNumber = Settlement.InsuranceClaimNumber;
            _insurancePolicyNumber = Settlement.InsurancePolicyNumber;
            _paymentReferenceNumber = Settlement.PaymentReferenceNumber;
            _attorneyFeePercentage = Settlement.AttorneyFeePercentage;
            _attorneyFeeOverride = Settlement.AttorneyFeeOverride;
            _referralFeePercentage = Settlement.ReferralFeePercentage;
            _referralAttorneyName = Settlement.ReferralAttorneyName;
            _caseCosts = Settlement.CaseCosts;
            _caseCostsDescription = Settlement.CaseCostsDescription;
            _totalLienAmount = Settlement.TotalLienAmount;
            _lienReductions = Settlement.LienReductions;
            _medicalProviderReductions = Settlement.MedicalProviderReductions;
            _disbursementDate = Settlement.DisbursementDate?.ToLocalTime();
            _disbursementMethod = Settlement.DisbursementMethodType;
            _notes = Settlement.Notes;
        }
        else
        {
            // Pre-fill from PI summary if available
            if (PISummary != null)
            {
                _totalLienAmount = PISummary.TotalLienAmount;
            }
            _settlementDate = DateTime.Today;
            _grossAmount = 0;
            _settlementType = SettlementTypeDto.LumpSum;
            _settlementStatus = SettlementStatusDto.Draft;
            _attorneyFeePercentage = 33.33m;
            _attorneyFeeOverride = null;
            _referralFeePercentage = null;
            _referralAttorneyName = null;
            _caseCosts = 0;
            _caseCostsDescription = null;
            _lienReductions = 0;
            _medicalProviderReductions = 0;
            _payingPartyContactId = null;
            _insuranceClaimNumber = null;
            _insurancePolicyNumber = null;
            _paymentReferenceNumber = null;
            _disbursementDate = null;
            _disbursementMethod = null;
            _notes = null;
        }
        _errorMessage = null;
        _isEditing = true;
    }

    private void CancelEdit()
    {
        _isEditing = false;
        _errorMessage = null;
    }

    private async Task SaveAsync()
    {
        if (_grossAmount <= 0) { _errorMessage = "Gross settlement amount must be greater than zero."; return; }
        try
        {
            _isSaving = true; _errorMessage = null; StateHasChanged();

            var cmd = new SaveSettlementCommand
            {
                SettlementDate = _settlementDate.ToUniversalTime(),
                GrossAmount = _grossAmount,
                Type = _settlementType,
                Status = _settlementStatus,
                PayingPartyContactId = _payingPartyContactId,
                InsuranceClaimNumber = _insuranceClaimNumber,
                InsurancePolicyNumber = _insurancePolicyNumber,
                PaymentReferenceNumber = _paymentReferenceNumber,
                AttorneyFeePercentage = _attorneyFeePercentage,
                AttorneyFeeOverride = _attorneyFeeOverride,
                ReferralFeePercentage = _referralFeePercentage,
                ReferralAttorneyName = _referralAttorneyName,
                CaseCosts = _caseCosts,
                CaseCostsDescription = _caseCostsDescription,
                TotalLienAmount = _totalLienAmount,
                LienReductions = _lienReductions,
                MedicalProviderReductions = _medicalProviderReductions,
                DisbursementDate = _disbursementDate?.ToUniversalTime(),
                DisbursementMethodType = _disbursementMethod,
                Notes = _notes
            };

            await ApiClient.SaveSettlementAsync(MatterId, cmd);
            _isEditing = false;
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save settlement for matter {MatterId}", MatterId);
            _errorMessage = $"Failed to save settlement: {ex.Message}";
        }
        finally { _isSaving = false; StateHasChanged(); }
    }

    private async Task DeleteAsync()
    {
        try
        {
            await ApiClient.DeleteSettlementAsync(MatterId);
            await OnRefresh.InvokeAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to delete settlement for matter {MatterId}", MatterId);
        }
    }

    private RenderFragment RenderSettlementSheet(decimal attorneyFee, decimal referralFee) => __builder =>
    {
        var gross = _isEditing ? _grossAmount : (Settlement?.GrossAmount ?? 0);
        var costs = _isEditing ? _caseCosts : (Settlement?.CaseCosts ?? 0);
        var effectiveLien = _isEditing ? CalculatedEffectiveLienAmount : (Settlement?.EffectiveLienAmount ?? 0);
        var medReductions = _isEditing ? _medicalProviderReductions : (Settlement?.MedicalProviderReductions ?? 0);
        var totalDeductions = attorneyFee + costs + effectiveLien + medReductions;
        var netToClient = gross - totalDeductions;

        <table class="table table-sm mb-0" style="max-width: 500px;">
            <tbody>
                <tr>
                    <td>Gross Settlement</td>
                    <td class="text-end fw-bold">@gross.ToString("C")</td>
                </tr>
                <tr class="text-muted">
                    <td class="ps-3">Less: Attorney Fee (@((_isEditing ? _attorneyFeePercentage : Settlement?.AttorneyFeePercentage ?? 0).ToString("0.##"))%)</td>
                    <td class="text-end">(@attorneyFee.ToString("C"))</td>
                </tr>
                @if (referralFee > 0)
                {
                    <tr class="text-muted">
                        <td class="ps-4 small">Includes referral fee</td>
                        <td class="text-end small">@referralFee.ToString("C")</td>
                    </tr>
                }
                @if (costs > 0)
                {
                    <tr class="text-muted">
                        <td class="ps-3">Less: Case Costs</td>
                        <td class="text-end">(@costs.ToString("C"))</td>
                    </tr>
                }
                @if (effectiveLien > 0)
                {
                    <tr class="text-muted">
                        <td class="ps-3">Less: Liens (after reductions)</td>
                        <td class="text-end">(@effectiveLien.ToString("C"))</td>
                    </tr>
                }
                @if (medReductions > 0)
                {
                    <tr class="text-muted">
                        <td class="ps-3">Less: Medical Provider Reductions</td>
                        <td class="text-end">(@medReductions.ToString("C"))</td>
                    </tr>
                }
                <tr class="border-top border-2">
                    <td class="fw-bold">Net to Client</td>
                    <td class="text-end fw-bold fs-5 @(netToClient >= 0 ? "text-success" : "text-danger")">@netToClient.ToString("C")</td>
                </tr>
            </tbody>
        </table>
    };

    private static string GetStatusClass(SettlementStatusDto status) => status switch
    {
        SettlementStatusDto.Draft => "neutral",
        SettlementStatusDto.Pending => "followup",
        SettlementStatusDto.Approved => "working",
        SettlementStatusDto.FundsReceived => "working",
        SettlementStatusDto.Disbursed => "done",
        SettlementStatusDto.Voided => "stuck",
        _ => "neutral"
    };

    private static string FormatStatus(SettlementStatusDto status) => status switch
    {
        SettlementStatusDto.FundsReceived => "Funds Received",
        _ => status.ToString()
    };

    private static string FormatType(SettlementTypeDto type) => type switch
    {
        SettlementTypeDto.LumpSum => "Lump Sum",
        _ => type.ToString()
    };

    private static string FormatDisbursementMethod(DisbursementMethodDto method) => method switch
    {
        DisbursementMethodDto.ACH => "ACH",
        DisbursementMethodDto.Wire => "Wire Transfer",
        DisbursementMethodDto.Trust => "Trust Account",
        _ => method.ToString()
    };
}
```

