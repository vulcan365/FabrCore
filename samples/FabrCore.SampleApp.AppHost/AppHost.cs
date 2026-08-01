var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.FabrCore_SampleApp>("fabrcore-sample-app");

builder.Build().Run();
