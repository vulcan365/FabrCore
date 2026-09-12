@{
    Solution = 'src/FabrCore.sln'
    Packages = @(
        'FabrCore.Core'
        'FabrCore.Sdk'
        'FabrCore.Client.Orleans'
        'FabrCore.Client.WebSocket'
        'FabrCore.Host'
        'FabrCore.Host.AzureStorage'
        'FabrCore.Host.Testing'
        'FabrCore.Services.Microsoft365Copilot'
        'FabrCore.Surface'
    )
    VSTestProjects = @(
        'src/FabrCore.Sdk.Tests/FabrCore.Sdk.Tests.csproj'
        'src/FabrCore.Host.Tests/FabrCore.Host.Tests.csproj'
        'src/FabrCore.Client.Orleans.Tests/FabrCore.Client.Orleans.Tests.csproj'
        'src/FabrCore.Client.WebSocket.Tests/FabrCore.Client.WebSocket.Tests.csproj'
        'src/FabrCore.Services.Microsoft365Copilot.Tests/FabrCore.Services.Microsoft365Copilot.Tests.csproj'
        'src/FabrCore.Surface.Tests/FabrCore.Surface.Tests.csproj'
        'samples/FabrCore.SampleApp.Tests/FabrCore.SampleApp.Tests.csproj'
    )
    TestingPlatformProjects = @(
        'src/FabrCore.Services.Memory.Tests/FabrCore.Services.Memory.Tests.csproj'
        'src/FabrCore.Services.GraphRag.Tests/FabrCore.Services.GraphRag.Tests.csproj'
    )
    OfflineTestFilter = 'TestCategory!=Integration&TestCategory!=Evaluation&TestCategory!=SqlMode'
}
