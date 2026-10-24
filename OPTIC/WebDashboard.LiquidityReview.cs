// Liquidity & Distribution Review page: receipts, network distributions, historical markets, scenarios, approvals.
public static partial class WebDashboard
{
    static void AppendLiquidityReviewCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(LiquidityReviewStyle);
        sb.Append(LiquidityReviewHtml);
        sb.Append(LiquidityReviewScript);
    }

    const string LiquidityReviewStyle = """
        <style>
            .lr { grid-column: 1 / -1; }
            .lr .card-content { display: flex; flex-direction: column; gap: 16px; }
            .lr-note { font-size: 13px; color: var(--text-secondary); margin: 0; max-width: 1100px; line-height: 1.5; }
            .lr-disclaimer { font-size: 13px; line-height: 1.5; border-left: 4px solid var(--info, var(--accent-primary)); background: color-mix(in srgb, var(--accent-primary) 6%, transparent); padding: 10px 14px; border-radius: 6px; }
            .lr-toolbar { display: flex; gap: 10px; flex-wrap: wrap; align-items: flex-end; }
            .lr-toolbar .form-group { margin-bottom: 0; }
            .lr-tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 10px; }
            .lr-tile { border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px; background: var(--surface); }
            .lr-tile .k { font-size: 12px; color: var(--text-secondary); }
            .lr-tile .v { font-size: 20px; font-weight: 700; margin-top: 2px; font-variant-numeric: tabular-nums; }
            .lr-tile .s { font-size: 12px; color: var(--text-muted); margin-top: 2px; }
            .lr-tabs { display: flex; gap: 4px; border-bottom: 1px solid var(--border); flex-wrap: wrap; }
            .lr-tab { background: none; box-shadow: none; color: var(--text-secondary); text-transform: none; letter-spacing: 0; font-weight: 600; font-size: 14px; padding: 9px 14px; border-radius: 7px 7px 0 0; border-bottom: 2px solid transparent; }
            .lr-tab:hover { background: color-mix(in srgb, var(--accent-primary) 6%, transparent); transform: none; box-shadow: none; color: var(--text-primary); }
            .lr-tab[aria-selected="true"] { color: var(--accent-primary); border-bottom-color: var(--accent-primary); }
            .lr-panel { display: flex; flex-direction: column; gap: 14px; }
            .lr-panel[hidden] { display: none; }
            .lr-section { border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; }
            .lr-section > h3 { margin: 0 0 4px; font-size: 15px; }
            .lr-section > .lr-note { margin-bottom: 10px; }
            .lr-grid2 { display: grid; grid-template-columns: repeat(auto-fit, minmax(420px, 1fr)); gap: 14px; }
            .lr-row { display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; }
            .lr-row .form-group { margin-bottom: 0; }
            .lr .btn-quiet, .lr .lr-small { background: var(--surface); color: var(--text-primary); border: 1px solid var(--border); box-shadow: none; text-transform: none; letter-spacing: 0; font-weight: 600; }
            .lr .btn-quiet:hover, .lr .lr-small:hover { background: color-mix(in srgb, var(--accent-primary) 6%, var(--surface)); transform: none; box-shadow: none; }
            .lr .lr-small { font-size: 12px; padding: 3px 9px; }
            .lr-actions { position: sticky; top: 64px; z-index: 5; display: flex; gap: 8px; align-items: center; flex-wrap: wrap; padding: 8px 10px; border: 1px solid var(--accent-primary); border-radius: 8px; background: var(--surface); }
            .lr-actions[hidden] { display: none; }
            .lr table.data-table td, .lr table.data-table th { font-size: 13px; }
            .lr th.lr-group { text-align: center; font-size: 11px; text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-muted); border-bottom: 1px solid var(--border); }
            .lr td.num, .lr th.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
            .lr td.sep, .lr th.sep { border-left: 1px solid var(--border); }
            .lr-badge { display: inline-block; font-size: 11px; font-weight: 600; border-radius: 10px; padding: 1px 8px; white-space: nowrap; border: 1px solid currentColor; }
            .lr-badge.ok { color: var(--success, #107c10); }
            .lr-badge.warn { color: #8a5a00; background: color-mix(in srgb, var(--warning) 18%, transparent); border-color: transparent; }
            .lr-badge.bad { color: var(--error); }
            .lr-badge.muted { color: var(--text-muted); }
            .lr-sub { font-size: 11px; color: var(--text-muted); display: block; }
            .lr-detail td { background: color-mix(in srgb, var(--accent-primary) 3%, transparent); }
            .lr-detail dl { display: grid; grid-template-columns: max-content 1fr; gap: 3px 12px; margin: 0; font-size: 12px; }
            .lr-detail dt { color: var(--text-secondary); }
            .lr-detail dd { margin: 0; word-break: break-word; }
            .lr-detail ul { margin: 4px 0 0 18px; padding: 0; font-size: 12px; }
            .lr-chart { width: 100%; height: auto; display: block; }
            .lr-legend { display: flex; gap: 14px; flex-wrap: wrap; font-size: 12px; color: var(--text-secondary); }
            .lr-legend i { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-right: 5px; vertical-align: -1px; }
            .lr-err { color: var(--error); font-size: 13px; white-space: pre-wrap; }
            .lr-ok { color: var(--success, #107c10); font-size: 13px; }
            .lr textarea { width: 100%; min-height: 70px; }
            .lr-report { border: 1px solid var(--border); border-radius: 8px; padding: 16px 18px; }
            .lr-report h2 { margin-top: 0; }
            @media print {
                .top-nav, .lr-toolbar, .lr-tabs, .lr-actions, .lr-no-print, header { display: none !important; }
                .lr-panel[hidden] { display: none !important; }
                .lr-report { border: none; padding: 0; }
            }
            .lr-clamp { display: -webkit-box; -webkit-line-clamp: 4; -webkit-box-orient: vertical; overflow: hidden; }
            @media (max-width: 640px) {
                .lr-grid2 { grid-template-columns: 1fr; }
                .lr-disclaimer { font-size: 12px; padding: 8px 10px; }
                .lr .form-group input:not([type=checkbox]):not([type=file]), .lr .form-group select { width: 100% !important; }
                /* Tables become labelled cards: each cell shows its column name (data-label, set by script). */
                .lr .data-table-wrap { max-height: none !important; overflow: visible; }
                .lr table.data-table { min-width: 0 !important; display: block; }
                .lr table.data-table thead { display: none; }
                .lr table.data-table tbody { display: block; }
                .lr table.data-table tr { position: relative; display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 6px 12px; padding: 10px 12px; margin: 0 0 8px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface); }
                .lr table.data-table td { display: block; border: none; padding: 0; min-width: 0; overflow-wrap: anywhere; text-align: left !important; white-space: normal !important; max-width: none !important; }
                .lr table.data-table td::before { content: attr(data-label); display: block; font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); margin-bottom: 1px; }
                .lr table.data-table td[data-label=""]::before { display: none; }
                .lr table.data-table td:empty { display: none; }
                .lr table.data-table td[colspan], .lr table.data-table td.lr-wide { grid-column: 1 / -1; }
                .lr table.data-table td.sep { border-left: none; }
                .lr td.lr-hide-m { display: none !important; }
                /* Receipt cards: date across the top, checkbox in the corner. */
                .lr td.lr-check { position: absolute; top: 8px; right: 10px; }
                .lr td.lr-check input { width: 22px; height: 22px; }
                .lr td.lr-when { grid-column: 1 / -1; padding-right: 34px; }
                .lr tr.lr-detail { margin-top: -6px; background: color-mix(in srgb, var(--accent-primary) 4%, var(--surface)); }
                .lr tr.lr-detail > td:first-child { display: none; }
                .lr .lr-detail dl { grid-template-columns: minmax(0, 36%) minmax(0, 1fr); }
                .lr .lr-detail dd { overflow-wrap: anywhere; }
                .lr tr.tr-total { border-color: var(--border-strong); font-weight: 600; }
                .lr-tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
                .lr-tile .v { font-size: 17px; }
                .lr-tabs { flex-wrap: nowrap; overflow-x: auto; -webkit-overflow-scrolling: touch; }
                .lr-tab { flex: 0 0 auto; white-space: nowrap; }
                .lr-filters > .form-group { flex: 1 1 calc(50% - 8px) !important; }
                .lr-section { padding: 10px; }
                .lr-actions { top: 56px; }
            }
        </style>
        """;

    const string LiquidityReviewHtml = """
        <div class="card lr">
            <div class="card-content">
                <div class="lr-disclaimer" id="lr-disclaimer">Historical market-capacity analysis supports valuation review. It is not an IRS-approved discount formula, a guarantee of sale proceeds, or a substitute for a tax determination. Network scenarios are hypothetical. Missing information is not evidence of zero value.</div>

                <form id="lr-form" class="lr-toolbar">
                    <div class="form-group" style="flex: 1 1 380px;"><label for="lr-address">Wallet</label><input type="text" id="lr-address" placeholder="optio1..." autocomplete="off" spellcheck="false"></div>
                    <div class="form-group" style="flex: 0 0 120px;"><label for="lr-year">Period</label><select id="lr-year"></select></div>
                    <div class="form-group lr-custom" style="flex: 0 0 150px;" hidden><label for="lr-from">From</label><input type="date" id="lr-from"></div>
                    <div class="form-group lr-custom" style="flex: 0 0 150px;" hidden><label for="lr-to">To</label><input type="date" id="lr-to"></div>
                    <button type="submit" id="lr-load">Load receipts</button>
                </form>
                <div id="lr-status" class="lr-note">Loads every reward receipt for the wallet from the chain, one row per payment event. The first load can take a few minutes.</div>

                <div class="lr-tiles" id="lr-tiles" hidden></div>

                <div class="lr-tabs" role="tablist" id="lr-tabs" hidden>
                    <button type="button" class="lr-tab" role="tab" data-tab="receipts" aria-selected="true">Receipts</button>
                    <button type="button" class="lr-tab" role="tab" data-tab="network" aria-selected="false">Network distributions</button>
                    <button type="button" class="lr-tab" role="tab" data-tab="markets" aria-selected="false">Historical markets</button>
                    <button type="button" class="lr-tab" role="tab" data-tab="scenarios" aria-selected="false">Scenarios &amp; analysis</button>
                    <button type="button" class="lr-tab" role="tab" data-tab="approvals" aria-selected="false">Approvals &amp; reports</button>
                </div>

                <!-- Receipts -->
                <section class="lr-panel" id="lr-tab-receipts" hidden>
                    <div class="lr-section" id="lr-rule"></div>
                    <div class="lr-row lr-filters">
                        <div class="form-group"><label for="lr-f-cat">Category</label><select id="lr-f-cat"><option value="">All</option><option>Node</option><option>Staking</option><option>Other</option><option>Unclassified</option></select></div>
                        <div class="form-group"><label for="lr-f-cls">Classification</label><select id="lr-f-cls"><option value="">All</option><option value="Confirmed">Confirmed</option><option value="unconfirmed">Not confirmed</option></select></div>
                        <div class="form-group"><label for="lr-f-rev">Tax review</label><select id="lr-f-rev"><option value="">All</option><option value="none">No approved value</option><option value="proposed">Proposed</option><option value="approved">Approved</option></select></div>
                        <div class="form-group"><label for="lr-f-out">Execution evidence</label><select id="lr-f-out"><option value="">All</option><option value="none">Not analyzed</option><option value="SupportedHistoricalEstimate">Supported estimate</option><option value="PartialCapacity">Partial capacity</option><option value="insufficient">Insufficient data</option><option value="blocked">Unavailable / no route</option></select></div>
                        <span class="lr-note" id="lr-count"></span>
                    </div>
                    <div class="lr-actions" id="lr-actions" hidden>
                        <strong id="lr-sel-count"></strong>
                        <button type="button" class="lr-small" data-act="node">Confirm as node</button>
                        <button type="button" class="lr-small" data-act="staking">Confirm as staking</button>
                        <button type="button" class="lr-small" data-act="analyze">Analyze market capacity</button>
                        <button type="button" class="lr-small" data-act="review">Propose a reviewed value…</button>
                        <button type="button" class="lr-small" data-act="clear">Clear selection</button>
                    </div>
                    <div class="data-table-wrap" style="max-height: 640px;">
                        <table class="data-table">
                            <thead>
                                <tr><th></th><th class="lr-group" colspan="4">Receipt</th><th class="lr-group sep" colspan="2">Reference</th><th class="lr-group sep" colspan="3">Execution evidence</th><th class="lr-group sep">Network</th><th class="lr-group sep" colspan="2">Tax review</th></tr>
                                <tr>
                                    <th><input type="checkbox" id="lr-all" title="Select all shown"></th>
                                    <th>Received</th><th>Category</th><th class="num">OPT</th><th>Wallet</th>
                                    <th class="num sep">Value (USD)</th><th>Source</th>
                                    <th class="sep">Outcome</th><th class="num">Modeled proceeds</th><th class="num">Filled / residual</th>
                                    <th class="num sep" title="Network distribution that UTC day">Daily distribution</th>
                                    <th class="num sep">Selected value</th><th>Status</th>
                                </tr>
                            </thead>
                            <tbody id="lr-body"></tbody>
                        </table>
                    </div>
                    <div><button type="button" class="btn-quiet" id="lr-more" hidden>Show more</button></div>
                    <div class="lr-grid2">
                        <div class="lr-section"><h3>Your receipts vs supported sale capacity</h3><p class="lr-note">Bars: OPT received per day. Line: modeled capacity at the 5% price-deterioration threshold for analyzed receipts (independent per-receipt estimates). Days with no analysis are shaded, not zero.</p><div id="lr-chart-capacity"></div></div>
                        <div class="lr-section"><h3>Valuation comparison by month</h3><p class="lr-note">Reference value, modeled execution output (analyzed receipts only, USD where a documented conversion exists) and approved values. Missing values are left blank.</p><div id="lr-chart-values"></div></div>
                    </div>
                </section>

                <!-- Network distributions -->
                <section class="lr-panel" id="lr-tab-network" hidden>
                    <div class="lr-section">
                        <h3>Daily network distribution schedule</h3>
                        <p class="lr-note">Scheduled, observed and user-assumed amounts are kept separate and each period has an end date: nothing is extended to dates it does not cover. The chain code (OptioNetwork/optio x/distro) sets a halving cap on total minting of about 41.1M OPT/day from 2024-09-15 and 20.5M OPT/day from 2025-09-15. That is a ceiling, not a payout schedule. Record what was <em>emitted</em> (minted) separately from what was <em>distributed</em>: minted OPT that was not paid out stays undistributed. Selling scenarios use distributed amounts only.</p>
                        <div class="data-table-wrap" style="max-height: 360px;"><table class="data-table"><thead><tr><th>From</th><th>To</th><th>Category</th><th class="num">Scheduled / day</th><th class="num">Observed / day</th><th class="num">User-assumed / day</th><th class="num">Emitted / day</th><th>Verification</th><th>Origin</th><th>Owner</th><th>Notes / evidence</th><th class="lr-no-print"></th></tr></thead><tbody id="lr-dist-body"></tbody></table></div>
                    </div>
                    <div class="lr-grid2">
                        <div class="lr-section">
                            <h3>Add a period</h3>
                            <form id="lr-dist-form" class="lr-row">
                                <div class="form-group"><label>From</label><input type="date" name="effectiveFrom" required></div>
                                <div class="form-group"><label>To</label><input type="date" name="effectiveTo" required></div>
                                <div class="form-group"><label>Category</label><select name="category"><option>Node</option><option>Staking</option><option>Combined</option><option>Other</option></select></div>
                                <div class="form-group"><label>Amount / day (OPT)</label><input type="number" name="amount" step="any" min="0" required></div>
                                <div class="form-group"><label>Amount is</label><select name="basis"><option value="emittedOptPerDay">Emitted (minted), network-wide</option><option value="observedOptPerDay">Distributed, observed</option><option value="scheduledOptPerDay">Distributed, scheduled</option><option value="userAssumedOptPerDay">Distributed, user-assumed</option></select></div>
                                <div class="form-group"><label>Verification</label><select name="verification"><option>UserReported</option><option>Unverified</option><option>Observed</option><option>Verified</option></select></div>
                                <div class="form-group" style="flex: 1 1 100%;"><label>Notes and evidence (source, URL)</label><input type="text" name="notes" placeholder="e.g. Announcement URL, block range, who said it"></div>
                                <div class="form-group"><label>Save for</label><select name="scope"><option value="mine">Me</option><option value="system">Everyone (admin)</option></select></div>
                                <button type="submit">Add period</button>
                            </form>
                            <div id="lr-dist-msg"></div>
                        </div>
                        <div class="lr-section">
                            <h3>Import and chain data</h3>
                            <div class="lr-row">
                                <div class="form-group"><label>Schedule CSV</label><input type="file" id="lr-dist-file" accept=".csv,text/csv"></div>
                                <div class="form-group"><label>Save for</label><select id="lr-dist-scope"><option value="mine">Me</option><option value="system">Everyone (admin)</option></select></div>
                                <button type="button" class="btn-quiet" id="lr-dist-import">Import CSV</button>
                                <a class="header-link" href="/api/valuation/templates/distributions.csv">Template</a>
                            </div>
                            <p class="lr-note" style="margin-top: 10px;">Admins can record observed daily totals from the synced chain data. They are all transfers from the distribution account per UTC day, so node and participation payouts together, stored as <em>Combined</em>.</p>
                            <div class="lr-row"><button type="button" class="btn-quiet" id="lr-dist-chain">Record observed daily totals</button><button type="button" class="btn-quiet" id="lr-supply-chain">Record daily total supply</button></div>
                            <div id="lr-import-msg"></div>
                        </div>
                    </div>
                    <div class="lr-section"><h3>Network distributions vs hypothetical selling</h3><p class="lr-note">Network amount per day by the selected basis, and the OPT offered if 1%, 5% or 10% of it were sold. The selling is hypothetical: no actual selling has been established. Days without a recorded amount are shaded.</p><div id="lr-chart-network"></div></div>
                </section>

                <!-- Historical markets -->
                <section class="lr-panel" id="lr-tab-markets" hidden>
                    <div class="lr-section">
                        <h3>Coverage</h3>
                        <p class="lr-note">Which market data exists near each receipt day. Only snapshots taken at or before a receipt, within the age limit, are used for it; later data is shown as corroboration only.</p>
                        <div id="lr-chart-coverage"></div>
                    </div>
                    <div class="lr-grid2">
                        <div class="lr-section">
                            <h3>Your access</h3>
                            <p class="lr-note">Your jurisdiction and the venues where you had a usable account. This decides the taxpayer-accessible universe. All venues stay in the market-evidence universe.</p>
                            <form id="lr-profile-form" class="lr-row">
                                <div class="form-group"><label>Jurisdiction</label><input type="text" name="jurisdiction" value="US" maxlength="10" style="width: 90px;"></div>
                                <div class="form-group" style="flex: 1 1 220px;"><label>Accounts at (comma separated)</label><input type="text" name="accountsAt" placeholder="e.g. Coinstore, Osmosis"></div>
                                <div class="form-group" style="flex: 1 1 100%;"><label>Custody, bridge and transfer notes</label><input type="text" name="custodyNote" placeholder="e.g. Keplr wallet; IBC transfer to Osmosis takes about a minute"></div>
                                <button type="submit" class="btn-quiet">Save</button>
                            </form>
                        </div>
                        <div class="lr-section">
                            <h3>Venue access history</h3>
                            <p class="lr-note">Effective-dated statuses for trading, deposits, withdrawals and jurisdiction access. An announcement is not proof of when a change took effect, and a US restriction does not mean trading stopped everywhere.</p>
                            <form id="lr-venue-form" class="lr-row">
                                <div class="form-group"><label>Venue</label><input type="text" name="venue" placeholder="Coinstore" required style="width: 120px;"></div>
                                <div class="form-group"><label>Covers</label><select name="kind"><option>Trading</option><option>Deposits</option><option>Withdrawals</option><option>JurisdictionAccess</option></select></div>
                                <div class="form-group"><label>State</label><select name="state"><option>Available</option><option>Suspended</option><option>Restricted</option><option>Unavailable</option><option>Unknown</option></select></div>
                                <div class="form-group"><label>From (UTC)</label><input type="datetime-local" name="effectiveFromUtc" required></div>
                                <div class="form-group"><label>To (UTC)</label><input type="datetime-local" name="effectiveToUtc"></div>
                                <div class="form-group"><label>Only for</label><input type="text" name="jurisdiction" placeholder="all" style="width: 70px;"></div>
                                <div class="form-group"><label>Verification</label><select name="verification"><option>UserReported</option><option>Observed</option><option>Verified</option><option>Unverified</option></select></div>
                                <label style="font-size: 12px;"><input type="checkbox" name="implementationVerified"> Effective time verified</label>
                                <div class="form-group" style="flex: 1 1 100%;"><label>Evidence (URL or description)</label><input type="text" name="evidence"></div>
                                <div class="form-group"><label>Save for</label><select name="scope"><option value="mine">Me</option><option value="system">Everyone (admin)</option></select></div>
                                <button type="submit" class="btn-quiet">Add status</button>
                            </form>
                            <div class="data-table-wrap" style="max-height: 240px; margin-top: 8px;"><table class="data-table"><thead><tr><th>Venue</th><th>Covers</th><th>State</th><th>From</th><th>To</th><th>Scope</th><th>Verification</th><th></th></tr></thead><tbody id="lr-venue-body"></tbody></table></div>
                        </div>
                    </div>
                    <div class="lr-grid2">
                        <div class="lr-section">
                            <h3>Osmosis pool state</h3>
                            <p class="lr-note">Reads pool state from an Osmosis archive node at a block height, and checks that the node answered for that height. Pruned or failed reads are reported as missing data, never as zero liquidity. Pools 3278 and 3329 are candidates, not the whole market.</p>
                            <form id="lr-osmo-form" class="lr-row">
                                <div class="form-group" style="flex: 1 1 200px;"><label>Pool ids</label><input type="text" name="poolIds" value="3278, 3329, 3391, 3400, 3565, 3571, 3572"></div>
                                <div class="form-group"><label>At (UTC)</label><input type="datetime-local" name="atUtc"></div>
                                <div class="form-group"><label>or height</label><input type="number" name="height" min="1" style="width: 120px;"></div>
                                <div class="form-group"><label>Save for</label><select name="scope"><option value="mine">Me</option><option value="system">Everyone (admin)</option></select></div>
                                <button type="submit" class="btn-quiet">Fetch</button>
                            </form>
                            <div id="lr-osmo-msg"></div>
                            <div class="lr-row" style="margin-top: 8px;">
                                <div class="form-group" style="flex: 1 1 300px;"><label>Check the math against an observed swap (tx hash)</label><input type="text" id="lr-validate-hash" placeholder="64-character Osmosis tx hash"></div>
                                <button type="button" class="btn-quiet" id="lr-validate">Replay swap</button>
                            </div>
                            <div class="data-table-wrap" style="max-height: 200px; margin-top: 8px;"><table class="data-table"><thead><tr><th>Tx</th><th>Pool</th><th class="num">In</th><th class="num">Observed out</th><th class="num">Modeled out</th><th class="num">Deviation</th><th>Notes</th></tr></thead><tbody id="lr-valid-body"></tbody></table></div>
                        </div>
                        <div class="lr-section">
                            <h3>Exchange order books and quote conversions</h3>
                            <p class="lr-note">Import historical bid books (sold into highest bids first) and documented quote-asset conversions. USD, USDC and USDT are different assets. Without a conversion, results stay in the quote asset and the USD figure is marked incomplete.</p>
                            <div class="lr-row">
                                <div class="form-group"><label>Order book CSV</label><input type="file" id="lr-book-file" accept=".csv,text/csv"></div>
                                <button type="button" class="btn-quiet" id="lr-book-import">Import</button>
                                <a class="header-link" href="/api/valuation/templates/orderbook.csv">Template</a>
                            </div>
                            <div class="lr-row" style="margin-top: 8px;">
                                <div class="form-group"><label>Conversions CSV</label><input type="file" id="lr-conv-file" accept=".csv,text/csv"></div>
                                <button type="button" class="btn-quiet" id="lr-conv-import">Import</button>
                                <a class="header-link" href="/api/valuation/templates/conversions.csv">Template</a>
                            </div>
                            <div id="lr-book-msg"></div>
                        </div>
                    </div>
                    <div class="lr-section">
                        <h3>Stored snapshots</h3>
                        <div class="data-table-wrap" style="max-height: 320px;"><table class="data-table"><thead><tr><th>Kind</th><th>Venue / pool</th><th>Observed (UTC)</th><th>Height</th><th>Detail</th><th>Problems</th><th>Scope</th><th></th></tr></thead><tbody id="lr-snap-body"></tbody></table></div>
                    </div>
                </section>

                <!-- Scenarios and analysis -->
                <section class="lr-panel" id="lr-tab-scenarios" hidden>
                    <div class="lr-section">
                        <h3>Distribution share and network-selling scenarios</h3>
                        <form id="lr-scen-form" class="lr-row">
                            <div class="form-group"><label>From</label><input type="date" name="from" required></div>
                            <div class="form-group"><label>To</label><input type="date" name="to" required></div>
                            <div class="form-group"><label>Network amount</label><select name="basis"><option value="BestAvailable">Best available</option><option value="Observed">Observed only</option><option value="Scheduled">Scheduled only</option><option value="UserAssumed">User-assumed only</option></select></div>
                            <div class="form-group"><label>Selling scenarios (%)</label><input type="text" name="fractions" value="1, 5, 10" style="width: 110px;"></div>
                            <div class="form-group"><label>Days counted in</label><select name="dayBasis"><option value="utc">UTC (as chain data)</option><option value="reporting">Reporting time zone</option></select></div>
                            <button type="submit" class="btn-quiet">Calculate</button>
                        </form>
                        <div id="lr-scen-out" style="margin-top: 10px;"></div>
                    </div>
                    <div class="lr-section">
                        <h3>Historical execution analysis</h3>
                        <p class="lr-note">Simulates selling each receipt into the market as it stood at receipt time, using only stored contemporaneous snapshots. Each receipt is an independent counterfactual. The cumulative stress view carries depleted liquidity from sale to sale and is shown separately. Network scenarios run in their own simulation state.</p>
                        <form id="lr-an-form" class="lr-row">
                            <div class="form-group"><label>From</label><input type="date" name="from" required></div>
                            <div class="form-group"><label>To</label><input type="date" name="to" required></div>
                            <div class="form-group"><label>Max snapshot age (h)</label><input type="number" name="maxAgeHours" value="24" min="0.1" step="any" style="width: 90px;"></div>
                            <div class="form-group"><label>Price-deterioration thresholds (%)</label><input type="text" name="thresholds" value="1, 5, 10, 25" style="width: 120px;"></div>
                            <div class="form-group"><label>Transaction costs per sale (USD)</label><input type="number" name="txCosts" step="any" min="0" style="width: 110px;" placeholder="optional"></div>
                            <button type="submit" class="btn-quiet">Run analysis</button>
                        </form>
                        <div id="lr-an-out" style="margin-top: 10px;"></div>
                    </div>
                </section>

                <!-- Approvals and reports -->
                <section class="lr-panel" id="lr-tab-approvals" hidden>
                    <div class="lr-section">
                        <h3>Receipt-date FMV</h3>
                        <p class="lr-note">Each receipt is valued at a supportable market price for the time it was received. A versioned source-quality policy picks a candidate price and flags anything questionable for your review. Flags are review triggers, never automatic discounts, and a missing value stays unresolved rather than zero. Nothing changes your taxes until you approve.</p>
                        <div id="lr-fmv-policy" class="lr-note"></div>
                        <div class="lr-row" style="margin-top: 8px;">
                            <button type="button" id="lr-fmv-build">Gather evidence and build candidates</button>
                            <label style="font-size: 12px;"><input type="checkbox" id="lr-fmv-fetch" checked> Read Osmosis pool state from the archive</label>
                            <button type="button" class="btn-quiet" id="lr-fmv-cancel" hidden>Cancel</button>
                        </div>
                        <div id="lr-fmv-progress" class="lr-note" style="margin-top: 8px;"></div>
                        <div id="lr-fmv-summary" style="margin-top: 8px;"></div>
                    </div>
                    <div class="lr-section" id="lr-exc-box" hidden>
                        <h3>Exceptions: decide with an explanation</h3>
                        <p class="lr-note">These receipts have a flag under the policy. Select them, choose which value to use, and explain why. The same option is applied to each selected receipt, at that receipt's own amount. Every decision is recorded with its evidence and can be revised later.</p>
                        <div class="lr-row lr-filters">
                            <div class="form-group"><label for="lr-exc-flag">Flag</label><select id="lr-exc-flag"><option value="">All flags</option></select></div>
                            <div class="form-group"><label for="lr-exc-cat">Category</label><select id="lr-exc-cat"><option value="">All</option><option>Node</option><option>Staking</option><option>Other</option><option>Unclassified</option></select></div>
                        </div>
                        <div class="data-table-wrap" style="max-height: 420px; margin-top: 8px;"><table class="data-table"><thead><tr><th><input type="checkbox" id="lr-exc-all" title="Select all shown"></th><th>Received (UTC)</th><th>Category</th><th class="num">OPT</th><th class="num">Reference</th><th class="num">Candidate</th><th>Flags</th><th>Other options</th></tr></thead><tbody id="lr-exc-body"></tbody></table></div>
                        <form id="lr-exc-form" class="lr-row" style="margin-top: 8px;">
                            <div class="form-group"><label for="lr-exc-option">Use</label><select id="lr-exc-option">
                                <option value="reference">CoinMarketCap reference</option>
                                <option value="osmosis-pool">Osmosis pool price just before receipt</option>
                                <option value="osmosis-swaps">Osmosis average swap price that day (timing approximation)</option>
                                <option value="modeled-sale">Modeled immediate sale of the whole receipt</option>
                            </select></div>
                            <div class="form-group" style="flex: 1 1 100%;"><label for="lr-exc-why">Explanation (required)</label><textarea id="lr-exc-why" placeholder="Why this value is the best-supported receipt-date market value for these receipts"></textarea></div>
                            <button type="submit">Approve decision for selected</button>
                            <span class="lr-note" id="lr-exc-count"></span>
                        </form>
                        <div id="lr-exc-msg"></div>
                    </div>
                    <div class="lr-section">
                        <h3>Import approved or supported valuations</h3>
                        <p class="lr-note">For values from outside OPTIC, such as a valuation professional's illiquid-market conclusion. Each row names a receipt and a value, with the method, reviewer and an explanation. The file is archived with its SHA-256 hash. Rows marked approved are recorded as approved; the rest are proposals.</p>
                        <div class="lr-row">
                            <div class="form-group"><label>Valuation CSV</label><input type="file" id="lr-imp-file" accept=".csv,text/csv"></div>
                            <button type="button" class="btn-quiet" id="lr-imp-go">Import</button>
                            <a class="header-link" href="/api/valuation/templates/valuations.csv">Template</a>
                            <a class="header-link" id="lr-imp-ids" href="#">Receipt ids (CSV)</a>
                        </div>
                        <div id="lr-imp-msg"></div>
                    </div>
                    <div class="lr-section" id="lr-review-box" hidden>
                        <h3>Propose a reviewed value</h3>
                        <p class="lr-note">A proposal does not change any tax figure. Only an explicit approval does, and approval is your own decision, not IRS approval. A zero value needs supporting evidence beyond your own statement and a substantive explanation.</p>
                        <form id="lr-review-form" class="lr-row">
                            <div class="form-group" style="flex: 1 1 100%;"><span id="lr-review-scope" class="lr-note"></span></div>
                            <div class="form-group"><label>Method</label><select name="method"><option value="AcceptReference">Accept the reference value</option><option value="AlternativeMarketPrice">Better-supported market price</option><option value="TimingApproximation">Timing approximation (documented nearby time)</option><option value="ImportedValuation">Supported illiquid-market valuation</option><option value="ObservedTransaction">Observed transaction</option><option value="IndependentAppraisal">Independent appraisal</option><option value="HistoricalExecutionEvidence">Modeled sale proceeds</option><option value="Other">Other</option></select></div>
                            <div class="form-group"><label>Selected value (USD, total)</label><input type="number" name="selectedValueUsd" step="any" min="0" required></div>
                            <div class="form-group lr-group-only"><label>Split among receipts</label><select name="allocationMethod"><option value="pro-rata-quantity">Pro rata by OPT</option></select></div>
                            <div class="form-group lr-group-only" style="flex: 1 1 100%;"><label>Why these receipts are reviewed together</label><input type="text" name="groupJustification"></div>
                            <div class="form-group" style="flex: 1 1 100%;"><label>Explanation</label><textarea name="explanation" required></textarea></div>
                            <div class="form-group" style="flex: 1 1 60%;"><label>Evidence (one per line: description | URL)</label><textarea name="evidence" placeholder="Osmosis pool 3278 state at height 12345 | https://..."></textarea></div>
                            <label style="font-size: 12px;"><input type="checkbox" name="userReportedOnly"> Evidence is only my own statement</label>
                            <button type="submit">Save as proposed</button>
                            <button type="button" class="btn-quiet" id="lr-review-cancel">Cancel</button>
                        </form>
                        <div id="lr-review-msg"></div>
                    </div>
                    <div class="lr-section">
                        <h3>Reviews</h3>
                        <div class="data-table-wrap" style="max-height: 360px;"><table class="data-table"><thead><tr><th>Review</th><th>Status</th><th>Method</th><th class="num">Receipts</th><th class="num">Reference (USD)</th><th class="num">Selected (USD)</th><th>Reviewer</th><th>Date</th><th>Explanation</th><th></th></tr></thead><tbody id="lr-rev-body"></tbody></table></div>
                        <details style="margin-top: 8px;"><summary style="cursor: pointer; font-size: 13px;">Full version history</summary><div class="data-table-wrap" style="max-height: 300px;"><table class="data-table"><thead><tr><th>Review</th><th class="num">Version</th><th>Status</th><th class="num">Selected (USD)</th><th>By</th><th>When</th></tr></thead><tbody id="lr-hist-body"></tbody></table></div></details>
                    </div>
                    <div class="lr-section">
                        <h3>Accountant report</h3>
                        <p class="lr-note">Builds a versioned report from approved values. Each receipt's approved value is both its income and its lot basis, and lots are matched first-in, first-out. Earlier versions are kept. Unresolved values make the report a draft.</p>
                        <div class="lr-row">
                            <button type="button" id="lr-report-new">Create report version</button>
                            <a class="header-link" id="lr-exp-receipts" href="#">Receipts CSV</a>
                            <a class="header-link" href="/api/valuation/export/distributions.csv">Daily distribution CSV</a>
                            <a class="header-link" href="/api/valuation/export/manifest.json">Evidence manifest</a>
                        </div>
                        <div class="data-table-wrap" style="max-height: 220px; margin-top: 8px;"><table class="data-table"><thead><tr><th>Created</th><th>Year</th><th>Status</th><th>Fingerprint</th><th></th></tr></thead><tbody id="lr-versions-body"></tbody></table></div>
                        <div id="lr-report-view" style="margin-top: 12px;"></div>
                    </div>
                </section>
            </div>
        </div>
        """;
}
