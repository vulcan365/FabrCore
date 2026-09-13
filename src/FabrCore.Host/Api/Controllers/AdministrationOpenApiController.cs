using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using FabrCore.Host.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace FabrCore.Host.Api.Controllers;

[ApiController, Authorize(Policy = FabrCoreAdminAuthenticationDefaults.Policy)]
[Route("fabrcoreapi/admin/v1/openapi.json")]
public sealed class AdministrationOpenApiController(IApiDescriptionGroupCollectionProvider descriptions) : ControllerBase
{
    [HttpGet]
    public IActionResult Get()
    {
        var paths = new JsonObject();
        foreach (var api in descriptions.ApiDescriptionGroups.Items.SelectMany(g => g.Items)
            .Where(a => a.RelativePath?.StartsWith("fabrcoreapi/admin/v1/", StringComparison.Ordinal) == true && a.HttpMethod is not null))
        {
            var path = "/" + api.RelativePath!.Split('?')[0];
            // ApiExplorer resolves attribute constraints to concrete route parameter names.
            if (paths[path] is not JsonObject methods) { methods = new(); paths[path] = methods; }
            var parameters = new JsonArray();
            var operation = new JsonObject { ["parameters"] = parameters, ["responses"] = new JsonObject
            {
                ["200"] = new JsonObject { ["description"] = "Successful response; schema depends on the operation." },
                ["202"] = new JsonObject { ["description"] = "Accepted; poll the operation or turn receipt." },
                ["409"] = new JsonObject { ["description"] = "Busy or conflicting submission identity." },
                ["412"] = new JsonObject { ["description"] = "Stale revision; refresh before retrying." },
                ["default"] = new JsonObject { ["description"] = "Authentication, validation, provider or transport failure." }
            } };
            foreach (var parameter in api.ParameterDescriptions)
            {
                if (parameter.Source == BindingSource.Body)
                    operation["requestBody"] = new JsonObject { ["required"] = parameter.IsRequired,
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Schema(parameter.Type) } } };
                else if (parameter.Source == BindingSource.Path || parameter.Source == BindingSource.Query || parameter.Source == BindingSource.Header)
                    parameters.Add(new JsonObject { ["name"] = parameter.Name,
                        ["in"] = parameter.Source == BindingSource.Path ? "path" : parameter.Source == BindingSource.Header ? "header" : "query",
                        ["required"] = parameter.Source == BindingSource.Path || parameter.IsRequired, ["schema"] = Schema(parameter.Type) });
            }
            methods[api.HttpMethod!.ToLowerInvariant()] = operation;
        }
        return Ok(new JsonObject { ["openapi"] = "3.1.0", ["info"] = new JsonObject { ["title"] = "FabrCore administration", ["version"] = "1" },
            ["paths"] = paths, ["security"] = new JsonArray(new JsonObject { ["administration"] = new JsonArray() }),
            ["components"] = new JsonObject { ["securitySchemes"] = new JsonObject { ["administration"] = new JsonObject { ["type"] = "http", ["scheme"] = "bearer" } } } });
    }
    private static JsonNode Schema(Type type) => JsonSerializerOptions.Web.GetJsonSchemaAsNode(type);
}
