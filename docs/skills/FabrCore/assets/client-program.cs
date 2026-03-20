using FabrCore.Client;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.AddFabrCoreClient();
builder.Services.AddFabrCoreClientComponents();

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// UseFabrCoreClient returns IHost (NOT awaitable)
app.UseFabrCoreClient();
app.Run();
