using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

// Validators page: every validator on the chain with the figures a block explorer's staking page shows
// (voting power, commission, self-bond, delegators, uptime, balances), plus network staking totals.
public static partial class WebDashboard
{
    static (DateTime At, object Data)? _validatorsCache;
    static readonly SemaphoreSlim ValidatorsGate = new(1, 1);

    static void MapValidators(WebApplication app)
    {
        app.MapGet("/api/validators", async (HttpContext ctx) =>
        {
            var ct = ctx.RequestAborted;
            var lcd = LoadDashboardConfig().GetValueOrDefault("lcd", "http://127.0.0.1:1317");
            object network;
            await ValidatorsGate.WaitAsync(ct);
            try
            {
                if (_validatorsCache is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(60) && ctx.Request.Query["refresh"] != "1")
                    network = c.Data;
                else
                {
                    var fresh = await BuildValidatorsAsync(lcd, ct);
                    if (fresh is null) return VError($"The node's REST endpoint ({lcd}) did not answer.", 502);
                    network = fresh;
                    _validatorsCache = (DateTime.UtcNow, fresh);
                }
            }
            finally { ValidatorsGate.Release(); }

            // The viewer's own stake per validator, when a wallet is given (not cached).
            object? you = null;
            var address = AddressParam(ctx);
            if (address is not null)
            {
                using var del = await LcdGetAsync(lcd, $"/cosmos/staking/v1beta1/delegations/{address}?pagination.limit=1000", ct);
                using var rw = await LcdGetAsync(lcd, $"/cosmos/distribution/v1beta1/delegators/{address}/rewards", ct);
                var stake = new Dictionary<string, decimal>(StringComparer.Ordinal);
                if (del is not null && del.RootElement.TryGetProperty("delegation_responses", out var dr))
                    foreach (var d in dr.EnumerateArray())
                        stake[d.GetProperty("delegation").GetProperty("validator_address").GetString()!] = Opt(ParseUnits(d.GetProperty("balance").GetProperty("amount").GetString()));
                var rewards = new Dictionary<string, decimal>(StringComparer.Ordinal);
                if (rw is not null && rw.RootElement.TryGetProperty("rewards", out var rr))
                    foreach (var r in rr.EnumerateArray())
                        rewards[r.GetProperty("validator_address").GetString()!] = r.GetProperty("reward").EnumerateArray()
                            .Where(x => string.Equals(x.GetProperty("denom").GetString(), "uOPT", StringComparison.OrdinalIgnoreCase))
                            .Sum(x => DecOpt(x.GetProperty("amount").GetString()));
                you = new { address, stake, rewards, totalStakedOpt = stake.Values.Sum(), totalRewardsOpt = rewards.Values.Sum() };
            }
            return VJson(new { network, you });
        });
    }

    static async Task<object?> BuildValidatorsAsync(string lcd, CancellationToken ct)
    {
        using var valsDoc = await LcdGetAsync(lcd, "/cosmos/staking/v1beta1/validators?pagination.limit=1000", ct);
        if (valsDoc is null) return null;
        var paths = new Dictionary<string, string>
        {
            ["pool"] = "/cosmos/staking/v1beta1/pool",
            ["params"] = "/cosmos/staking/v1beta1/params",
            ["slashing"] = "/cosmos/slashing/v1beta1/params",
            ["signing"] = "/cosmos/slashing/v1beta1/signing_infos?pagination.limit=1000",
            ["distr"] = "/cosmos/distribution/v1beta1/params",
            ["community"] = "/cosmos/distribution/v1beta1/community_pool",
            ["supply"] = "/cosmos/bank/v1beta1/supply/by_denom?denom=uOPT",
            ["inflation"] = "/cosmos/mint/v1beta1/inflation",
            ["block"] = "/cosmos/base/tendermint/v1beta1/blocks/latest",
        };
        var docs = new Dictionary<string, JsonDocument?>();
        await Task.WhenAll(paths.Select(async kv => { var d = await LcdGetAsync(lcd, kv.Value, ct); lock (docs) docs[kv.Key] = d; }));
        try
        {
            JsonElement? El(string k, params string[] path)
            {
                if (docs.GetValueOrDefault(k) is not { } d) return null;
                var e = d.RootElement;
                foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null; }
                return e;
            }
            string? Str(string k, params string[] path) => El(k, path) is not { } e ? null : e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString();
            static double? Dbl(string? s) => double.TryParse((s ?? "").TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

            var bonded = DecOpt(Str("pool", "pool", "bonded_tokens"));
            var notBonded = DecOpt(Str("pool", "pool", "not_bonded_tokens"));
            var supply = DecOpt(Str("supply", "amount", "amount"));
            var community = El("community", "pool") is { ValueKind: JsonValueKind.Array } cp
                ? cp.EnumerateArray().Where(x => string.Equals(x.GetProperty("denom").GetString(), "uOPT", StringComparison.OrdinalIgnoreCase)).Sum(x => DecOpt(x.GetProperty("amount").GetString()))
                : 0m;
            var window = Dbl(Str("slashing", "params", "signed_blocks_window")) ?? 0;

            // Signing info by consensus address, for uptime over the slashing window.
            var signing = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (El("signing", "info") is { ValueKind: JsonValueKind.Array } si)
                foreach (var s in si.EnumerateArray()) signing[s.GetProperty("address").GetString()!] = s;

            var raw = valsDoc.RootElement.GetProperty("validators").EnumerateArray().Select(v => new
            {
                El = v,
                Op = v.GetProperty("operator_address").GetString()!,
                Tokens = DecOpt(v.GetProperty("tokens").GetString()),
                Status = (v.GetProperty("status").GetString() ?? "").Replace("BOND_STATUS_", "").ToLowerInvariant(),
                Jailed = v.TryGetProperty("jailed", out var j) && j.ValueKind == JsonValueKind.True,
            }).OrderByDescending(v => v.Status == "bonded").ThenByDescending(v => v.Tokens).ToList();
            var bondedPower = raw.Where(v => v.Status == "bonded").Sum(v => v.Tokens);

            // Per-validator figures that need their own query: self-bond, delegators, commission and rewards owed, operator balance.
            using var gate = new SemaphoreSlim(8);
            var extra = new Dictionary<string, (decimal? Self, long? Delegators, decimal? Commission, decimal? Outstanding, decimal? Balance)>();
            await Task.WhenAll(raw.Select(async v =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var acc = Bech32Reprefix(v.Op, "optio");
                    var t1 = acc is null ? Task.FromResult<JsonDocument?>(null) : LcdGetAsync(lcd, $"/cosmos/staking/v1beta1/validators/{v.Op}/delegations/{acc}", ct);
                    var t2 = LcdGetAsync(lcd, $"/cosmos/staking/v1beta1/validators/{v.Op}/delegations?pagination.limit=1&pagination.count_total=true", ct);
                    var t3 = LcdGetAsync(lcd, $"/cosmos/distribution/v1beta1/validators/{v.Op}/commission", ct);
                    var t4 = LcdGetAsync(lcd, $"/cosmos/distribution/v1beta1/validators/{v.Op}/outstanding_rewards", ct);
                    var t5 = acc is null ? Task.FromResult<JsonDocument?>(null) : LcdGetAsync(lcd, $"/cosmos/bank/v1beta1/balances/{acc}/by_denom?denom=uOPT", ct);
                    await Task.WhenAll(t1, t2, t3, t4, t5);
                    using var d1 = t1.Result; using var d2 = t2.Result; using var d3 = t3.Result; using var d4 = t4.Result; using var d5 = t5.Result;
                    decimal? self = d1 is not null && d1.RootElement.TryGetProperty("delegation_response", out var sr) ? DecOpt(sr.GetProperty("balance").GetProperty("amount").GetString()) : (acc is null ? null : 0m);
                    long? count = d2 is not null && d2.RootElement.TryGetProperty("pagination", out var pg) && pg.TryGetProperty("total", out var tot) && long.TryParse(tot.GetString(), out var n) ? n : null;
                    static decimal? Coins(JsonDocument? d, string a, string b) => d is not null && d.RootElement.TryGetProperty(a, out var x) && x.TryGetProperty(b, out var arr) && arr.ValueKind == JsonValueKind.Array
                        ? arr.EnumerateArray().Where(c => string.Equals(c.GetProperty("denom").GetString(), "uOPT", StringComparison.OrdinalIgnoreCase)).Sum(c => DecOpt(c.GetProperty("amount").GetString()))
                        : null;
                    decimal? balance = d5 is not null && d5.RootElement.TryGetProperty("balance", out var bl) && bl.ValueKind == JsonValueKind.Object ? DecOpt(bl.GetProperty("amount").GetString()) : null;
                    lock (extra) extra[v.Op] = (self, count, Coins(d3, "commission", "commission"), Coins(d4, "rewards", "rewards"), balance);
                }
                finally { gate.Release(); }
            }));

            var cumulative = 0m;
            var rank = 0;
            var validators = raw.Select(v =>
            {
                var e = v.El;
                var desc = e.TryGetProperty("description", out var dd) ? dd : default;
                string D(string k) => desc.ValueKind == JsonValueKind.Object && desc.TryGetProperty(k, out var x) ? x.GetString() ?? "" : "";
                var rates = e.GetProperty("commission").GetProperty("commission_rates");
                var pub = e.TryGetProperty("consensus_pubkey", out var pk) && pk.TryGetProperty("key", out var key) ? key.GetString() : null;
                var cons = pub is null ? null : Bech32Encode("optiovalcons", SHA256.HashData(Convert.FromBase64String(pub)).AsSpan(0, 20).ToArray());
                var sign = cons is not null && signing.TryGetValue(cons, out var s) ? s : (JsonElement?)null;
                long missed = sign is { } s1 && long.TryParse(s1.GetProperty("missed_blocks_counter").GetString(), out var m) ? m : 0;
                var share = v.Status == "bonded" && bondedPower > 0 ? v.Tokens / bondedPower : 0m;
                if (v.Status == "bonded") { cumulative += share; rank++; }
                extra.TryGetValue(v.Op, out var x2);
                return new
                {
                    rank = v.Status == "bonded" ? rank : (int?)null,
                    operatorAddress = v.Op,
                    accountAddress = Bech32Reprefix(v.Op, "optio"),
                    consensusAddress = cons,
                    moniker = D("moniker"), identity = D("identity"), website = D("website"), details = D("details"), securityContact = D("security_contact"),
                    status = v.Jailed ? "jailed" : v.Status,
                    jailed = v.Jailed,
                    tokensOpt = v.Tokens,
                    votingPowerShare = share,
                    cumulativeShare = v.Status == "bonded" ? cumulative : (decimal?)null,
                    commission = new
                    {
                        rate = Dbl(rates.GetProperty("rate").GetString()),
                        maxRate = Dbl(rates.GetProperty("max_rate").GetString()),
                        maxChangeRate = Dbl(rates.GetProperty("max_change_rate").GetString()),
                        updated = e.GetProperty("commission").TryGetProperty("update_time", out var ut) ? ut.GetString() : null,
                    },
                    minSelfDelegationOpt = DecOpt(e.TryGetProperty("min_self_delegation", out var msd) ? msd.GetString() : null),
                    selfBondedOpt = x2.Self,
                    selfBondShare = x2.Self is { } sb && v.Tokens > 0 ? sb / v.Tokens : (decimal?)null,
                    delegators = x2.Delegators,
                    commissionOwedOpt = x2.Commission,
                    outstandingRewardsOpt = x2.Outstanding,
                    operatorBalanceOpt = x2.Balance,
                    uptime = sign is null ? null : new
                    {
                        missed, window,
                        share = window > 0 ? Math.Max(0, 1 - missed / window) : (double?)null,
                        startHeight = sign.Value.GetProperty("start_height").GetString(),
                        jailedUntil = sign.Value.GetProperty("jailed_until").GetString(),
                        tombstoned = sign.Value.TryGetProperty("tombstoned", out var tb) && tb.ValueKind == JsonValueKind.True,
                    },
                    unbondingHeight = e.TryGetProperty("unbonding_height", out var uh) ? uh.GetString() : null,
                    unbondingTime = e.TryGetProperty("unbonding_time", out var ubt) ? ubt.GetString() : null,
                };
            }).ToList();

            return new
            {
                asOfUtc = DateTime.UtcNow,
                chainId = Str("block", "block", "header", "chain_id"),
                height = Str("block", "block", "header", "height"),
                blockTime = Str("block", "block", "header", "time"),
                pool = new
                {
                    bondedOpt = bonded, notBondedOpt = notBonded, supplyOpt = supply,
                    bondedRatio = supply > 0 ? bonded / supply : (decimal?)null,
                    communityPoolOpt = community,
                    inflation = Dbl(Str("inflation", "inflation")),
                },
                parameters = new
                {
                    maxValidators = Str("params", "params", "max_validators"),
                    unbondingDays = Dbl(Str("params", "params", "unbonding_time")) / 86400,
                    minCommission = Dbl(Str("params", "params", "min_commission_rate")),
                    signedBlocksWindow = window,
                    minSignedPerWindow = Dbl(Str("slashing", "params", "min_signed_per_window")),
                    downtimeJailSeconds = Dbl(Str("slashing", "params", "downtime_jail_duration")),
                    slashDoubleSign = Dbl(Str("slashing", "params", "slash_fraction_double_sign")),
                    slashDowntime = Dbl(Str("slashing", "params", "slash_fraction_downtime")),
                    communityTax = Dbl(Str("distr", "params", "community_tax")),
                },
                counts = new { active = validators.Count(v => v.status == "bonded"), total = validators.Count, jailed = validators.Count(v => v.jailed) },
                validators,
                source = lcd,
            };
        }
        finally { foreach (var d in docs.Values) d?.Dispose(); }
    }

    // Amount strings from the chain (integers or decimals, in uOPT) as OPT.
    static decimal DecOpt(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v / 1_000_000m : 0m;

    // ---------------------------------------------------------------- bech32 (BIP-173), for valoper / account / valcons addresses
    const string Bech32Chars = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";

    static uint Bech32Polymod(IEnumerable<byte> values)
    {
        uint[] gen = { 0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3 };
        uint chk = 1;
        foreach (var v in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ v;
            for (var i = 0; i < 5; i++) if (((top >> i) & 1) != 0) chk ^= gen[i];
        }
        return chk;
    }

    static byte[] Bech32HrpExpand(string hrp) =>
        hrp.Select(c => (byte)(c >> 5)).Append((byte)0).Concat(hrp.Select(c => (byte)(c & 31))).ToArray();

    static byte[]? ConvertBits(IEnumerable<byte> data, int from, int to, bool pad)
    {
        int acc = 0, bits = 0, maxv = (1 << to) - 1;
        var ret = new List<byte>();
        foreach (var value in data)
        {
            if (value >> from != 0) return null;
            acc = (acc << from) | value; bits += from;
            while (bits >= to) { bits -= to; ret.Add((byte)((acc >> bits) & maxv)); }
        }
        if (pad) { if (bits > 0) ret.Add((byte)((acc << (to - bits)) & maxv)); }
        else if (bits >= from || ((acc << (to - bits)) & maxv) != 0) return null;
        return ret.ToArray();
    }

    internal static string Bech32Encode(string hrp, byte[] data)
    {
        var five = ConvertBits(data, 8, 5, true)!;
        var values = Bech32HrpExpand(hrp).Concat(five).Concat(new byte[6]);
        var mod = Bech32Polymod(values) ^ 1;
        var chk = Enumerable.Range(0, 6).Select(i => (byte)((mod >> (5 * (5 - i))) & 31));
        return hrp + "1" + new string(five.Concat(chk).Select(b => Bech32Chars[b]).ToArray());
    }

    internal static (string Hrp, byte[] Data)? Bech32Decode(string s)
    {
        s = s.ToLowerInvariant();
        var pos = s.LastIndexOf('1');
        if (pos < 1 || pos + 7 > s.Length) return null;
        var hrp = s[..pos];
        var vals = new List<byte>();
        foreach (var c in s[(pos + 1)..]) { var i = Bech32Chars.IndexOf(c); if (i < 0) return null; vals.Add((byte)i); }
        if (Bech32Polymod(Bech32HrpExpand(hrp).Concat(vals)) != 1) return null;
        var data = ConvertBits(vals.Take(vals.Count - 6), 5, 8, false);
        return data is null ? null : (hrp, data);
    }

    // Same key bytes under another prefix, e.g. optiovaloper1... -> optio1... (the operator's own account).
    internal static string? Bech32Reprefix(string address, string hrp) =>
        Bech32Decode(address) is { } d ? Bech32Encode(hrp, d.Data) : null;
}
