# Dashboard Page

The main dashboard/home page showing summary statistics and recent activity.

## Home.razor

```razor
@page "/"

<PageTitle>Dashboard - App</PageTitle>

<div class="d-flex align-items-center justify-content-between mb-4">
    <div>
        <h1 class="app-page-title">Dashboard</h1>
        <p class="app-subtitle">Welcome to App</p>
    </div>
</div>

<div class="row g-4 mb-4">
    <div class="col-sm-6 col-lg-3">
        <div class="stat-card">
            <div class="stat-card-icon" style="background-color: var(--app-color-primary-soft); color: var(--app-color-primary);">
                <i class="bi bi-folder"></i>
            </div>
            <div class="stat-card-body">
                <div class="stat-card-value">0</div>
                <div class="stat-card-label">Active Matters</div>
            </div>
        </div>
    </div>
    <div class="col-sm-6 col-lg-3">
        <div class="stat-card">
            <div class="stat-card-icon" style="background-color: #ecfdf5; color: #059669;">
                <i class="bi bi-people"></i>
            </div>
            <div class="stat-card-body">
                <div class="stat-card-value">0</div>
                <div class="stat-card-label">Contacts</div>
            </div>
        </div>
    </div>
    <div class="col-sm-6 col-lg-3">
        <div class="stat-card">
            <div class="stat-card-icon" style="background-color: #fff7ed; color: #ea580c;">
                <i class="bi bi-check2-square"></i>
            </div>
            <div class="stat-card-body">
                <div class="stat-card-value">0</div>
                <div class="stat-card-label">Open Tasks</div>
            </div>
        </div>
    </div>
    <div class="col-sm-6 col-lg-3">
        <div class="stat-card">
            <div class="stat-card-icon" style="background-color: #fef2f2; color: #dc2626;">
                <i class="bi bi-calendar-event"></i>
            </div>
            <div class="stat-card-body">
                <div class="stat-card-value">0</div>
                <div class="stat-card-label">Upcoming Events</div>
            </div>
        </div>
    </div>
</div>

<div class="row g-4">
    <div class="col-lg-8">
        <div class="app-card">
            <div class="card-header">Recent Activity</div>
            <div class="card-body">
                <div class="app-empty-state">
                    <i class="bi bi-clock-history"></i>
                    <h5>No Recent Activity</h5>
                    <p>Activity from your matters will appear here.</p>
                </div>
            </div>
        </div>
    </div>
    <div class="col-lg-4">
        <div class="app-card">
            <div class="card-header">Upcoming Deadlines</div>
            <div class="card-body">
                <div class="app-empty-state">
                    <i class="bi bi-calendar3"></i>
                    <h5>No Upcoming Deadlines</h5>
                    <p>Deadlines from your matters will appear here.</p>
                </div>
            </div>
        </div>
    </div>
</div>
```
