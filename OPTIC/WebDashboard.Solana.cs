// Solana: base58 addresses (case-sensitive). Current balances: SOL (9 decimals) and USDC / USDT (SPL tokens, 6 decimals),
// read over public Solana RPC. Transaction history is not read yet.
using OPTIC.Services;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

public static partial class WebDashboard
{
    const string SolanaPriceKey = "coingecko:solana";
    static readonly string[] SolanaRpcs = { "https://api.mainnet-beta.solana.com", "https://solana-rpc.publicnode.com" };
    static readonly (string Mint, string Symbol)[] SolanaTokens =
    {
        ("EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v", "USDC"),
        ("Es9vMFrzaCERmJfrF4H2FYD4KCoNkY11McCe8BenwNYB", "USDT"),
    };

    internal static bool IsSolanaAddress(string? a) => a is not null && Regex.IsMatch(a, "^[1-9A-HJ-NP-Za-km-z]{32,44}$");

    // The signed-in user's saved Solana addresses (exact case) with their labels.
    static List<(string Address, string Label)> UserSolanaWallets(HttpContext ctx)
    {
        var path = Path.Combine(DashboardAuth.UserDataDir(ctx), "wallets.json");
        if (!File.Exists(path)) return new();
        var arr = JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? new JsonArray();
        return arr.Select(w => (Address: (w?["a"]?.ToString() ?? "").Trim(), Label: w?["l"]?.ToString() ?? ""))
            .Where(w => IsSolanaAddress(w.Address)).ToList();
    }

    static async Task<JsonElement> SolanaRpcAsync(string method, JsonArray prms, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in SolanaRpcs)
        {
            try
            {
                var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = prms.DeepClone() };
                using var resp = await EvmHttp.PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                // An empty result is not an answer: publicnode prunes old transactions and returns null for them.
                if (doc.RootElement.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Null) return r.Clone();
                last = new HttpRequestException(doc.RootElement.TryGetProperty("error", out var e) ? e.GetRawText() : "no result");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { last = ex; }
        }
        throw new HttpRequestException("Solana: no endpoint answered" + (last is null ? "." : ": " + last.Message));
    }

    // SOL, USDC and USDT held by one address (token amounts summed over its token accounts).
    static async Task<List<(string Symbol, decimal Amount)>> LoadSolanaBalancesAsync(string address, CancellationToken ct)
    {
        var rows = new List<(string Symbol, decimal Amount)>();
        var bal = await SolanaRpcAsync("getBalance", new JsonArray(address), ct);
        rows.Add(("SOL", bal.GetProperty("value").GetInt64() / 1_000_000_000m));
        foreach (var (mint, sym) in SolanaTokens)
        {
            var res = await SolanaRpcAsync("getTokenAccountsByOwner", new JsonArray(address, new JsonObject { ["mint"] = mint }, new JsonObject { ["encoding"] = "jsonParsed" }), ct);
            var amt = 0m;
            foreach (var acc in res.GetProperty("value").EnumerateArray())
            {
                var ta = acc.GetProperty("account").GetProperty("data").GetProperty("parsed").GetProperty("info").GetProperty("tokenAmount");
                amt += decimal.Parse(ta.GetProperty("uiAmountString").GetString()!, CultureInfo.InvariantCulture);
            }
            rows.Add((sym, amt));
        }
        return rows.Where(r => r.Amount > 0.0000001m).ToList();
    }
}
