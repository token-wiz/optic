using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using OPTIC.Services;
using OPTIC.Services.Valuation;

// Receipt-date FMV with evidence review: gathers the market evidence at each receipt, applies the versioned
// source-quality policy to choose a candidate market price, and lets the user approve qualifying candidates in bulk
// (under an approved policy) and decide exceptions with an explanation. Nothing is approved automatically.
public static partial class WebDashboard
{
    static readonly string[] FmvPoolIds = { "3278", "3329", "3391", "3400", "3565", "3571", "3572" };
    const string UsdcNoble = "USDC (Noble)";
    const string UsdcSource = "CoinMarketCap USDC (id 3408) daily close, quote.close, data-api v3.1 historical; applied to Noble USDC (native Circle USDC, Osmosis channel-750)";
    const int MinExceptionExplanation = 20;

    sealed class FmvJob
    {
        public string User { get; init; } = "";
        public string Address { get; init; } = "";
        public DateOnly From { get; init; }
        public DateOnly To { get; init; }
        public bool FetchMarkets { get; init; } = true;
        public string Stage { get; set; } = "Starting";
        public int Done { get; set; }
        public int Total { get; set; }
        public bool Running { get; set; } = true;
        public string? Error { get; set; }
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
        public DateTime? FinishedUtc { get; set; }
        public List<string> Log { get; } = new();
        public object? Result { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public CancellationTokenSource Cts { get; } = new();
        public void Note(string s) { lock (Log) { Log.Add($"{DateTime.UtcNow:HH:mm:ss} {s}"); if (Log.Count > 200) Log.RemoveAt(0); } }
    }

    static readonly ConcurrentDictionary<string, FmvJob> _fmvJobs = new();
    static readonly HttpClient CmcHttp = CreateCmcClient();

    static HttpClient CreateCmcClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        h.DefaultRequestHeaders.Referrer = new Uri("https://coinmarketcap.com/currencies/usd-coin/");
        return h;
    }

    static string JobKey(string user, string address) => user.ToLowerInvariant() + "|" + address;
    static string CandidateId(string user, string receiptId) => "cand:" + user.ToLowerInvariant() + ":" + receiptId;

    sealed class PolicyApproval
    {
        public string Version { get; set; } = "";
        public string Description { get; set; } = "";
        public string ApprovedBy { get; set; } = "";
        public DateTime ApprovedUtc { get; set; }
    }

    static PolicyApproval? PolicyApprovalFor(string user) =>
        VStore.Get<PolicyApproval>("fmv-policy", "policy:" + user.ToLowerInvariant())?.Value;

    /// <summary>
    /// Daily USDC→USD closes from CoinMarketCap for the range, stored as documented conversions for Noble USDC.
    /// The endpoint returns 400 days ending at timeEnd, so the range is walked back in 400-day windows.
    /// </summary>
    static async Task<int> EnsureUsdcConversionsAsync(FmvJob job, string owner, CancellationToken ct)
    {
        var have = Visible<QuoteConversion>(job.User, "conversion").Where(c => c.QuoteAsset == UsdcNoble).Select(c => c.Date).ToHashSet();
        var need = Enumerable.Range(0, job.To.DayNumber - job.From.DayNumber + 1).Select(i => job.From.AddDays(i))
            .Where(d => !have.Contains(d) && d < DateOnly.FromDateTime(DateTime.UtcNow)).ToHashSet();
        if (need.Count == 0) return 0;
        var added = 0;
        var end = need.Max().AddDays(1);
        while (end > need.Min())
        {
            var endUnix = new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
            var url = $"https://api.coinmarketcap.com/data-api/v3.1/cryptocurrency/historical?id=3408&convertId=2781&interval=1d&timeStart={endUnix - 400L * 86400}&timeEnd={endUnix}";
            var bytes = await CmcHttp.GetByteArrayAsync(url, ct);
            var sha = VStore.ArchiveEvidence($"cmc-usdc-daily-to-{end:yyyyMMdd}.json", bytes, url, job.User);
            using var doc = JsonDocument.Parse(bytes);
            var quotes = doc.RootElement.GetProperty("data").GetProperty("quotes").EnumerateArray().ToList();
            if (quotes.Count == 0) break;
            DateTime Open(JsonElement q) => DateTime.Parse(q.GetProperty("timeOpen").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            foreach (var q in quotes)
            {
                var day = DateOnly.FromDateTime(Open(q));
                if (!need.Contains(day)) continue;
                var c = new QuoteConversion { QuoteAsset = UsdcNoble, Date = day, UsdPerUnit = q.GetProperty("quote").GetProperty("close").GetDecimal(), Source = UsdcSource + $" (raw response sha256 {sha[..12]})" };
                VStore.Put("conversion", $"{c.QuoteAsset}|{day:yyyy-MM-dd}|cmc", owner, c, job.User, day.ToDateTime(TimeOnly.MinValue), sha);
                added++;
            }
            var next = DateOnly.FromDateTime(quotes.Min(Open));
            if (next >= end) break;
            end = next;
        }
        return added;
    }

    /// <summary>Reads Osmosis pool state just before each payout run that does not already have it (read-only archive queries).</summary>
    static async Task FetchPoolStatesAsync(FmvJob job, List<RewardReceipt> receipts, string owner, CancellationToken ct)
    {
        job.Stage = "Finding when the Osmosis pools were created";
        var client = new OsmosisHistory(OsmosisHttp, LoadDashboardConfig().GetValueOrDefault("osmosisArchiveLcd"), (n, b, u) => VStore.ArchiveEvidence(n, b, u, job.User));
        var created = new Dictionary<string, long>();
        long? previous = null;
        foreach (var id in FmvPoolIds.OrderBy(x => long.Parse(x, CultureInfo.InvariantCulture)))
        {
            var h = VStore.Get<long>("pool-created", "pool-created:" + id)?.Value;
            if (h is null or 0)
            {
                // Pool ids are assigned in order, so this pool was created after the previous one.
                job.Note($"Finding when pool {id} was created…");
                h = await client.PoolCreationHeightAsync(id, ct, previous);
                if (h is not null) VStore.Put("pool-created", "pool-created:" + id, ValuationStore.SystemOwner, h.Value, job.User);
            }
            if (h is not null) { created[id] = h.Value; previous = h.Value; }
            else job.Note($"Pool {id}: creation height unknown; it is checked for existence at each height instead.");
        }
        // When the first pool appeared, remembered once read.
        DateTime? firstTime = null;
        if (created.Count > 0)
        {
            var firstHeight = created.Values.Min();
            var key = "pool-created-time:" + firstHeight.ToString(CultureInfo.InvariantCulture);
            firstTime = VStore.Get<DateTime>("pool-created-time", key)?.Value;
            if (firstTime is null)
            {
                try
                {
                    firstTime = await client.BlockTimeAsync(firstHeight, ct);
                    if (firstTime is not null) VStore.Put("pool-created-time", key, ValuationStore.SystemOwner, firstTime.Value, job.User);
                }
                catch (HistoricalFetchException ex) { job.Note("Could not read when the first pool was created: " + ex.Message + " Payouts are still checked by height."); }
            }
        }
        var firstCreatedHeight = created.Count > 0 ? created.Values.Min() : (long?)null;
        if (firstTime is not null) job.Note($"The earliest of these pools was created {firstTime:yyyy-MM-dd HH:mm} UTC; receipts before that have no Osmosis pool state.");

        var runs = new List<DateTime>();
        foreach (var r in receipts.OrderBy(r => r.TimestampUtc))
            if (runs.Count == 0 || r.TimestampUtc - runs[^1] > TimeSpan.FromHours(2)) runs.Add(r.TimestampUtc);
        var existing = Visible<OsmosisPoolSnapshot>(job.User, "osmosis-pool").Where(p => p.BlockTimeUtc != null && p.ServedHeight != null).Select(p => p.BlockTimeUtc!.Value).ToList();
        var todo = runs.Where(t => (firstTime is null || t > firstTime.Value) && !existing.Any(x => x <= t && t - x <= TimeSpan.FromHours(1))).ToList();
        job.Stage = "Reading Osmosis pool state before each payout";
        job.Total = todo.Count;
        job.Done = 0;
        job.Note($"{runs.Count} payout runs; {todo.Count} need pool state from the archive.");
        foreach (var t in todo)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (height, blockTime) = await client.HeightAtOrBeforeAsync(t.AddMinutes(-1), ct);
                if (firstCreatedHeight is long fc && height < fc) { job.Done++; continue; } // no pool existed yet
                foreach (var id in FmvPoolIds)
                {
                    if (created.TryGetValue(id, out var c) && c > height) continue;
                    try
                    {
                        var snap = await client.FetchPoolAsync(id, height, ct, sellDenom: OsmosisAssets.OptDenom, takerFeeReuseBlocks: 200_000);
                        snap.CreatedHeight = created.TryGetValue(id, out var ch) ? ch : null;
                        VStore.Put("osmosis-pool", snap.Id, owner, snap, job.User, snap.BlockTimeUtc ?? blockTime);
                    }
                    catch (HistoricalFetchException ex) { job.Note($"Pool {id} at {height}: {ex.Message}"); }
                }
            }
            catch (HistoricalFetchException ex) { job.Note($"Payout at {t:yyyy-MM-dd HH:mm}: {ex.Message} (missing data, not an empty market)"); }
            job.Done++;
        }
    }

    static async Task RunFmvJobAsync(FmvJob job, bool isAdmin)
    {
        var ct = job.Cts.Token;
        var owner = isAdmin ? ValuationStore.SystemOwner : job.User;
        var policy = FmvPolicy.Current;
        try
        {
            job.Stage = "Reading receipts";
            var all = await ReceiptsForUserAsync(job.User, job.Address, ct);
            var receipts = all.Where(r => { var d = DateOnly.FromDateTime(r.TimestampUtc); return d >= job.From && d <= job.To; }).ToList();
            job.Note($"{receipts.Count} receipts from {job.From:yyyy-MM-dd} to {job.To:yyyy-MM-dd}.");

            if (job.FetchMarkets) await FetchPoolStatesAsync(job, receipts, owner, ct);

            job.Stage = "Recording USDC→USD rates";
            job.Total = 0;
            try { job.Note($"{await EnsureUsdcConversionsAsync(job, owner, ct)} daily USDC→USD rates recorded from CoinMarketCap."); }
            catch (HttpRequestException ex) { job.Note("USDC→USD rates could not be read: " + ex.Message + " Osmosis prices stay unconverted (flagged)."); }

            // Reference staleness: the hourly closes before each receipt.
            job.Stage = "Checking the reference price history";
            var hours = receipts.SelectMany(r => Enumerable.Range(1, policy.StaleHours).Select(k => r.TimestampUtc.AddHours(-k))).Distinct().ToList();
            var priorPrices = await TokenPriceService.GetPricesAtAsync(hours, ct);
            var daily = (await TokenPriceService.GetDailyCandlesAsync(ct)).GroupBy(d => DateOnly.FromDateTime(d.DateUtc)).ToDictionary(g => g.Key, g => g.Last());
            var marketDays = new Dictionary<DateOnly, MarketDay>();
            try { foreach (var m in await MarketVolumeService.GetDailyAsync(ct)) marketDays[DateOnly.FromDateTime(m.DateUtc)] = m; }
            catch (Exception ex) when (ex is not OperationCanceledException) { job.Note("Osmosis and exchange daily volumes unavailable: " + ex.Message); }

            job.Stage = "Gathering market evidence for each receipt";
            var pools = Visible<OsmosisPoolSnapshot>(job.User, "osmosis-pool").Where(p => p.ServedHeight != null && p.BlockTimeUtc != null && p.Exists).ToList();
            var groups = pools.GroupBy(p => p.ServedHeight!.Value).Select(g => (Time: g.First().BlockTimeUtc!.Value, Height: g.Key, Pools: g.ToList())).OrderBy(g => g.Time).ToList();
            var times = groups.Select(g => g.Time).ToList();
            var venues = Visible<VenueStatus>(job.User, "venue-status");
            var conversions = Visible<QuoteConversion>(job.User, "conversion");
            var profile = ProfileFor(job.User);
            var settings = new AnalysisSettings { Thresholds = Array.Empty<decimal>(), Increments = 50 };
            job.Total = receipts.Count;
            job.Done = 0;
            var candidates = new List<FmvCandidate>();
            foreach (var r in receipts)
            {
                ct.ThrowIfCancellationRequested();
                var day = DateOnly.FromDateTime(r.TimestampUtc);
                var e = new ReceiptMarketEvidence
                {
                    ReferenceUnitUsd = r.Reference.UnitPriceUsd,
                    ReferenceInterval = r.Reference.UnitPriceUsd is null ? null
                        : r.Reference.Source?.Interval?.StartsWith("close of the 1-hour", StringComparison.Ordinal) == true ? "1h" : "1d",
                };
                if (r.Reference.UnitPriceUsd is { } refPrice && e.ReferenceInterval == "1h")
                {
                    var unchanged = 0;
                    for (var k = 1; k <= policy.StaleHours; k++)
                    {
                        if (!priorPrices.TryGetValue(r.TimestampUtc.AddHours(-k), out var pp) || pp is null || pp.Interval != "1h" || pp.PriceUsd != refPrice) break;
                        unchanged++;
                    }
                    e.ReferenceUnchangedHours = unchanged;
                    e.ReferenceStale = unchanged >= policy.StaleHours;
                }
                if (daily.TryGetValue(day, out var dc) && dc.VolumeUsd is { } vol && dc.CloseUsd > 0) e.ReportedDayVolumeOpt = vol / dc.CloseUsd;
                if (marketDays.TryGetValue(day, out var md))
                {
                    e.OsmosisDayTxs = md.OsmosisTxs;
                    e.OsmosisDayVolumeOpt = md.OsmosisGrossOpt;
                    if (md.OsmosisPriceUsd is { } sp && sp > 0)
                    {
                        // Osmosis swap prices are stablecoin-quoted; convert at the documented USDC rate where there is one.
                        var rate = conversions.FirstOrDefault(c => c.QuoteAsset == UsdcNoble && c.Date == day);
                        e.OsmosisDaySwapPriceUsd = rate is null ? sp : sp * rate.UsdPerUnit;
                        if (rate is null) e.Notes.Add("Day's Osmosis swap price is in stablecoins; no USDC→USD rate for the day, treated at par.");
                    }
                }
                foreach (var v in venues.Where(v => v.Kind == VenueAccessKind.Deposits).Select(v => v.Venue).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var st = VenueAccess.StatusAt(venues, v, VenueAccessKind.Deposits, r.TimestampUtc, profile.Jurisdiction);
                    if (st is not null && st.State != VenueAccessState.Available)
                        e.DepositRestrictions.Add($"{v} deposits {st.State.ToString().ToLowerInvariant()} ({st.Verification}{(st.ImplementationVerified ? "" : ", effective time not verified")}).");
                }

                // Contemporaneous Osmosis pool state: marginal price (no size effect) and the modeled whole-receipt sale.
                var idx = times.BinarySearch(r.TimestampUtc);
                idx = idx >= 0 ? idx : ~idx - 1;
                if (idx >= 0 && r.TimestampUtc - groups[idx].Time <= TimeSpan.FromHours(policy.MaxMarketAgeHours))
                {
                    var g = groups[idx];
                    var (routes, _) = PoolRouteMarket.DiscoverRoutes(g.Pools, g.Height);
                    var markets = routes.Where(rt => PoolRouteMarket.Unsupported(rt) is null).Select(rt => new PoolRouteMarket(rt)).ToList();
                    var best = markets.Select(m => (m, p: m.MarginalPrice())).Where(x => x.p is > 0).OrderByDescending(x => x.p).FirstOrDefault();
                    if (best.m is not null)
                    {
                        e.OsmosisObservedUtc = g.Time;
                        e.OsmosisRoute = best.m.Id;
                        e.OsmosisSpotQuote = best.p;
                        e.OsmosisSpotQuoteAsset = best.m.QuoteAsset;
                        e.OsmosisSpotUsd = ValuationMath.ToUsd(best.p, best.m.QuoteAsset, day, conversions).Usd;
                        e.ConversionDocumented = e.OsmosisSpotUsd is not null;
                        var access = VenueAccess.Decide(venues, "Osmosis", r.TimestampUtc, profile, onChainVenue: true);
                        var evidence = markets.Select(m => new CandidateMarket(m, access with { TaxpayerAccessible = true, Blocking = null }, null)).ToList();
                        var analysis = ReceiptAnalyzer.Analyze(r, evidence, conversions, settings);
                        analysis.Assumptions.Add("Market-evidence universe: the market as it stood, regardless of the taxpayer's own access (recorded separately).");
                        e.ModeledProceedsBeforeFeesUsd = analysis.ExecutableOpt >= analysis.RequestedOpt ? analysis.ModeledOutputBeforeFeesUsd : null;
                        e.ModeledFillRatio = analysis.ModeledFillRatio;
                        if (e.ModeledProceedsBeforeFeesUsd is { } mp && e.OsmosisSpotUsd is { } su && su > 0)
                            e.ModeledPriceImpact = 1m - mp / (su * r.QuantityOpt);
                        else if (analysis.ExecutableOpt < analysis.RequestedOpt)
                            e.Notes.Add($"The pools could absorb {analysis.ExecutableOpt:N0} of {analysis.RequestedOpt:N0} OPT in one sale; the rest is not shown to be worthless.");
                        VStore.Put("analysis-market", "analysis-market:" + job.User.ToLowerInvariant() + ":" + r.Id, job.User, analysis, job.User, r.TimestampUtc);
                    }
                }
                var cand = FmvCandidateBuilder.Build(r, e, policy, DateTime.UtcNow);
                VStore.Put("fmv-candidate", CandidateId(job.User, r.Id), job.User, cand, job.User, r.TimestampUtc);
                candidates.Add(cand);
                job.Done++;
            }

            // Earlier drafts from the previous builder are withdrawn; approved values and the user's own reviews are kept.
            var reviews = VStore.Reviews(job.User);
            var inRange = receipts.Select(r => r.Id).ToHashSet();
            foreach (var old in ValuationReviews.Current(reviews).Where(v => v.Status == ReviewStatus.Proposed && v.Source?.StartsWith("fmv-builder", StringComparison.Ordinal) == true && v.ReceiptIds.All(inRange.Contains)))
                VStore.AppendReview(job.User, ValuationReviews.NextVersion(reviews, old, ReviewStatus.Withdrawn, job.User, DateTime.UtcNow));

            job.Result = new
            {
                policy = policy.Version,
                receipts = candidates.Count,
                qualifying = candidates.Count(c => c.Qualifies),
                exceptions = candidates.Count(c => !c.Qualifies && c.ValueUsd != null),
                noCandidate = candidates.Count(c => c.ValueUsd == null),
                bySource = candidates.GroupBy(c => c.Source ?? "none").Select(g => new { source = g.Key, count = g.Count(), valueUsd = g.Sum(c => c.ValueUsd ?? 0) }),
                byTrigger = candidates.SelectMany(c => c.Flags).GroupBy(f => f.Trigger).Select(g => new { trigger = g.Key.ToString(), count = g.Count() }),
            };
            job.Note($"{candidates.Count(c => c.Qualifies)} candidates qualify under {policy.Version}; {candidates.Count(c => !c.Qualifies)} need review.");
            job.Stage = "Done";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { job.Stage = "Cancelled"; }
        catch (Exception ex) { job.Error = ex.Message; job.Stage = "Failed"; }
        finally { job.Running = false; job.FinishedUtc = DateTime.UtcNow; }
    }

    static List<EvidenceRef> CandidateEvidence(FmvCandidate c, RewardReceipt r)
    {
        var ev = new List<EvidenceRef>();
        if (r.Reference.Source is { } src)
            ev.Add(new EvidenceRef { Kind = "url", Url = src.Endpoint, EffectiveUtc = src.ObservedUtc, CollectedUtc = c.BuiltUtc, Description = $"{src.Provider} {src.Field}, {src.Interval}, {src.QuoteCurrency}" });
        var e = c.Evidence;
        if (e.OsmosisObservedUtc is not null)
            ev.Add(new EvidenceRef { Kind = "chain-query", EffectiveUtc = e.OsmosisObservedUtc, CollectedUtc = c.BuiltUtc, Description = $"Osmosis {e.OsmosisRoute}: marginal price {e.OsmosisSpotQuote:0.000000} {e.OsmosisSpotQuoteAsset}/OPT" + (e.ModeledProceedsBeforeFeesUsd is { } m ? $"; whole-receipt modeled sale {m:0.00} USD before fees" : "") });
        if (e.OsmosisDaySwapPriceUsd is not null)
            ev.Add(new EvidenceRef { Kind = "chain-query", EffectiveUtc = r.TimestampUtc.Date, CollectedUtc = c.BuiltUtc, Description = $"Osmosis swaps that UTC day: {e.OsmosisDayTxs} swap(s), {e.OsmosisDayVolumeOpt:N0} OPT, average {e.OsmosisDaySwapPriceUsd:0.000000} USD/OPT" });
        ev.Add(new EvidenceRef { Kind = "policy", Description = $"{c.PolicyVersion}: flags {(c.Flags.Count == 0 ? "none" : string.Join(", ", c.Flags.Select(f => f.Trigger)))}", CollectedUtc = c.BuiltUtc });
        return ev;
    }

    static void MapFmv(WebApplication app)
    {
        app.MapGet("/api/valuation/fmv/policy", (HttpContext ctx) =>
            VJson(new { policy = FmvPolicy.Current, description = FmvPolicy.Current.Describe(), approval = PolicyApprovalFor(DashboardAuth.UserName(ctx)) }));

        // The user approves the policy version once; bulk approval of qualifying candidates then runs under it.
        app.MapPost("/api/valuation/fmv/policy", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (!(body.TryGetProperty("confirm", out var cf) && cf.ValueKind == JsonValueKind.True)) return VError("Approval needs explicit confirmation.");
            var user = DashboardAuth.UserName(ctx);
            var a = new PolicyApproval { Version = FmvPolicy.Current.Version, Description = FmvPolicy.Current.Describe(), ApprovedBy = user, ApprovedUtc = DateTime.UtcNow };
            VStore.Put("fmv-policy", "policy:" + user.ToLowerInvariant(), user, a, user);
            return VJson(a);
        });

        app.MapPost("/api/valuation/fmv/build", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            var address = (body.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "").Trim().ToLowerInvariant();
            if (!OptioAddress.IsMatch(address)) return VError("Enter a valid optio1... wallet address.");
            if (!DateOnly.TryParse(body.TryGetProperty("from", out var f) ? f.GetString() : null, CultureInfo.InvariantCulture, out var from) ||
                !DateOnly.TryParse(body.TryGetProperty("to", out var t) ? t.GetString() : null, CultureInfo.InvariantCulture, out var to) || to < from)
                return VError("Give a valid period.");
            if (to.DayNumber - from.DayNumber > 400) return VError("Build at most about one year at a time.");
            var user = DashboardAuth.UserName(ctx);
            var key = JobKey(user, address);
            if (_fmvJobs.TryGetValue(key, out var running) && running.Running) return VJson(running);
            var job = new FmvJob { User = user, Address = address, From = from, To = to, FetchMarkets = !(body.TryGetProperty("fetchMarkets", out var fm) && fm.ValueKind == JsonValueKind.False) };
            _fmvJobs[key] = job;
            var isAdmin = DashboardAuth.IsAdmin(ctx);
            _ = Task.Run(() => RunFmvJobAsync(job, isAdmin));
            return VJson(job);
        });

        app.MapGet("/api/valuation/fmv/status", (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            if (address is null) return VError("Enter a valid optio1... wallet address.");
            return _fmvJobs.TryGetValue(JobKey(DashboardAuth.UserName(ctx), address), out var job) ? VJson(job) : VJson(new { stage = "Not started", running = false });
        });

        app.MapPost("/api/valuation/fmv/cancel", (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            if (address is not null && _fmvJobs.TryGetValue(JobKey(DashboardAuth.UserName(ctx), address), out var job)) job.Cts.Cancel();
            return VJson(new { cancelled = true });
        });

        // Candidates with their receipt and current decision, for the review page.
        app.MapGet("/api/valuation/fmv/candidates", async (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            var range = RangeParams(ctx);
            if (address is null || range is null) return VError("Give the address and period.");
            var user = DashboardAuth.UserName(ctx);
            var receipts = (await ReceiptsForUserAsync(user, address, ctx.RequestAborted))
                .Where(r => { var d = DateOnly.FromDateTime(r.TimestampUtc); return d >= range.Value.From && d <= range.Value.To; }).ToList();
            var byId = receipts.ToDictionary(r => r.Id);
            var current = ValuationReviews.Current(VStore.Reviews(user));
            var selected = ValuationReviews.Selected(current, byId);
            var cands = VStore.List<FmvCandidate>("fmv-candidate", user).Where(c => c.Owner != ValuationStore.SystemOwner).Select(c => c.Value).Where(c => byId.ContainsKey(c.ReceiptId)).ToDictionary(c => c.ReceiptId);
            var rows = receipts.Select(r =>
            {
                cands.TryGetValue(r.Id, out var c);
                selected.TryGetValue(r.Id, out var s);
                return new
                {
                    receiptId = r.Id, timestampUtc = r.TimestampUtc, category = r.Category.ToString(), classification = r.Classification.ToString(), quantityOpt = r.QuantityOpt,
                    referenceUsd = r.Reference.ValueUsd, candidate = c, selected = s,
                    adjustmentUsd = s is not null && r.Reference.ValueUsd is { } rv ? s.ValueUsd - rv : (decimal?)null,
                };
            }).ToList();
            var policyApproval = PolicyApprovalFor(user);
            return VJson(new
            {
                policy = FmvPolicy.Current.Version,
                policyApproved = policyApproval?.Version == FmvPolicy.Current.Version,
                totals = new
                {
                    receipts = rows.Count,
                    withCandidate = rows.Count(x => x.candidate is not null),
                    qualifying = rows.Count(x => x.candidate?.Qualifies == true && x.selected is null),
                    qualifyingUsd = rows.Where(x => x.candidate?.Qualifies == true && x.selected is null).Sum(x => x.candidate!.ValueUsd ?? 0),
                    exceptions = rows.Count(x => x.candidate is { Qualifies: false } && x.selected is null),
                    approved = rows.Count(x => x.selected is not null),
                    approvedUsd = rows.Where(x => x.selected is not null).Sum(x => x.selected!.ValueUsd),
                    referenceUsd = rows.Sum(x => x.referenceUsd ?? 0),
                    unresolvedOpt = rows.Where(x => x.selected is null).Sum(x => x.quantityOpt),
                    unresolvedReferenceUsd = rows.Where(x => x.selected is null).Sum(x => x.referenceUsd ?? 0),
                },
                byTrigger = rows.Where(x => x.selected is null && x.candidate is not null).SelectMany(x => x.candidate!.Flags.Select(f => f.Trigger)).GroupBy(t => t).Select(g => new { trigger = g.Key.ToString(), count = g.Count() }),
                rows,
            });
        });

        // Bulk approval: only candidates that qualify under the policy version the user approved.
        app.MapPost("/api/valuation/fmv/approve-qualifying", async (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            var range = RangeParams(ctx);
            if (address is null || range is null) return VError("Give the address and period.");
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (!(body.TryGetProperty("confirm", out var cf) && cf.ValueKind == JsonValueKind.True)) return VError("Approval needs explicit confirmation.");
            var user = DashboardAuth.UserName(ctx);
            if (PolicyApprovalFor(user)?.Version != FmvPolicy.Current.Version) return VError($"Approve policy {FmvPolicy.Current.Version} first.");
            var receipts = (await ReceiptsForUserAsync(user, address, ctx.RequestAborted)).ToDictionary(r => r.Id);
            var reviews = VStore.Reviews(user);
            var current = ValuationReviews.Current(reviews);
            var taken = current.Where(v => v.Status is ReviewStatus.Approved or ReviewStatus.Proposed).SelectMany(v => v.ReceiptIds).ToHashSet();
            var approved = 0;
            foreach (var c in VStore.List<FmvCandidate>("fmv-candidate", user).Where(x => x.Owner != ValuationStore.SystemOwner).Select(x => x.Value))
            {
                if (!c.Qualifies || c.ValueUsd is null || c.Method is null || c.PolicyVersion != FmvPolicy.Current.Version) continue;
                if (!receipts.TryGetValue(c.ReceiptId, out var r) || taken.Contains(c.ReceiptId)) continue;
                var d = DateOnly.FromDateTime(r.TimestampUtc);
                if (d < range.Value.From || d > range.Value.To) continue;
                var draft = new ValuationReview
                {
                    ReceiptIds = { r.Id }, SelectedValueUsd = c.ValueUsd.Value, Method = c.Method.Value, Explanation = c.Explanation,
                    Evidence = CandidateEvidence(c, r), Reviewer = user, ReferenceValueUsdAtReview = r.Reference.ValueUsd, Source = "policy:" + c.PolicyVersion,
                };
                if (ValuationReviews.Validate(draft, receipts).Count > 0) continue;
                VStore.AppendReview(user, ValuationReviews.NextVersion(reviews, draft, ReviewStatus.Approved, user, DateTime.UtcNow));
                approved++;
            }
            return VJson(new { approved, policy = FmvPolicy.Current.Version });
        });

        // Exceptions: one explained decision applied to the chosen receipts, each getting the chosen option's value.
        app.MapPost("/api/valuation/fmv/decide", async (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            if (address is null) return VError("Give the wallet address.");
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (!(body.TryGetProperty("confirm", out var cf) && cf.ValueKind == JsonValueKind.True)) return VError("Approval needs explicit confirmation.");
            var ids = body.TryGetProperty("receiptIds", out var ie) ? ie.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new();
            var option = body.TryGetProperty("option", out var oe) ? oe.GetString() ?? "" : "";
            var explanation = (body.TryGetProperty("explanation", out var ee) ? ee.GetString() ?? "" : "").Trim();
            if (ids.Count == 0) return VError("Choose receipts.");
            if (explanation.Length < MinExceptionExplanation) return VError($"Explain the decision (at least {MinExceptionExplanation} characters).");
            var user = DashboardAuth.UserName(ctx);
            var receipts = (await ReceiptsForUserAsync(user, address, ctx.RequestAborted)).ToDictionary(r => r.Id);
            var reviews = VStore.Reviews(user);
            var approved = 0;
            var errors = new List<string>();
            foreach (var id in ids.Distinct())
            {
                var c = VStore.Get<FmvCandidate>("fmv-candidate", CandidateId(user, id))?.Value;
                if (c is null || !receipts.TryGetValue(id, out var r)) { errors.Add($"{id}: no candidate; build candidates first."); continue; }
                var opt = c.Options.FirstOrDefault(o => o.Key == option);
                if (opt?.ValueUsd is null) { errors.Add($"{r.TimestampUtc:yyyy-MM-dd HH:mm}: option '{option}' is not available for this receipt."); continue; }
                var draft = new ValuationReview
                {
                    ReceiptIds = { id }, SelectedValueUsd = opt.ValueUsd.Value, Method = opt.Method,
                    Explanation = $"{explanation}\n[{opt.Label}: {opt.Detail}. Flags: {(c.Flags.Count == 0 ? "none" : string.Join("; ", c.Flags.Select(f => f.Detail)))}]",
                    Evidence = CandidateEvidence(c, r), Reviewer = user, ReferenceValueUsdAtReview = r.Reference.ValueUsd, Source = "exception-decision:" + c.PolicyVersion,
                };
                var v = ValuationReviews.Validate(draft, receipts);
                if (v.Count > 0) { errors.Add($"{r.TimestampUtc:yyyy-MM-dd HH:mm}: {string.Join(" ", v)}"); continue; }
                // A later decision for the same receipt supersedes the earlier one; both stay in the history.
                VStore.AppendReview(user, ValuationReviews.NextVersion(reviews, draft, ReviewStatus.Approved, user, DateTime.UtcNow));
                approved++;
            }
            return VJson(new { approved, errors });
        });

        // Import reviewed or professionally supported values (e.g. an illiquid-market valuation) from CSV.
        app.MapPost("/api/valuation/fmv/import", async (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            if (address is null) return VError("Give the wallet address.");
            string text;
            try { text = await BodyTextAsync(ctx); } catch (InvalidDataException ex) { return VError(ex.Message, 413); }
            var name = ctx.Request.Query["name"].ToString();
            name = string.IsNullOrWhiteSpace(name) ? "valuations.csv" : name;
            var user = DashboardAuth.UserName(ctx);
            var receipts = (await ReceiptsForUserAsync(user, address, ctx.RequestAborted)).ToDictionary(r => r.Id);
            var (drafts, errors, sha) = ParseValuationImport(text, name, receipts, user);
            if (errors.Count > 0) return VJson(new { imported = 0, errors }, 400);
            VStore.ArchiveEvidence(name, Encoding.UTF8.GetBytes(text), null, user);
            foreach (var (draft, status) in drafts)
            {
                var next = ValuationReviews.NextVersion(VStore.Reviews(user), draft, status, draft.Reviewer, DateTime.UtcNow);
                next.Explanation += $"\n[Imported by {user} from {name}, sha256 {sha[..12]}]";
                VStore.AppendReview(user, next);
            }
            return VJson(new { imported = drafts.Count, approved = drafts.Count(d => d.Status == ReviewStatus.Approved), proposed = drafts.Count(d => d.Status == ReviewStatus.Proposed), sourceSha256 = sha });
        });

        // Approve every proposed value in the period (the user's own proposals or imported ones), with one confirmation.
        app.MapPost("/api/valuation/reviews/approve-proposed", async (HttpContext ctx) =>
        {
            var address = AddressParam(ctx);
            var range = RangeParams(ctx);
            if (address is null || range is null) return VError("Give the address and period.");
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            if (!(body.TryGetProperty("confirm", out var cf) && cf.ValueKind == JsonValueKind.True)) return VError("Approval needs explicit confirmation.");
            var user = DashboardAuth.UserName(ctx);
            var receipts = (await ReceiptsForUserAsync(user, address, ctx.RequestAborted)).ToDictionary(r => r.Id);
            var inRange = receipts.Values.Where(r => { var d = DateOnly.FromDateTime(r.TimestampUtc); return d >= range.Value.From && d <= range.Value.To; }).Select(r => r.Id).ToHashSet();
            var reviews = VStore.Reviews(user);
            var approved = 0;
            var failed = new List<string>();
            foreach (var v in ValuationReviews.Current(reviews).Where(v => v.Status == ReviewStatus.Proposed && v.ReceiptIds.All(inRange.Contains)))
            {
                var errors = ValuationReviews.Validate(v, receipts);
                if (errors.Count > 0) { failed.Add($"{v.ReviewId[..8]}: {string.Join(" ", errors)}"); continue; }
                VStore.AppendReview(user, ValuationReviews.NextVersion(reviews, v, ReviewStatus.Approved, user, DateTime.UtcNow));
                approved++;
            }
            return VJson(new { approved, failed });
        });
    }

    public const string ValuationImportHeader = "receipt_id,tx_hash,event_index,value_usd,method,status,reviewer,review_date,explanation,evidence_url,evidence_description";

    /// <summary>
    /// Parses an approved-valuation import. Each row names one receipt (by receipt_id, or tx_hash + event_index) and
    /// a value. Rows that fail are reported and nothing is imported; a missing value is an error, never zero.
    /// </summary>
    internal static (List<(ValuationReview Draft, ReviewStatus Status)> Drafts, List<string> Errors, string Sha256) ParseValuationImport(
        string text, string name, IReadOnlyDictionary<string, RewardReceipt> receipts, string user)
    {
        var byTxEvent = receipts.Values.Where(r => r.TxHash != null && r.EventIndex != null).GroupBy(r => (r.TxHash!.ToUpperInvariant(), r.EventIndex!.Value)).ToDictionary(g => g.Key, g => g.ToList());
        var sha = ReceiptIdentity.Sha256Hex(text);
        var errors = new List<string>();
        var drafts = new List<(ValuationReview, ReviewStatus)>();
        var seen = new HashSet<string>();
        foreach (var (row, f) in Csv.ParseWithHeader(text))
        {
            RewardReceipt? r = null;
            var rid = f.GetValueOrDefault("receipt_id", "");
            if (rid.Length > 0) receipts.TryGetValue(rid, out r);
            else if (f.GetValueOrDefault("tx_hash", "") is { Length: > 0 } tx && int.TryParse(f.GetValueOrDefault("event_index", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ev)
                     && byTxEvent.TryGetValue((tx.ToUpperInvariant(), ev), out var list))
            {
                if (list.Count > 1) { errors.Add($"Row {row}: tx {tx} event {ev} matches {list.Count} receipts; use receipt_id."); continue; }
                r = list[0];
            }
            if (r is null) { errors.Add($"Row {row}: no matching receipt for this wallet (give receipt_id, or tx_hash and event_index)."); continue; }
            if (!seen.Add(r.Id)) { errors.Add($"Row {row}: the receipt appears twice in the file."); continue; }
            var value = Csv.Dec(f, "value_usd", errors, row);
            if (value is null) { errors.Add($"Row {row}: value_usd is required (leave the row out to keep a receipt unresolved; blank is never zero)."); continue; }
            var method = f.GetValueOrDefault("method", "imported").ToLowerInvariant() switch
            {
                "reference" => ReviewMethod.AcceptReference,
                "appraisal" or "illiquid-market" or "imported" or "" => ReviewMethod.ImportedValuation,
                "alternative-market" => ReviewMethod.AlternativeMarketPrice,
                "timing" => ReviewMethod.TimingApproximation,
                "observed-transaction" => ReviewMethod.ObservedTransaction,
                "other" => ReviewMethod.Other,
                _ => (ReviewMethod?)null,
            };
            if (method is null) { errors.Add($"Row {row}: method must be reference, appraisal, illiquid-market, alternative-market, timing, observed-transaction or other."); continue; }
            var status = f.GetValueOrDefault("status", "proposed").Equals("approved", StringComparison.OrdinalIgnoreCase) ? ReviewStatus.Approved : ReviewStatus.Proposed;
            var reviewer = f.GetValueOrDefault("reviewer", "");
            if (status == ReviewStatus.Approved && reviewer.Length == 0) { errors.Add($"Row {row}: an approved row needs the reviewer's name."); continue; }
            var evidence = new List<EvidenceRef> { new() { Kind = "file", FileName = name, Sha256 = sha, Description = $"Imported valuation file, row {row}", CollectedUtc = DateTime.UtcNow } };
            var eu = f.GetValueOrDefault("evidence_url", "");
            var ed = f.GetValueOrDefault("evidence_description", "");
            if (eu.Length > 0 || ed.Length > 0) evidence.Add(new EvidenceRef { Kind = eu.Length > 0 ? "url" : "note", Url = eu.Length > 0 ? eu : null, Description = ed });
            var reviewDate = f.GetValueOrDefault("review_date", "");
            var draft = new ValuationReview
            {
                ReceiptIds = { r.Id }, SelectedValueUsd = value.Value, Method = method.Value,
                Explanation = (f.GetValueOrDefault("explanation", "") is { Length: > 0 } ex ? ex : method == ReviewMethod.AcceptReference ? "Reference value accepted (imported)." : "")
                    + (reviewDate.Length > 0 ? $" (reviewed {reviewDate})" : ""),
                Evidence = evidence, Reviewer = reviewer.Length > 0 ? reviewer : user, ReferenceValueUsdAtReview = r.Reference.ValueUsd, Source = $"import:{sha[..12]}",
            };
            if (draft.Method != ReviewMethod.AcceptReference && f.GetValueOrDefault("explanation", "").Trim().Length < MinExceptionExplanation)
            { errors.Add($"Row {row}: explain a value other than the reference (at least {MinExceptionExplanation} characters)."); continue; }
            var v = ValuationReviews.Validate(draft, receipts);
            if (v.Count > 0) { errors.Add($"Row {row}: {string.Join(" ", v)}"); continue; }
            drafts.Add((draft, status));
        }
        return (drafts, errors, sha);
    }
}
