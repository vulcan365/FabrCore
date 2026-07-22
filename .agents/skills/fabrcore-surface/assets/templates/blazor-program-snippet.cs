using FabrCore.Surface;

builder.AddFabrCoreSurfaceFromConfig("fabrcore-surface.json", "default");
builder.Services.AddFabrCoreSurfaceComponents();

// After var app = builder.Build():
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddFabrCoreSurfaceRoutes();

// The packaged /surface pages declare InteractiveServer themselves.
// Surface-only registration shows only the Chat navigation item.
// In Routes.razor, include the Surface RCL route assembly for enhanced navigation:
// <Router AppAssembly="typeof(Program).Assembly"
//         AdditionalAssemblies="[typeof(FabrCore.Surface.Components.SurfaceCommandCenter).Assembly]">
