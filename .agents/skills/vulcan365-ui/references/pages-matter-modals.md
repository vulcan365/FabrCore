# Matter Modal Components

Modal dialogs for creating and editing matters.

## CreateMatterModal.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<CreateMatterModal> Logger

<AppModal @bind-IsVisible="IsVisible" Title="Create New Matter" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger app-alert-inline" role="alert">
                <i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage
            </div>
        }
        <form @onsubmit="HandleSubmit">
            <div class="row g-3">
                <div class="col-12">
                    <label for="matterTitle" class="form-label">Title <span class="text-danger">*</span></label>
                    <input type="text" class="form-control" id="matterTitle" @bind="_model.Title" required maxlength="200" placeholder="Brief description of the matter" />
                </div>
                <div class="col-md-6">
                    <label for="matterType" class="form-label">Matter Type <span class="text-danger">*</span></label>
                    <select class="form-select" id="matterType" @bind="_model.MatterTypeCode" required>
                        <option value="">Select matter type...</option>
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
                <div class="col-md-6">
                    <ContactAutocomplete Label="Client" Required="true" ContactTypeCode="CLIENT"
                                         SelectedContact="_selectedClient" SelectedContactChanged="(c) => _selectedClient = c" />
                </div>
                <div class="col-md-6">
                    <label for="jurisdiction" class="form-label">Jurisdiction</label>
                    <input type="text" class="form-control" id="jurisdiction" @bind="_model.Jurisdiction" maxlength="100" placeholder="e.g., Travis County, TX" />
                </div>
                <div class="col-md-6">
                    <label for="courtMatterNumber" class="form-label">Court Matter Number</label>
                    <input type="text" class="form-control" id="courtMatterNumber" @bind="_model.CourtMatterNumber" maxlength="50" />
                </div>
                <div class="col-md-6">
                    <label for="estimatedValue" class="form-label">Estimated Value</label>
                    <input type="number" class="form-control" id="estimatedValue" @bind="_model.EstimatedValue" step="0.01" placeholder="0.00" />
                </div>
                <div class="col-md-6">
                    <label for="trialDate" class="form-label">Trial Date</label>
                    <input type="date" class="form-control" id="trialDate" value="@_trialDateText" @onchange="@((ChangeEventArgs e) => _trialDateText = e.Value?.ToString())" />
                </div>
                <div class="col-12">
                    <label for="description" class="form-label">Description</label>
                    <textarea class="form-control" id="description" @bind="_model.Description" rows="3" maxlength="1000" placeholder="Detailed description..."></textarea>
                </div>
            </div>
        </form>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="Cancel" disabled="@_isSubmitting">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="HandleSubmit" disabled="@_isSubmitting">
            @if (_isSubmitting) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Creating...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>Create Matter</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter] public bool IsVisible { get; set; }
    [Parameter] public EventCallback<bool> IsVisibleChanged { get; set; }
    [Parameter] public EventCallback OnMatterCreated { get; set; }

    private CreateMatterCommand _model = new();
    private ContactSummaryDto? _selectedClient;
    private string? _trialDateText;
    private bool _isSubmitting;
    private string? _errorMessage;

    private async Task HandleSubmit()
    {
        try
        {
            _isSubmitting = true; _errorMessage = null; StateHasChanged();

            if (_selectedClient == null) { _errorMessage = "Client is required."; return; }
            _model.ClientId = _selectedClient.Id;
            if (string.IsNullOrWhiteSpace(_model.Title)) { _errorMessage = "Title is required."; return; }
            if (string.IsNullOrWhiteSpace(_model.MatterTypeCode)) { _errorMessage = "Matter Type is required."; return; }
            if (!string.IsNullOrWhiteSpace(_trialDateText) && DateTime.TryParse(_trialDateText, out var trialDate))
                _model.TrialDate = trialDate.ToUniversalTime();

            await ApiClient.CreateMatterAsync(_model);
            ResetForm();
            await IsVisibleChanged.InvokeAsync(false);
            await OnMatterCreated.InvokeAsync();
        }
        catch (Exception ex) { _errorMessage = $"Failed to create matter: {ex.Message}"; }
        finally { _isSubmitting = false; StateHasChanged(); }
    }

    private async Task Cancel() { ResetForm(); await IsVisibleChanged.InvokeAsync(false); }

    private void ResetForm() { _model = new(); _selectedClient = null; _trialDateText = null; _errorMessage = null; }
}
```

## EditMatterModal.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<EditMatterModal> Logger

<AppModal @bind-IsVisible="IsVisible" Title="Edit Matter" Size="lg" CloseOnBackdropClick="false">
    <ChildContent>
        @if (_errorMessage != null)
        {
            <div class="alert alert-danger app-alert-inline" role="alert">
                <i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage
            </div>
        }
        @if (_model != null)
        {
            <form @onsubmit="HandleSubmit">
                <div class="row g-3">
                    <div class="col-12">
                        <label for="editTitle" class="form-label">Title <span class="text-danger">*</span></label>
                        <input type="text" class="form-control" id="editTitle" @bind="_model.Title" required maxlength="200" />
                    </div>
                    <div class="col-md-6">
                        <label for="editType" class="form-label">Matter Type <span class="text-danger">*</span></label>
                        <select class="form-select" id="editType" @bind="_model.MatterTypeCode" required>
                            <option value="">Select type...</option>
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
                    <div class="col-md-6">
                        <label for="editJurisdiction" class="form-label">Jurisdiction</label>
                        <input type="text" class="form-control" id="editJurisdiction" @bind="_model.Jurisdiction" maxlength="100" />
                    </div>
                    <div class="col-md-6">
                        <label for="editCourtNum" class="form-label">Court Matter Number</label>
                        <input type="text" class="form-control" id="editCourtNum" @bind="_model.CourtMatterNumber" maxlength="50" />
                    </div>
                    <div class="col-md-6">
                        <label for="editValue" class="form-label">Estimated Value</label>
                        <input type="number" class="form-control" id="editValue" @bind="_model.EstimatedValue" step="0.01" />
                    </div>
                    <div class="col-md-6">
                        <label for="editTrialDate" class="form-label">Trial Date</label>
                        <input type="date" class="form-control" id="editTrialDate" value="@_trialDateText" @onchange="@((ChangeEventArgs e) => _trialDateText = e.Value?.ToString())" />
                    </div>
                    <div class="col-12">
                        <label for="editDesc" class="form-label">Description</label>
                        <textarea class="form-control" id="editDesc" @bind="_model.Description" rows="3" maxlength="1000"></textarea>
                    </div>
                </div>
            </form>
        }
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="Cancel" disabled="@_isSubmitting">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="HandleSubmit" disabled="@_isSubmitting">
            @if (_isSubmitting) { <span class="spinner-border spinner-border-sm me-1"></span> <span>Saving...</span> }
            else { <i class="bi bi-check-circle me-1"></i> <span>Save Changes</span> }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter] public bool IsVisible { get; set; }
    [Parameter] public EventCallback<bool> IsVisibleChanged { get; set; }
    [Parameter] public Guid MatterId { get; set; }
    [Parameter] public MatterDetailDto? MatterDetail { get; set; }
    [Parameter] public EventCallback OnMatterSaved { get; set; }

    private UpdateMatterCommand? _model;
    private string? _trialDateText;
    private bool _isSubmitting;
    private string? _errorMessage;

    protected override void OnParametersSet()
    {
        if (IsVisible && MatterDetail != null)
        {
            _model = new UpdateMatterCommand
            {
                Title = MatterDetail.Title, Description = MatterDetail.Description,
                MatterTypeCode = MatterDetail.MatterType.Code, Jurisdiction = MatterDetail.Jurisdiction,
                EstimatedValue = MatterDetail.EstimatedValue, CourtMatterNumber = MatterDetail.CourtMatterNumber,
                TrialDate = MatterDetail.TrialDate, AssignedAttorneyId = MatterDetail.AssignedAttorneyId
            };
            _trialDateText = MatterDetail.TrialDate?.ToString("yyyy-MM-dd");
        }
    }

    private async Task HandleSubmit()
    {
        if (_model == null) return;
        try
        {
            _isSubmitting = true; _errorMessage = null; StateHasChanged();
            if (string.IsNullOrWhiteSpace(_model.Title)) { _errorMessage = "Title is required."; return; }
            if (string.IsNullOrWhiteSpace(_model.MatterTypeCode)) { _errorMessage = "Matter Type is required."; return; }
            if (!string.IsNullOrWhiteSpace(_trialDateText) && DateTime.TryParse(_trialDateText, out var td))
                _model.TrialDate = td.ToUniversalTime();
            else _model.TrialDate = null;

            await ApiClient.UpdateMatterAsync(MatterId, _model);
            await IsVisibleChanged.InvokeAsync(false);
            await OnMatterSaved.InvokeAsync();
        }
        catch (Exception ex) { _errorMessage = $"Failed to update matter: {ex.Message}"; }
        finally { _isSubmitting = false; StateHasChanged(); }
    }

    private async Task Cancel() { _errorMessage = null; await IsVisibleChanged.InvokeAsync(false); }
}
```
