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

var app = builder.Build();

app.UseStratumWiring();

// Public contract: versioned from day one.
var v1 = app.MapGroup("/api/v1");
v1.MapPingEndpoints();
v1.MapFeatures();

await app.RunAsync().ConfigureAwait(false);
