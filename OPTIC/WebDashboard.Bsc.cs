using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

// BNB Chain (BSC) for the user's saved 0x... wallets: BNB, NILA (MindWave), USDT and USDC, and NILA stakes.
//   - History comes from NodeReal's public BSC endpoint (nr_getAssetTransfers: native, internal and BEP-20 transfers with
//     the transaction's input and gas). BscScan's API has no free tier for BSC, and public RPCs return logs only 5,000
//     blocks at a time. Cached in odata/evm/bsc-<address>-nodereal.json for 3 hours.
//   - NILA stakes (amount, start, lock, reward accrued, claimed) are read live from the NILA staking contract. It pays a
//     fixed REWARD_RATE percent a year on each stake for its lock (one year), claimable after the claim cooldown.
//   - PATRIOT (Ethereum) is tracked with the other Ethereum tokens; its constants live here with NILA's.
public static partial class WebDashboard
{
    const string BscRpc = "https://bsc-mainnet.nodereal.io/v1/64a9df0874fb4a93b9d0a3849de012d3";
    const string NilaToken = "0x00f8da33734feb9b946fec2228c25072d2e2e41f";
    const string NilaStakingContract = "0x81bb535765170d61a5f0c2336e3b83ee80d3df6a";
    const string BscUsdt = "0x55d398326f99059ff775485246999027b3197955";
    const string BscUsdc = "0x8ac76a51cc950d9822d68b83fe1ad97b32cd580d";
    const string PatriotToken = "0xe9732d4b1e7d3789004ff029f032ba3034db059c";
    const string NilaPriceKey = "coingecko:mindwavedao";
    const string BnbPriceKey = "coingecko:binancecoin";
    const string PatriotPriceKey = "ethereum:" + PatriotToken;

    // Every network read for 0x... wallets; BNB Chain is read through NodeReal, the others through Blockscout.
    static readonly string[] EvmChainsAll = { "ethereum", "arbitrum", "base", "bsc" };
    static string NativeOf(string chain) => chain == "bsc" ? "BNB" : "ETH";

    // Tokens tracked as lots besides USDC / USDT: (network, contract) -> asset.
    static string? TrackedTokenAsset(string chain, string token) =>
        token == TitnToken ? "TITN" : TgtTokens.Contains(token) ? "TGT" : chain == "bsc" && token == NilaToken ? "NILA" : chain == "ethereum" && token == PatriotToken ? "PATRIOT" : null;

    // NILA staking calls (by function selector): stake(uint256,uint256), withdraw(uint256,uint256), claimReward(uint256).
    const string NilaStakeSelector = "0x7b0472f0", NilaWithdrawSelector = "0x441a3e70", NilaClaimSelector = "0xae169a50";

    // Contract reads go to PublicNode first: NodeReal's shared key answers "0x" when it is busy. NodeReal's own
    // methods (nr_*) only exist there.
    const string BscPublicRpc = "https://bsc-rpc.publicnode.com";

    static async Task<JsonDocument> BscRpcAsync(string method, JsonArray prms, CancellationToken ct)
    {
        var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = method, ["params"] = prms }.ToJsonString();
        var hosts = method.StartsWith("nr_", StringComparison.Ordinal) ? new[] { BscRpc } : new[] { BscPublicRpc, BscRpc };
        Exception? last = null;
        foreach (var host in hosts)
        {
            try
            {
                using var res = await EvmHttp.PostAsync(host, new StringContent(body, Encoding.UTF8, "application/json"), ct);
                var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("error", out var err)) { last = new HttpRequestException("BNB Chain: " + (err.TryGetProperty("message", out var m) ? m.GetString() : err.GetRawText())); doc.Dispose(); continue; }
                if (method == "eth_call" && doc.RootElement.TryGetProperty("result", out var r) && r.GetString() == "0x") { last = new HttpRequestException("BNB Chain: empty answer"); doc.Dispose(); continue; }
                return doc;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException) { last = ex; }
        }
        throw last ?? new HttpRequestException("BNB Chain: no answer");
    }

    // Every native, internal and BEP-20 transfer to or from the address (deduplicated), cached 3 hours.
    static async Task<List<JsonElement>> LoadBscRawAsync(string address, bool refresh, CancellationToken ct)
    {
        var path = Path.Combine(Environment.CurrentDirectory, "odata", "evm", $"bsc-{address}-nodereal.json");
        if (!refresh && File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(3))
        {
            using var cached = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            return cached.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        var seen = new HashSet<string>();
        var all = new JsonArray();
        foreach (var side in new[] { "toAddress", "fromAddress" })
        {
            var pageKey = "";
            for (var page = 0; page < 50; page++)
            {
                var q = new JsonObject
                {
                    ["category"] = new JsonArray("external", "internal", "20"), [side] = address, ["order"] = "asc", ["excludeZeroValue"] = false, ["maxCount"] = "0x3e8",
                };
                if (pageKey != "") q["pageKey"] = pageKey;
                JsonDocument doc;
                try { doc = await BscRpcAsync("nr_getAssetTransfers", new JsonArray(q), ct); }
                catch (Exception ex) when (ex is not OperationCanceledException && File.Exists(path))
                {
                    // NodeReal's free quota runs out now and then: keep using the last full copy rather than failing every report.
                    Console.WriteLine($"BNB Chain history for {address}: {ex.Message} Using the copy saved {File.GetLastWriteTimeUtc(path):yyyy-MM-dd HH:mm} UTC.");
                    using var stale = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
                    return stale.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
                }
                using var _ = doc;
                var result = doc.RootElement.GetProperty("result");
                foreach (var t in result.GetProperty("transfers").EnumerateArray())
                {
                    var key = t.GetProperty("hash").GetString() + "#" + t.GetProperty("category").GetString() + "#" + t.GetProperty("id").GetRawText();
                    if (seen.Add(key)) all.Add(JsonNode.Parse(t.GetRawText()));
                }
                pageKey = result.TryGetProperty("pageKey", out var pk) && pk.ValueKind == JsonValueKind.String ? pk.GetString()! : "";
                if (pageKey == "") break;
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, all.ToJsonString(), ct);
        using var fresh = JsonDocument.Parse(all.ToJsonString());
        return fresh.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    static string Str(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    static BigInteger HexInt(string hex) => hex.Length > 2 ? BigInteger.Parse("0" + hex[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : BigInteger.Zero;
    static long Num(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetInt64() : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n) ? n : 0 : 0;
    static DateTime BscTime(JsonElement e) => DateTimeOffset.FromUnixTimeSeconds(Num(e, "blockTimeStamp")).UtcDateTime;
    static bool BscOk(JsonElement e) => !e.TryGetProperty("receiptsStatus", out var s) || s.ValueKind != JsonValueKind.Number || s.GetInt32() == 1;
    // A transaction's function: "transfer" for a plain token transfer, "" for a plain BNB send, else the selector.
    static string BscMethod(JsonElement e)
    {
        var input = Str(e, "input");
        return input.StartsWith("0xa9059cbb", StringComparison.Ordinal) ? "transfer" : input.Length < 10 ? "" : input[..10];
    }

    static async Task<List<EvmTransfer>> LoadBscTransfersAsync(string address, bool refresh, CancellationToken ct) =>
        (await LoadBscRawAsync(address, refresh, ct)).Where(t => Str(t, "category") == "20" && BscOk(t)).Select(t =>
        {
            var dec = int.TryParse(Str(t, "decimal"), out var d) ? d : 18;
            return new EvmTransfer(BscTime(t), Str(t, "hash").ToLowerInvariant(), Num(t, "logIndex"), Str(t, "from").ToLowerInvariant(), Str(t, "to").ToLowerInvariant(),
                Str(t, "contractAddress").ToLowerInvariant(), Str(t, "asset"), Scale(HexInt(Str(t, "value")), dec), BscMethod(t));
        }).ToList();

    static async Task<List<EvmTx>> LoadBscTxsAsync(string address, bool refresh, CancellationToken ct) =>
        (await LoadBscRawAsync(address, refresh, ct)).Where(t => Str(t, "category") == "external").Select(t =>
        {
            var from = Str(t, "from").ToLowerInvariant();
            var fee = from == address ? Scale(new BigInteger(Num(t, "gasPrice")) * Num(t, "gasUsed"), 18) : 0m;
            return BscOk(t)
                ? new EvmTx(BscTime(t), Str(t, "hash").ToLowerInvariant(), from, Str(t, "to").ToLowerInvariant(), Scale(HexInt(Str(t, "value")), 18), BscMethod(t), fee)
                : new EvmTx(BscTime(t), Str(t, "hash").ToLowerInvariant(), from, Str(t, "to").ToLowerInvariant(), 0m, "failed", fee);
        }).ToList();

    static async Task<List<EvmInternal>> LoadBscInternalsAsync(string address, bool refresh, CancellationToken ct) =>
        (await LoadBscRawAsync(address, refresh, ct)).Where(t => Str(t, "category") == "internal" && BscOk(t) && Str(t, "to").ToLowerInvariant() == address)
            .Select(t => new EvmInternal(BscTime(t), Str(t, "hash").ToLowerInvariant(), Str(t, "from").ToLowerInvariant(), Scale(HexInt(Str(t, "value")), 18)))
            .Where(i => i.Value > 0).ToList();

    // Current BNB and token balances on BNB Chain: (symbol -> amount) for the tokens shown on My Wallets.
    static async Task<JsonObject> LoadBscBalancesAsync(string address, CancellationToken ct)
    {
        var row = new JsonObject();
        using var doc = await BscRpcAsync("nr_getTokenHoldings", new JsonArray(address, "0x1", "0x64"), ct);
        var res = doc.RootElement.GetProperty("result");
        row["BNB"] = Scale(HexInt(Str(res, "nativeTokenBalance")), 18);
        if (res.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
            foreach (var b in details.EnumerateArray())
            {
                var match = EvmBalanceTokens.FirstOrDefault(t => t.Chain == "bsc" && t.Token == Str(b, "tokenAddress").ToLowerInvariant());
                if (match.Symbol is null) continue;
                row[match.Symbol] = Scale(HexInt(Str(b, "tokenBalance")), (int)HexInt(Str(b, "tokenDecimals")));
            }
        return row;
    }

    sealed record NilaStake(int Index, decimal Amount, DateTime Staked, DateTime Unlocks, bool Claimed, decimal Accrued, decimal Withdrawn, bool FullyWithdrawn, decimal ClaimedReward, decimal Pending);

    // getUserStakeDetailsWithRewards(address): every stake of the wallet with its reward so far.
    static async Task<List<NilaStake>> LoadNilaStakesAsync(string address, CancellationToken ct)
    {
        // A wallet with no stakes still gets an empty array back (64 hex digits of offset and 64 of length); a shorter
        // answer is a node hiccup, so ask again.
        var hex = "";
        for (var attempt = 0; attempt < 3 && hex.Length < 128; attempt++)
        {
            using var doc = await BscRpcAsync("eth_call", new JsonArray(new JsonObject { ["to"] = NilaStakingContract, ["data"] = "0x24bcc2ca" + address[2..].PadLeft(64, '0') }, "latest"), ct);
            hex = (doc.RootElement.GetProperty("result").GetString() ?? "0x")[2..];
        }
        if (hex.Length < 128) throw new HttpRequestException("BNB Chain: the staking contract returned no answer");
        BigInteger W(int i) => i * 64 + 64 <= hex.Length ? BigInteger.Parse("0" + hex.Substring(i * 64, 64), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : BigInteger.Zero;
        var list = new List<NilaStake>();
        var n = (int)W(1);
        for (var i = 0; i < n; i++)
        {
            var b = 2 + i * 9; // tuple: amount, stakeTime, lockDuration, claimed, liveReward, withdrawnAmount, fullyWithdrawn, claimedReward, pendingReward
            var start = DateTimeOffset.FromUnixTimeSeconds((long)W(b + 1)).UtcDateTime;
            list.Add(new NilaStake(i, Scale(W(b), 18), start, start.AddSeconds((double)W(b + 2)), W(b + 3) != 0, Scale(W(b + 4), 18), Scale(W(b + 5), 18), W(b + 6) != 0, Scale(W(b + 7), 18), Scale(W(b + 8), 18)));
        }
        return list;
    }

    // REWARD_RATE() in percent a year; null when the node does not answer with a rate (it sometimes returns an empty result).
    static decimal? nilaRewardRate; // kept for the life of the server once read

    static async Task<decimal?> NilaRewardRateAsync(CancellationToken ct)
    {
        if (nilaRewardRate is { } known) return known;
        using var doc = await BscRpcAsync("eth_call", new JsonArray(new JsonObject { ["to"] = NilaStakingContract, ["data"] = "0x5997bb37" }, "latest"), ct);
        var rate = (decimal)HexInt(doc.RootElement.GetProperty("result").GetString() ?? "0x") / 100m;
        return rate > 0 ? nilaRewardRate = rate : null;
    }

    // Staked tokens across the user's wallets with unlock dates and rewards (NILA per stake; TITN from TitnStaking).
    static async Task<object> BuildStakesAsync(List<(string Address, string Label)> wallets, CancellationToken ct)
    {
        var nilaPx = await LoadDailyPricesAsync(NilaPriceKey, false, ct);
        var nilaPrice = nilaPx.Count > 0 ? nilaPx.Last().Value : (decimal?)null;
        var stakes = new List<object>();
        var notes = new List<string>();
        decimal? rate = null;
        try { rate = await NilaRewardRateAsync(ct); } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { notes.Add("The NILA reward rate could not be read."); }
        var now = DateTime.UtcNow;
        foreach (var w in wallets)
        {
            List<NilaStake> list;
            try { list = await LoadNilaStakesAsync(w.Address, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { notes.Add($"NILA stakes of {w.Address[..6]}… could not be read: {ex.Message}"); continue; }
            foreach (var s in list.Where(s => !s.FullyWithdrawn))
            {
                var lockYears = (decimal)(s.Unlocks - s.Staked).TotalDays / 365m;
                var atUnlock = rate is { } r ? Math.Round(s.Amount * r * lockYears, 6) : (decimal?)null;
                var earned = s.Accrued + s.ClaimedReward;
                stakes.Add(new
                {
                    wallet = w.Address, label = w.Label, chain = "bsc", asset = "NILA", index = s.Index,
                    amount = s.Amount - s.Withdrawn, staked = s.Staked, unlocks = s.Unlocks, unlocked = now >= s.Unlocks,
                    rewardRate = rate, earnedSoFar = earned, claimedReward = s.ClaimedReward, unclaimedReward = s.Accrued,
                    rewardAtUnlock = atUnlock, rewardAsset = "NILA",
                    earnedUsdNow = nilaPrice is { } p ? Math.Round(earned * p, 2) : (decimal?)null,
                    rewardAtUnlockUsdNow = nilaPrice is { } p2 && atUnlock is { } a ? Math.Round(a * p2, 2) : (decimal?)null,
                });
            }
        }
        if (stakes.Count > 0)
        {
            decimal? cooldownDays = null;
            try
            {
                using var cd = await BscRpcAsync("eth_call", new JsonArray(new JsonObject { ["to"] = NilaStakingContract, ["data"] = "0xfdea3657" }, "latest"), ct);
                cooldownDays = (decimal)HexInt(cd.RootElement.GetProperty("result").GetString()!) / 86400m;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            notes.Add($"NILA staking pays {(rate is { } rr ? (rr * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%" : "a fixed rate")} a year on each stake for its lock. The reward builds up in the staking contract"
                + (cooldownDays is { } cdd ? $" and can be claimed at most once every {cdd:0.#} days" : "") + ". Staking rewards are income when you gain control of them (Rev. Rul. 2023-14); OPTIC records a reward when the claim reaches your wallet, at NILA's price that day.");
        }
        return new { stakes, prices = new { NILA = nilaPrice }, notes, asOfUtc = now };
    }

    static void MapBsc(WebApplication app)
    {
        app.MapGet("/api/stakes", async (HttpContext ctx) =>
        {
            try { return VJson(await BuildStakesAsync(UserEvmWallets(ctx), ctx.RequestAborted)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return VError("Could not read your stakes: " + ex.Message, 502); }
        });
    }
}
