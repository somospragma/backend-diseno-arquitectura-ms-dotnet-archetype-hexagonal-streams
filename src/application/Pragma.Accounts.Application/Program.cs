using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Pragma.Accounts.Application;
using Pragma.Accounts.Infrastructure.Api;
using Pragma.Accounts.Infrastructure.Helpers;
using Pragma.Accounts.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = ApiServiceCollectionExtensions.MaxRequestBodyBytes;
});

builder.Services
    .AddAccountsApi()
    .AddAccountsUseCases()
    .AddAccountsAdapters(builder.Configuration)
    .AddAccountsObservability(builder.Configuration);
builder.Services.AddOpenApi();

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("Persistence:AutoCreateSchema"))
{
    // Solo desarrollo local; en ambientes reales el esquema va por migraciones.
    await app.Services.EnsurePersistenceSchemaAsync();
}

app.UsePragmaHelpers();
app.UseAccountsApiErrors();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapAccountEndpoints();

await app.RunAsync();
