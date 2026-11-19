using {{Service}}.Api;
using {{Service}}.Api.Endpoints;
using {{Service}}.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// Everything every service does the same way (health, errors, logging, telemetry...).
builder.AddServiceDefaults();

// Service-specific configuration, validated at startup.
builder.Services.AddValidatedOptions<ServiceOptions>(ServiceOptions.SectionName);
// @msg:if openApi
builder.Services.AddOpenApi();
// @msg:end

var app = builder.Build();

app.UseServiceDefaults();
app.MapDefaultEndpoints();
// @msg:if openApi

if (app.Environment.IsDevelopment())
{
    // @msg:if auth
    app.MapOpenApi().AllowAnonymous();   // GET /openapi/v1.json
    // @msg:else
    app.MapOpenApi();   // GET /openapi/v1.json
    // @msg:end
}
// @msg:end

// Public contract: versioned from day one.
var v1 = app.MapGroup("/api/v1");
v1.MapPingEndpoints();

await app.RunAsync().ConfigureAwait(false);
