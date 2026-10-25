// Portfolio value history: one point per day (UTC) for the signed-in user, saved when the dashboard finishes reading the
// wallets, so the dashboard can show the change since yesterday / last week and a small chart. Values are what the page
// added up at today's prices that day; days the dashboard wasn't opened have no point (not zero).
using OPTIC.Services;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class WebDashboard
{
    static readonly object PortfolioHistoryLock = new();

    static string PortfolioHistoryPath(HttpContext ctx) => Path.Combine(DashboardAuth.UserDataDir(ctx), "portfolio-history.json");

    static JsonArray LoadPortfolioHistory(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    static void MapPortfolioHistory(WebApplication app)
    {
        app.MapGet("/api/portfolio/history", (HttpContext ctx) =>
        {
            lock (PortfolioHistoryLock) return VJson(new { points = LoadPortfolioHistory(PortfolioHistoryPath(ctx)) });
        });

        // Body: { total, assets: { "OPT": 1234.5, ... } } in USD. Today's point is replaced, earlier days are kept.
        app.MapPost("/api/portfolio/history", async (HttpContext ctx) =>
        {
            JsonObject? body;
            try { body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted) as JsonObject; }
            catch (JsonException) { return VError("Not valid JSON.", 400); }
            if (body?["total"] is not JsonValue tv || !tv.TryGetValue<decimal>(out var total) || total < 0 || total > 1_000_000_000m)
                return VError("A total in USD is needed.", 400);
            var assets = new JsonObject();
            foreach (var (k, v) in body["assets"] as JsonObject ?? new())
                if (k.Length is > 0 and <= 20 && v is JsonValue av && av.TryGetValue<decimal>(out var d) && d >= 0) assets[k] = Math.Round(d, 2);
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var path = PortfolioHistoryPath(ctx);
            lock (PortfolioHistoryLock)
            {
                var points = LoadPortfolioHistory(path);
                var keep = new JsonArray();
                foreach (var p in points.OfType<JsonObject>().Where(p => p["date"]?.ToString() != today).TakeLast(1500)) keep.Add(p.DeepClone());
                keep.Add(new JsonObject { ["date"] = today, ["total"] = Math.Round(total, 2), ["assets"] = assets });
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, keep.ToJsonString());
                return VJson(new { points = keep });
            }
        });
    }
}
