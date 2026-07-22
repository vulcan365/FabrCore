using FabrCore.Services.DataIntelligence.Configuration;
using FabrCore.Services.DataIntelligence.Context;
using FabrCore.Services.DataIntelligence.Factory;
using FabrCore.Services.DataIntelligence.Metadata;

// === Recommended: One-Call Registration ===
// Registers ISpecificationFactory<T> per entity, IEntitySchemaService,
// ISpecificationMetadataService, and DataIntelligenceContext.
services.AddDataIntelligence(di => di
    .AddEntity<Order>()
    .AddEntity<OrderLine>()
    .AddEntity<Product>()
    .FromAssemblyOf<OrderByIdSpecification>());
// Omit FromAssembly/FromAssemblyOf to scan the entity types' own assemblies.


// === Manual Registration (finer control) ===
// Register factories for each entity type (scans assembly for [SpecificationFor<T>] classes)
services.AddSingleton<ISpecificationFactory<Order>>(
    new SpecificationFactory<Order>(typeof(OrderByIdSpecification).Assembly));
services.AddSingleton<ISpecificationFactory<OrderLine>>(
    new SpecificationFactory<OrderLine>(typeof(OrderByIdSpecification).Assembly));
services.AddSingleton<ISpecificationFactory<Product>>(
    new SpecificationFactory<Product>(typeof(ProductNameContainsSpecification).Assembly));

// Schema service — register all entity types for string-based lookup
services.AddSingleton<IEntitySchemaService>(
    new EntitySchemaService(typeof(Order), typeof(OrderLine), typeof(Product)));

// Metadata service — aggregates specification info from all factories
services.AddSingleton<ISpecificationMetadataService>(sp =>
    SpecificationMetadataService.Create(
        sp.GetRequiredService<ISpecificationFactory<Order>>(),
        sp.GetRequiredService<ISpecificationFactory<OrderLine>>(),
        sp.GetRequiredService<ISpecificationFactory<Product>>()));

// Unified context — one service for schema + specs + formatting
services.AddSingleton<DataIntelligenceContext>();


// === Multiple Assembly Registration ===
// When specifications live in multiple assemblies
services.AddSingleton<ISpecificationFactory<Order>>(
    new SpecificationFactory<Order>(new[]
    {
        typeof(Order).Assembly,                    // Entity assembly
        typeof(SharedSpecifications).Assembly       // Shared specs assembly
    }));


// === Usage in Agent OnInitialize ===
// Inject schema + filter awareness into system prompt (one line!)
var diContext = serviceProvider.GetRequiredService<DataIntelligenceContext>();
config.SystemPrompt += "\n\n" + diContext.FormatForSystemPrompt();

// Or inject for a specific entity only
config.SystemPrompt += "\n\n" + diContext.FormatForSystemPrompt<Order>();

// Or inject per-message (compact format)
var context = diContext.FormatForMessage("Order");
