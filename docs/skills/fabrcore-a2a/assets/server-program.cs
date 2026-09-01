// A FabrCore server publishing its agents over A2A.
//
// A2A is part of FabrCore.Host: AddFabrCoreServer registers it and UseFabrCoreServer maps it,
// both gated on A2A:Enabled. There is nothing else to wire. Settings live in the "A2A" section
// of fabrcore.json — see fabrcore-json-a2a.json.

using FabrCore.Host;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.AddFabrCoreServer();

// Behind a reverse proxy, either set A2A:PublicBaseUrl or let forwarded headers rewrite the
// request, so agent cards advertise the URL clients can actually reach.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseFabrCoreServer();

app.Run();
