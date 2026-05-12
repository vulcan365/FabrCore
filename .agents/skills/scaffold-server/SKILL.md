---
name: scaffold-server
description: Scaffold a new FabrCore server project — Orleans silo infrastructure for hosting and scaling AI agents with REST API and WebSocket endpoints.
argument-hint: [ProjectName]
---

# Scaffold a FabrCore Server

Create a new FabrCore server project that provides the Orleans silo infrastructure for hosting and scaling AI agents.

## Arguments

The project name is provided as: `$ARGUMENTS`

If no project name is provided, ask the user for one (e.g., `MyApp.Server`).

## What to Generate

Create the following files inside a `<ProjectName>/` directory:

### 1. `<ProjectName>.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FabrCore.Host" Version="*" />
  </ItemGroup>
</Project>
```

If the user has an agents class library project, add a `<ProjectReference>` to it so the server can load agent types.

### 2. `Program.cs`

```csharp
using FabrCore.Host;

var builder = WebApplication.CreateBuilder(args);

// Add FabrCore server with Orleans silo, REST API, and WebSocket support.
// AdditionalAssemblies tells Orleans where to find your agent types.
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    // Add assemblies containing your [AgentAlias] agent types here:
    // AdditionalAssemblies = [typeof(MyAgent).Assembly]
});

var app = builder.Build();

// Maps API controllers, enables WebSocket middleware at /ws
app.UseFabrCoreServer();

app.Run();
```

**IMPORTANT:** If the user has an agents project/assembly, uncomment and update the `AdditionalAssemblies` line with the correct type reference (e.g., `typeof(SampleAgent).Assembly`). Add the corresponding `using` statement.

### 3. `appsettings.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Orleans": {
    "ClusteringMode": "Localhost",
    "ClusterId": "fabrcore-cluster",
    "ServiceId": "fabrcore-service"
  }
}
```

Note: For production, `ClusteringMode` can be `"SqlServer"` or `"AzureStorage"`, which require a `ConnectionString`.

### 4. `appsettings.Development.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore": "Warning",
      "FabrCore": "Debug",
      "Orleans": "Warning"
    }
  }
}
```

### 5. `fabrcore.json`

Ask the user which AI provider they want to use (OpenAI, Azure OpenAI, or both). Generate the appropriate template:

**OpenAI:**
```json
{
  "ModelConfigurations": [
    {
      "Name": "default",
      "Provider": "OpenAI",
      "Uri": "https://api.openai.com/v1",
      "Model": "gpt-4.1-mini",
      "ApiKeyAlias": "OPENAI_KEY",
      "TimeoutSeconds": 60,
      "MaxOutputTokens": 2048
    }
  ],
  "ApiKeys": [
    {
      "Alias": "OPENAI_KEY",
      "Value": "your-openai-api-key-here"
    }
  ]
}
```

**Azure OpenAI:**
```json
{
  "ModelConfigurations": [
    {
      "Name": "default",
      "Provider": "Azure",
      "Uri": "https://your-resource.cognitiveservices.azure.com/",
      "Model": "gpt-4.1-mini",
      "ApiKeyAlias": "AZURE_KEY",
      "TimeoutSeconds": 60,
      "MaxOutputTokens": 2048
    }
  ],
  "ApiKeys": [
    {
      "Alias": "AZURE_KEY",
      "Value": "your-azure-api-key-here"
    }
  ]
}
```

### 6. `.gitignore`

```
fabrcore.json
```

This prevents committing API keys to source control.

## After Generation

1. Run `dotnet build` to verify the project compiles.
2. Remind the user:
   - Replace API key placeholders in `fabrcore.json` with real keys.
   - Add agent assemblies to `AdditionalAssemblies` in `Program.cs` when they create agents.
   - The server exposes REST API at `/fabrcoreapi/` and WebSocket at `/ws`.
   - Orleans clustering is set to `Localhost` for development. Use `SqlServer` or `AzureStorage` for production.
