using {{Service}}.Api;
using {{Service}}.Api.Endpoints;
using Msg.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);

// What every service does the same way (Msg.ServiceDefaults.* packages, upgraded with "msg upgrade").
builder.AddMsgServiceDefaults();
// @msg:if observability
builder.AddMsgOpenTelemetry("{{service-kebab}}");
// @msg:end
// @msg:if resilience
builder.AddMsgResilience();
// @msg:end
// @msg:if auth
builder.AddMsgAuthentication();
// @msg:end

// What is specific to this service.
builder.Services.AddValidatedOptions<ServiceOptions>(ServiceOptions.SectionName);
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
