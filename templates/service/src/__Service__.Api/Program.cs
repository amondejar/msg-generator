using {{Service}}.Api;
using {{Service}}.Api.Endpoints;
using {{Service}}.Api.Features;
using Msg.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Generated from msg.json (MsgWiring.g.cs): defaults, telemetry, resilience, auth, OpenAPI, validation and entities.
// Turn features on or off in the Studio or with "msg feature": this file doesn't change.
builder.AddMsgWiring();

// What is specific to this service.
builder.Services.AddValidatedOptions<ServiceOptions>(ServiceOptions.SectionName);

var app = builder.Build();

app.UseMsgWiring();

// Public contract: versioned from day one.
var v1 = app.MapGroup("/api/v1");
v1.MapPingEndpoints();
v1.MapFeatures();

await app.RunAsync().ConfigureAwait(false);
