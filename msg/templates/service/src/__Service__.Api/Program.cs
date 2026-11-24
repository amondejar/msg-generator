using System.Text.Json.Serialization;
using {{Service}}.Api;
using {{Service}}.Api.Endpoints;
using {{Service}}.Api.Features;
using Stratum.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Generated from stratum.json (StratumWiring.g.cs): defaults, telemetry, resilience, auth, OpenAPI, validation and entities.
// Turn features on or off in the Studio or with "stratum feature": this file doesn't change.
builder.AddStratumWiring();

// What is specific to this service.
builder.Services.AddValidatedOptions<ServiceOptions>(ServiceOptions.SectionName);

// Strict JSON in requests: numbers as numbers ("2", not "\"2\""), property names as documented, no repeated properties.
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
    options.SerializerOptions.AllowDuplicateProperties = false;
});

var app = builder.Build();

app.UseStratumWiring();

// Public contract: versioned from day one.
var v1 = app.MapGroup("/api/v1");
v1.MapPingEndpoints();
v1.MapFeatures();

await app.RunAsync().ConfigureAwait(false);
