# System Admin Pages

System administration pages including the main system page with tab navigation, and tabs for database management, SharePoint diagnostics, and user management.

## SystemPage.razor

```razor
@page "/system"
@rendermode InteractiveServer
@inject IApiClient ApiClient
@inject ILogger<SystemPage> Logger

<PageTitle>System - App</PageTitle>

<h1 class="app-page-title mb-4">System</h1>

<!-- Tab Navigation -->
<ul class="nav nav-tabs" role="tablist">
    <li class="nav-item" role="presentation">
        <button class="nav-link @(_activeTab == "users" ? "active" : "")" type="button" @onclick='() => _activeTab = "users"' role="tab">
            <i class="bi bi-people me-1"></i> Users
        </button>
    </li>
    <li class="nav-item" role="presentation">
        <button class="nav-link @(_activeTab == "database" ? "active" : "")" type="button" @onclick='() => _activeTab = "database"' role="tab">
            <i class="bi bi-database me-1"></i> Database
        </button>
    </li>
    <li class="nav-item" role="presentation">
        <button class="nav-link @(_activeTab == "sharepoint" ? "active" : "")" type="button" @onclick='() => _activeTab = "sharepoint"' role="tab">
            <i class="bi bi-cloud me-1"></i> SharePoint
        </button>
    </li>
</ul>

<!-- Tab Content -->
<div class="app-content">
    @switch (_activeTab)
    {
        case "users":
            <UsersTab />
            break;
        case "database":
            <DatabaseTab />
            break;
        case "sharepoint":
            <SharePointTab />
            break;
    }
</div>

@code {
    private string _activeTab = "users";
}
```

## SystemAdmin/DatabaseTab.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<DatabaseTab> Logger

<div class="mt-3">
    @if (_isLoading)
    {
        <AppLoadingState Message="Checking database status..." />
    }
    else if (_errorMessage != null)
    {
        <div class="alert alert-danger">
            <i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage
            <button class="btn btn-sm btn-outline-light ms-2" @onclick="LoadStatusAsync">Retry</button>
        </div>
    }
    else if (_status != null)
    {
        <!-- Connection Status -->
        <div class="row g-3 mb-4">
            <div class="col-md-3">
                <AppCard>
                    <ChildContent>
                        <div class="text-muted small">Connection</div>
                        <div class="d-flex align-items-center gap-2 mt-1">
                            @if (_status.CanConnect)
                            {
                                <i class="bi bi-check-circle-fill text-success"></i>
                                <span class="fw-bold text-success">Connected</span>
                            }
                            else
                            {
                                <i class="bi bi-x-circle-fill text-danger"></i>
                                <span class="fw-bold text-danger">Disconnected</span>
                            }
                        </div>
                    </ChildContent>
                </AppCard>
            </div>
            <div class="col-md-3">
                <AppCard>
                    <ChildContent>
                        <div class="text-muted small">Database</div>
                        <div class="fw-bold mt-1">@(_status.DatabaseName ?? "N/A")</div>
                    </ChildContent>
                </AppCard>
            </div>
            <div class="col-md-3">
                <AppCard>
                    <ChildContent>
                        <div class="text-muted small">Provider</div>
                        <div class="fw-bold mt-1">@FormatProvider(_status.ProviderName)</div>
                    </ChildContent>
                </AppCard>
            </div>
            <div class="col-md-3">
                <AppCard>
                    <ChildContent>
                        <div class="text-muted small">Migration Status</div>
                        <div class="d-flex align-items-center gap-2 mt-1">
                            @if (_status.IsUpToDate)
                            {
                                <AppBadge Status="done" Label="Up to Date" />
                            }
                            else
                            {
                                <AppBadge Status="stuck" Label="@($"{_status.PendingMigrations.Count} Pending")" />
                            }
                        </div>
                    </ChildContent>
                </AppCard>
            </div>
        </div>

        <!-- Pending Migrations -->
        @if (_status.PendingMigrations.Count > 0)
        {
            <AppCard CssClass="mb-4">
                <ChildContent>
                    <div class="d-flex justify-content-between align-items-center mb-3">
                        <h6 class="mb-0">
                            <i class="bi bi-exclamation-triangle text-warning me-2"></i>
                            Pending Migrations (@_status.PendingMigrations.Count)
                        </h6>
                        <button class="btn btn-sm app-btn-primary" @onclick="ApplyMigrationsAsync" disabled="@_isApplying">
                            @if (_isApplying)
                            {
                                <span class="spinner-border spinner-border-sm me-1"></span> <span>Applying...</span>
                            }
                            else
                            {
                                <i class="bi bi-play-fill me-1"></i> <span>Apply Migrations</span>
                            }
                        </button>
                    </div>

                    @if (_migrationResult != null)
                    {
                        @if (_migrationResult.Success)
                        {
                            <div class="alert alert-success mb-3">
                                <i class="bi bi-check-circle me-2"></i>
                                Successfully applied @_migrationResult.AppliedMigrations.Count migration(s).
                            </div>
                        }
                        else
                        {
                            <div class="alert alert-danger mb-3">
                                <i class="bi bi-x-circle me-2"></i>
                                Migration failed: @_migrationResult.Error
                            </div>
                        }
                    }

                    <div class="table-responsive">
                        <table class="table table-sm table-hover align-middle mb-0">
                            <thead>
                                <tr>
                                    <th style="width: 40px;">#</th>
                                    <th>Migration Name</th>
                                    <th style="width: 100px;">Status</th>
                                </tr>
                            </thead>
                            <tbody>
                                @{ var pendingIndex = 1; }
                                @foreach (var migration in _status.PendingMigrations)
                                {
                                    <tr>
                                        <td class="text-muted">@pendingIndex</td>
                                        <td><code>@migration</code></td>
                                        <td><AppBadge Status="stuck" Label="Pending" /></td>
                                    </tr>
                                    pendingIndex++;
                                }
                            </tbody>
                        </table>
                    </div>
                </ChildContent>
            </AppCard>
        }

        <!-- Applied Migrations -->
        <AppCard>
            <ChildContent>
                <div class="d-flex justify-content-between align-items-center mb-3">
                    <h6 class="mb-0">
                        <i class="bi bi-check-circle text-success me-2"></i>
                        Applied Migrations (@_status.AppliedMigrations.Count)
                    </h6>
                    <button class="btn btn-sm btn-outline-secondary" @onclick="LoadStatusAsync">
                        <i class="bi bi-arrow-clockwise me-1"></i> Refresh
                    </button>
                </div>

                @if (_status.AppliedMigrations.Count == 0)
                {
                    <p class="text-muted mb-0">No migrations have been applied yet.</p>
                }
                else
                {
                    <div class="table-responsive">
                        <table class="table table-sm table-hover align-middle mb-0">
                            <thead>
                                <tr>
                                    <th style="width: 40px;">#</th>
                                    <th>Migration Name</th>
                                    <th style="width: 100px;">Status</th>
                                </tr>
                            </thead>
                            <tbody>
                                @{ var appliedIndex = _status.AppliedMigrations.Count; }
                                @foreach (var migration in _status.AppliedMigrations.Reverse())
                                {
                                    <tr>
                                        <td class="text-muted">@appliedIndex</td>
                                        <td><code>@migration</code></td>
                                        <td><AppBadge Status="done" Label="Applied" /></td>
                                    </tr>
                                    appliedIndex--;
                                }
                            </tbody>
                        </table>
                    </div>
                }
            </ChildContent>
        </AppCard>
    }
</div>

@code {
    private DatabaseStatusDto? _status;
    private MigrationResultDto? _migrationResult;
    private bool _isLoading = true;
    private bool _isApplying;
    private string? _errorMessage;

    protected override async Task OnInitializedAsync()
    {
        await LoadStatusAsync();
    }

    private async Task LoadStatusAsync()
    {
        try
        {
            _isLoading = true;
            _errorMessage = null;
            _migrationResult = null;
            StateHasChanged();

            _status = await ApiClient.GetDatabaseStatusAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load database status");
            _errorMessage = $"Failed to load database status: {ex.Message}";
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    private async Task ApplyMigrationsAsync()
    {
        try
        {
            _isApplying = true;
            _migrationResult = null;
            StateHasChanged();

            _migrationResult = await ApiClient.ApplyMigrationsAsync();

            if (_migrationResult.Success)
            {
                await LoadStatusAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to apply migrations");
            _migrationResult = new MigrationResultDto
            {
                Success = false,
                Error = ex.Message,
                AppliedMigrations = []
            };
        }
        finally
        {
            _isApplying = false;
            StateHasChanged();
        }
    }

    private static string FormatProvider(string? provider)
    {
        if (string.IsNullOrEmpty(provider)) return "N/A";
        return provider switch
        {
            _ when provider.Contains("SqlServer") => "SQL Server",
            _ when provider.Contains("Sqlite") => "SQLite",
            _ when provider.Contains("Npgsql") || provider.Contains("PostgreSQL") => "PostgreSQL",
            _ when provider.Contains("MySql") => "MySQL",
            _ => provider
        };
    }
}
```

## SystemAdmin/SharePointTab.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<SharePointTab> Logger

<div class="mt-3">
    @if (_isLoading)
    {
        <AppLoadingState Message="Loading SharePoint Embedded diagnostics..." />
    }
    else if (_errorMessage != null)
    {
        <div class="alert alert-danger">
            <i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage
            <button class="btn btn-sm btn-outline-light ms-2" @onclick="LoadDiagnosticsAsync">Retry</button>
        </div>
    }
    else if (_diagnostics != null)
    {
        <!-- Configuration -->
        <div class="row g-3 mb-4">
            <div class="col-md-4">
                <AppCard>
                    <ChildContent>
                        <div class="text-muted small">Tenant ID</div>
                        <div class="fw-bold mt-1"><code class="small">@_diagnostics.TenantId</code></div>
                    </ChildContent>
                </AppCard>
            </div>
            <div class="col-md-4">
                <AppCard>
                    <ChildContent>
                        <div class="text-muted small">Client ID</div>
                        <div class="fw-bold mt-1"><code class="small">@_diagnostics.ClientId</code></div>
                    </ChildContent>
                </AppCard>
            </div>
            <div class="col-md-4">
                <AppCard>
                    <ChildContent>
                        <div class="text-muted small">Container Type ID</div>
                        <div class="fw-bold mt-1"><code class="small">@_diagnostics.ContainerTypeId</code></div>
                    </ChildContent>
                </AppCard>
            </div>
        </div>

        <!-- Token Roles -->
        @if (_diagnostics.TokenRoles?.Count > 0)
        {
            <div class="mb-4">
                <h6 class="text-muted mb-2">
                    <i class="bi bi-key me-1"></i> Token Permissions
                    <span class="text-muted fw-normal small ms-1">(@_diagnostics.TokenAppName)</span>
                </h6>
                <div class="d-flex flex-wrap gap-1">
                    @foreach (var role in _diagnostics.TokenRoles)
                    {
                        <span class="badge bg-secondary bg-opacity-25 text-body">@role</span>
                    }
                </div>
            </div>
        }

        <!-- Registration -->
        <AppCard CssClass="mb-4">
            <ChildContent>
                <div class="d-flex justify-content-between align-items-center mb-3">
                    <h6 class="mb-0">
                        <i class="bi bi-shield-check me-2"></i>
                        Container Type Registration
                    </h6>
                    <button class="btn btn-sm app-btn-primary" @onclick="RegisterAsync" disabled="@_isRegistering">
                        @if (_isRegistering)
                        {
                            <span class="spinner-border spinner-border-sm me-1"></span> <span>Registering...</span>
                        }
                        else
                        {
                            <i class="bi bi-box-arrow-in-right me-1"></i> <span>Register Container Type</span>
                        }
                    </button>
                </div>

                <p class="text-muted small mb-3">
                    Registers the container type in the consuming tenant. This must be done once before
                    containers can be created. Safe to call multiple times.
                </p>

                <div class="d-flex align-items-center gap-2">
                    @if (_diagnostics.RegisterContainerTypeOk)
                    {
                        <i class="bi bi-check-circle-fill text-success"></i>
                        <span class="text-success fw-bold">Registered</span>
                        <span class="text-muted small">(@_diagnostics.RegisterContainerTypeStatus)</span>
                    }
                    else if (_diagnostics.RegisterContainerTypeError != null)
                    {
                        <i class="bi bi-x-circle-fill text-danger"></i>
                        <span class="text-danger fw-bold">Not Registered</span>
                    }
                    else
                    {
                        <i class="bi bi-dash-circle text-muted"></i>
                        <span class="text-muted">Unknown</span>
                    }
                </div>

                @if (_registrationResult != null)
                {
                    <div class="mt-3">
                        @if (_registrationResult.Ok)
                        {
                            <div class="alert alert-success mb-0">
                                <i class="bi bi-check-circle me-2"></i>
                                Container type registered successfully! (@_registrationResult.Status)
                            </div>
                        }
                        else
                        {
                            <div class="alert alert-danger mb-0">
                                <i class="bi bi-x-circle me-2"></i>
                                Registration failed: @_registrationResult.Error
                            </div>
                        }
                    </div>
                }

                @if (!string.IsNullOrEmpty(_diagnostics.RegisterContainerTypeError) && _registrationResult == null)
                {
                    <div class="mt-2">
                        <details>
                            <summary class="text-muted small">Error details</summary>
                            <pre class="mt-1 p-2 bg-light rounded small mb-0" style="white-space: pre-wrap;">@_diagnostics.RegisterContainerTypeError</pre>
                        </details>
                    </div>
                }
            </ChildContent>
        </AppCard>

        <!-- Connectivity Tests -->
        <AppCard CssClass="mb-4">
            <ChildContent>
                <div class="d-flex justify-content-between align-items-center mb-3">
                    <h6 class="mb-0">
                        <i class="bi bi-activity me-2"></i>
                        Connectivity Tests
                    </h6>
                    <button class="btn btn-sm btn-outline-secondary" @onclick="LoadDiagnosticsAsync">
                        <i class="bi bi-arrow-clockwise me-1"></i> Run Diagnostics
                    </button>
                </div>

                <div class="table-responsive">
                    <table class="table table-sm align-middle mb-0">
                        <thead>
                            <tr>
                                <th style="width: 40px;"></th>
                                <th>Test</th>
                                <th>Details</th>
                                <th style="width: 100px;">Status</th>
                            </tr>
                        </thead>
                        <tbody>
                            <!-- Auth Test -->
                            <tr>
                                <td>
                                    @if (_diagnostics.AuthenticationOk)
                                    {
                                        <i class="bi bi-check-circle-fill text-success"></i>
                                    }
                                    else
                                    {
                                        <i class="bi bi-exclamation-circle-fill text-warning"></i>
                                    }
                                </td>
                                <td>Graph Authentication</td>
                                <td class="text-muted small">
                                    @if (_diagnostics.AuthenticationOk)
                                    {
                                        @(_diagnostics.TenantDisplayName ?? "OK")
                                    }
                                    else
                                    {
                                        @_diagnostics.AuthenticationError
                                    }
                                </td>
                                <td>
                                    <AppBadge Status="@(_diagnostics.AuthenticationOk ? "done" : "stuck")"
                                                Label="@(_diagnostics.AuthenticationOk ? "Pass" : "Warn")" />
                                </td>
                            </tr>

                            <!-- Registration Test -->
                            <tr>
                                <td>
                                    @if (_diagnostics.RegisterContainerTypeOk)
                                    {
                                        <i class="bi bi-check-circle-fill text-success"></i>
                                    }
                                    else
                                    {
                                        <i class="bi bi-x-circle-fill text-danger"></i>
                                    }
                                </td>
                                <td>Container Type Registration</td>
                                <td class="text-muted small">
                                    @if (_diagnostics.RegisterContainerTypeOk)
                                    {
                                        @($"Registered ({_diagnostics.RegisterContainerTypeStatus})")
                                    }
                                    else
                                    {
                                        @($"Failed: {_diagnostics.RegisterContainerTypeStatus}")
                                    }
                                </td>
                                <td>
                                    <AppBadge Status="@(_diagnostics.RegisterContainerTypeOk ? "done" : "stuck")"
                                                Label="@(_diagnostics.RegisterContainerTypeOk ? "Pass" : "Fail")" />
                                </td>
                            </tr>

                            <!-- List Containers Test -->
                            <tr>
                                <td>
                                    @if (_diagnostics.ListContainersOk)
                                    {
                                        <i class="bi bi-check-circle-fill text-success"></i>
                                    }
                                    else
                                    {
                                        <i class="bi bi-x-circle-fill text-danger"></i>
                                    }
                                </td>
                                <td>List Containers</td>
                                <td class="text-muted small">
                                    @if (_diagnostics.ListContainersOk)
                                    {
                                        @($"{_diagnostics.ExistingContainerCount} container(s) found")
                                    }
                                    else
                                    {
                                        @_diagnostics.ListContainersError
                                    }
                                </td>
                                <td>
                                    <AppBadge Status="@(_diagnostics.ListContainersOk ? "done" : "stuck")"
                                                Label="@(_diagnostics.ListContainersOk ? "Pass" : "Fail")" />
                                </td>
                            </tr>

                            <!-- Create Container Test -->
                            <tr>
                                <td>
                                    @if (_diagnostics.CreateContainerOk)
                                    {
                                        <i class="bi bi-check-circle-fill text-success"></i>
                                    }
                                    else
                                    {
                                        <i class="bi bi-x-circle-fill text-danger"></i>
                                    }
                                </td>
                                <td>Create Container</td>
                                <td class="text-muted small">
                                    @if (_diagnostics.CreateContainerOk)
                                    {
                                        var cleaned = _diagnostics.TestContainerCleaned ? "cleaned up" : "NOT cleaned up";
                                        @($"Created test container ({cleaned})")
                                    }
                                    else
                                    {
                                        @_diagnostics.CreateContainerError
                                    }
                                </td>
                                <td>
                                    <AppBadge Status="@(_diagnostics.CreateContainerOk ? "done" : "stuck")"
                                                Label="@(_diagnostics.CreateContainerOk ? "Pass" : "Fail")" />
                                </td>
                            </tr>
                        </tbody>
                    </table>
                </div>
            </ChildContent>
        </AppCard>
    }
</div>

@code {
    private SpeDiagnosticDto? _diagnostics;
    private SpeRegistrationResultDto? _registrationResult;
    private bool _isLoading = true;
    private bool _isRegistering;
    private string? _errorMessage;

    protected override async Task OnInitializedAsync()
    {
        await LoadDiagnosticsAsync();
    }

    private async Task LoadDiagnosticsAsync()
    {
        try
        {
            _isLoading = true;
            _errorMessage = null;
            _registrationResult = null;
            StateHasChanged();

            _diagnostics = await ApiClient.GetSpeDiagnosticsAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load SPE diagnostics");
            _errorMessage = $"Failed to load diagnostics: {ex.Message}";
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    private async Task RegisterAsync()
    {
        try
        {
            _isRegistering = true;
            _registrationResult = null;
            StateHasChanged();

            _registrationResult = await ApiClient.RegisterSpeContainerTypeAsync();

            if (_registrationResult.Ok)
            {
                // Refresh diagnostics to show updated status
                await LoadDiagnosticsAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to register container type");
            _registrationResult = new SpeRegistrationResultDto
            {
                Ok = false,
                Error = ex.Message
            };
        }
        finally
        {
            _isRegistering = false;
            StateHasChanged();
        }
    }
}
```

## SystemAdmin/UsersTab.razor

```razor
@inject IApiClient ApiClient
@inject ILogger<UsersTab> Logger

<div class="mt-3">
    @if (_isLoading)
    {
        <AppLoadingState Message="Loading users..." />
    }
    else if (_errorMessage != null)
    {
        <div class="alert alert-danger">
            <i class="bi bi-exclamation-triangle me-2"></i>@_errorMessage
            <button class="btn btn-sm btn-outline-light ms-2" @onclick="LoadUsersAsync">Retry</button>
        </div>
    }
    else
    {
        <!-- Header Row -->
        <div class="d-flex justify-content-between align-items-center mb-3">
            <div>
                <h5 class="mb-0">Users Management</h5>
            </div>
            <div class="d-flex gap-2">
                <button class="btn btn-sm btn-outline-secondary" @onclick="LoadUsersAsync">
                    <i class="bi bi-arrow-clockwise me-1"></i> Refresh
                </button>
                <button class="btn btn-sm app-btn-primary" @onclick="SyncEntraUsersAsync" disabled="@_isSyncing">
                    @if (_isSyncing)
                    {
                        <span class="spinner-border spinner-border-sm me-1"></span> <span>Syncing...</span>
                    }
                    else
                    {
                        <i class="bi bi-cloud-download me-1"></i> <span>Sync from Entra</span>
                    }
                </button>
            </div>
        </div>

        <!-- Sync Result Toast -->
        @if (_syncResult != null)
        {
            <div class="alert @(_syncResult.Errors.Count > 0 ? "alert-warning" : "alert-success") alert-dismissible mb-3">
                <i class="bi @(_syncResult.Errors.Count > 0 ? "bi-exclamation-triangle" : "bi-check-circle") me-2"></i>
                Entra sync complete: @_syncResult.Added added, @_syncResult.Updated updated, @_syncResult.Unchanged unchanged.
                @if (_syncResult.Errors.Count > 0)
                {
                    <br /><small class="text-muted">@string.Join("; ", _syncResult.Errors)</small>
                }
                <button type="button" class="btn-close" @onclick="() => _syncResult = null"></button>
            </div>
        }

        <!-- Filter Toggle -->
        <div class="btn-group btn-group-sm mb-3" role="group">
            <button class="btn @(_filter == "all" ? "btn-primary" : "btn-outline-primary")" @onclick='() => SetFilter("all")'>
                All Users (@_allUsers.Count)
            </button>
            <button class="btn @(_filter == "system" ? "btn-primary" : "btn-outline-primary")" @onclick='() => SetFilter("system")'>
                System Users (@_allUsers.Count(u => !u.IsExternal))
            </button>
            <button class="btn @(_filter == "external" ? "btn-primary" : "btn-outline-primary")" @onclick='() => SetFilter("external")'>
                External Attorneys (@_allUsers.Count(u => u.IsExternal))
            </button>
        </div>

        <!-- Search -->
        <div class="mb-3">
            <input type="text" class="form-control form-control-sm" placeholder="Search by name or email..."
                   @bind="_searchText" @bind:event="oninput" style="max-width: 400px;" />
        </div>

        <!-- System Users Section -->
        @if (_filter == "all" || _filter == "system")
        {
            <AppCard CssClass="mb-4">
                <ChildContent>
                    <h6 class="mb-3">
                        <i class="bi bi-people me-2"></i>System Users (from Entra ID)
                    </h6>

                    @{ var systemUsers = GetFilteredUsers(false); }
                    @if (systemUsers.Count == 0)
                    {
                        <AppEmptyState Icon="people" Title="No system users"
                                         Message="Sync from Entra ID to import users." />
                    }
                    else
                    {
                        <div class="table-responsive">
                            <table class="table table-sm table-hover align-middle mb-0">
                                <thead>
                                    <tr>
                                        <th>Name</th>
                                        <th>Email</th>
                                        <th>Role</th>
                                        <th style="width: 80px;">Active</th>
                                        <th style="width: 120px;">Last Login</th>
                                        <th style="width: 50px;"></th>
                                    </tr>
                                </thead>
                                <tbody>
                                    @foreach (var user in systemUsers)
                                    {
                                        <tr>
                                            <td>@user.DisplayName</td>
                                            <td class="text-muted small">@user.Email</td>
                                            <td>
                                                @if (user.Roles.Count > 0)
                                                {
                                                    <AppBadge Status="@GetRoleBadgeStatus(user.Roles[0])" Label="@user.Roles[0]" />
                                                }
                                                else
                                                {
                                                    <span class="text-muted small">No role</span>
                                                }
                                            </td>
                                            <td>
                                                @if (user.IsActive)
                                                {
                                                    <i class="bi bi-check-circle-fill text-success"></i>
                                                }
                                                else
                                                {
                                                    <i class="bi bi-x-circle-fill text-muted"></i>
                                                }
                                            </td>
                                            <td class="text-muted small">
                                                @(user.LastLoginUtc?.ToString("MMM d, yyyy") ?? "Never")
                                            </td>
                                            <td>
                                                <button class="btn btn-sm btn-link p-0" title="Edit" @onclick="() => OpenEditSystemUser(user)">
                                                    <i class="bi bi-pencil"></i>
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
        }

        <!-- External Attorneys Section -->
        @if (_filter == "all" || _filter == "external")
        {
            <AppCard CssClass="mb-4">
                <ChildContent>
                    <div class="d-flex justify-content-between align-items-center mb-3">
                        <h6 class="mb-0">
                            <i class="bi bi-briefcase me-2"></i>External Attorneys
                        </h6>
                        <button class="btn btn-sm app-btn-primary" @onclick="OpenAddExternal">
                            <i class="bi bi-plus-lg me-1"></i> Add Attorney
                        </button>
                    </div>

                    @{ var externalUsers = GetFilteredUsers(true); }
                    @if (externalUsers.Count == 0)
                    {
                        <AppEmptyState Icon="briefcase" Title="No external attorneys"
                                         Message="Add external attorneys for matter assignment." />
                    }
                    else
                    {
                        <div class="table-responsive">
                            <table class="table table-sm table-hover align-middle mb-0">
                                <thead>
                                    <tr>
                                        <th>Name</th>
                                        <th>Email</th>
                                        <th>Firm</th>
                                        <th>Bar #</th>
                                        <th>Phone</th>
                                        <th style="width: 80px;">Active</th>
                                        <th style="width: 80px;"></th>
                                    </tr>
                                </thead>
                                <tbody>
                                    @foreach (var user in externalUsers)
                                    {
                                        <tr>
                                            <td>@user.DisplayName</td>
                                            <td class="text-muted small">
                                                @(user.Email.EndsWith("@external.local") ? "" : user.Email)
                                            </td>
                                            <td class="small">@(user.FirmName ?? "")</td>
                                            <td class="small">@(user.BarNumber ?? "")</td>
                                            <td class="small">@(user.Phone ?? "")</td>
                                            <td>
                                                @if (user.IsActive)
                                                {
                                                    <i class="bi bi-check-circle-fill text-success"></i>
                                                }
                                                else
                                                {
                                                    <i class="bi bi-x-circle-fill text-muted"></i>
                                                }
                                            </td>
                                            <td>
                                                <button class="btn btn-sm btn-link p-0 me-2" title="Edit" @onclick="() => OpenEditExternal(user)">
                                                    <i class="bi bi-pencil"></i>
                                                </button>
                                                <button class="btn btn-sm btn-link p-0 text-danger" title="Deactivate" @onclick="() => ConfirmDeactivate(user)">
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
        }
    }
</div>

<!-- Edit System User Modal -->
<AppModal @bind-IsVisible="_showEditSystemModal" Title="Edit System User" Size="md">
    <ChildContent>
        @if (_editingUser != null)
        {
            <div class="mb-3">
                <label class="form-label fw-bold">@_editingUser.DisplayName</label>
                <div class="text-muted small">@_editingUser.Email</div>
            </div>
            <div class="mb-3">
                <label class="form-label">Role</label>
                <select class="form-select form-select-sm" @bind="_editRoleName">
                    <option value="">-- No Role --</option>
                    @foreach (var role in _availableRoles)
                    {
                        <option value="@role">@role</option>
                    }
                </select>
            </div>
            <div class="mb-3">
                <div class="form-check form-switch">
                    <input class="form-check-input" type="checkbox" @bind="_editIsActive" id="editActiveSwitch" />
                    <label class="form-check-label" for="editActiveSwitch">Active</label>
                </div>
            </div>
        }
    </ChildContent>
    <Footer>
        <button class="btn btn-sm btn-secondary" @onclick="() => _showEditSystemModal = false">Cancel</button>
        <button class="btn btn-sm app-btn-primary" @onclick="SaveSystemUserAsync" disabled="@_isSaving">
            @if (_isSaving)
            {
                <span class="spinner-border spinner-border-sm me-1"></span>
            }
            Save
        </button>
    </Footer>
</AppModal>

<!-- Add/Edit External Attorney Modal -->
<AppModal @bind-IsVisible="_showExternalModal" Title="@(_isEditingExternal ? "Edit External Attorney" : "Add External Attorney")" Size="lg">
    <ChildContent>
        <div class="row g-3">
            <div class="col-md-6">
                <label class="form-label">First Name <span class="text-danger">*</span></label>
                <input type="text" class="form-control form-control-sm" @bind="_extFirstName" />
            </div>
            <div class="col-md-6">
                <label class="form-label">Last Name <span class="text-danger">*</span></label>
                <input type="text" class="form-control form-control-sm" @bind="_extLastName" />
            </div>
            <div class="col-md-6">
                <label class="form-label">Email</label>
                <input type="email" class="form-control form-control-sm" @bind="_extEmail" />
            </div>
            <div class="col-md-6">
                <label class="form-label">Phone</label>
                <input type="text" class="form-control form-control-sm" @bind="_extPhone" />
            </div>
            <div class="col-md-6">
                <label class="form-label">Firm / Organization</label>
                <input type="text" class="form-control form-control-sm" @bind="_extFirmName" />
            </div>
            <div class="col-md-6">
                <label class="form-label">Bar Number</label>
                <input type="text" class="form-control form-control-sm" @bind="_extBarNumber" />
            </div>
            <div class="col-12">
                <label class="form-label">Notes</label>
                <textarea class="form-control form-control-sm" rows="3" @bind="_extNotes"></textarea>
            </div>
        </div>
    </ChildContent>
    <Footer>
        <button class="btn btn-sm btn-secondary" @onclick="() => _showExternalModal = false">Cancel</button>
        <button class="btn btn-sm app-btn-primary" @onclick="SaveExternalAttorneyAsync" disabled="@_isSaving">
            @if (_isSaving)
            {
                <span class="spinner-border spinner-border-sm me-1"></span>
            }
            @(_isEditingExternal ? "Save Changes" : "Add Attorney")
        </button>
    </Footer>
</AppModal>

<!-- Deactivate Confirmation Modal -->
<AppModal @bind-IsVisible="_showDeactivateModal" Title="Deactivate External Attorney" Size="sm">
    <ChildContent>
        @if (_deactivatingUser != null)
        {
            <p>Are you sure you want to deactivate <strong>@_deactivatingUser.DisplayName</strong>?</p>
            <p class="text-muted small">This will make them inactive but their records will be preserved.</p>
        }
    </ChildContent>
    <Footer>
        <button class="btn btn-sm btn-secondary" @onclick="() => _showDeactivateModal = false">Cancel</button>
        <button class="btn btn-sm btn-danger" @onclick="DeactivateUserAsync" disabled="@_isSaving">
            @if (_isSaving)
            {
                <span class="spinner-border spinner-border-sm me-1"></span>
            }
            Deactivate
        </button>
    </Footer>
</AppModal>

@code {
    // Data
    private List<UserDetailDto> _allUsers = [];
    private List<string> _availableRoles = [];
    private bool _isLoading = true;
    private string? _errorMessage;
    private string _filter = "all";
    private string _searchText = "";

    // Sync
    private bool _isSyncing;
    private EntraSyncResultDto? _syncResult;

    // Edit System User
    private bool _showEditSystemModal;
    private UserDetailDto? _editingUser;
    private string _editRoleName = "";
    private bool _editIsActive = true;
    private bool _isSaving;

    // Add/Edit External
    private bool _showExternalModal;
    private bool _isEditingExternal;
    private Guid? _editingExternalId;
    private string _extFirstName = "";
    private string _extLastName = "";
    private string _extEmail = "";
    private string _extPhone = "";
    private string _extFirmName = "";
    private string _extBarNumber = "";
    private string _extNotes = "";

    // Deactivate
    private bool _showDeactivateModal;
    private UserDetailDto? _deactivatingUser;

    protected override async Task OnInitializedAsync()
    {
        await LoadUsersAsync();
    }

    private async Task LoadUsersAsync()
    {
        try
        {
            _isLoading = true;
            _errorMessage = null;
            StateHasChanged();

            var usersTask = ApiClient.GetAllUsersAsync();
            var rolesTask = ApiClient.GetRolesAsync();
            await Task.WhenAll(usersTask, rolesTask);

            _allUsers = usersTask.Result.ToList();
            _availableRoles = rolesTask.Result.ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to load users");
            _errorMessage = $"Failed to load users: {ex.Message}";
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    private void SetFilter(string filter)
    {
        _filter = filter;
    }

    private List<UserDetailDto> GetFilteredUsers(bool isExternal)
    {
        var users = _allUsers.Where(u => u.IsExternal == isExternal);

        if (!string.IsNullOrWhiteSpace(_searchText))
        {
            var search = _searchText.Trim();
            users = users.Where(u =>
                u.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                u.Email.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (u.FirmName != null && u.FirmName.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        return users.ToList();
    }

    // ==================== Entra Sync ====================

    private async Task SyncEntraUsersAsync()
    {
        try
        {
            _isSyncing = true;
            _syncResult = null;
            StateHasChanged();

            _syncResult = await ApiClient.SyncEntraUsersAsync();
            await LoadUsersAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to sync Entra users");
            _syncResult = new EntraSyncResultDto { Errors = [$"Sync failed: {ex.Message}"] };
        }
        finally
        {
            _isSyncing = false;
            StateHasChanged();
        }
    }

    // ==================== Edit System User ====================

    private void OpenEditSystemUser(UserDetailDto user)
    {
        _editingUser = user;
        _editRoleName = user.Roles.Count > 0 ? user.Roles[0] : "";
        _editIsActive = user.IsActive;
        _showEditSystemModal = true;
    }

    private async Task SaveSystemUserAsync()
    {
        if (_editingUser == null) return;

        try
        {
            _isSaving = true;
            StateHasChanged();

            await ApiClient.UpdateUserAsync(_editingUser.Id, new UpdateUserCommand
            {
                RoleName = _editRoleName,
                IsActive = _editIsActive
            });

            _showEditSystemModal = false;
            await LoadUsersAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to update user");
        }
        finally
        {
            _isSaving = false;
            StateHasChanged();
        }
    }

    // ==================== External Attorney ====================

    private void OpenAddExternal()
    {
        _isEditingExternal = false;
        _editingExternalId = null;
        _extFirstName = "";
        _extLastName = "";
        _extEmail = "";
        _extPhone = "";
        _extFirmName = "";
        _extBarNumber = "";
        _extNotes = "";
        _showExternalModal = true;
    }

    private void OpenEditExternal(UserDetailDto user)
    {
        _isEditingExternal = true;
        _editingExternalId = user.Id;
        _extFirstName = user.FirstName ?? "";
        _extLastName = user.LastName ?? "";
        _extEmail = user.Email.EndsWith("@external.local") ? "" : user.Email;
        _extPhone = user.Phone ?? "";
        _extFirmName = user.FirmName ?? "";
        _extBarNumber = user.BarNumber ?? "";
        _extNotes = user.Notes ?? "";
        _showExternalModal = true;
    }

    private async Task SaveExternalAttorneyAsync()
    {
        if (string.IsNullOrWhiteSpace(_extFirstName) || string.IsNullOrWhiteSpace(_extLastName))
            return;

        try
        {
            _isSaving = true;
            StateHasChanged();

            if (_isEditingExternal && _editingExternalId.HasValue)
            {
                await ApiClient.UpdateUserAsync(_editingExternalId.Value, new UpdateUserCommand
                {
                    FirstName = _extFirstName,
                    LastName = _extLastName,
                    Email = string.IsNullOrWhiteSpace(_extEmail) ? null : _extEmail,
                    FirmName = string.IsNullOrWhiteSpace(_extFirmName) ? null : _extFirmName,
                    BarNumber = string.IsNullOrWhiteSpace(_extBarNumber) ? null : _extBarNumber,
                    Phone = string.IsNullOrWhiteSpace(_extPhone) ? null : _extPhone,
                    Notes = string.IsNullOrWhiteSpace(_extNotes) ? null : _extNotes
                });
            }
            else
            {
                await ApiClient.CreateExternalAttorneyAsync(new CreateExternalAttorneyCommand
                {
                    FirstName = _extFirstName,
                    LastName = _extLastName,
                    Email = string.IsNullOrWhiteSpace(_extEmail) ? null : _extEmail,
                    FirmName = string.IsNullOrWhiteSpace(_extFirmName) ? null : _extFirmName,
                    BarNumber = string.IsNullOrWhiteSpace(_extBarNumber) ? null : _extBarNumber,
                    Phone = string.IsNullOrWhiteSpace(_extPhone) ? null : _extPhone,
                    Notes = string.IsNullOrWhiteSpace(_extNotes) ? null : _extNotes
                });
            }

            _showExternalModal = false;
            await LoadUsersAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to save external attorney");
        }
        finally
        {
            _isSaving = false;
            StateHasChanged();
        }
    }

    // ==================== Deactivate ====================

    private void ConfirmDeactivate(UserDetailDto user)
    {
        _deactivatingUser = user;
        _showDeactivateModal = true;
    }

    private async Task DeactivateUserAsync()
    {
        if (_deactivatingUser == null) return;

        try
        {
            _isSaving = true;
            StateHasChanged();

            await ApiClient.DeactivateUserAsync(_deactivatingUser.Id);

            _showDeactivateModal = false;
            await LoadUsersAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to deactivate user");
        }
        finally
        {
            _isSaving = false;
            StateHasChanged();
        }
    }

    // ==================== Helpers ====================

    private static string GetRoleBadgeStatus(string roleName) => roleName switch
    {
        "Admin" => "stuck",
        "Attorney" => "active",
        "Paralegal" => "active",
        "LegalAssistant" => "active",
        "ReadOnly" => "done",
        _ => "done"
    };
}
```
