# Blazor Integration

For all-in-one Host + Blazor apps, register Surface from one config definition:

```csharp
builder.AddFabrCoreSurfaceFromConfig("fabrcore-surface.json", "crm-demo");
builder.Services.AddFabrCoreSurfaceComponents();
```

This maps the selected definition into producer planning options and consumer validation/render policy.

For split client-only apps, register Surface in the Blazor/FabrCore app:

```csharp
builder.AddFabrCoreSurface(options =>
{
    options.MaxAdaptiveCardVersion = "1.6";
});

builder.Services.AddFabrCoreSurfaceComponents();
```

Useful policy options:

```csharp
builder.AddFabrCoreSurface(options =>
{
    options.MaxAdaptiveCardVersion = "1.6";
    options.MaxPayloadBytes = 64 * 1024;
    options.MaxDepth = 64;
    options.AllowHttpUrls = false;
    options.AllowAnyActionVerb = true;
    options.EnableDiagnostics = true;
});
```

Add the stylesheet:

```html
<link href="_content/FabrCore.Surface/surface.css" rel="stylesheet" />
```

Render the receiving surface:

```razor
<DynamicAgentSurface UserHandle="@userId" MaxItems="5" />
```

The component listens for `ui.render` messages with the Adaptive Card Surface media type, expands `card` + `data`, validates the result, and renders through the Adaptive Cards browser renderer.

`DynamicAgentSurface` loads `_content/FabrCore.Surface/adaptiveCardsSurface.js`. The module uses `window.AdaptiveCards` if the app has already loaded the Adaptive Cards renderer; otherwise it loads the renderer from the configured CDN fallback in that JS module.

Register a real `ISurfaceActionRegistry` in the app if Adaptive Card actions should perform trusted app-side work.

## Target Handles

The built-in `surface` agent can deliver `ui.render` directly to the intended user/client handle when the calling message includes:

```csharp
message.Args["targetHandle"] = "demo-user";
```

or:

```csharp
message.Args["surface:TargetHandle"] = "demo-user";
```

An envelope can also set:

```json
"metadata": {
  "targetHandle": "demo-user"
}
```
