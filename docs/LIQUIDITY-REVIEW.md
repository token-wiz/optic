# OPT Historical Liquidity and Distribution Analysis

The **Liquidity & Distribution Review** page (Taxes menu, `/page/liquidity-review`) supports a reviewer deciding the value of OPT reward receipts. It keeps three layers apart:

| Layer | What it holds | Where |
|---|---|---|
| Reference valuation | The CoinMarketCap price for each receipt, with its endpoint, field, candle and interval. Never edited. | `RewardReceipt.Reference` |
| Market-capacity evidence | Historical order books, Osmosis pool state, venue access, simulations. Never writes a tax value. | `odata/valuation.db`, `Services/Valuation/*` |
| Reviewed tax valuation | A value a person explicitly approved, with method, evidence and history. | `reviews` table (append-only) |

> Historical market-capacity analysis supports valuation review. It is not an IRS-approved discount formula, a guarantee of sale proceeds, or a substitute for a tax determination. Network scenarios are hypothetical. Missing information is not evidence of zero value.

## Receipts

* Read from the Optio chain: every transfer **event** from the distribution account (`optio103r5…dst`) or the distribution module to the wallet. Receipt id = `optio:<tx>:e<event>.<position>:<wallet>:uOPT`, so two payments in one transaction stay separate and a re-read gives the same ids.
* Imported receipts (`/api/valuation/templates/receipts.csv`) without a transaction hash are identified by the source file's SHA-256 and row number; they are never dropped. With a hash but no event index, the n-th identical payment in the tx gets ordinal n.
* Quantities are exact base units (`uOPT`, 6 decimals, per cosmos/chain-registry `optio/assetlist.json`).
* **Node vs staking:** the chain does not label payouts (OptioNetwork/optio has no category field; see *Chain facts*).
  * **User's rule:** on a UTC day with more than one payout from the distribution account, the smaller payout(s) are staking/participation rewards and the larger are node rewards. Each day is split at the largest size gap, and only when the smaller side is at most 85% of the larger (`PayoutClassifier`).
  * **Where it applies:** the Tax Report, My Distributions and the review page. It replaced the earlier "under 10% of the day's largest" rule, which labelled 2026 staking payouts as node because they are similar in size to node payouts.
  * **Status:** labels from the rule are **Inferred** until the user clicks *Confirm this rule* on the review page. That records who confirmed it and when, and it can be revoked. They then become **Confirmed**.
  * **Days the rule cannot decide:** single-payout days and days with same-size payouts stay **Uncertain** with their basis shown, until the user labels them one by one.
  * **Distribution-module payments** are chain staking rewards (Confirmed).
* Reference price: `https://api.coinmarketcap.com/data-api/v3.1/cryptocurrency/historical?id=35828&convertId=2781`, field `quote.close`, the 1-hour candle containing the receipt; the UTC daily close only when the hourly candle is unavailable (recorded as a fallback). This is a CoinMarketCap market-pair price, not an execution price.
* Times are stored in UTC. Daily summaries use the `tz` setting in `optic.conf` (America/New_York, with daylight-saving transitions); network data from the chain is per UTC day.

## Network distribution schedule

* Each period has a start **and end** date. A date no period covers is *unknown*; nothing is extended (no 60M default).
* Per period: scheduled, observed and user-assumed distribution per day, **emitted (minted) per day**, minted/burned totals, reserve releases, unlocks, verification status and evidence. Emission is not distribution: minted OPT that was not paid out is reported as *emitted but not distributed*. Selling scenarios use distributed amounts only.
* *Record observed daily totals* (admin) stores the synced chain sum of all transfers from the distribution account per UTC day as **Combined** (node + participation together). A node-only share needs node-only data; the page labels a combined comparison as such.
* Formulas (all return *unknown with a reason* when an input is missing):
  * `ShareOfNodeDistributions = PersonalNodeOPT / ActualNetworkNodeDistributions` (observed only)
  * `ScenarioSellingOPT = SelectedNetworkDistributionOPT × ScenarioPercentage` (1%, 5%, 10% or custom; labelled hypothetical)
  * `NetMintedOPT = MintedOPT − BurnedOPT`; `NetMintSupplyGrowth = NetMintedOPT / TotalSupplyAtPeriodStart`
* Reference notional = scenario OPT × the average reference price of your receipts in the period. It is not the cash needed to absorb that sale. Supply growth is not assumed to move the price, and the page warns that expected distributions may already be in the reference price.

## Historical markets

**Snapshot rule.** A receipt uses the latest snapshot observed at or before its time and no older than the maximum age (default 24 h). Later data is listed as corroboration only. Two universes are kept:

* *Market-evidence universe*: every venue with data.
* *Taxpayer-accessible universe*: venues where effective-dated statuses show trading and deposits (and, for exchanges, jurisdiction access) were available to you. An announced change without a verified effective time counts as unknown access. A US-only restriction does not remove a venue from the evidence universe.

**Centralized exchanges** (CSV import, `templates/orderbook.csv`): bids are consumed highest first, taker fee deducted. A truncated book that runs out gives *PartialCapacity* with the residual marked unmodeled. Trades or candles alone give *InsufficientDepthData*; no order book is invented from them.

**Osmosis** (read-only, default archive `https://lcd.archive.osmosis.zone`, override with `osmosisArchiveLcd=` in `optic.conf`):

* State is requested with `x-cosmos-block-height`. A response whose `Grpc-Metadata-X-Cosmos-Block-Height` differs from the request is rejected. Pruned state, timeouts or errors are reported as missing data, never as zero liquidity.
* A pool missing at a height (`not found` or `failed to find route for pool id`) is recorded as *did not exist*. Creation heights come from the `pool_created` event (verified: pool 3278 at 49,880,604; pool 3329 at 51,346,389). A funded pool with no trades is still usable.
* Supported pool types:

| Type | Math | Source |
|---|---|---|
| Concentrated liquidity | Exact-in swap over initialized ticks, Osmosis tick↔price mapping, 18/36-decimal fixed point with Osmosis rounding, spread factor per step (v25+ formula; pre-v25 formula available) | osmosis-labs/osmosis @ e4ae1fcd `x/concentrated-liquidity` |
| Balancer (weighted) | `calcOutGivenIn` with `osmomath.Pow`/`PowApprox` | `x/gamm/pool-models/balancer` |
| Taker fee | Deducted before the pool (`trading_pair_takerfee`, else `default_taker_fee` at that height) | `x/poolmanager` |
| Stableswap, CosmWasm | **Not implemented**: reported as *UnsupportedPoolType* | |

* Routes: direct pools and two-hop routes through pools observed at the same height. A split never uses the same pool twice. Coverage is limited to the stored pools; no global optimality is claimed.
* Atomicity: an exact-in swap the pool cannot fill fails as a whole (Osmosis reverts). Smaller trades are evaluated separately. A route that fails on a later hop changes no state.
* Validation: *Replay swap* re-runs an observed swap against the previous block's state. A CL pool's `token_swapped.tokens_in` is net of the spread factor, so the gross input is reconstructed first. Verified on pool 3278, tx `99A50649…F385` (height 50,298,598): modeled 37,907,329 = observed 37,907,329. Remaining uncertainty is intrablock: earlier transactions in the same block can move the pool.

**Quote assets.** USD, USDC (Noble), USDC (alloyed) and USDT (alloyed) are different assets. USD results need a documented historical conversion (`templates/conversions.csv`); otherwise the result stays in the quote asset and is marked *USD incomplete*.

## Receipt metrics

```
ReferenceValueUSD          = ReceiptOPT × ReferenceUnitPriceUSD
ModeledFillRatio           = ExecutableOPT / RequestedOPT
ResidualOPT                = RequestedOPT − ExecutableOPT
EffectiveExecutionPriceUSD = ModeledOutputUSD / ExecutableOPT          (filled part only)
ExecutionShortfall         = 1 − ModeledOutputUSD / ReferenceValueUSD  (fully modeled, both in USD; may be negative)
```

*Execution shortfall* is a modeled comparison, not a tax discount. Transaction costs are shown as estimated net wallet proceeds, never subtracted from the receipt value. **Capacity at threshold t** is the largest quantity whose marginal price stays at or above `(1 − t) ×` the pre-trade best price, from price impact only (fees reported separately).

Outcomes: SupportedHistoricalEstimate, PartialCapacity, InsufficientData, InsufficientDepthData, VerifiedVenueUnavailable, NoExecutableRouteWithinVerifiedCoverage, UnsupportedPoolType, ConflictingEvidence.

## Independent vs cumulative

* Per-receipt estimates are independent counterfactuals on cloned market state. They must not be summed as proceeds.
* The **cumulative stress view** sells each day's receipts into one evolving state. Depleted liquidity carries forward and is replaced only by a new contemporaneous snapshot or a stated replenishment assumption.
* 1/3/7-day **marketing-horizon** runs refuse to start without inventory, buyer flow (with source), replenishment and route assumptions.
* Network-selling scenarios run in their own simulation state.

## Fair market value builder

The *Fair market value* section on the Approvals & reports tab drafts one proposed value per receipt in the loaded period. It runs as a background job with progress shown.

1. **Receipts.** It reads the period's receipts.
2. **Pool creation.** It finds when each candidate Osmosis pool was created: by bisection on the pool query, using the previous pool id's creation as a lower bound. The `pool_created` search is a short-timeout fallback.
3. **Pool state.** For each payout run (payouts within two hours), it reads the state of every pool that existed one minute before the first payout. Only the selling direction's ticks are fetched. A taker fee read within 200,000 blocks is reused, with the height it came from recorded.
4. **USD rates.** It records daily USDC→USD closes from CoinMarketCap (USDC, id 3408) for Noble USDC, archiving the raw responses.
5. **Execution model.** It models each receipt's sale in the market-evidence universe, using the latest pool state within 24 hours before the receipt.
6. **Proposals.** It proposes a value for each receipt, using the chosen method:
   * *Market evidence where available, otherwise reference* (default). This is the full receipt's modeled sale including price impact, before pool and taker fees (fair market value does not deduct selling costs), converted to USD. It is used only when the whole receipt is modeled. Otherwise the reference value is proposed, with the reason.
   * *Reference price for every receipt.*
   * *Lower of the two.* This is labelled as a choice needing justification, not a safe harbor.

Receipts with no usable value get no proposal and stay unvalued, never zero. Rebuilding withdraws the builder's earlier proposals in the period. Your own reviews and approved values are untouched. *Approve all proposed values* approves them in one step after an explicit confirmation; each approval is versioned and can be withdrawn.

Example (December 2025, primary wallet): the reference value was $6,146.51 and the proposed value $4,827.09. 61 receipts are valued from Osmosis pool 3278 state. 8 receipts from before the first pool existed (2025-12-04 22:25 UTC) use the reference value.

## Review and reports

* A *proposal* changes nothing. *Approve* is a separate action with an explicit confirmation and creates a new version; nothing is edited in place.
* Methods: accept the reference value (must equal it), historical execution evidence, observed transaction, independent appraisal, other.
* Group reviews need a justification and keep their member receipts. Pro-rata allocation is exact to the cent, with the last receipt taking the remainder.
* A zero value needs evidence beyond the user's own statement and an explanation of at least 40 characters.
* Approval is the user's decision, not IRS approval.
* **Accountant report**: each approved value is both the receipt's income and its lot basis. Lots are matched first-in, first-out per wallet, the same as the Tax Report. Proceeds and sale dates come from the Tax Report's entries and are never changed. Unvalued receipts stay unvalued (not zero) and make the report a **draft**. Every build is stored as a new version with a fingerprint of the approved values, so earlier reports are kept.
* Section 179, start-up costs, hobby/business and self-employment classifications are not touched.
* **Tax Report page**: a new *Reviewed values* option uses only approved values. The existing automatic rules (Finnerty marketability discount, the default; next/last sellable price; blockage discount) still work but now show a *Needs review* banner and print as DRAFT. They are method choices, not safe harbors. No `min(dailyPrice, sevenDayAverage)` rule exists in OPTIC.

## Exports and data rules

Receipts CSV, daily distribution CSV, evidence manifest (file hashes, snapshot heights, parser versions, repository references, review history) and versioned report JSON. Reference prices derived from CoinMarketCap appear only per receipt inside your own reports; no raw price-history export is added. Hashes show a file is unchanged since capture, not that it is true. All endpoints require sign-in. Shared ("Everyone") evidence can be changed only by an admin; personal records, reviews and reports are visible only to their owner.

## Chain facts (verified from source)

* Live chain code: **OptioNetwork/optio**. OptioServices/optio `main` has no custom module.
* Tags: v2.0.0 (9e7becb, upgrade `v2-distro`), v3.0.0 (8795b3c, `v3-lockup`).
* x/mint inflation is 0 at genesis.
* Module `distro`: `MsgMint` may be sent only by `MintingAddress` to one `ReceivingAddress`. Its cumulative cap is MaxSupply 30B OPT, halving from 2024-09-15: about 41.1M OPT/day to 2025-09-14, then about 20.5M/day. The cap is a **ceiling**, not an emission schedule. `x/distro/keeper/msg_server_mint.go` 14–135 @ v3.0.0.
* Genesis supply is 7.5B OPT, of which 1,488,670,000 OPT went to OPTIC's distribution account.
* No on-chain field separates node from staking/participation payouts.
* The OPT IBC denom on Osmosis is `ibc/6AA47B1A…5045` (channel-108371), 6 decimals.

## Not verified / limitations

* The 60M OPT/day emission figure is user-reported. It is not in the chain code, and it exceeds the ~41.1M/day period-1 cap unless earlier unminted headroom was being caught up (the cap is cumulative). Verify it from `MsgMint` transactions (`message.action='/optio.distro.MsgMint'`) or from supply at daily heights.
* Mainnet upgrade heights and the live `distro` params were not queried.
* Coinstore and other exchanges have no historical order-book source in OPTIC. Import books you can document; otherwise exchange analysis returns *InsufficientDepthData*.
* Stableswap and CosmWasm pools are not modeled.
* A CL swap that stops exactly at the minimum price is treated as failed (Osmosis may charge a partial amount there). Irrelevant at realistic sizes.
* Pool state is the end of the previous block (intrablock uncertainty). Ticks are fetched in both directions from the current tick with no bound.

## Running the tests (Windows)

```powershell
powershell -ExecutionPolicy Bypass -File scripts\test-valuation.ps1
```

48 tests cover:

* Osmosis repo vectors
* the 35,000 ÷ 60,000,000 share and the 1/5/10% scenarios
* the synthetic $350 / $105 shortfall
* partial depth, missing data, pool creation
* shared routes and atomic failure
* independent vs cumulative results
* identity and duplicate imports
* the EST/EDT boundary
* the node/staking payout rule
* schedule non-extension and emission vs distribution
* review rules, revaluation of lots
* storage permissions and migrations

All test data is synthetic. See `docs/samples/` for synthetic import files and a sample report.
