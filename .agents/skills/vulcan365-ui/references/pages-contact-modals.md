# Contact Modal Components

Modal dialogs for creating contacts, editing contacts, and adding contacts to matters.

## CreateContactModal.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<CreateContactModal> Logger

<AppModal IsVisible="@IsVisible"
            IsVisibleChanged="OnVisibilityChanged"
            Title="Create Contact"
            Size="lg"
            CloseOnBackdropClick="false">
    <ChildContent>
        @if (!string.IsNullOrWhiteSpace(_errorMessage))
        {
            <div class="alert alert-danger alert-dismissible" role="alert">
                <i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage
                <button type="button" class="btn-close" @onclick="() => _errorMessage = null"></button>
            </div>
        }

        @if (_duplicateWarning != null)
        {
            <div class="alert alert-warning alert-dismissible" role="alert">
                <i class="bi bi-exclamation-circle me-2"></i>@_duplicateWarning
                <button type="button" class="btn-close" @onclick="() => _duplicateWarning = null"></button>
            </div>
        }

        <EditForm Model="@_model" OnValidSubmit="HandleSubmitAsync">
            <DataAnnotationsValidator />

            <!-- Entity Type Toggle -->
            <div class="mb-3">
                <label class="form-label fw-semibold">Contact Type</label>
                <div class="btn-group w-100" role="group">
                    <input type="radio" class="btn-check" name="entityType" id="entityPerson"
                           checked="@(!_isOrganization)" @onchange="() => SetEntityType(false)" />
                    <label class="btn btn-outline-primary" for="entityPerson">
                        <i class="bi bi-person me-1"></i> Person
                    </label>
                    <input type="radio" class="btn-check" name="entityType" id="entityOrg"
                           checked="@_isOrganization" @onchange="() => SetEntityType(true)" />
                    <label class="btn btn-outline-primary" for="entityOrg">
                        <i class="bi bi-building me-1"></i> Organization
                    </label>
                </div>
            </div>

            <!-- Name Fields -->
            @if (_isOrganization)
            {
                <div class="mb-3">
                    <label for="orgName" class="form-label">Organization Name <span class="text-danger">*</span></label>
                    <input id="orgName" type="text" class="form-control @GetValidationClass(nameof(_model.OrganizationName))"
                           @bind="_model.OrganizationName" @bind:after="() => ValidateField(nameof(_model.OrganizationName))"
                           maxlength="200" placeholder="Enter organization name" />
                    @if (_fieldErrors.ContainsKey(nameof(_model.OrganizationName)))
                    {
                        <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.OrganizationName)]</div>
                    }
                </div>
            }
            else
            {
                <div class="row g-3 mb-3">
                    <div class="col-md-4">
                        <label for="firstName" class="form-label">First Name <span class="text-danger">*</span></label>
                        <input id="firstName" type="text" class="form-control @GetValidationClass(nameof(_model.FirstName))"
                               @bind="_model.FirstName" @bind:after="() => ValidateField(nameof(_model.FirstName))"
                               maxlength="100" placeholder="First name" />
                        @if (_fieldErrors.ContainsKey(nameof(_model.FirstName)))
                        {
                            <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.FirstName)]</div>
                        }
                    </div>
                    <div class="col-md-3">
                        <label for="middleName" class="form-label">Middle Name</label>
                        <input id="middleName" type="text" class="form-control"
                               @bind="_model.MiddleName" maxlength="100" placeholder="Middle" />
                    </div>
                    <div class="col-md-4">
                        <label for="lastName" class="form-label">Last Name <span class="text-danger">*</span></label>
                        <input id="lastName" type="text" class="form-control @GetValidationClass(nameof(_model.LastName))"
                               @bind="_model.LastName" @bind:after="() => ValidateField(nameof(_model.LastName))"
                               maxlength="100" placeholder="Last name" />
                        @if (_fieldErrors.ContainsKey(nameof(_model.LastName)))
                        {
                            <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.LastName)]</div>
                        }
                    </div>
                    <div class="col-md-1">
                        <label for="suffix" class="form-label">Suffix</label>
                        <input id="suffix" type="text" class="form-control"
                               @bind="_model.Suffix" maxlength="20" placeholder="Jr." />
                    </div>
                </div>
            }

            <!-- Contact Type -->
            <div class="mb-3">
                <label for="contactType" class="form-label">Category <span class="text-danger">*</span></label>
                <select id="contactType" class="form-select @GetValidationClass(nameof(_model.ContactTypeCode))"
                        @bind="_model.ContactTypeCode" @bind:after="() => ValidateField(nameof(_model.ContactTypeCode))">
                    <option value="">Select a category...</option>
                    <option value="CLIENT">Client</option>
                    <option value="FAMILY">Family Member</option>
                    <option value="ADJUSTER">Adjuster</option>
                    <option value="MEDICAL">Medical Provider</option>
                    <option value="INSURANCE">Insurance Carrier</option>
                    <option value="LIENHOLDER">Lien Holder</option>
                </select>
                @if (_fieldErrors.ContainsKey(nameof(_model.ContactTypeCode)))
                {
                    <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.ContactTypeCode)]</div>
                }
            </div>

            <!-- Person-Only: DOB & SSN -->
            @if (!_isOrganization)
            {
                <div class="row g-3 mb-3">
                    <div class="col-md-4">
                        <label for="dob" class="form-label">Date of Birth</label>
                        <input id="dob" type="date" class="form-control" @bind="_model.BirthDate" />
                    </div>
                    <div class="col-md-4">
                        <label for="ssn" class="form-label">Social Security Number</label>
                        <div class="input-group">
                            <input id="ssn" type="@(_showSsn ? "text" : "password")" class="form-control @GetValidationClass(nameof(_model.SocialSecurityNumber))"
                                   @bind="_model.SocialSecurityNumber" @bind:after="() => ValidateField(nameof(_model.SocialSecurityNumber))"
                                   maxlength="11" placeholder="###-##-####" />
                            <button type="button" class="btn btn-outline-secondary" @onclick="() => _showSsn = !_showSsn" tabindex="-1">
                                <i class="bi @(_showSsn ? "bi-eye-slash" : "bi-eye")"></i>
                            </button>
                        </div>
                        @if (_fieldErrors.ContainsKey(nameof(_model.SocialSecurityNumber)))
                        {
                            <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.SocialSecurityNumber)]</div>
                        }
                    </div>
                </div>
            }

            <!-- Contact Information -->
            <div class="row g-3 mb-3">
                <div class="col-md-6">
                    <label for="email" class="form-label">Email</label>
                    <input id="email" type="email" class="form-control @GetValidationClass(nameof(_model.Email))"
                           @bind="_model.Email" @bind:after="() => ValidateField(nameof(_model.Email))"
                           maxlength="254" placeholder="email@example.com" />
                    @if (_fieldErrors.ContainsKey(nameof(_model.Email)))
                    {
                        <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.Email)]</div>
                    }
                </div>
                <div class="col-md-3">
                    <label for="primaryPhone" class="form-label">Primary Phone</label>
                    <input id="primaryPhone" type="tel" class="form-control @GetValidationClass(nameof(_model.PrimaryPhone))"
                           @bind="_model.PrimaryPhone" @bind:after="() => ValidateField(nameof(_model.PrimaryPhone))"
                           maxlength="20" placeholder="(555) 123-4567" />
                    @if (_fieldErrors.ContainsKey(nameof(_model.PrimaryPhone)))
                    {
                        <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.PrimaryPhone)]</div>
                    }
                </div>
                <div class="col-md-3">
                    <label for="secondaryPhone" class="form-label">Secondary Phone</label>
                    <input id="secondaryPhone" type="tel" class="form-control @GetValidationClass(nameof(_model.SecondaryPhone))"
                           @bind="_model.SecondaryPhone" @bind:after="() => ValidateField(nameof(_model.SecondaryPhone))"
                           maxlength="20" placeholder="(555) 987-6543" />
                    @if (_fieldErrors.ContainsKey(nameof(_model.SecondaryPhone)))
                    {
                        <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.SecondaryPhone)]</div>
                    }
                </div>
            </div>

            <!-- Address -->
            <fieldset class="mb-3">
                <legend class="form-label fw-semibold fs-6">Address <small class="text-muted fw-normal">(optional)</small></legend>
                <div class="row g-3">
                    <div class="col-12">
                        <input type="text" class="form-control" @bind="_model.Street1" maxlength="200" placeholder="Street Address" />
                    </div>
                    <div class="col-12">
                        <input type="text" class="form-control" @bind="_model.Street2" maxlength="200" placeholder="Street Address Line 2" />
                    </div>
                    <div class="col-md-5">
                        <input type="text" class="form-control" @bind="_model.City" maxlength="100" placeholder="City" />
                    </div>
                    <div class="col-md-3">
                        <select class="form-select" @bind="_model.State">
                            <option value="">State</option>
                            @foreach (var state in _usStates)
                            {
                                <option value="@state.Key">@state.Value</option>
                            }
                        </select>
                    </div>
                    <div class="col-md-2">
                        <input type="text" class="form-control" @bind="_model.PostalCode" maxlength="10" placeholder="ZIP" />
                    </div>
                    <div class="col-md-2">
                        <input type="text" class="form-control" @bind="_model.Country" maxlength="100" placeholder="Country" />
                    </div>
                </div>
            </fieldset>

            <!-- Notes -->
            <div class="mb-3">
                <label for="notes" class="form-label">Notes</label>
                <textarea id="notes" class="form-control" @bind="_model.Notes" rows="3" maxlength="2000" placeholder="Optional notes about this contact..."></textarea>
                <div class="form-text text-end">@(_model.Notes?.Length ?? 0)/2000</div>
            </div>

            <!-- Save and Add Another -->
            <div class="form-check mb-3">
                <input class="form-check-input" type="checkbox" id="saveAndAddAnother" @bind="_saveAndAddAnother" />
                <label class="form-check-label" for="saveAndAddAnother">Save and add another</label>
            </div>
        </EditForm>
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="Close" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="HandleSubmitAsync" disabled="@_isSaving">
            @if (_isSaving)
            {
                <span class="spinner-border spinner-border-sm me-1"></span>
                <span>Saving...</span>
            }
            else
            {
                <i class="bi bi-check-circle me-1"></i>
                <span>Create Contact</span>
            }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter]
    public bool IsVisible { get; set; }

    [Parameter]
    public EventCallback<bool> IsVisibleChanged { get; set; }

    [Parameter]
    public EventCallback<Guid> OnContactCreated { get; set; }

    private ContactFormModel _model = new();
    private bool _isOrganization = false;
    private bool _isSaving = false;
    private bool _saveAndAddAnother = false;
    private bool _showSsn = false;
    private string? _errorMessage;
    private string? _duplicateWarning;
    private Dictionary<string, string> _fieldErrors = new();
    private HashSet<string> _touchedFields = new();

    private void SetEntityType(bool isOrganization)
    {
        _isOrganization = isOrganization;
        _fieldErrors.Clear();
        _touchedFields.Clear();
    }

    private void ValidateField(string fieldName)
    {
        _touchedFields.Add(fieldName);
        _fieldErrors.Remove(fieldName);

        switch (fieldName)
        {
            case nameof(_model.FirstName) when !_isOrganization && string.IsNullOrWhiteSpace(_model.FirstName):
                _fieldErrors[fieldName] = "First name is required.";
                break;
            case nameof(_model.LastName) when !_isOrganization && string.IsNullOrWhiteSpace(_model.LastName):
                _fieldErrors[fieldName] = "Last name is required.";
                break;
            case nameof(_model.OrganizationName) when _isOrganization && string.IsNullOrWhiteSpace(_model.OrganizationName):
                _fieldErrors[fieldName] = "Organization name is required.";
                break;
            case nameof(_model.ContactTypeCode) when string.IsNullOrWhiteSpace(_model.ContactTypeCode):
                _fieldErrors[fieldName] = "Please select a category.";
                break;
            case nameof(_model.Email) when !string.IsNullOrWhiteSpace(_model.Email) && !IsValidEmail(_model.Email):
                _fieldErrors[fieldName] = "Please enter a valid email address.";
                break;
            case nameof(_model.PrimaryPhone) when !string.IsNullOrWhiteSpace(_model.PrimaryPhone) && !IsValidPhone(_model.PrimaryPhone):
                _fieldErrors[fieldName] = "Please enter a valid phone number.";
                break;
            case nameof(_model.SecondaryPhone) when !string.IsNullOrWhiteSpace(_model.SecondaryPhone) && !IsValidPhone(_model.SecondaryPhone):
                _fieldErrors[fieldName] = "Please enter a valid phone number.";
                break;
            case nameof(_model.SocialSecurityNumber) when !string.IsNullOrWhiteSpace(_model.SocialSecurityNumber) && !IsValidSsn(_model.SocialSecurityNumber):
                _fieldErrors[fieldName] = "Enter SSN as ###-##-#### or #########.";
                break;
        }
    }

    private bool ValidateAll()
    {
        _fieldErrors.Clear();

        if (_isOrganization)
        {
            if (string.IsNullOrWhiteSpace(_model.OrganizationName))
                _fieldErrors[nameof(_model.OrganizationName)] = "Organization name is required.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_model.FirstName))
                _fieldErrors[nameof(_model.FirstName)] = "First name is required.";
            if (string.IsNullOrWhiteSpace(_model.LastName))
                _fieldErrors[nameof(_model.LastName)] = "Last name is required.";
        }

        if (string.IsNullOrWhiteSpace(_model.ContactTypeCode))
            _fieldErrors[nameof(_model.ContactTypeCode)] = "Please select a category.";

        if (!string.IsNullOrWhiteSpace(_model.Email) && !IsValidEmail(_model.Email))
            _fieldErrors[nameof(_model.Email)] = "Please enter a valid email address.";

        if (!string.IsNullOrWhiteSpace(_model.PrimaryPhone) && !IsValidPhone(_model.PrimaryPhone))
            _fieldErrors[nameof(_model.PrimaryPhone)] = "Please enter a valid phone number.";

        if (!string.IsNullOrWhiteSpace(_model.SecondaryPhone) && !IsValidPhone(_model.SecondaryPhone))
            _fieldErrors[nameof(_model.SecondaryPhone)] = "Please enter a valid phone number.";

        return _fieldErrors.Count == 0;
    }

    private async Task HandleSubmitAsync()
    {
        if (!ValidateAll()) return;

        try
        {
            _isSaving = true;
            _errorMessage = null;
            _duplicateWarning = null;
            StateHasChanged();

            // Check for duplicates
            var searchName = _isOrganization ? _model.OrganizationName! : $"{_model.FirstName} {_model.LastName}";
            var duplicates = await ApiClient.SearchContactsAsync(searchName);

            if (duplicates.Any())
            {
                var nameMatches = duplicates.Where(d =>
                    d.DisplayName.Equals(searchName, StringComparison.OrdinalIgnoreCase)).ToList();

                if (nameMatches.Any() && _duplicateWarning == null)
                {
                    _duplicateWarning = $"A contact named \"{nameMatches.First().DisplayName}\" already exists. Click Create Contact again to proceed.";
                    _isSaving = false;
                    StateHasChanged();
                    return;
                }
            }

            Guid contactId;
            var address = HasAddress() ? new AddressDto
            {
                Street1 = _model.Street1?.Trim(),
                Street2 = _model.Street2?.Trim(),
                City = _model.City?.Trim(),
                State = _model.State?.Trim(),
                PostalCode = _model.PostalCode?.Trim(),
                Country = _model.Country?.Trim()
            } : null;

            if (_isOrganization)
            {
                contactId = await ApiClient.CreateOrganizationContactAsync(new CreateOrganizationContactRequest
                {
                    OrganizationName = _model.OrganizationName!.Trim(),
                    ContactTypeCode = _model.ContactTypeCode!,
                    Email = _model.Email?.Trim(),
                    PrimaryPhone = _model.PrimaryPhone?.Trim(),
                    SecondaryPhone = _model.SecondaryPhone?.Trim(),
                    Address = address,
                    Notes = _model.Notes?.Trim()
                });
            }
            else
            {
                contactId = await ApiClient.CreatePersonContactAsync(new CreatePersonContactRequest
                {
                    FirstName = _model.FirstName!.Trim(),
                    MiddleName = _model.MiddleName?.Trim(),
                    LastName = _model.LastName!.Trim(),
                    Suffix = _model.Suffix?.Trim(),
                    ContactTypeCode = _model.ContactTypeCode!,
                    Email = _model.Email?.Trim(),
                    PrimaryPhone = _model.PrimaryPhone?.Trim(),
                    SecondaryPhone = _model.SecondaryPhone?.Trim(),
                    Address = address,
                    Notes = _model.Notes?.Trim(),
                    BirthDate = _model.BirthDate.HasValue ? DateOnly.FromDateTime(_model.BirthDate.Value) : null,
                    SocialSecurityNumber = _model.SocialSecurityNumber?.Trim()
                });
            }

            Logger.LogInformation("Created contact {ContactId}", contactId);
            await OnContactCreated.InvokeAsync(contactId);

            if (_saveAndAddAnother)
            {
                ResetForm();
            }
            else
            {
                await Close();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create contact");
            _errorMessage = $"Failed to create contact: {ex.Message}";
        }
        finally
        {
            _isSaving = false;
            StateHasChanged();
        }
    }

    private bool HasAddress() =>
        !string.IsNullOrWhiteSpace(_model.Street1) ||
        !string.IsNullOrWhiteSpace(_model.City) ||
        !string.IsNullOrWhiteSpace(_model.State) ||
        !string.IsNullOrWhiteSpace(_model.PostalCode);

    private async Task Close()
    {
        ResetForm();
        await IsVisibleChanged.InvokeAsync(false);
    }

    private async Task OnVisibilityChanged(bool isVisible)
    {
        if (!isVisible) ResetForm();
        await IsVisibleChanged.InvokeAsync(isVisible);
    }

    private void ResetForm()
    {
        _model = new ContactFormModel { Country = "United States" };
        _errorMessage = null;
        _duplicateWarning = null;
        _showSsn = false;
        _fieldErrors.Clear();
        _touchedFields.Clear();
    }

    private string GetValidationClass(string fieldName)
    {
        if (!_touchedFields.Contains(fieldName) && !_fieldErrors.ContainsKey(fieldName)) return "";
        return _fieldErrors.ContainsKey(fieldName) ? "is-invalid" : "is-valid";
    }

    private static bool IsValidEmail(string email) =>
        System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    private static bool IsValidPhone(string phone) =>
        System.Text.RegularExpressions.Regex.IsMatch(phone, @"^[\d\s\(\)\-\+\.]+$") && phone.Length >= 7;

    private static bool IsValidSsn(string ssn) =>
        System.Text.RegularExpressions.Regex.IsMatch(ssn, @"^\d{3}-?\d{2}-?\d{4}$");

    private class ContactFormModel
    {
        public string? FirstName { get; set; }
        public string? MiddleName { get; set; }
        public string? LastName { get; set; }
        public string? Suffix { get; set; }
        public string? OrganizationName { get; set; }
        public string? ContactTypeCode { get; set; }
        public string? Email { get; set; }
        public string? PrimaryPhone { get; set; }
        public string? SecondaryPhone { get; set; }
        public string? Street1 { get; set; }
        public string? Street2 { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? PostalCode { get; set; }
        public string? Country { get; set; } = "United States";
        public string? Notes { get; set; }
        public DateTime? BirthDate { get; set; }
        public string? SocialSecurityNumber { get; set; }
    }

    private static readonly Dictionary<string, string> _usStates = new()
    {
        { "AL", "Alabama" }, { "AK", "Alaska" }, { "AZ", "Arizona" }, { "AR", "Arkansas" },
        { "CA", "California" }, { "CO", "Colorado" }, { "CT", "Connecticut" }, { "DE", "Delaware" },
        { "FL", "Florida" }, { "GA", "Georgia" }, { "HI", "Hawaii" }, { "ID", "Idaho" },
        { "IL", "Illinois" }, { "IN", "Indiana" }, { "IA", "Iowa" }, { "KS", "Kansas" },
        { "KY", "Kentucky" }, { "LA", "Louisiana" }, { "ME", "Maine" }, { "MD", "Maryland" },
        { "MA", "Massachusetts" }, { "MI", "Michigan" }, { "MN", "Minnesota" }, { "MS", "Mississippi" },
        { "MO", "Missouri" }, { "MT", "Montana" }, { "NE", "Nebraska" }, { "NV", "Nevada" },
        { "NH", "New Hampshire" }, { "NJ", "New Jersey" }, { "NM", "New Mexico" }, { "NY", "New York" },
        { "NC", "North Carolina" }, { "ND", "North Dakota" }, { "OH", "Ohio" }, { "OK", "Oklahoma" },
        { "OR", "Oregon" }, { "PA", "Pennsylvania" }, { "RI", "Rhode Island" }, { "SC", "South Carolina" },
        { "SD", "South Dakota" }, { "TN", "Tennessee" }, { "TX", "Texas" }, { "UT", "Utah" },
        { "VT", "Vermont" }, { "VA", "Virginia" }, { "WA", "Washington" }, { "WV", "West Virginia" },
        { "WI", "Wisconsin" }, { "WY", "Wyoming" }, { "DC", "District of Columbia" }
    };
}
```

## EditContactModal.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<EditContactModal> Logger

<AppModal IsVisible="@IsVisible"
            IsVisibleChanged="OnVisibilityChanged"
            Title="Edit Contact"
            Size="lg"
            CloseOnBackdropClick="false">
    <ChildContent>
        @if (_isLoading)
        {
            <AppLoadingState Message="Loading contact..." />
        }
        else if (_loadError != null)
        {
            <div class="alert alert-danger" role="alert">
                <i class="bi bi-exclamation-triangle me-2"></i>@_loadError
            </div>
        }
        else if (_contact != null)
        {
            @if (!string.IsNullOrWhiteSpace(_errorMessage))
            {
                <div class="alert alert-danger alert-dismissible" role="alert">
                    <i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage
                    <button type="button" class="btn-close" @onclick="() => _errorMessage = null"></button>
                </div>
            }

            <EditForm Model="@_model" OnValidSubmit="HandleSubmitAsync">
                <DataAnnotationsValidator />

                <!-- Entity Type (read-only) -->
                <div class="mb-3">
                    <label class="form-label fw-semibold">Contact Type</label>
                    <div class="form-control-plaintext">
                        @if (_contact.IsOrganization)
                        {
                            <i class="bi bi-building me-1 text-primary"></i>
                            <span>Organization</span>
                        }
                        else
                        {
                            <i class="bi bi-person me-1 text-primary"></i>
                            <span>Person</span>
                        }
                    </div>
                </div>

                <!-- Name Fields -->
                @if (_contact.IsOrganization)
                {
                    <div class="mb-3">
                        <label for="editOrgName" class="form-label">Organization Name <span class="text-danger">*</span></label>
                        <input id="editOrgName" type="text" class="form-control @GetValidationClass(nameof(_model.OrganizationName))"
                               @bind="_model.OrganizationName" @bind:after="() => ValidateField(nameof(_model.OrganizationName))"
                               maxlength="200" />
                        @if (_fieldErrors.ContainsKey(nameof(_model.OrganizationName)))
                        {
                            <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.OrganizationName)]</div>
                        }
                    </div>
                }
                else
                {
                    <div class="row g-3 mb-3">
                        <div class="col-md-4">
                            <label for="editFirstName" class="form-label">First Name <span class="text-danger">*</span></label>
                            <input id="editFirstName" type="text" class="form-control @GetValidationClass(nameof(_model.FirstName))"
                                   @bind="_model.FirstName" @bind:after="() => ValidateField(nameof(_model.FirstName))"
                                   maxlength="100" />
                            @if (_fieldErrors.ContainsKey(nameof(_model.FirstName)))
                            {
                                <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.FirstName)]</div>
                            }
                        </div>
                        <div class="col-md-3">
                            <label for="editMiddleName" class="form-label">Middle Name</label>
                            <input id="editMiddleName" type="text" class="form-control" @bind="_model.MiddleName" maxlength="100" />
                        </div>
                        <div class="col-md-4">
                            <label for="editLastName" class="form-label">Last Name <span class="text-danger">*</span></label>
                            <input id="editLastName" type="text" class="form-control @GetValidationClass(nameof(_model.LastName))"
                                   @bind="_model.LastName" @bind:after="() => ValidateField(nameof(_model.LastName))"
                                   maxlength="100" />
                            @if (_fieldErrors.ContainsKey(nameof(_model.LastName)))
                            {
                                <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.LastName)]</div>
                            }
                        </div>
                        <div class="col-md-1">
                            <label for="editSuffix" class="form-label">Suffix</label>
                            <input id="editSuffix" type="text" class="form-control" @bind="_model.Suffix" maxlength="20" />
                        </div>
                    </div>
                }

                <!-- Contact Type -->
                <div class="mb-3">
                    <label for="editContactType" class="form-label">Category <span class="text-danger">*</span></label>
                    <select id="editContactType" class="form-select @GetValidationClass(nameof(_model.ContactTypeCode))"
                            @bind="_model.ContactTypeCode" @bind:after="() => ValidateField(nameof(_model.ContactTypeCode))">
                        <option value="">Select a category...</option>
                        <option value="CLIENT">Client</option>
                        <option value="FAMILY">Family Member</option>
                        <option value="ADJUSTER">Adjuster</option>
                        <option value="MEDICAL">Medical Provider</option>
                        <option value="INSURANCE">Insurance Carrier</option>
                        <option value="LIENHOLDER">Lien Holder</option>
                    </select>
                    @if (_fieldErrors.ContainsKey(nameof(_model.ContactTypeCode)))
                    {
                        <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.ContactTypeCode)]</div>
                    }
                </div>

                <!-- Contact Information -->
                <div class="row g-3 mb-3">
                    <div class="col-md-6">
                        <label for="editEmail" class="form-label">Email</label>
                        <input id="editEmail" type="email" class="form-control @GetValidationClass(nameof(_model.Email))"
                               @bind="_model.Email" @bind:after="() => ValidateField(nameof(_model.Email))"
                               maxlength="254" />
                        @if (_fieldErrors.ContainsKey(nameof(_model.Email)))
                        {
                            <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.Email)]</div>
                        }
                    </div>
                    <div class="col-md-3">
                        <label for="editPrimaryPhone" class="form-label">Primary Phone</label>
                        <input id="editPrimaryPhone" type="tel" class="form-control @GetValidationClass(nameof(_model.PrimaryPhone))"
                               @bind="_model.PrimaryPhone" @bind:after="() => ValidateField(nameof(_model.PrimaryPhone))"
                               maxlength="20" />
                        @if (_fieldErrors.ContainsKey(nameof(_model.PrimaryPhone)))
                        {
                            <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.PrimaryPhone)]</div>
                        }
                    </div>
                    <div class="col-md-3">
                        <label for="editSecondaryPhone" class="form-label">Secondary Phone</label>
                        <input id="editSecondaryPhone" type="tel" class="form-control @GetValidationClass(nameof(_model.SecondaryPhone))"
                               @bind="_model.SecondaryPhone" @bind:after="() => ValidateField(nameof(_model.SecondaryPhone))"
                               maxlength="20" />
                        @if (_fieldErrors.ContainsKey(nameof(_model.SecondaryPhone)))
                        {
                            <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.SecondaryPhone)]</div>
                        }
                    </div>
                </div>

                <!-- Person-only fields: Birth Date and SSN -->
                @if (!_contact.IsOrganization)
                {
                    <div class="row g-3 mb-3">
                        <div class="col-md-6">
                            <label for="editBirthDate" class="form-label">Birth Date</label>
                            <input id="editBirthDate" type="date" class="form-control"
                                   @bind="_model.BirthDate" max="@DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd")" />
                        </div>
                        <div class="col-md-6">
                            <label for="editSsn" class="form-label">SSN</label>
                            <input id="editSsn" type="text" class="form-control @GetValidationClass(nameof(_model.Ssn))"
                                   @bind="_model.Ssn" @bind:after="() => ValidateField(nameof(_model.Ssn))"
                                   maxlength="11" placeholder="Leave blank to keep current" />
                            @if (_fieldErrors.ContainsKey(nameof(_model.Ssn)))
                            {
                                <div class="invalid-feedback d-block">@_fieldErrors[nameof(_model.Ssn)]</div>
                            }
                            <div class="form-text">Format: 123-45-6789 or 123456789</div>
                        </div>
                    </div>
                }

                <!-- Address -->
                <fieldset class="mb-3">
                    <legend class="form-label fw-semibold fs-6">Address</legend>
                    <div class="row g-3">
                        <div class="col-12">
                            <input type="text" class="form-control" @bind="_model.Street1" maxlength="200" placeholder="Street Address" />
                        </div>
                        <div class="col-12">
                            <input type="text" class="form-control" @bind="_model.Street2" maxlength="200" placeholder="Street Address Line 2" />
                        </div>
                        <div class="col-md-5">
                            <input type="text" class="form-control" @bind="_model.City" maxlength="100" placeholder="City" />
                        </div>
                        <div class="col-md-3">
                            <select class="form-select" @bind="_model.State">
                                <option value="">State</option>
                                @foreach (var state in _usStates)
                                {
                                    <option value="@state.Key">@state.Value</option>
                                }
                            </select>
                        </div>
                        <div class="col-md-2">
                            <input type="text" class="form-control" @bind="_model.PostalCode" maxlength="10" placeholder="ZIP" />
                        </div>
                        <div class="col-md-2">
                            <input type="text" class="form-control" @bind="_model.Country" maxlength="100" placeholder="Country" />
                        </div>
                    </div>
                </fieldset>

                <!-- Notes -->
                <div class="mb-3">
                    <label for="editNotes" class="form-label">Notes</label>
                    <textarea id="editNotes" class="form-control" @bind="_model.Notes" rows="3" maxlength="2000"></textarea>
                    <div class="form-text text-end">@(_model.Notes?.Length ?? 0)/2000</div>
                </div>
            </EditForm>
        }
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="Close" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="HandleSubmitAsync" disabled="@(_isSaving || _isLoading || _contact == null)">
            @if (_isSaving)
            {
                <span class="spinner-border spinner-border-sm me-1"></span>
                <span>Saving...</span>
            }
            else
            {
                <i class="bi bi-check-circle me-1"></i>
                <span>Save Changes</span>
            }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter]
    public Guid ContactId { get; set; }

    [Parameter]
    public bool IsVisible { get; set; }

    [Parameter]
    public EventCallback<bool> IsVisibleChanged { get; set; }

    [Parameter]
    public EventCallback OnContactUpdated { get; set; }

    private ContactDto? _contact;
    private EditFormModel _model = new();
    private bool _isLoading = false;
    private bool _isSaving = false;
    private string? _errorMessage;
    private string? _loadError;
    private Dictionary<string, string> _fieldErrors = new();
    private HashSet<string> _touchedFields = new();

    // Track original values for change detection
    private string? _originalContactTypeCode;
    private string? _originalFirstName;
    private string? _originalMiddleName;
    private string? _originalLastName;
    private string? _originalSuffix;
    private string? _originalOrganizationName;

    protected override async Task OnParametersSetAsync()
    {
        if (IsVisible && _contact == null && ContactId != Guid.Empty)
        {
            await LoadContactAsync();
        }
    }

    private async Task LoadContactAsync()
    {
        try
        {
            _isLoading = true;
            _loadError = null;
            StateHasChanged();

            _contact = await ApiClient.GetContactByIdAsync(ContactId);
            if (_contact == null)
            {
                _loadError = "Contact not found.";
                return;
            }

            PopulateForm();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load contact {ContactId}", ContactId);
            _loadError = $"Failed to load contact: {ex.Message}";
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    private void PopulateForm()
    {
        if (_contact == null) return;

        _model = new EditFormModel
        {
            FirstName = _contact.FirstName,
            MiddleName = _contact.MiddleName,
            LastName = _contact.LastName,
            Suffix = _contact.Suffix,
            OrganizationName = _contact.OrganizationName,
            ContactTypeCode = _contact.ContactTypeCode,
            Email = _contact.Email,
            PrimaryPhone = _contact.PrimaryPhone,
            SecondaryPhone = _contact.SecondaryPhone,
            BirthDate = _contact.BirthDate,
            Street1 = _contact.Address?.Street1,
            Street2 = _contact.Address?.Street2,
            City = _contact.Address?.City,
            State = _contact.Address?.State,
            PostalCode = _contact.Address?.PostalCode,
            Country = _contact.Address?.Country,
            Notes = _contact.Notes
        };

        _originalContactTypeCode = _contact.ContactTypeCode;
        _originalFirstName = _contact.FirstName;
        _originalMiddleName = _contact.MiddleName;
        _originalLastName = _contact.LastName;
        _originalSuffix = _contact.Suffix;
        _originalOrganizationName = _contact.OrganizationName;

        _fieldErrors.Clear();
        _touchedFields.Clear();
    }

    private void ValidateField(string fieldName)
    {
        _touchedFields.Add(fieldName);
        _fieldErrors.Remove(fieldName);

        switch (fieldName)
        {
            case nameof(_model.FirstName) when _contact?.IsOrganization == false && string.IsNullOrWhiteSpace(_model.FirstName):
                _fieldErrors[fieldName] = "First name is required.";
                break;
            case nameof(_model.LastName) when _contact?.IsOrganization == false && string.IsNullOrWhiteSpace(_model.LastName):
                _fieldErrors[fieldName] = "Last name is required.";
                break;
            case nameof(_model.OrganizationName) when _contact?.IsOrganization == true && string.IsNullOrWhiteSpace(_model.OrganizationName):
                _fieldErrors[fieldName] = "Organization name is required.";
                break;
            case nameof(_model.ContactTypeCode) when string.IsNullOrWhiteSpace(_model.ContactTypeCode):
                _fieldErrors[fieldName] = "Please select a category.";
                break;
            case nameof(_model.Email) when !string.IsNullOrWhiteSpace(_model.Email) && !IsValidEmail(_model.Email):
                _fieldErrors[fieldName] = "Please enter a valid email address.";
                break;
            case nameof(_model.PrimaryPhone) when !string.IsNullOrWhiteSpace(_model.PrimaryPhone) && !IsValidPhone(_model.PrimaryPhone):
                _fieldErrors[fieldName] = "Please enter a valid phone number.";
                break;
            case nameof(_model.SecondaryPhone) when !string.IsNullOrWhiteSpace(_model.SecondaryPhone) && !IsValidPhone(_model.SecondaryPhone):
                _fieldErrors[fieldName] = "Please enter a valid phone number.";
                break;
            case nameof(_model.Ssn) when !string.IsNullOrWhiteSpace(_model.Ssn) && !IsValidSsn(_model.Ssn):
                _fieldErrors[fieldName] = "SSN must be in format 123-45-6789 or 123456789.";
                break;
        }
    }

    private bool ValidateAll()
    {
        _fieldErrors.Clear();

        if (_contact == null) return false;

        if (_contact.IsOrganization)
        {
            if (string.IsNullOrWhiteSpace(_model.OrganizationName))
                _fieldErrors[nameof(_model.OrganizationName)] = "Organization name is required.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_model.FirstName))
                _fieldErrors[nameof(_model.FirstName)] = "First name is required.";
            if (string.IsNullOrWhiteSpace(_model.LastName))
                _fieldErrors[nameof(_model.LastName)] = "Last name is required.";
        }

        if (string.IsNullOrWhiteSpace(_model.ContactTypeCode))
            _fieldErrors[nameof(_model.ContactTypeCode)] = "Please select a category.";

        if (!string.IsNullOrWhiteSpace(_model.Email) && !IsValidEmail(_model.Email))
            _fieldErrors[nameof(_model.Email)] = "Please enter a valid email address.";

        if (!string.IsNullOrWhiteSpace(_model.PrimaryPhone) && !IsValidPhone(_model.PrimaryPhone))
            _fieldErrors[nameof(_model.PrimaryPhone)] = "Please enter a valid phone number.";

        if (!string.IsNullOrWhiteSpace(_model.SecondaryPhone) && !IsValidPhone(_model.SecondaryPhone))
            _fieldErrors[nameof(_model.SecondaryPhone)] = "Please enter a valid phone number.";

        if (!string.IsNullOrWhiteSpace(_model.Ssn) && !IsValidSsn(_model.Ssn))
            _fieldErrors[nameof(_model.Ssn)] = "SSN must be in format 123-45-6789 or 123456789.";

        return _fieldErrors.Count == 0;
    }

    private async Task HandleSubmitAsync()
    {
        if (_contact == null || !ValidateAll()) return;

        try
        {
            _isSaving = true;
            _errorMessage = null;
            StateHasChanged();

            // Update contact type if changed
            if (_model.ContactTypeCode != _originalContactTypeCode)
            {
                await ApiClient.ChangeContactTypeAsync(_contact.Id, _model.ContactTypeCode!);
            }

            // Update name if changed
            if (_contact.IsOrganization)
            {
                if (_model.OrganizationName?.Trim() != _originalOrganizationName)
                {
                    await ApiClient.UpdateOrganizationNameAsync(_contact.Id, _model.OrganizationName!.Trim());
                }
            }
            else
            {
                if (_model.FirstName?.Trim() != _originalFirstName ||
                    _model.MiddleName?.Trim() != _originalMiddleName ||
                    _model.LastName?.Trim() != _originalLastName ||
                    _model.Suffix?.Trim() != _originalSuffix)
                {
                    await ApiClient.UpdatePersonNameAsync(_contact.Id, new UpdatePersonNameRequest
                    {
                        FirstName = _model.FirstName!.Trim(),
                        MiddleName = _model.MiddleName?.Trim(),
                        LastName = _model.LastName!.Trim(),
                        Suffix = _model.Suffix?.Trim()
                    });
                }
            }

            // Update details
            var address = new AddressDto
            {
                Street1 = _model.Street1?.Trim(),
                Street2 = _model.Street2?.Trim(),
                City = _model.City?.Trim(),
                State = _model.State?.Trim(),
                PostalCode = _model.PostalCode?.Trim(),
                Country = _model.Country?.Trim()
            };

            await ApiClient.UpdateContactDetailsAsync(_contact.Id, new UpdateContactDetailsRequest
            {
                Email = _model.Email?.Trim(),
                PrimaryPhone = _model.PrimaryPhone?.Trim(),
                SecondaryPhone = _model.SecondaryPhone?.Trim(),
                Address = address,
                Notes = _model.Notes?.Trim(),
                BirthDate = _model.BirthDate,
                SocialSecurityNumber = string.IsNullOrWhiteSpace(_model.Ssn) ? null : _model.Ssn.Trim()
            });

            Logger.LogInformation("Updated contact {ContactId}", _contact.Id);
            await OnContactUpdated.InvokeAsync();
            await Close();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update contact {ContactId}", _contact?.Id);
            _errorMessage = $"Failed to update contact: {ex.Message}";
        }
        finally
        {
            _isSaving = false;
            StateHasChanged();
        }
    }

    private async Task Close()
    {
        _contact = null;
        _model = new();
        _fieldErrors.Clear();
        _touchedFields.Clear();
        _errorMessage = null;
        _loadError = null;
        await IsVisibleChanged.InvokeAsync(false);
    }

    private async Task OnVisibilityChanged(bool isVisible)
    {
        if (!isVisible)
        {
            _contact = null;
            _model = new();
            _fieldErrors.Clear();
            _touchedFields.Clear();
        }
        await IsVisibleChanged.InvokeAsync(isVisible);
    }

    private string GetValidationClass(string fieldName)
    {
        if (!_touchedFields.Contains(fieldName) && !_fieldErrors.ContainsKey(fieldName)) return "";
        return _fieldErrors.ContainsKey(fieldName) ? "is-invalid" : "is-valid";
    }

    private static bool IsValidEmail(string email) =>
        System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    private static bool IsValidPhone(string phone) =>
        System.Text.RegularExpressions.Regex.IsMatch(phone, @"^[\d\s\(\)\-\+\.]+$") && phone.Length >= 7;

    private static bool IsValidSsn(string ssn) =>
        System.Text.RegularExpressions.Regex.IsMatch(ssn, @"^\d{3}-?\d{2}-?\d{4}$");

    private class EditFormModel
    {
        public string? FirstName { get; set; }
        public string? MiddleName { get; set; }
        public string? LastName { get; set; }
        public string? Suffix { get; set; }
        public string? OrganizationName { get; set; }
        public string? ContactTypeCode { get; set; }
        public string? Email { get; set; }
        public string? PrimaryPhone { get; set; }
        public string? SecondaryPhone { get; set; }
        public DateOnly? BirthDate { get; set; }
        public string? Ssn { get; set; }
        public string? Street1 { get; set; }
        public string? Street2 { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? PostalCode { get; set; }
        public string? Country { get; set; }
        public string? Notes { get; set; }
    }

    private static readonly Dictionary<string, string> _usStates = new()
    {
        { "AL", "Alabama" }, { "AK", "Alaska" }, { "AZ", "Arizona" }, { "AR", "Arkansas" },
        { "CA", "California" }, { "CO", "Colorado" }, { "CT", "Connecticut" }, { "DE", "Delaware" },
        { "FL", "Florida" }, { "GA", "Georgia" }, { "HI", "Hawaii" }, { "ID", "Idaho" },
        { "IL", "Illinois" }, { "IN", "Indiana" }, { "IA", "Iowa" }, { "KS", "Kansas" },
        { "KY", "Kentucky" }, { "LA", "Louisiana" }, { "ME", "Maine" }, { "MD", "Maryland" },
        { "MA", "Massachusetts" }, { "MI", "Michigan" }, { "MN", "Minnesota" }, { "MS", "Mississippi" },
        { "MO", "Missouri" }, { "MT", "Montana" }, { "NE", "Nebraska" }, { "NV", "Nevada" },
        { "NH", "New Hampshire" }, { "NJ", "New Jersey" }, { "NM", "New Mexico" }, { "NY", "New York" },
        { "NC", "North Carolina" }, { "ND", "North Dakota" }, { "OH", "Ohio" }, { "OK", "Oklahoma" },
        { "OR", "Oregon" }, { "PA", "Pennsylvania" }, { "RI", "Rhode Island" }, { "SC", "South Carolina" },
        { "SD", "South Dakota" }, { "TN", "Tennessee" }, { "TX", "Texas" }, { "UT", "Utah" },
        { "VT", "Vermont" }, { "VA", "Virginia" }, { "WA", "Washington" }, { "WV", "West Virginia" },
        { "WI", "Wisconsin" }, { "WY", "Wyoming" }, { "DC", "District of Columbia" }
    };
}
```

## AddToMatterModal.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<AddToMatterModal> Logger

<AppModal IsVisible="@IsVisible"
            IsVisibleChanged="OnVisibilityChanged"
            Title="Add to Matter"
            Size="md">
    <ChildContent>
        @if (!string.IsNullOrWhiteSpace(_errorMessage))
        {
            <div class="alert alert-danger alert-dismissible" role="alert">
                <i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage
                <button type="button" class="btn-close" @onclick="() => _errorMessage = null"></button>
            </div>
        }

        @if (!string.IsNullOrWhiteSpace(_successMessage))
        {
            <div class="alert alert-success alert-dismissible" role="alert">
                <i class="bi bi-check-circle me-2"></i>@_successMessage
                <button type="button" class="btn-close" @onclick="() => _successMessage = null"></button>
            </div>
        }

        <p class="text-muted mb-3">
            Add <strong>@ContactName</strong> as a party to a matter.
        </p>

        <!-- Matter Search -->
        <div class="mb-3">
            <label for="matterSearch" class="form-label fw-semibold">Search Matters <span class="text-danger">*</span></label>
            <div class="input-group">
                <span class="input-group-text"><i class="bi bi-search"></i></span>
                <input id="matterSearch" type="text" class="form-control"
                       placeholder="Search by matter number or title..."
                       @bind="_matterSearchQuery"
                       @bind:event="oninput"
                       @bind:after="OnMatterSearchChanged" />
                @if (!string.IsNullOrWhiteSpace(_matterSearchQuery))
                {
                    <button class="btn btn-outline-secondary" @onclick="ClearMatterSearch">
                        <i class="bi bi-x"></i>
                    </button>
                }
            </div>
        </div>

        <!-- Search Results -->
        @if (_isSearching)
        {
            <div class="text-center py-3">
                <div class="spinner-border spinner-border-sm text-primary" role="status">
                    <span class="visually-hidden">Searching...</span>
                </div>
                <p class="text-muted small mt-1 mb-0">Searching matters...</p>
            </div>
        }
        else if (_searchResults.Any())
        {
            <div class="list-group mb-3" style="max-height: 200px; overflow-y: auto;">
                @foreach (var matter in _searchResults)
                {
                    <button type="button"
                            class="list-group-item list-group-item-action @(_selectedMatter?.Id == matter.Id ? "active" : "")"
                            @onclick="() => SelectMatter(matter)">
                        <div class="d-flex justify-content-between align-items-start">
                            <div>
                                <div class="fw-bold">@matter.MatterNumber</div>
                                <small class="@(_selectedMatter?.Id == matter.Id ? "" : "text-muted")">@matter.Title</small>
                            </div>
                            <div class="text-end">
                                <span class="badge @(_selectedMatter?.Id == matter.Id ? "bg-light text-dark" : "bg-secondary")">@matter.MatterTypeName</span>
                                <div><small class="@(_selectedMatter?.Id == matter.Id ? "" : "text-muted")">@matter.StatusName</small></div>
                            </div>
                        </div>
                    </button>
                }
            </div>
        }
        else if (!string.IsNullOrWhiteSpace(_matterSearchQuery) && _matterSearchQuery.Length >= 2)
        {
            <div class="text-muted text-center py-3 mb-3">
                <i class="bi bi-search fs-4 d-block mb-1"></i>
                <small>No matters found</small>
            </div>
        }

        @if (_selectedMatter != null)
        {
            <div class="border rounded p-3 mb-3 bg-light">
                <div class="d-flex justify-content-between align-items-center mb-2">
                    <div>
                        <strong>@_selectedMatter.MatterNumber</strong> - @_selectedMatter.Title
                    </div>
                    <button class="btn btn-sm btn-outline-secondary" @onclick="() => _selectedMatter = null" title="Clear selection">
                        <i class="bi bi-x"></i>
                    </button>
                </div>
            </div>

            <!-- Party Role -->
            <div class="mb-3">
                <label for="partyRole" class="form-label fw-semibold">Party Role <span class="text-danger">*</span></label>
                <select id="partyRole" class="form-select" @bind="_selectedRoleCode">
                    <option value="">Select a role...</option>
                    <option value="CLIENT">Client</option>
                    <option value="PLAINTIFF">Plaintiff</option>
                    <option value="DEFENDANT">Defendant</option>
                    <option value="WITNESS">Witness</option>
                    <option value="EXPERT_WITNESS">Expert Witness</option>
                    <option value="ATTORNEY">Attorney</option>
                    <option value="OPPOSING_COUNSEL">Opposing Counsel</option>
                    <option value="JUDGE">Judge</option>
                    <option value="COURT_REPORTER">Court Reporter</option>
                    <option value="INSURANCE_ADJUSTER">Insurance Adjuster</option>
                    <option value="MEDIATOR">Mediator</option>
                    <option value="ARBITRATOR">Arbitrator</option>
                    <option value="OTHER">Other</option>
                </select>
            </div>

            <!-- Primary Party -->
            <div class="form-check mb-3">
                <input class="form-check-input" type="checkbox" id="isPrimary" @bind="_isPrimaryParty" />
                <label class="form-check-label" for="isPrimary">Primary party</label>
            </div>

            <!-- Notes -->
            <div class="mb-3">
                <label for="partyNotes" class="form-label">Notes</label>
                <textarea id="partyNotes" class="form-control" @bind="_partyNotes" rows="2" maxlength="500" placeholder="Optional party notes..."></textarea>
            </div>
        }
    </ChildContent>
    <Footer>
        <button type="button" class="btn btn-secondary" @onclick="Close" disabled="@_isSaving">Cancel</button>
        <button type="button" class="btn app-btn-primary" @onclick="HandleSubmitAsync"
                disabled="@(_isSaving || _selectedMatter == null || string.IsNullOrWhiteSpace(_selectedRoleCode))">
            @if (_isSaving)
            {
                <span class="spinner-border spinner-border-sm me-1"></span>
                <span>Adding...</span>
            }
            else
            {
                <i class="bi bi-plus-circle me-1"></i>
                <span>Add to Matter</span>
            }
        </button>
    </Footer>
</AppModal>

@code {
    [Parameter]
    public Guid ContactId { get; set; }

    [Parameter]
    public string ContactName { get; set; } = string.Empty;

    [Parameter]
    public bool IsVisible { get; set; }

    [Parameter]
    public EventCallback<bool> IsVisibleChanged { get; set; }

    [Parameter]
    public EventCallback OnContactAddedToMatter { get; set; }

    private string _matterSearchQuery = string.Empty;
    private List<MatterListItemDto> _searchResults = new();
    private MatterListItemDto? _selectedMatter;
    private string _selectedRoleCode = string.Empty;
    private bool _isPrimaryParty = false;
    private string _partyNotes = string.Empty;
    private bool _isSearching = false;
    private bool _isSaving = false;
    private string? _errorMessage;
    private string? _successMessage;
    private System.Threading.Timer? _searchDebounceTimer;

    private void OnMatterSearchChanged()
    {
        _searchDebounceTimer?.Dispose();

        if (string.IsNullOrWhiteSpace(_matterSearchQuery) || _matterSearchQuery.Length < 2)
        {
            _searchResults.Clear();
            return;
        }

        _searchDebounceTimer = new System.Threading.Timer(async _ =>
        {
            await InvokeAsync(async () =>
            {
                await SearchMattersAsync();
            });
        }, null, 300, Timeout.Infinite);
    }

    private async Task SearchMattersAsync()
    {
        try
        {
            _isSearching = true;
            StateHasChanged();

            var result = await ApiClient.GetMattersAsync(new MatterQueryParameters
            {
                Search = _matterSearchQuery,
                PageSize = 10
            });

            _searchResults = result.Items.ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to search matters");
            _errorMessage = "Failed to search matters.";
        }
        finally
        {
            _isSearching = false;
            StateHasChanged();
        }
    }

    private void SelectMatter(MatterListItemDto matter)
    {
        _selectedMatter = matter;
        _searchResults.Clear();
        _matterSearchQuery = string.Empty;
    }

    private void ClearMatterSearch()
    {
        _matterSearchQuery = string.Empty;
        _searchResults.Clear();
    }

    private async Task HandleSubmitAsync()
    {
        if (_selectedMatter == null || string.IsNullOrWhiteSpace(_selectedRoleCode))
            return;

        try
        {
            _isSaving = true;
            _errorMessage = null;
            _successMessage = null;
            StateHasChanged();

            await ApiClient.AddPartyToMatterAsync(_selectedMatter.Id, new CreatePartyCommand
            {
                ContactId = ContactId,
                RoleCode = _selectedRoleCode,
                IsPrimary = _isPrimaryParty,
                Notes = string.IsNullOrWhiteSpace(_partyNotes) ? null : _partyNotes.Trim()
            });

            Logger.LogInformation("Added contact {ContactId} to matter {MatterId} as {Role}", ContactId, _selectedMatter.Id, _selectedRoleCode);

            _successMessage = $"Successfully added to {_selectedMatter.MatterNumber}.";
            await OnContactAddedToMatter.InvokeAsync();

            // Reset for another addition
            _selectedMatter = null;
            _selectedRoleCode = string.Empty;
            _isPrimaryParty = false;
            _partyNotes = string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to add contact to matter");
            _errorMessage = ex.Message.Contains("already")
                ? "This contact is already a party to this matter."
                : $"Failed to add to matter: {ex.Message}";
        }
        finally
        {
            _isSaving = false;
            StateHasChanged();
        }
    }

    private async Task Close()
    {
        ResetForm();
        await IsVisibleChanged.InvokeAsync(false);
    }

    private async Task OnVisibilityChanged(bool isVisible)
    {
        if (!isVisible) ResetForm();
        await IsVisibleChanged.InvokeAsync(isVisible);
    }

    private void ResetForm()
    {
        _matterSearchQuery = string.Empty;
        _searchResults.Clear();
        _selectedMatter = null;
        _selectedRoleCode = string.Empty;
        _isPrimaryParty = false;
        _partyNotes = string.Empty;
        _errorMessage = null;
        _successMessage = null;
        _searchDebounceTimer?.Dispose();
    }

    public void Dispose()
    {
        _searchDebounceTimer?.Dispose();
    }
}
```
