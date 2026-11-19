using {{Service}}.Api;
using {{Service}}.Api.Endpoints;
using {{Service}}.Api.Features;
using Msg.ServiceDefaults;
// @msg:if openApi
using Scalar.AspNetCore;
// @msg:end

var builder = WebApplication.CreateBuilder(args);

// What every service does the same way (Msg.ServiceDefaults.* packages, upgraded with "msg upgrade").
builder.AddMsgServiceDefaults();
// @msg:if observability
builder.AddMsgOpenTelemetry("{{service-kebab}}");
// @msg:end
// @msg:if resilience
builder.AddMsgResilience();          // retries, timeouts and circuit breaker on every HttpClient
builder.AddMsgServiceDiscovery();    // http://other-service resolved by Aspire / configuration
// @msg:end
// @msg:if auth
builder.AddMsgAuthentication();
// @msg:end

// What is specific to this service.
builder.Services.AddValidatedOptions<ServiceOptions>(ServiceOptions.SectionName);
builder.Services.AddValidation();   // DataAnnotations on request DTOs → 400 ProblemDetails
builder.AddFeatures();              // generated from msg.json ("msg add entity")
// @msg:if openApi
builder.Services.AddOpenApi();
// @msg:end

var app = builder.Build();

app.UseMsgServiceDefaults();
// @msg:if auth
app.UseAuthentication();
app.UseAuthorization();
// @msg:end
app.MapMsgDefaultEndpoints();
// @msg:if openApi

if (app.Environment.IsDevelopment())
{
    // @msg:if auth
    app.MapOpenApi().AllowAnonymous();               // GET /openapi/v1.json
    app.MapScalarApiReference().AllowAnonymous();    // GET /scalar: explore and try the API
    // @msg:else
    app.MapOpenApi();                 // GET /openapi/v1.json
    app.MapScalarApiReference();      // GET /scalar: explore and try the API
    // @msg:end
}
// @msg:end

// Public contract: versioned from day one.
var v1 = app.MapGroup("/api/v1");
v1.MapPingEndpoints();
v1.MapFeatures();

await app.RunAsync().ConfigureAwait(false);
