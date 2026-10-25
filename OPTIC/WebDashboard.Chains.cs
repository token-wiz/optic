// Admin > Chains: the networks OPTIC supports (address format, assets, what is read for each), live health checks of
// every data source it relies on, how fresh the saved histories are, and how many saved addresses each chain has.
// Read-only: chain handling is built into the cost engines, so nothing here can switch a chain on or change tax figures.
using OPTIC.Services;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static partial class WebDashboard
{
    sealed record ChainInfo(string Id, string Name, string Address, string[] Assets, bool Balances, bool History, string Prices, string Notes);

    static readonly ChainInfo[] SupportedChains =
    {
        new("optio", "Optio", "optio1…", new[] { "OPT" }, true, true, "CoinMarketCap daily and hourly; Osmosis swaps after CoinMarketCap's last close",
            "Node payouts, staking, locks and every transfer, from the connected Optio node."),
        new("osmosis", "Osmosis", "osmo1…", new[] { "OPT", "OSMO", "USDC", "USDT" }, true, true, "DefiLlama (OSMO); OPT as on Optio",
            "Swaps, liquidity positions and IBC transfers from the Osmosis archive node."),
        new("ethereum", "Ethereum", "0x…", new[] { "ETH", "USDC", "USDT", "TGT", "PATRIOT" }, true, true, "DefiLlama",
            "History from Blockscout; balances read from the chain over public RPC."),
        new("base", "Base", "0x…", new[] { "ETH", "USDC", "TITN" }, true, true, "DefiLlama",
            "THORWallet TITN staking and rewards; Aerodrome TITN/USDC liquidity."),
        new("arbitrum", "Arbitrum", "0x…", new[] { "ETH", "USDC", "USDC.e", "TITN", "TGT" }, true, true, "DefiLlama", "TGT and the TGT→TITN migration."),
        new("bsc", "BNB Chain", "0x…", new[] { "BNB", "NILA", "USDT", "USDC" }, true, true, "DefiLlama",
            "History from NodeReal's free endpoint (the last saved copy is used when its quota runs out); NILA staking read from its contract."),
        new("solana", "Solana", "base58 (case-sensitive)", new[] { "SOL", "USDC", "USDT", "other SPL tokens" }, true, true, "DefiLlama (SOL); token names from DexScreener",
            "History over public Solana RPC, which rate-limits: a long history can take more than one load."),
        new("gonka", "Gonka", "gonka1…", new[] { "GNK", "USDT", "USDC" }, true, false, "DefiLlama", "Balances only: transaction history is not read yet, so GNK is not in the Tax Report."),
    };

    sealed record HealthCheck(string Chain, string Source, string Url, string Kind, string? Body);

    static IEnumerable<HealthCheck> ChainHealthChecks()
    {
        var lcd = LoadDashboardConfig().GetValueOrDefault("lcd", "http://127.0.0.1:1317");
        const string blockNumber = """{"jsonrpc":"2.0","id":1,"method":"eth_blockNumber","params":[]}""";
        const string slot = """{"jsonrpc":"2.0","id":1,"method":"getSlot","params":[]}""";
        yield return new("optio", "Optio node (REST)", lcd.TrimEnd('/') + "/cosmos/base/tendermint/v1beta1/blocks/latest", "cosmos", null);
        yield return new("osmosis", "Osmosis archive node", OsmosisArchiveLcd + "/cosmos/base/tendermint/v1beta1/blocks/latest", "cosmos", null);
        foreach (var b in OsmosisBackupLcds.Take(2)) yield return new("osmosis", "Osmosis backup", b.TrimEnd('/') + "/cosmos/base/tendermint/v1beta1/blocks/latest", "cosmos", null);
        foreach (var (chain, api) in EvmExplorersAll) yield return new(chain, "Blockscout (history)", api + "/stats", "blockscout", null);
        foreach (var (chain, urls) in EvmPublicRpcs) foreach (var u in urls) yield return new(chain, "Public RPC (balances)", u, "evm", blockNumber);
        yield return new("bsc", "NodeReal (history)", BscRpc, "evm", blockNumber);
        yield return new("bsc", "PublicNode (balances, staking)", BscPublicRpc, "evm", blockNumber);
        foreach (var u in SolanaRpcs) yield return new("solana", "Solana RPC", u, "solana", slot);
        foreach (var u in GonkaLcds) yield return new("gonka", "Gonka REST", u + "/cosmos/base/tendermint/v1beta1/blocks/latest", "cosmos", null);
        yield return new("prices", "DefiLlama (prices)", "https://coins.llama.fi/prices/current/coingecko:ethereum,coingecko:solana", "llama", null);
        yield return new("prices", "DexScreener (token names)", "https://api.dexscreener.com/latest/dex/tokens/So11111111111111111111111111111111111111112", "json", null);
    }

    static async Task<object> RunHealthCheckAsync(HealthCheck c, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = c.Body is null
                ? await EvmHttp.GetAsync(c.Url, cts.Token)
                : await EvmHttp.PostAsync(c.Url, new StringContent(c.Body, Encoding.UTF8, "application/json"), cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            var ms = sw.ElapsedMilliseconds;
            if (!resp.IsSuccessStatusCode) return Result(false, ms, $"HTTP {(int)resp.StatusCode}{(text.Contains("Just a moment", StringComparison.Ordinal) ? " (Cloudflare challenge)" : "")}");
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            string? detail = c.Kind switch
            {
                "cosmos" => r.TryGetProperty("block", out var b) ? "block " + b.GetProperty("header").GetProperty("height").GetString() : null,
                "evm" => r.TryGetProperty("result", out var h) && h.ValueKind == JsonValueKind.String ? "block " + HexInt(h.GetString()!) : null,
                "solana" => r.TryGetProperty("result", out var s) && s.ValueKind == JsonValueKind.Number ? "slot " + s.GetInt64() : null,
                "blockscout" => r.TryGetProperty("total_blocks", out var tb) ? "blocks " + tb : "answered",
                "llama" => r.TryGetProperty("coins", out var co) && co.EnumerateObject().Any() ? $"{co.EnumerateObject().Count()} prices" : null,
                _ => "answered",
            };
            if (detail is null) return Result(false, ms, r.TryGetProperty("error", out var e) ? Trim(e.ToString()) : "unexpected answer");
            return Result(true, ms, detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return Result(false, sw.ElapsedMilliseconds, ex is TaskCanceledException ? "no answer in 10 seconds" : Trim(ex.Message));
        }
        object Result(bool ok, long ms, string detail) => new { chain = c.Chain, source = c.Source, url = Redact(c.Url), ok, ms, detail };
        static string Trim(string s) => s.Length > 160 ? s[..160] + "…" : s;
        // NodeReal's key is part of its URL: show only its start.
        static string Redact(string u) => u.Contains("nodereal.io/v1/", StringComparison.Ordinal) ? u[..(u.IndexOf("/v1/", StringComparison.Ordinal) + 8)] + "…" : u;
    }

    // Saved history copies per chain: how many, and the oldest and newest update.
    static object HistoryCacheInfo()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "odata");
        object Info(string chain, IEnumerable<string> files)
        {
            var f = files.Select(p => new FileInfo(p)).Where(x => x.Exists).ToList();
            return new { chain, files = f.Count, sizeKb = f.Sum(x => x.Length) / 1024, oldest = f.Count > 0 ? f.Min(x => x.LastWriteTimeUtc) : (DateTime?)null, newest = f.Count > 0 ? f.Max(x => x.LastWriteTimeUtc) : (DateTime?)null };
        }
        IEnumerable<string> Glob(string dir, string pattern) => Directory.Exists(Path.Combine(root, dir)) ? Directory.GetFiles(Path.Combine(root, dir), pattern) : Array.Empty<string>();
        var evm = Glob("evm", "*.json").ToList();
        return new[]
        {
            Info("ethereum", evm.Where(p => Path.GetFileName(p).StartsWith("ethereum-", StringComparison.Ordinal))),
            Info("base", evm.Where(p => Path.GetFileName(p).StartsWith("base-", StringComparison.Ordinal))),
            Info("arbitrum", evm.Where(p => Path.GetFileName(p).StartsWith("arbitrum-", StringComparison.Ordinal))),
            Info("bsc", evm.Where(p => Path.GetFileName(p).StartsWith("bsc-", StringComparison.Ordinal))),
            Info("solana", Glob("solana", "*.json")),
            Info("osmosis", Glob("osmosis", "*")),
        };
    }

    // Saved addresses per chain across all users (0x… addresses count once for all four EVM networks).
    static object SavedAddressCounts()
    {
        var counts = new Dictionary<string, int>();
        var usersDir = Path.Combine(Environment.CurrentDirectory, "odata", "users");
        if (!Directory.Exists(usersDir)) return counts;
        foreach (var file in Directory.GetFiles(usersDir, "wallets.json", SearchOption.AllDirectories))
        {
            JsonArray arr;
            try { arr = JsonNode.Parse(File.ReadAllText(file)) as JsonArray ?? new(); } catch (JsonException) { continue; }
            foreach (var w in arr)
            {
                var a = w?["a"]?.ToString() ?? "";
                var kind = a.StartsWith("optio1", StringComparison.Ordinal) ? "optio" : a.StartsWith("osmo1", StringComparison.Ordinal) ? "osmosis" : a.StartsWith("gonka1", StringComparison.Ordinal) ? "gonka"
                    : a.StartsWith("0x", StringComparison.Ordinal) ? "evm" : IsSolanaAddress(a) ? "solana" : null;
                if (kind is not null) counts[kind] = counts.GetValueOrDefault(kind) + 1;
            }
        }
        return counts;
    }

    static void MapChains(WebApplication app)
    {
        app.MapGet("/api/admin/chains", async (HttpContext ctx) =>
        {
            if (!DashboardAuth.IsAdmin(ctx)) return VError("Admins only.", 403);
            var checks = await Task.WhenAll(ChainHealthChecks().Select(c => RunHealthCheckAsync(c, ctx.RequestAborted)));
            return VJson(new { chains = SupportedChains, checks, caches = HistoryCacheInfo(), addresses = SavedAddressCounts(), checkedUtc = DateTime.UtcNow });
        });
    }
}
