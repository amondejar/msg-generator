// Gateway of the {{System}} system: the only door for the frontend (and for anyone outside the system).
// This file is yours: Stratum creates it once and never touches it again. What Stratum keeps up to date is in
// GatewayWiring.g.cs and GatewayRoutes.g.cs ("stratum aspire" regenerates them with the services of the system).
using {{System}}.Gateway;

var builder = WebApplication.CreateBuilder(args);

builder.AddGateway();   // routes to the services, CORS, health, traces{{GatewayModeComment}}

// Your services here: rate limiting, output cache, a client for an aggregation endpoint...

var app = builder.Build();

app.UseGateway();

// Your endpoints here, e.g. one that composes several services for a screen of the frontend:
// app.MapGet("/api/dashboard", async (IHttpClientFactory http) => ...);

app.Run();
