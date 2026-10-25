// Gonka (GNK): a Cosmos SDK chain with gonka1... addresses. Current balances only: GNK (ngonka, 9 decimals) and the
// USDT and USDC that arrive over IBC (6 decimals). Transaction history is not read yet.
using OPTIC.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

public static partial class WebDashboard
{
    const string GonkaPriceKey = "coingecko:gonka";
    static readonly string[] GonkaLcds = { "https://rpc.gonka.gg/chain-api", "https://gonka.coin.ledger.com" };

    internal static bool IsGonkaAddress(string? a) => a is not null && Regex.IsMatch(a, "^gonka1[02-9ac-hj-np-z]{38,58}$");

    // The signed-in user's saved gonka1... addresses with their labels.
    static List<(string Address, string Label)> UserGonkaWallets(HttpContext ctx)
    {
        var path = Path.Combine(DashboardAuth.UserDataDir(ctx), "wallets.json");
        if (!File.Exists(path)) return new();
        var arr = JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray();
        return arr.Select(w => (Address: (w?["a"]?.ToString() ?? "").ToLowerInvariant(), Label: w?["l"]?.ToString() ?? ""))
            .Where(w => IsGonkaAddress(w.Address)).ToList();
    }

    // IBC denom -> USDT or USDC, from the chain's denom metadata (the trace ends in uusdt / uusdc, or the symbol says so).
    static Dictionary<string, string>? _gonkaStableDenoms;

    static async Task<Dictionary<string, string>> GonkaStableDenomsAsync(string lcd, CancellationToken ct)
    {
        if (_gonkaStableDenoms is { } cached) return cached;
        using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync($"{lcd}/cosmos/bank/v1beta1/denoms_metadata?pagination.limit=500", ct));
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in doc.RootElement.GetProperty("metadatas").EnumerateArray())
        {
            var text = ((m.TryGetProperty("display", out var d) ? d.GetString() : "") + " " + (m.TryGetProperty("symbol", out var s) ? s.GetString() : "")).ToLowerInvariant();
            var sym = text.Contains("usdt") ? "USDT" : text.Contains("usdc") ? "USDC" : null;
            if (sym is not null) map[m.GetProperty("base").GetString()!] = sym;
        }
        return _gonkaStableDenoms = map;
    }

    // GNK, USDT and USDC held by one address (summed over every IBC route), trying each public endpoint in turn.
    static async Task<List<(string Symbol, decimal Amount)>> LoadGonkaBalancesAsync(string address, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var lcd in GonkaLcds)
        {
            try
            {
                var stables = await GonkaStableDenomsAsync(lcd, ct);
                using var doc = JsonDocument.Parse(await EvmHttp.GetStringAsync($"{lcd}/cosmos/bank/v1beta1/balances/{address}?pagination.limit=200", ct));
                var rows = new List<(string Symbol, decimal Amount)>();
                foreach (var b in doc.RootElement.GetProperty("balances").EnumerateArray())
                {
                    var denom = b.GetProperty("denom").GetString()!;
                    var raw = decimal.Parse(b.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);
                    if (denom == "ngonka") rows.Add(("GNK", raw / 1_000_000_000m));
                    else if (stables.TryGetValue(denom, out var sym)) rows.Add((sym, raw / 1_000_000m));
                }
                return rows.GroupBy(r => r.Symbol).Select(g => (g.Key, g.Sum(r => r.Amount))).Where(r => r.Item2 > 0.0000001m).ToList();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { last = ex; }
        }
        throw new HttpRequestException("Gonka: no endpoint answered" + (last is null ? "." : ": " + last.Message));
    }
}
