using Bloom.LoadTest.Fakes;

FakesApp.Build(args).Run();

/// <summary>Marker for hosting the app from a test project.</summary>
public partial class Program;

public static class FakesApp
{
    private record IdTokenRequest(string? Email, string? Name);

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        builder.Services.AddHttpClient(Webhooks.ClientName);
        builder.Services.AddSingleton<StripeFake>();
        builder.Services.AddSingleton<GoogleFake>();
        builder.Services.AddSingleton<Webhooks>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Webhooks>());
        var app = builder.Build();

        app.MapMethods("/v1/{**rest}", ["GET", "POST"], async (HttpContext ctx, StripeFake stripe, Webhooks webhooks) =>
        {
            var form = ctx.Request.Method == "GET"
                ? ctx.Request.Query.SelectMany(q => q.Value.Select(v => KeyValuePair.Create(q.Key, v ?? ""))).ToList()
                : (await ctx.Request.ReadFormAsync()).SelectMany(q => q.Value.Select(v => KeyValuePair.Create(q.Key, v ?? ""))).ToList();
            var (status, body) = stripe.Handle(ctx.Request.Method, ctx.Request.Path, form, ctx.Request.Headers["Idempotency-Key"].FirstOrDefault(), out var created);
            if (created is not null) webhooks.Offer(created);
            return Results.Json(body, statusCode: status);
        });

        app.MapGet("/google/certs", (GoogleFake google) => Results.Json(google.Jwks()));
        app.MapPost("/google/id-token", (IdTokenRequest request, GoogleFake google) =>
            string.IsNullOrWhiteSpace(request.Email) ? Results.BadRequest() : Results.Json(new { idToken = google.Mint(request.Email, request.Name) }));

        app.MapGet("/_stats", (StripeFake stripe, Webhooks webhooks) => new { sessionsCreated = stripe.SessionsCreated, webhooksDelivered = webhooks.Delivered, webhooksFailed = webhooks.Failed });

        return app;
    }
}
