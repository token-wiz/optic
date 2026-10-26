// Chains > TITN: THORWallet's token at a glance. Supply on Base and Arbitrum, where the unreleased TITN sits (the
// treasury Safe, the vesting contracts, staking), the published unlock plan next to what the chain enforces, every
// treasury transfer and vesting claim, and alerts (push and bell) when big amounts move. Data from /api/titn/watch.
public static partial class WebDashboard
{
    static void AppendTitnChainPage(System.Text.StringBuilder sb) => sb.Append(TitnChainPageHtml);

    const string TitnChainPageHtml = """
        <style>
            .tc { grid-column: 1 / -1; }
            .tc .tc-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 8px; margin-bottom: 12px; }
            .tc .tc-tile { border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px; }
            .tc .tc-tile .k { font-size: 12px; color: var(--text-secondary); }
            .tc .tc-tile .v { font-size: 18px; font-weight: 700; }
            .tc .tc-tile .s { font-size: 12px; color: var(--text-muted); }
            .tc .tc-note { font-size: 13px; color: var(--text-secondary); margin: 0 0 8px; max-width: 1040px; }
            .tc .tc-bar { display: flex; height: 14px; border-radius: 7px; overflow: hidden; background: var(--border); margin: 6px 0; }
            .tc .tc-legend { display: flex; flex-wrap: wrap; gap: 4px 14px; font-size: 12px; margin-bottom: 8px; }
            .tc .tc-legend i { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-right: 5px; vertical-align: -1px; }
            .tc h3 { margin: 18px 0 6px; font-size: 16px; }
            .tc .num { text-align: right; white-space: nowrap; }
            .tc .tc-kind { display: inline-block; font-size: 11px; padding: 1px 6px; border-radius: 9px; border: 1px solid var(--border); white-space: nowrap; }
            .tc .tc-kind.wallet { border-color: #d13438; color: #d13438; }
            .tc .tc-kind.vesting, .tc .tc-kind.claim { border-color: #ca5010; color: #ca5010; }
            .tc .tc-alert { border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px; margin-bottom: 12px; font-size: 13px; }
            .tc .tc-alert input[type=number] { width: 120px; }
            .tc .tc-alert select, .tc .tc-alert input { width: auto; display: inline-block; margin: 2px 4px; }
            .tc input[type=date], .tc td input[type=number] { width: auto; }
            .tc .tc-prog { height: 6px; border-radius: 3px; background: var(--border); overflow: hidden; min-width: 70px; }
            .tc .tc-prog b { display: block; height: 100%; background: #1098ad; }
            .tc .tc-mono { font-family: Consolas, monospace; font-size: 12px; }
        </style>
        <div class="card tc">
            <h2>TITN · THORWallet</h2>
            <div class="card-content">
                <p class="tc-note" id="tc-status">Reading the TITN treasury and vesting contracts…</p>
                <div class="tc-grid" id="tc-tiles"></div>
                <div id="tc-alert"></div>

                <h3>Your path to 1,000,000 TITN</h3>
                <div class="tc-grid" id="tg-goal"></div>

                <h3>Next payout</h3>
                <p class="tc-note">Swap fees collect in THORWallet's fee splitter (<span id="tn-splitter"></span>) until <code>distribute()</code> is called; then <span id="tn-pct">50%</span> goes to the staking contract at once and is ready to harvest straight away. So the USDC waiting in the splitter is your next payout before it happens: your cut is the stakers' share times your share of all staked TITN. Fees still with the swap services, not yet sent to the splitter, can't be seen. Checked every minute while this page is open.</p>
                <div class="tc-grid" id="tn-tiles"></div>
                <details style="margin: 8px 0 18px;"><summary>Recent payouts</summary><div id="tn-list"></div></details>

                <h3>Growth and revenue</h3>
                <p class="tc-note">Read from the chain. <strong>Fees</strong> are the USDC paid into THORWallet's fee splitter (<span id="tg-splitter"></span>), which the contract shares out as it arrives: <strong id="tg-share">50%</strong> to TITN stakers, 45% to a THORWallet operating wallet (it pays invoices from it) and 5% to the raffle. The THORWallet app describes staking as <strong>“~70% of Fees”</strong>; a THORWallet founder confirmed (October 2026) that 70% was the original target, not the configured split, and that raising the share depends on THORWallet first returning to at least break-even (it is operating at a loss in 2026 and drawing on its treasury). Buybacks from future perps and card income are planned but at THORWallet's discretion. So rising fees here are also the path to a bigger staker share. Fees follow how much people swap through THORWallet, so a rising line means more use. <strong>Holders</strong> are wallets with at least 1 TITN on Base, counting staked TITN as its owner's.</p>
                <div class="tc-grid" id="tg-tiles"></div>
                <div class="tc-legend" style="margin-top: 6px;"><span>Show</span>
                    <label><input type="radio" name="tg-range" value="180" checked> 6 months</label>
                    <label><input type="radio" name="tg-range" value="365"> 1 year</label>
                    <label><input type="radio" name="tg-range" value="9999"> All</label></div>
                <div id="tg-fees"></div>
                <div class="tc-legend" id="tg-fees-legend"></div>
                <div id="tg-yield"></div>
                <div class="tc-legend" id="tg-yield-legend"></div>
                <div id="tg-holders"></div>
                <div class="tc-legend" id="tg-holders-legend"></div>
                <p class="tc-note" id="tg-signal" style="font-size: 14px; color: var(--text-primary);"></p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Week</th><th class="num">Fees in</th><th class="num">To stakers</th><th class="num">Change</th><th class="num">Staker yield</th><th class="num">Staked</th><th class="num">Holders</th><th class="num">Price</th></tr></thead><tbody id="tg-weeks"></tbody></table></div>
                <p class="tc-note" id="tg-note"></p>

                <h3>Swap activity behind the fees</h3>
                <p class="tc-note">THORWallet's own swap volume isn't published on chain in one place, so these measure it through the fees: how many fee payments reach the splitter each week and from how many different payers. More payments from more payers means more swapping; one payer suddenly dominating, or payments stopping, means routing changed. The bell warns if no fees arrive for 7 days, and you get a notification if THORWallet changes the split.</p>
                <div class="tc-grid" id="tv-tiles"></div>
                <div id="tv-chart"></div>
                <div class="tc-legend" id="tv-legend"></div>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Biggest fee payers, last 90 days</th><th class="num">USDC</th><th class="num">Share</th><th class="num">Payments</th><th>Last paid</th></tr></thead><tbody id="tv-sources"></tbody></table></div>

                <h3>THORWallet's finances</h3>
                <p class="tc-note">The two THORWallet wallets that hold its dollars: the <strong>operating wallet</strong> (<span id="tf-ops-addr"></span>), which gets 45% of the fees and pays THORWallet's bills, and the <strong>treasury Safe</strong> (<span id="tf-safe-addr"></span>), which also holds the unreleased TITN. A founder said in October 2026 that THORWallet is operating at a loss this year and drawing on its treasury; runway below is the USDC both wallets hold divided by the last 90 days' net spending, before any TITN sales. These two wallets don't show all of THORWallet's costs (the operating wallet pays out roughly $10–15K a month, far less than a team's salaries), so read runway as these wallets' cash only. Payments are shown as they appear on chain; spam transfers under $1 are left out.</p>
                <div class="tc-grid" id="tf-tiles"></div>
                <div id="tf-chart"></div>
                <div class="tc-legend" id="tf-legend"></div>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Month</th><th class="num">Fees in (45%)</th><th class="num">Other money in</th><th class="num">Paid out</th><th class="num">Net</th><th class="num">Operating wallet at month end</th><th class="num">Treasury Safe USDC</th></tr></thead><tbody id="tf-months"></tbody></table></div>
                <details style="margin-top: 6px;"><summary class="tc-note" style="cursor: pointer; display: list-item;">Payments from the operating wallet</summary>
                    <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Paid to</th><th class="num">Total, last 12 months</th><th class="num">Payments</th><th>Last</th></tr></thead><tbody id="tf-payees"></tbody></table></div>
                    <div class="data-table-wrap" style="margin-top: 8px;"><table class="data-table"><thead><tr><th>When</th><th>To</th><th class="num">USDC</th></tr></thead><tbody id="tf-payments"></tbody></table></div>
                </details>

                <h3>Price and unlocks</h3>
                <p class="tc-note">TITN's daily price (log scale) with each unlock marked. Past unlocks have hit the price hard; the table shows the price the day before each one and 1, 7 and 30 days later.</p>
                <div id="tc-price"></div>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Date</th><th>Unlock</th><th class="num">TITN</th><th class="num">Price before</th><th class="num">1 day</th><th class="num">7 days</th><th class="num">30 days</th></tr></thead><tbody id="tc-impact"></tbody></table></div>

                <h3>Price predictor</h3>
                <p class="tc-note">A model, not a promise: it projects TITN's price from the coming unlocks, using how the price reacted to new supply in the past. Unlocked tokens aren't always sold, news and the wider market move TITN too, and the shaded band (one typical spread of TITN's daily moves) shows how wide the outcomes can be.</p>
                <div class="tc-alert" id="tp-controls"></div>
                <div id="tp-chart"></div>
                <div class="tc-legend" id="tp-legend"></div>
                <p class="tc-note" id="tp-summary" style="font-size: 14px; color: var(--text-primary);"></p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Date</th><th class="num">Circulating TITN</th><th class="num">Mild reaction</th><th class="num">Half of March</th><th class="num">Like March</th><th class="num">Range (half of March)</th></tr></thead><tbody id="tp-table"></tbody></table></div>
                <details style="margin: 6px 0 4px;"><summary class="tc-note" style="cursor: pointer; display: list-item;">Unlock schedule used (edit to try your own)</summary>
                    <p class="tc-note">Investor, seed and advisor tokens follow the on-chain vesting contracts. The rows below are the rest of CoinMarketCap's published plan; the default dates are an approximate reading of its chart (a large step from November 2026, the team over three years). Changes are kept in this browser.</p>
                    <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Allocation</th><th class="num">Still to unlock</th><th>Starts</th><th>Spread over (months)</th></tr></thead><tbody id="tp-sched"></tbody></table></div>
                    <button type="button" class="btn btn-secondary" id="tp-reset" style="margin-top: 6px;">Back to the default schedule</button>
                </details>
                <p class="tc-note" id="tp-fit"></p>

                <h3>Where the supply is</h3>
                <div class="tc-bar" id="tc-supply-bar"></div>
                <div class="tc-legend" id="tc-supply-legend"></div>
                <p class="tc-note">The TITN token contract has <strong>no unlock schedule</strong>: the whole supply was minted at launch, and most of it went to THORWallet's treasury, a <span id="tc-safe"></span> that the token contract exempts from every transfer restriction. So announced cliffs and vesting for tokens still in the treasury are promises, not code. What the chain does enforce are the vesting contracts below. Your own TITN, staking and rewards are on <a href="/page/my-titn">My TITN</a>.</p>

                <h3>Vesting contracts (enforced on chain)</h3>
                <p class="tc-note">Each one releases its tokens evenly from its start date to its end date; the vested accounts can claim what has been released. "Released, not claimed" can be claimed at any moment.</p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Contract</th><th>Runs</th><th class="num">Total</th><th class="num">Claimed</th><th class="num">Released, not claimed</th><th class="num">Still locked</th><th class="num">Released per day</th><th>Progress</th></tr></thead><tbody id="tc-vest"></tbody></table></div>

                <h3>Published unlock plan (CoinMarketCap)</h3>
                <p class="tc-note" id="tc-cmc-note"></p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Allocation</th><th class="num">Share</th><th class="num">Total</th><th class="num">Unlocked</th><th class="num">Still locked</th><th>Progress</th></tr></thead><tbody id="tc-cmc"></tbody></table></div>

                <h3>Leaving the treasury, by month</h3>
                <div id="tc-chart"></div>
                <div class="tc-legend" id="tc-chart-legend"></div>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Month</th><th class="num">To wallets (net)</th><th class="num">Into vesting</th><th class="num">To rewards</th><th class="num">Net into own liquidity</th><th class="num">Claimed from vesting</th></tr></thead><tbody id="tc-months"></tbody></table></div>

                <h3>Treasury transfers</h3>
                <p class="tc-note"><label><input type="checkbox" id="tc-hide-liq" checked> Hide moves to and from the treasury's own Aerodrome liquidity</label> · <label><input type="checkbox" id="tc-big"> Only 1,000,000 TITN or more</label></p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>When</th><th>Direction</th><th>What</th><th class="num">TITN</th><th class="num">Value now</th><th>Counterparty</th></tr></thead><tbody id="tc-moves"></tbody></table></div>
                <div id="tc-moves-more"></div>

                <h3>Vesting claims</h3>
                <p class="tc-note">Tokens the vested accounts took out of the vesting contracts: new supply that can reach the market.</p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>When</th><th class="num">TITN</th><th>Claimed by</th><th>From</th></tr></thead><tbody id="tc-claims"></tbody></table></div>
                <p class="tc-note" id="tc-source" style="margin-top: 10px;"></p>
            </div>
        </div>
        <script>
            window.opticGet = window.opticGet || (() => { /* Last visit's answers (saved in this browser per user) are returned at once and replaced when the fresh ones arrive; each arrival fires 'optic-fresh' so the page can redraw. */ const mem = {}, key = (u) => 'optic.cache.' + ((window.opticUser || {}).name || '') + '.' + u; let timer = null; const get = (u) => { if (mem[u]) return mem[u]; let old = null; try { old = JSON.parse(localStorage.getItem(key(u)) || 'null'); } catch (e) { } const live = fetch(u).then(r => r.ok ? r.json() : null).then(j => { if (j) { try { localStorage.setItem(key(u), JSON.stringify({ t: Date.now(), j })); } catch (e) { } } return j; }); if (!old || !old.j) return mem[u] = live; get.cachedAt = Math.min(get.cachedAt || Infinity, old.t); get.waiting = (get.waiting || 0) + 1; live.catch(() => null).then(j => { mem[u] = Promise.resolve(j || old.j); get.waiting--; clearTimeout(timer); timer = setTimeout(() => window.dispatchEvent(new Event('optic-fresh')), 300); }); return mem[u] = Promise.resolve(old.j); }; return get; })();
            (function () {
                const $ = (id) => document.getElementById(id);
                const esc = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
                const n0 = (v) => Number(v || 0).toLocaleString('en-US', { maximumFractionDigits: 0 });
                const mil = (v) => Math.abs(v) >= 1e6 ? (v / 1e6).toLocaleString('en-US', { maximumFractionDigits: 2 }) + 'M' : n0(v);
                const usd = (v) => '$' + Number(v || 0).toLocaleString('en-US', { maximumFractionDigits: v >= 100 ? 0 : 2, minimumFractionDigits: v >= 100 ? 0 : 2 });
                const day = (t) => new Date(t).toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' });
                const when = (t) => new Date(t).toLocaleString('en-US', { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' });
                const short = (a) => a ? a.slice(0, 6) + '…' + a.slice(-4) : '';
                const addr = (a, name) => `<a class="tc-mono" href="https://basescan.org/address/${esc(a)}" target="_blank" rel="noopener" title="${esc(a)}">${esc(name ? name + ' · ' + short(a) : short(a))}</a>`;
                const txl = (h, text) => `<a href="https://basescan.org/tx/${esc(h)}" target="_blank" rel="noopener">${text}</a>`;
                const prog = (p) => `<div class="tc-prog" title="${p.toFixed(1)}%"><b style="width: ${Math.max(0, Math.min(100, p))}%"></b></div>`;
                const kindText = { wallet: 'To a wallet', vesting: 'Into vesting', rewards: 'Rewards', liquidity: 'Own liquidity', claim: 'Claim' };
                const kindIn = { wallet: 'From a wallet', vesting: 'From vesting', rewards: 'From rewards', liquidity: 'Own liquidity' };
                let data = null, alerts = null, prices = [], showAll = false, msg = '';

                // Unlock events: the published ones, plus days the treasury sent 2M+ TITN out (not to its own liquidity) and vesting starts.
                const unlockEvents = (d) => {
                    const ev = [{ d: '2026-03-03', what: 'TGT merge community unlock (announced)', amt: 299130000 }];
                    (d.vesting || []).forEach(v => ev.push({ d: v.start.slice(0, 10), what: `Vesting starts releasing (${n0(v.days)} days)`, amt: v.allocation }));
                    const byDay = {}, label = { wallet: 'to wallets', vesting: 'into vesting', rewards: 'to rewards' };
                    d.moves.filter(m => m.dir === 'out' && m.kind !== 'liquidity').forEach(m => { const k = m.time.slice(0, 10); const o = byDay[k] = byDay[k] || { a: 0, kinds: {} }; o.a += m.amount; o.kinds[m.kind] = (o.kinds[m.kind] || 0) + m.amount; });
                    Object.entries(byDay).filter(([, o]) => o.a >= 2e6).forEach(([k, o]) => { if (!ev.some(e => e.d === k)) ev.push({ d: k, what: 'Treasury sent TITN ' + Object.entries(o.kinds).sort((x, y) => y[1] - x[1]).map(([kk]) => label[kk]).join(' and '), amt: o.a }); });
                    return ev.sort((a, b) => a.d < b.d ? -1 : 1);
                };
                const priceAt = (iso, off) => { const t = new Date(Date.parse(iso + 'T00:00:00Z') + off * 864e5).toISOString().slice(0, 10); let best = null; for (const r of prices) { if (r.d <= t) best = r; else break; } return best && best.d >= new Date(Date.parse(t) - 4 * 864e5).toISOString().slice(0, 10) ? best.p : null; };
                const pct = (a, b) => a && b ? `<span style="color: ${b < a ? '#d13438' : '#107c10'};">${b >= a ? '+' : ''}${((b / a - 1) * 100).toFixed(0)}%</span>` : '–';
                const renderPrice = () => {
                    if (!prices.length) { $('tc-price').innerHTML = '<p class="tc-note">No price history yet.</p>'; $('tc-impact').innerHTML = ''; return; }
                    const ev = unlockEvents(data);
                    const W = 720, H = 200, lo = Math.log(Math.min(...prices.map(r => r.p))), hi = Math.log(Math.max(...prices.map(r => r.p)));
                    const t0 = Date.parse(prices[0].d), t1 = Date.parse(prices[prices.length - 1].d);
                    const x = (d) => (Date.parse(d) - t0) / Math.max(1, t1 - t0) * W, y = (p) => H - 6 - (Math.log(p) - lo) / Math.max(1e-9, hi - lo) * (H - 20);
                    const xy = prices.map(r => `${x(r.d).toFixed(1)},${y(r.p).toFixed(1)}`).join(' ');
                    const marks = ev.filter(e => e.d >= prices[0].d).map(e => `<line x1="${x(e.d)}" x2="${x(e.d)}" y1="4" y2="${H}" stroke="#d13438" stroke-dasharray="3 3" opacity="0.7"><title>${e.d}: ${esc(e.what)}, ${n0(e.amt)} TITN</title></line>`).join('');
                    const ticks = [Math.exp(hi), Math.exp((hi + lo) / 2), Math.exp(lo)].map(p => `<text x="4" y="${y(p) + (p === Math.exp(hi) ? 10 : -3)}" font-size="10" fill="currentColor" opacity="0.7">$${p.toFixed(p < 0.01 ? 4 : 3)}</text>`).join('');
                    const months = []; for (let t = new Date(prices[0].d.slice(0, 7) + '-01T00:00:00Z'); t.getTime() <= t1; t.setUTCMonth(t.getUTCMonth() + 2)) if (t.getTime() >= t0) months.push(t.toISOString().slice(0, 10));
                    $('tc-price').innerHTML = `<svg viewBox="0 0 ${W} ${H + 16}" style="width: 100%; max-width: 900px; height: auto; display: block;" role="img" aria-label="TITN price with unlocks marked">${marks}<polyline points="${xy}" fill="none" stroke="#1098ad" stroke-width="2"/>${ticks}${months.map(m => `<text x="${x(m)}" y="${H + 13}" font-size="10" text-anchor="middle" fill="currentColor" opacity="0.7">${new Date(m).toLocaleDateString('en-US', { month: 'short', year: '2-digit', timeZone: 'UTC' })}</text>`).join('')}</svg><div class="tc-legend"><span><i style="background: #1098ad;"></i>Price</span><span><i style="background: #d13438;"></i>Unlock (hover for details)</span></div>`;
                    $('tc-impact').innerHTML = ev.slice().reverse().map(e => { const b = priceAt(e.d, -1); return `<tr><td>${day(e.d + 'T12:00:00Z')}</td><td>${esc(e.what)}</td><td class="num">${mil(e.amt)}</td><td class="num">${b ? '$' + b.toFixed(4) : '–'}</td><td class="num">${Date.parse(e.d) + 864e5 < Date.now() ? pct(b, priceAt(e.d, 1)) : '–'}</td><td class="num">${Date.parse(e.d) + 7 * 864e5 < Date.now() ? pct(b, priceAt(e.d, 7)) : '–'}</td><td class="num">${Date.parse(e.d) + 30 * 864e5 < Date.now() ? pct(b, priceAt(e.d, 30)) : '–'}</td></tr>`; }).join('');
                };

                const render = () => {
                    const d = data, px = d.price || 0;
                    const vest = d.vesting || [];
                    const vLocked = vest.reduce((s, v) => s + v.locked, 0), vFree = vest.reduce((s, v) => s + v.claimable, 0);
                    const elsewhere = Math.max(0, d.supply.total - d.treasury.balance - vLocked - vFree - d.staking);
                    const out30 = d.moves.filter(m => m.dir === 'out' && m.kind !== 'liquidity' && Date.now() - Date.parse(m.time) < 30 * 864e5).reduce((s, m) => s + m.amount, 0);
                    const nextEnd = vest.find(v => Date.parse(v.end) > Date.now());
                    $('tc-tiles').innerHTML = [
                        ['Price', px ? '$' + px.toFixed(5) : '–', px ? 'Market value of all TITN ' + usd(px * d.supply.total) : 'No price yet'],
                        ['Total supply', mil(d.supply.total), `${mil(d.supply.base)} on Base · ${mil(d.supply.arbitrum)} on Arbitrum`],
                        ['In the treasury', mil(d.treasury.balance), `${(d.treasury.balance / d.supply.total * 100).toFixed(1)}% of supply · ${px ? usd(d.treasury.balance * px) : ''}`],
                        ['Locked in vesting', mil(vLocked), `${mil(vFree)} released but not claimed yet`],
                        ['Staked by holders', mil(d.staking), `${(d.staking / d.supply.total * 100).toFixed(1)}% of supply`],
                        ['Left the treasury, 30 days', mil(out30), 'Not counting its own liquidity moves'],
                        ['Next vesting end', nextEnd ? day(nextEnd.end) : '–', nextEnd ? `${mil(nextEnd.locked)} TITN still locked in it` : 'No vesting running'],
                    ].map(([k, v, s]) => `<div class="tc-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s}</div></div>`).join('');

                    const segs = [['Treasury Safe', d.treasury.balance, '#5c6bc0'], ['Vesting, still locked', vLocked, '#ca5010'], ['Vesting, released not claimed', vFree, '#ffb900'], ['Staked by holders', d.staking, '#1098ad'], ['Everywhere else (circulating)', elsewhere, '#8a8886']];
                    $('tc-supply-bar').innerHTML = segs.map(([k, v, c]) => `<div title="${esc(k)}: ${n0(v)} TITN" style="width: ${v / d.supply.total * 100}%; background: ${c};"></div>`).join('');
                    $('tc-supply-legend').innerHTML = segs.map(([k, v, c]) => `<span><i style="background: ${c};"></i>${esc(k)} ${mil(v)} (${(v / d.supply.total * 100).toFixed(1)}%)</span>`).join('');
                    $('tc-safe').innerHTML = `${d.treasury.threshold ? d.treasury.threshold + '-of-3 ' : ''}Safe multisig (${addr(d.treasury.address)})`;

                    $('tc-vest').innerHTML = vest.length ? vest.map(v => `<tr><td>${addr(v.address, 'TitnVesting')}</td><td>${day(v.start)} → ${day(v.end)}<br><span class="tc-note">${n0(v.days)} days</span></td><td class="num">${n0(v.allocation)}</td><td class="num">${n0(v.claimed)}</td><td class="num">${n0(v.claimable)}</td><td class="num">${n0(v.locked)}</td><td class="num">${v.perDay ? n0(v.perDay) : '–'}</td><td>${prog(v.allocation ? (v.allocation - v.locked) / v.allocation * 100 : 0)}</td></tr>`).join('')
                        : '<tr><td colspan="8">No vesting contracts found.</td></tr>';

                    const cmc = d.cmc.rows || [];
                    const cmcLocked = cmc.reduce((s, r) => s + (r.total - r.unlocked), 0);
                    $('tc-cmc-note').innerHTML = cmc.length
                        ? `What THORWallet reported to CoinMarketCap. ${mil(cmcLocked)} TITN is still due to unlock under this plan. Its chart shows a large step from November 2026, mostly the team allocation and the treasury. Those tokens sit in the treasury Safe today, so when they really move they show up below as treasury transfers (and in your alerts). The investor, seed and advisor rows match the on-chain vesting contract that runs until May 2027.`
                        : 'CoinMarketCap’s published allocations could not be read right now.';
                    $('tc-cmc').innerHTML = cmc.map(r => `<tr><td>${esc(r.name)}</td><td class="num">${r.totalPct.toFixed(2)}%</td><td class="num">${mil(r.total)}</td><td class="num">${mil(r.unlocked)}</td><td class="num">${mil(r.total - r.unlocked)}</td><td>${prog(r.progress)}</td></tr>`).join('');

                    // Monthly chart: what left the treasury (wallets, vesting, rewards) and vesting claims.
                    const months = d.months.slice(-14);
                    const parts = [['wallet', 'To wallets', '#d13438'], ['vesting', 'Into vesting', '#ca5010'], ['rewards', 'To rewards', '#1098ad']];
                    const top = Math.max(1, ...months.map(m => parts.reduce((s, [k]) => s + Math.max(0, m[k]), 0)));
                    const W = 720, H = 160, bw = W / Math.max(1, months.length);
                    $('tc-chart').innerHTML = `<svg viewBox="0 0 ${W} ${H + 18}" style="width: 100%; max-width: 900px; height: auto; display: block;" role="img" aria-label="TITN leaving the treasury by month">${months.map((m, i) => {
                        let y = H; return parts.map(([k, , c]) => { const h = Math.max(0, m[k]) / top * (H - 10); y -= h; return h > 0 ? `<rect x="${i * bw + 4}" y="${y}" width="${bw - 8}" height="${h}" fill="${c}"><title>${m.month} ${k}: ${n0(m[k])} TITN</title></rect>` : ''; }).join('')
                            + `<text x="${i * bw + bw / 2}" y="${H + 14}" font-size="10" text-anchor="middle" fill="currentColor">${m.month.slice(2)}</text>`;
                    }).join('')}</svg>`;
                    $('tc-chart-legend').innerHTML = parts.map(([, t, c]) => `<span><i style="background: ${c};"></i>${t}</span>`).join('') + `<span>Tallest bar: ${mil(top)} TITN</span>`;
                    $('tc-months').innerHTML = d.months.slice().reverse().map(m => `<tr><td>${m.month}</td><td class="num">${n0(m.wallet)}</td><td class="num">${n0(m.vesting)}</td><td class="num">${n0(m.rewards)}</td><td class="num">${n0(m.liquidity)}</td><td class="num">${n0(m.claims)}</td></tr>`).join('');

                    const hide = $('tc-hide-liq').checked, big = $('tc-big').checked;
                    const rows = d.moves.filter(m => !(hide && m.kind === 'liquidity') && !(big && m.amount < 1e6));
                    const shown = showAll ? rows : rows.slice(0, 40);
                    $('tc-moves').innerHTML = shown.length ? shown.map(m => `<tr><td>${txl(m.tx, when(m.time))}</td><td>${m.dir === 'out' ? 'Out' : 'In'}</td><td><span class="tc-kind ${m.dir === 'out' ? m.kind : ''}">${(m.dir === 'out' ? kindText : kindIn)[m.kind]}</span></td><td class="num">${n0(m.amount)}</td><td class="num">${px ? usd(m.amount * px) : '–'}</td><td>${addr(m.counterparty, m.name)}</td></tr>`).join('')
                        : '<tr><td colspan="6">No transfers match.</td></tr>';
                    $('tc-moves-more').innerHTML = rows.length > 40 ? `<button type="button" class="btn btn-secondary" id="tc-more" style="margin-top: 6px;">${showAll ? 'Show the latest 40' : `Show all ${rows.length}`}</button>` : '';
                    if ($('tc-more')) $('tc-more').onclick = () => { showAll = !showAll; render(); };

                    $('tc-claims').innerHTML = d.claims.length ? d.claims.slice(0, 60).map(c => `<tr><td>${txl(c.tx, when(c.time))}</td><td class="num">${n0(c.amount)}</td><td>${addr(c.to)}</td><td>${addr(c.contract, 'TitnVesting')}</td></tr>`).join('') : '<tr><td colspan="4">No claims yet.</td></tr>';
                    $('tc-source').innerHTML = `Transfers from Blockscout, balances and vesting terms read from Base and Arbitrum, checked ${when(d.updatedUtc)}${d.cmc.fetchedUtc ? `; published allocations from CoinMarketCap` : ''}; price from DefiLlama. Counterparty names are what the explorer calls each contract. <a href="#" id="tc-refresh">Check now</a>`;
                    $('tc-refresh').onclick = (e) => { e.preventDefault(); load(true); };
                    renderAlert();
                    renderPrice();
                    renderPredict();
                    renderGrowth();
                };

                // ---- Price predictor ----
                // Circulating supply over time: the launch float, the TGT merge unlock, on-chain vesting as it releases, and treasury
                // sends to wallets and rewards; shifted so today matches CoinMarketCap's unlocked total.
                const DAY = 864e5, isoD = (t) => new Date(t).toISOString().slice(0, 10);
                const SCHED_KEY = 'optic.titn.schedule';
                const defaultSched = (cmc) => {
                    const left = (n) => { const r = cmc.find(x => x.name === n); return r ? Math.max(0, r.total - r.unlocked) : 0; };
                    return [
                        { name: 'Team Allocation', amt: left('Team Allocation'), start: '2026-11-01', months: 36 },
                        { name: 'Treasury', amt: left('Treasury'), start: '2026-11-01', months: 3 },
                        { name: 'Future Rounds', amt: left('Future Rounds'), start: '2026-11-01', months: 3 },
                        { name: 'Airdrop', amt: left('Airdrop'), start: '2026-11-01', months: 3 },
                    ].filter(r => r.amt > 0);
                };
                let sched = null;
                const loadSched = (cmc) => {
                    const def = defaultSched(cmc);
                    let saved = null; try { saved = JSON.parse(localStorage.getItem(SCHED_KEY) || 'null'); } catch (e) { }
                    return def.map(r => { const v = saved && saved[r.name]; return v ? { ...r, start: v.start || r.start, months: Number(v.months) >= 0 ? Number(v.months) : r.months } : r; });
                };
                const saveSched = () => { try { const o = {}; sched.forEach(r => o[r.name] = { start: r.start, months: r.months }); localStorage.setItem(SCHED_KEY, JSON.stringify(o)); } catch (e) { } };
                const linear = (t, start, months) => { const st = Date.parse(start + 'T00:00:00Z'), len = Math.max(1, months) * 30.44 * DAY; return months <= 0 ? (t >= st ? 1 : 0) : Math.min(1, Math.max(0, (t - st) / len)); };
                const circModel = (d) => {
                    const cmc = d.cmc.rows || [];
                    const total = (n) => (cmc.find(x => x.name === n) || {}).total || 0;
                    const base = total('Listings') + total('Liquidity Pool') + total('Market Maker Pools') + total('LP Launch Rewards');
                    const merge = total('TGT Merge Community') || 299.13e6;
                    const sends = d.moves.filter(m => m.kind === 'wallet' || m.kind === 'rewards').map(m => ({ t: Date.parse(m.time), a: m.dir === 'out' ? m.amount : -m.amount })).sort((a, b) => a.t - b.t);
                    const raw = (t) => {
                        let c = base + (t >= Date.parse('2026-03-03T00:00:00Z') ? merge : 0);
                        (d.vesting || []).forEach(v => { const st = Date.parse(v.start), e = Date.parse(v.end); c += v.allocation * Math.min(1, Math.max(0, (t - st) / (e - st))); });
                        for (const x of sends) { if (x.t > t) break; c += x.a; }
                        return c;
                    };
                    const now = Date.now(), cmcNow = cmc.reduce((a, r) => a + r.unlocked, 0);
                    const shift = cmcNow > 0 ? cmcNow - raw(now) : 0;
                    const past = (t) => raw(t) + shift;
                    // Future: today's supply plus vesting still to release plus the editable schedule (the part after today).
                    const future = (t) => {
                        let c = past(now);
                        (d.vesting || []).forEach(v => { const st = Date.parse(v.start), e = Date.parse(v.end), f = (x) => Math.min(1, Math.max(0, (x - st) / (e - st))); c += v.allocation * (f(t) - f(now)); });
                        sched.forEach(r => c += r.amt * Math.max(0, linear(t, r.start, r.months) - linear(now, r.start, r.months)));
                        return c;
                    };
                    return { past, future, now };
                };
                // Least squares of daily log price change on daily log supply change (slope = reaction, intercept = trend).
                const fitModel = (m) => {
                    const xs = [], ys = [], dates = [];
                    for (let i = 1; i < prices.length; i++) {
                        const a = prices[i - 1], b = prices[i], ta = Date.parse(a.d + 'T00:00:00Z'), tb = Date.parse(b.d + 'T00:00:00Z');
                        if (!(a.p > 0 && b.p > 0)) continue;
                        xs.push(Math.log(m.past(tb) / m.past(ta))); ys.push(Math.log(b.p / a.p)); dates.push(b.d);
                    }
                    const n = xs.length, mx = xs.reduce((a, x) => a + x, 0) / n, my = ys.reduce((a, y) => a + y, 0) / n;
                    const k = xs.reduce((a, x, i) => a + (x - mx) * (ys[i] - my), 0) / Math.max(1e-12, xs.reduce((a, x) => a + (x - mx) ** 2, 0));
                    const res = ys.map((y, i) => y - (my - k * mx) - k * xs[i]);
                    const sd = Math.sqrt(res.reduce((a, r) => a + r * r, 0) / Math.max(1, n - 2));
                    const quiet = ys.filter((y, i) => Math.abs(xs[i]) < 0.002);
                    const recent = ys.slice(-90);
                    return { k, sd, n, from: dates[0], driftAll: quiet.reduce((a, y) => a + y, 0) / Math.max(1, quiet.length), drift90: recent.reduce((a, y) => a + y, 0) / Math.max(1, recent.length) };
                };
                let tpHorizon = 12, tpDrift = 'none', tpSold = 100;
                const renderPredict = () => {
                    if (!prices.length || !data.cmc.rows || !data.cmc.rows.length) { $('tp-controls').innerHTML = 'The predictor needs the price history and CoinMarketCap’s allocations, which couldn’t be read right now.'; return; }
                    if (!sched) sched = loadSched(data.cmc.rows);
                    const m = circModel(data), fit = fitModel(m);
                    const k = Math.min(0, fit.k), p0 = prices[prices.length - 1].p, c0 = m.past(m.now);
                    const drift = tpDrift === 'recent' ? fit.drift90 : tpDrift === 'all' ? fit.driftAll : 0;
                    const scen = [['Mild reaction (a quarter of March)', k / 4, '#107c10'], ['Half of March', k / 2, '#ca5010'], ['Like March', k, '#d13438']];
                    const end = m.now + tpHorizon * 30.44 * DAY;
                    const pts = []; for (let t = m.now; t <= end + 1; t += DAY) pts.push(t);
                    const proj = (t, kk) => { const c = m.future(t), sold = c0 + (c - c0) * tpSold / 100; return p0 * Math.exp(drift * (t - m.now) / DAY + kk * Math.log(sold / c0)); };
                    const band = (t, mid) => { const sp = fit.sd * Math.sqrt(Math.max(0, (t - m.now) / DAY)); return [mid * Math.exp(-sp), mid * Math.exp(sp)]; };
                    const pct0 = (p) => `${p >= p0 ? '+' : ''}${((p / p0 - 1) * 100).toFixed(0)}%`;

                    $('tp-controls').innerHTML = `Look ahead <select id="tp-h">${[3, 6, 12, 24].map(h => `<option value="${h}"${h === tpHorizon ? ' selected' : ''}>${h} months</option>`).join('')}</select>
                        · Trend apart from unlocks <select id="tp-d"><option value="none"${tpDrift === 'none' ? ' selected' : ''}>none</option><option value="recent"${tpDrift === 'recent' ? ' selected' : ''}>like the last 90 days (${((Math.exp(fit.drift90 * 30.44) - 1) * 100).toFixed(1)}% a month)</option><option value="all"${tpDrift === 'all' ? ' selected' : ''}>like since launch (${((Math.exp(fit.driftAll * 30.44) - 1) * 100).toFixed(1)}% a month)</option></select>
                        · Share of unlocked TITN that reaches the market <input type="range" id="tp-s" min="10" max="100" step="10" value="${tpSold}" style="vertical-align: middle;"> <b>${tpSold}%</b>`;
                    $('tp-h').onchange = (e) => { tpHorizon = Number(e.target.value); renderPredict(); };
                    $('tp-d').onchange = (e) => { tpDrift = e.target.value; renderPredict(); };
                    $('tp-s').onchange = (e) => { tpSold = Number(e.target.value); renderPredict(); };

                    // Chart: the last 120 days of price, then each scenario, with the band around "half of March".
                    const hist = prices.filter(r => Date.parse(r.d) >= m.now - 120 * DAY).map(r => ({ t: Date.parse(r.d + 'T00:00:00Z'), p: r.p }));
                    const lines = scen.map(([, kk]) => pts.map(t => ({ t, p: proj(t, kk) })));
                    const bnd = pts.map(t => { const [lo, hi] = band(t, proj(t, k / 2)); return { t, lo, hi }; });
                    const all = hist.map(r => r.p).concat(...lines.map(l => l.map(r => r.p)), bnd.map(b => b.lo), bnd.map(b => b.hi)).filter(v => v > 0);
                    const W = 720, H = 220, lo = Math.log(Math.min(...all)), hi = Math.log(Math.max(...all)), t0 = hist.length ? hist[0].t : m.now;
                    const x = (t) => (t - t0) / Math.max(1, end - t0) * W, y = (p) => H - 6 - (Math.log(p) - lo) / Math.max(1e-9, hi - lo) * (H - 20);
                    const poly = (arr) => arr.map(r => `${x(r.t).toFixed(1)},${y(r.p).toFixed(1)}`).join(' ');
                    const area = bnd.map(b => `${x(b.t).toFixed(1)},${y(b.hi).toFixed(1)}`).join(' ') + ' ' + bnd.slice().reverse().map(b => `${x(b.t).toFixed(1)},${y(b.lo).toFixed(1)}`).join(' ');
                    const unl = [...new Set(sched.map(r => Date.parse(r.start + 'T00:00:00Z')).filter(t => t > t0 && t < end))];
                    const ticks = [Math.exp(hi), Math.exp((hi + lo) / 2), Math.exp(lo)].map((p, i) => `<text x="4" y="${y(p) + (i === 0 ? 10 : -3)}" font-size="10" fill="currentColor" opacity="0.7">$${p.toFixed(p < 0.01 ? 4 : 3)}</text>`).join('');
                    const months = []; for (let t = new Date(isoD(t0).slice(0, 7) + '-01T00:00:00Z'); t.getTime() <= end; t.setUTCMonth(t.getUTCMonth() + (tpHorizon > 12 ? 3 : tpHorizon > 6 ? 2 : 1))) if (t.getTime() >= t0) months.push(t.getTime());
                    $('tp-chart').innerHTML = `<svg viewBox="0 0 ${W} ${H + 16}" style="width: 100%; max-width: 900px; height: auto; display: block;" role="img" aria-label="TITN price projection">
                        <polygon points="${area}" fill="#ca5010" opacity="0.12"/>
                        ${unl.map(t => `<line x1="${x(t)}" x2="${x(t)}" y1="4" y2="${H}" stroke="#d13438" stroke-dasharray="3 3" opacity="0.6"><title>Unlocks start ${isoD(t)}</title></line>`).join('')}
                        <line x1="${x(m.now)}" x2="${x(m.now)}" y1="4" y2="${H}" stroke="currentColor" opacity="0.35"/><text x="${x(m.now) + 4}" y="14" font-size="10" fill="currentColor" opacity="0.7">Today</text>
                        <polyline points="${poly(hist)}" fill="none" stroke="#1098ad" stroke-width="2"/>
                        ${lines.map((l, i) => `<polyline points="${poly(l)}" fill="none" stroke="${scen[i][2]}" stroke-width="2" stroke-dasharray="${i === 1 ? '' : '5 3'}"/>`).join('')}
                        ${ticks}${months.map(t => `<text x="${x(t)}" y="${H + 13}" font-size="10" text-anchor="middle" fill="currentColor" opacity="0.7">${new Date(t).toLocaleDateString('en-US', { month: 'short', year: '2-digit', timeZone: 'UTC' })}</text>`).join('')}</svg>`;
                    $('tp-legend').innerHTML = `<span><i style="background: #1098ad;"></i>Price so far</span>` + scen.map(([t, , c]) => `<span><i style="background: ${c};"></i>${t}</span>`).join('') + `<span><i style="background: #ca5010; opacity: 0.25;"></i>Range</span><span><i style="background: #d13438;"></i>Unlocks start</span>`;

                    // Milestones: month ends inside the horizon (at most 8 rows).
                    const ms = []; for (let t = new Date(isoD(m.now).slice(0, 7) + '-01T00:00:00Z'); ;) { t.setUTCMonth(t.getUTCMonth() + 1); const e = t.getTime() - DAY; if (e > end) break; if (e > m.now) ms.push(e); }
                    const step = Math.max(1, Math.ceil(ms.length / 8)), rowsT = ms.filter((_, i) => (i + 1) % step === 0 || i === ms.length - 1);
                    const cell = (t, kk) => { const p = proj(t, kk); return `$${p.toFixed(4)} <span style="color: ${p < p0 ? '#d13438' : '#107c10'}; font-size: 12px;">${pct0(p)}</span>`; };
                    $('tp-table').innerHTML = `<tr><td>Today</td><td class="num">${mil(c0)}</td><td class="num" colspan="3" style="text-align: center;">$${p0.toFixed(4)}</td><td></td></tr>` + rowsT.map(t => { const [bl, bh] = band(t, proj(t, k / 2)); return `<tr><td>${day(t)}</td><td class="num">${mil(m.future(t))}</td><td class="num">${cell(t, k / 4)}</td><td class="num">${cell(t, k / 2)}</td><td class="num">${cell(t, k)}</td><td class="num">$${bl.toFixed(4)} – $${bh.toFixed(4)}</td></tr>`; }).join('');

                    // Summary: where the big unlock step ends, and the price then.
                    const shortRows = sched.filter(r => r.months <= 6);
                    const stepEnd = shortRows.length ? Math.max(...shortRows.map(r => Date.parse(r.start + 'T00:00:00Z') + r.months * 30.44 * DAY)) : m.now + 90 * DAY;
                    const tS = Math.min(Math.max(stepEnd, m.now + 30 * DAY), end), pS = proj(tS, k / 2), pSm = proj(tS, k / 4), pSs = proj(tS, k), cS = m.future(tS);
                    $('tp-summary').innerHTML = `If the schedule below happens, circulating TITN goes from <b>${mil(c0)}</b> today to <b>${mil(cS)}</b> by ${day(tS)} (+${((cS / c0 - 1) * 100).toFixed(0)}%). With half of March’s reaction that points to about <b>$${pS.toFixed(4)}</b> (${pct0(pS)}), between $${pSs.toFixed(4)} (like March) and $${pSm.toFixed(4)} (mild). ${pS < p0 * 0.9 ? 'On this model, buying after the unlocks beats buying before them, if the unlocked tokens really are sold.' : 'On this model the coming unlocks are small next to today’s supply.'}`;
                    $('tp-fit').innerHTML = `Fitted from ${fit.n} days of prices since ${day(fit.from + 'T12:00:00Z')}: each 1% rise in circulating supply went with a ${Math.abs(fit.k).toFixed(2)}% ${fit.k < 0 ? 'fall' : 'rise'} in price (almost all of it from the March 3 unlock). Typical daily move apart from unlocks: ${(fit.sd * 100).toFixed(1)}%. Circulating supply today in this model: ${mil(c0)} (CoinMarketCap’s unlocked total).`;

                    $('tp-sched').innerHTML = sched.map((r, i) => `<tr><td>${esc(r.name)}</td><td class="num">${mil(r.amt)}</td><td><input type="date" data-tp-start="${i}" value="${r.start}"></td><td><input type="number" data-tp-months="${i}" min="0" max="120" value="${r.months}" style="width: 70px;"></td></tr>`).join('');
                    document.querySelectorAll('[data-tp-start]').forEach(el => el.onchange = () => { if (el.value) { sched[Number(el.dataset.tpStart)].start = el.value; saveSched(); renderPredict(); } });
                    document.querySelectorAll('[data-tp-months]').forEach(el => el.onchange = () => { sched[Number(el.dataset.tpMonths)].months = Math.max(0, Number(el.value) || 0); saveSched(); renderPredict(); });
                    $('tp-reset').onclick = () => { try { localStorage.removeItem(SCHED_KEY); } catch (e) { } sched = null; renderPredict(); };
                };

                // ---- Growth and revenue ----
                let growth = null, mine = null, tgRange = 180;
                const wk = (iso) => { const t = Date.parse(iso + 'T00:00:00Z'); const d = new Date(t - ((new Date(t).getUTCDay() + 6) % 7) * DAY); return isoD(d); }; // Monday of the week
                const lineChart = (el, series, opts) => {
                    // series: [{ pts: [{t, v}], color, dash, area }]; opts: { log, money, H, right }
                    const all = series.flatMap(s => s.pts.map(p => p.v)).filter(v => v > 0 || !opts.log);
                    if (!all.length) { $(el).innerHTML = ''; return; }
                    const W = 720, H = opts.H || 170, t0 = Math.min(...series.flatMap(s => s.pts.map(p => p.t))), t1 = Math.max(...series.flatMap(s => s.pts.map(p => p.t)));
                    const f = opts.log ? Math.log : (v) => v, lo = opts.log ? f(Math.min(...all)) : 0, hi = f(Math.max(...all));
                    const x = (t) => (t - t0) / Math.max(1, t1 - t0) * W, y = (v) => H - 6 - (f(Math.max(v, opts.log ? Math.min(...all) : 0)) - lo) / Math.max(1e-9, hi - lo) * (H - 20);
                    const lab = (v) => v === 0 ? (opts.money ? '$0' : '0') : opts.money ? '$' + (v >= 1000 ? (v / 1000).toFixed(v >= 1e4 ? 0 : 1) + 'K' : v.toFixed(v < 1 ? 4 : 0)) : opts.pct ? (v * 100).toFixed(0) + '%' : mil(v);
                    const ticks = [hi, (hi + lo) / 2, lo].map((tv, i) => { const v = opts.log ? Math.exp(tv) : tv; return `<text x="4" y="${y(v) + (i === 0 ? 10 : -3)}" font-size="10" fill="currentColor" opacity="0.7">${lab(v)}</text>`; }).join('');
                    const months = []; for (let t = new Date(isoD(t0).slice(0, 7) + '-01T00:00:00Z'); t.getTime() <= t1; t.setUTCMonth(t.getUTCMonth() + (t1 - t0 > 400 * DAY ? 3 : t1 - t0 > 200 * DAY ? 2 : 1))) if (t.getTime() >= t0) months.push(t.getTime());
                    $(el).innerHTML = `<svg viewBox="0 0 ${W} ${H + 16}" style="width: 100%; max-width: 900px; height: auto; display: block; margin-top: 8px;" role="img" aria-label="${esc(opts.label || '')}">${series.map(s => s.bars
                        ? s.pts.map(p => `<rect x="${x(p.t) - s.bars / 2}" y="${y(p.v)}" width="${s.bars}" height="${Math.max(0, H - 6 - y(p.v))}" fill="${s.color}" opacity="0.55"><title>${isoD(p.t)}: ${lab(p.v)}</title></rect>`).join('')
                        : `<polyline points="${s.pts.map(p => `${x(p.t).toFixed(1)},${y(p.v).toFixed(1)}`).join(' ')}" fill="none" stroke="${s.color}" stroke-width="2"${s.dash ? ` stroke-dasharray="${s.dash}"` : ''}/>`).join('')}${ticks}${months.map(t => `<text x="${x(t)}" y="${H + 13}" font-size="10" text-anchor="middle" fill="currentColor" opacity="0.7">${new Date(t).toLocaleDateString('en-US', { month: 'short', year: '2-digit', timeZone: 'UTC' })}</text>`).join('')}</svg>`;
                };
                const renderGrowth = () => {
                    if (!growth) return;
                    if (growth.building) { $('tg-tiles').innerHTML = '<p class="tc-note">Reading the fee and holder history from the chain for the first time; this takes a while. Reload this page later.</p>'; return; }
                    $('tg-splitter').innerHTML = addr(growth.splitter, 'ERC20Splitter3Way');
                    const px = data ? data.price : 0, priceOn = (iso) => { let b = null; for (const r of prices) { if (r.d <= iso) b = r.p; else break; } return b; };
                    const stakedOn = (() => { const m = growth.staked; return (iso) => { let b = null; for (const r of m) { if (r.d <= iso) b = r.total; else break; } return b; }; })();
                    const holdersOn = (() => { const m = growth.holders; return (iso) => { let b = null; for (const r of m) { if (r.d <= iso) b = r.count; else break; } return b; }; })();
                    // Weekly totals (Monday to Sunday).
                    const weeks = {};
                    growth.fees.forEach(f => { const k = wk(f.d); const w = weeks[k] = weeks[k] || { in: 0, stakers: 0, n: 0 }; w.in += f.in; w.stakers += f.stakers; w.n += f.n; });
                    const keys = Object.keys(weeks).sort(), thisWeek = wk(isoD(Date.now()));
                    const full = keys.filter(k => k < thisWeek);
                    const sum = (days) => growth.fees.filter(f => Date.parse(f.d) > Date.now() - days * DAY).reduce((a, f) => a + f.in, 0);
                    const sumSt = (days) => growth.fees.filter(f => Date.parse(f.d) > Date.now() - days * DAY).reduce((a, f) => a + f.stakers, 0);
                    const f30 = sum(30), f30b = sum(60) - f30, st30 = sumSt(30);
                    const stakedNow = data ? data.staking : (growth.staked.length ? growth.staked[growth.staked.length - 1].total : 0);
                    const yieldNow = stakedNow && px ? st30 / 30 * 365 / (stakedNow * px) : null;
                    const shareAll = growth.fees.filter(f => Date.parse(f.d) > Date.now() - 90 * DAY).reduce((a, f) => [a[0] + f.in, a[1] + f.stakers], [0, 0]);
                    $('tg-share').textContent = shareAll[0] ? Math.round(shareAll[1] / shareAll[0] * 100) + '%' : '50%';
                    const h = growth.holders, hNow = growth.holdersNow, h30 = h.length ? holdersOn(isoD(Date.now() - 30 * DAY)) : null;
                    const st30ago = stakedOn(isoD(Date.now() - 30 * DAY));
                    const chg = (a, b) => b ? `${a >= b ? '+' : ''}${((a / b - 1) * 100).toFixed(0)}%` : '–';
                    $('tg-tiles').innerHTML = [
                        ['Fees, last 30 days', usd(f30), `${chg(f30, f30b)} vs the 30 days before`],
                        ['Paid to stakers, 30 days', usd(st30), `${shareAll[0] ? (shareAll[1] / shareAll[0] * 100).toFixed(0) : 50}% of fees (70% is a target, after break-even)`],
                        ['Staker yield now', yieldNow != null ? (yieldNow * 100).toFixed(0) + '% a year' : '–', 'Last 30 days of USDC, at today’s price'],
                        ['Total staked', mil(stakedNow), st30ago ? `${chg(stakedNow, st30ago)} in 30 days` : ''],
                        (() => { const ex = growth.explorerHolders || [], e = ex[ex.length - 1], e30 = ex.find(r => Date.parse(r.d) >= Date.now() - 31 * DAY);
                            return h.length ? ['Holders (1+ TITN)', n0(hNow), h30 ? `${chg(hNow, h30)} in 30 days` : '']
                                : ['Holders (explorer)', e ? n0(e.base + e.arbitrum) : '…', e ? `${n0(e.base)} on Base · ${n0(e.arbitrum)} on Arbitrum${e30 && e30.d !== e.d ? ` · ${chg(e.base + e.arbitrum, e30.base + e30.arbitrum)} since ${day(e30.d + 'T12:00:00Z')}` : ' · recorded daily from today'}` : 'Recorded daily from today']; })(),
                    ].map(([k, v, s2]) => `<div class="tc-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s2}</div></div>`).join('');

                    const from = Date.now() - tgRange * DAY;
                    const wpts = (fn) => full.filter(k => Date.parse(k) >= from).map(k => ({ t: Date.parse(k + 'T00:00:00Z'), v: fn(k) }));
                    const avg4 = (k) => { const i = full.indexOf(k), w = full.slice(Math.max(0, i - 3), i + 1); return w.reduce((a, x) => a + weeks[x].in, 0) / w.length; };
                    lineChart('tg-fees', [{ pts: wpts(k => weeks[k].in), color: '#1098ad', bars: Math.max(2, 720 / Math.max(8, wpts(k => 0).length) - 3) }, { pts: wpts(avg4), color: '#ca5010' }], { money: true, label: 'Weekly fees' });
                    $('tg-fees-legend').innerHTML = `<span><i style="background: #1098ad; opacity: 0.6;"></i>Fees per week (USDC)</span><span><i style="background: #ca5010;"></i>4-week average</span>`;
                    const yw = (k) => { const s2 = stakedOn(k), p = priceOn(k); return s2 && p ? weeks[k].stakers * 52 / (s2 * p) : null; };
                    lineChart('tg-yield', [{ pts: wpts(yw).filter(p => p.v != null && p.v < 20), color: '#107c10' }, { pts: wpts(k => priceOn(k)).filter(p => p.v), color: '#8a8886', dash: '4 3', scale: true }].slice(0, 1), { pct: true, label: 'Staker yield' });
                    $('tg-yield-legend').innerHTML = `<span><i style="background: #107c10;"></i>Staker yield each week (USDC paid × 52 ÷ value staked)</span>`;
                    if (h.length) {
                        lineChart('tg-holders', [{ pts: h.filter(r => Date.parse(r.d) >= from).map(r => ({ t: Date.parse(r.d + 'T00:00:00Z'), v: r.count })), color: '#5c6bc0' }], { label: 'Holders', H: 140 });
                        $('tg-holders-legend').innerHTML = `<span><i style="background: #5c6bc0;"></i>Wallets holding 1+ TITN on Base (staked counted)</span>`;
                    } else { $('tg-holders').innerHTML = ''; $('tg-holders-legend').innerHTML = '<span>Holder history is still being read from the chain.</span>'; }

                    // As a signal: weeks when the 4-week fee average rose vs fell, and the price over the next 4 weeks.
                    const fwd = (k) => { const a = priceOn(k), b = priceOn(isoD(Date.parse(k) + 35 * DAY)); return a && b && Date.parse(k) + 35 * DAY < Date.now() ? b / a - 1 : null; };
                    const up = [], down = [];
                    full.forEach((k, i) => { if (i < 4 || Date.parse(k) < Date.parse('2026-03-10')) return; const r = fwd(k); if (r == null) return; (avg4(k) > avg4(full[i - 1]) ? up : down).push(r); });
                    const mean = (a) => a.length ? a.reduce((x, y2) => x + y2, 0) / a.length : null;
                    const last = full.length > 1 ? avg4(full[full.length - 1]) / avg4(full[full.length - 2]) - 1 : null;
                    $('tg-signal').innerHTML = up.length && down.length
                        ? `Since the March unlock, in weeks when the 4-week fee average was <b>rising</b>, TITN moved <b>${(mean(up) * 100).toFixed(0)}%</b> on average over the next month (${up.length} weeks); when it was <b>falling</b>, <b>${(mean(down) * 100).toFixed(0)}%</b> (${down.length} weeks). Last full week the average was ${last == null ? '–' : last >= 0 ? `<b>rising</b> (+${(last * 100).toFixed(0)}%)` : `<b>falling</b> (${(last * 100).toFixed(0)}%)`}. A small sample, and unlocks move the price too, so use it as one input, not a rule.`
                        : '';
                    $('tg-weeks').innerHTML = keys.slice().reverse().slice(0, 16).map((k, i, arr) => { const w = weeks[k], prev = weeks[arr[i + 1]], y2 = yw(k); return `<tr><td>${day(k + 'T12:00:00Z')}${k === thisWeek ? ' (so far)' : ''}</td><td class="num">${usd(w.in)}</td><td class="num">${usd(w.stakers)}</td><td class="num">${prev && k !== thisWeek ? chg(w.in, prev.in) : '–'}</td><td class="num">${y2 != null && k !== thisWeek ? (y2 * 100).toFixed(0) + '%' : '–'}</td><td class="num">${mil(stakedOn(isoD(Date.parse(k) + 6 * DAY)) || 0)}</td><td class="num">${h.length ? n0(holdersOn(isoD(Date.parse(k) + 6 * DAY)) || 0) : '…'}</td><td class="num">${priceOn(isoD(Date.parse(k) + 6 * DAY)) ? '$' + priceOn(isoD(Date.parse(k) + 6 * DAY)).toFixed(4) : '–'}</td></tr>`; }).join('');
                    $('tg-note').innerHTML = `Fee and staking history from Blockscout, checked ${when(growth.updatedUtc)}. Staker yield uses each week's USDC to stakers and that week's staked TITN and price. Fees in the last few weeks came from many different wallets rather than the one or two sources seen before September, which fits a new swap route paying its fees; the chain alone can't confirm that every dollar is swap fees.`;
                    renderGoal(yieldNow, px);
                    renderVolume(full, weeks, from);
                    renderFinances();
                };
                const renderVolume = (full, weeks, from) => {
                    const src = growth.feeSources || [];
                    if (!src.length) { $('tv-tiles').innerHTML = '<p class="tc-note">Fee sources are still being read.</p>'; return; }
                    // Per week: number of payments (from the daily counts) and distinct payers.
                    const payers = {};
                    src.forEach(r => { const k = wk(r.d); const set = payers[k] = payers[k] || new Set(); Object.keys(r.src).forEach(a => set.add(a)); });
                    const pts = full.filter(k => Date.parse(k) >= from);
                    lineChart('tv-chart', [{ pts: pts.map(k => ({ t: Date.parse(k + 'T00:00:00Z'), v: weeks[k].n })), color: '#1098ad', bars: Math.max(2, 720 / Math.max(8, pts.length) - 3) },
                        { pts: pts.map(k => ({ t: Date.parse(k + 'T00:00:00Z'), v: payers[k] ? payers[k].size : 0 })), color: '#ca5010' }], { label: 'Fee payments per week' });
                    $('tv-legend').innerHTML = `<span><i style="background: #1098ad; opacity: 0.6;"></i>Fee payments per week</span><span><i style="background: #ca5010;"></i>Different payers that week</span>`;
                    const in90 = src.filter(r => Date.parse(r.d) > Date.now() - 90 * DAY), tot = {}, cnt = {}, last = {};
                    in90.forEach(r => Object.entries(r.src).forEach(([a, v]) => { tot[a] = (tot[a] || 0) + v; cnt[a] = (cnt[a] || 0) + 1; if (!last[a] || r.d > last[a]) last[a] = r.d; }));
                    const all = Object.values(tot).reduce((a, v) => a + v, 0), top = Object.entries(tot).sort((a, b) => b[1] - a[1]);
                    const nWeek = (k) => k && weeks[k] ? weeks[k].n : 0, lastK = full[full.length - 1], prevK = full[full.length - 2];
                    const n30 = growth.fees.filter(f => Date.parse(f.d) > Date.now() - 30 * DAY).reduce((a, f) => a + f.n, 0), n30b = growth.fees.filter(f => Date.parse(f.d) > Date.now() - 60 * DAY && Date.parse(f.d) <= Date.now() - 30 * DAY).reduce((a, f) => a + f.n, 0);
                    const chg = (a, b) => b ? `${a >= b ? '+' : ''}${((a / b - 1) * 100).toFixed(0)}%` : '–';
                    const sp = growth.split && growth.split.rows ? growth.split.rows : [];
                    const pctOf = (a) => (sp.find(r => r.to === a) || {}).pct;
                    $('tv-tiles').innerHTML = [
                        ['Fee payments, 30 days', n0(n30), `${chg(n30, n30b)} vs the 30 days before`],
                        ['Last full week', n0(nWeek(lastK)) + ' payments', `${payers[lastK] ? payers[lastK].size : 0} different payers · ${chg(nWeek(lastK), nWeek(prevK))} vs the week before`],
                        ['Average fee payment, 30 days', n30 ? usd(growth.fees.filter(f => Date.parse(f.d) > Date.now() - 30 * DAY).reduce((a, f) => a + f.in, 0) / n30) : '–', 'Bigger payments usually mean bigger swaps'],
                        ['Biggest payer’s share, 90 days', top.length ? (top[0][1] / all * 100).toFixed(0) + '%' : '–', top.length ? `${top.length} different payers in 90 days` : ''],
                        ['Fee split now', sp.length ? `${pctOf(TITN_STAKING) ?? '?'}% stakers` : '–', sp.length ? `${pctOf(OPS) ?? '?'}% operating wallet · ${pctOf(RAFFLE) ?? '?'}% raffle, read from the contract` : 'Not read yet'],
                    ].map(([k, v, s2]) => `<div class="tc-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s2}</div></div>`).join('');
                    $('tv-sources').innerHTML = top.slice(0, 10).map(([a, v]) => `<tr><td>${addr(a, a === '0x68208d99746b805a1ae41421950a47b711e35681' ? 'THORChain router' : '')}</td><td class="num">${usd(v)}</td><td class="num">${(v / all * 100).toFixed(0)}%</td><td class="num">${n0(cnt[a])} days</td><td>${day(last[a] + 'T12:00:00Z')}</td></tr>`).join('') || '<tr><td colspan="5">No fees in the last 90 days.</td></tr>';
                };
                const TITN_STAKING = '0x269e9c0e300dd6c84a38b5781551bf767b2b5327', OPS = '0x1da31de63085794f1ce317ed7da7c0167da862bf', RAFFLE = '0x583ecd462e6a8f3bbba71ec3a3aab53a7b6c01c0';
                const renderFinances = () => {
                    const ops = growth.ops, safe = growth.safe;
                    if (!ops || !ops.days || !ops.days.length) { $('tf-tiles').innerHTML = '<p class="tc-note">The operating wallet’s history is still being read from the chain; reload this page in a few minutes.</p>'; return; }
                    $('tf-ops-addr').innerHTML = addr(ops.address); $('tf-safe-addr').innerHTML = addr(safe.address, 'Safe');
                    const within = (rows, days) => rows.filter(r => Date.parse(r.d) > Date.now() - days * DAY);
                    const s = (rows, k) => rows.reduce((a, r) => a + (r[k] || 0), 0);
                    const o90 = within(ops.days, 90), sf90 = within(safe.days || [], 90);
                    const spend90 = s(o90, 'out') + s(sf90, 'out'), income90 = s(o90, 'in') + s(sf90, 'in');
                    const net90 = income90 - spend90, burnMonth = -net90 / 3;
                    const cash = (ops.balance || 0) + (safe.balance || 0);
                    const runway = burnMonth > 0 ? cash / burnMonth : null;
                    const o30 = within(ops.days, 30);
                    $('tf-tiles').innerHTML = [
                        ['Operating wallet', usd(ops.balance || 0), 'USDC now'],
                        ['Treasury Safe', usd(safe.balance || 0), `USDC now${data ? ' · plus ' + mil(data.treasury.balance) + ' TITN' : ''}`],
                        ['Paid out, 30 days', usd(s(o30, 'out')), `Fees in: ${usd(s(o30, 'fees'))} (its 45%)`],
                        ['Net per month, 90 days', (net90 >= 0 ? '+' : '−') + usd(Math.abs(net90 / 3)), net90 >= 0 ? 'More came in than went out' : 'More went out than came in'],
                        ['USDC runway', runway ? (runway > 36 ? '3+ years' : runway.toFixed(0) + ' months') : 'Not burning', runway ? 'At the last 90 days’ net spending, before any TITN sales' : 'Fees covered spending over the last 90 days'],
                    ].map(([k, v, s2]) => `<div class="tc-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s2}</div></div>`).join('');
                    const from = Date.now() - Math.max(tgRange, 180) * DAY;
                    const months = {};
                    ops.days.forEach(r => { const k = r.d.slice(0, 7); const m = months[k] = months[k] || { fees: 0, other: 0, out: 0, bal: 0, safe: null }; m.fees += r.fees; m.other += r.in - r.fees; m.out += r.out; m.bal = r.balance; });
                    let sb = null; const sMonths = {}; (safe.days || []).forEach(r => { sMonths[r.d.slice(0, 7)] = r.balance; });
                    const mk = Object.keys(months).sort();
                    lineChart('tf-chart', [{ pts: ops.days.filter(r => Date.parse(r.d) >= from).map(r => ({ t: Date.parse(r.d + 'T00:00:00Z'), v: r.balance })), color: '#5c6bc0' },
                        { pts: mk.filter(k => Date.parse(k + '-01') >= from).map(k => ({ t: Date.parse(k + '-15T00:00:00Z'), v: months[k].out })), color: '#d13438', bars: 18 }], { money: true, label: 'Operating wallet' });
                    $('tf-legend').innerHTML = `<span><i style="background: #5c6bc0;"></i>Operating wallet USDC</span><span><i style="background: #d13438; opacity: 0.6;"></i>Paid out each month</span>`;
                    $('tf-months').innerHTML = mk.slice().reverse().slice(0, 14).map(k => { const m = months[k]; let sv = null; Object.keys(sMonths).sort().forEach(x => { if (x <= k) sv = sMonths[x]; }); const net = m.fees + m.other - m.out; return `<tr><td>${k}</td><td class="num">${usd(m.fees)}</td><td class="num">${usd(m.other)}</td><td class="num">${usd(m.out)}</td><td class="num" style="color: ${net < 0 ? '#d13438' : '#107c10'};">${net < 0 ? '−' : '+'}${usd(Math.abs(net))}</td><td class="num">${usd(m.bal)}</td><td class="num">${sv != null ? usd(sv) : '–'}</td></tr>`; }).join('');
                    const pay = ops.payments || [], yr = pay.filter(p => Date.parse(p.time) > Date.now() - 365 * DAY), by = {};
                    yr.forEach(p => { const b = by[p.to] = by[p.to] || { a: 0, n: 0, last: p.time }; b.a += p.amount; b.n++; if (p.time > b.last) b.last = p.time; });
                    const label = (a) => a === TITN_STAKING ? 'TITN staking' : a === '0x68fbde17200f69d0d17e2a858c79c264ee7bcea9' ? 'Treasury Safe' : '';
                    $('tf-payees').innerHTML = Object.entries(by).sort((a, b) => b[1].a - a[1].a).slice(0, 15).map(([a, b]) => `<tr><td>${addr(a, label(a))}</td><td class="num">${usd(b.a)}</td><td class="num">${b.n}</td><td>${day(b.last)}</td></tr>`).join('') || '<tr><td colspan="4">No payments in the last 12 months.</td></tr>';
                    $('tf-payments').innerHTML = pay.slice(0, 40).map(p => `<tr><td>${when(p.time)}</td><td>${addr(p.to, label(p.to))}</td><td class="num">${usd(p.amount)}</td></tr>`).join('');
                };
                const renderGoal = (yieldNow, px) => {
                    if (!mine || !mine.wallets) { $('tg-goal').innerHTML = '<p class="tc-note">Add your Base wallets on <a href="/page/my-wallets">My Wallets</a> to see your progress.</p>'; return; }
                    const held = mine.wallets.reduce((a, w) => a + (w.titnBalance || 0) + (w.titnStaked || 0), 0);
                    const staked = mine.wallets.reduce((a, w) => a + (w.titnStaked || 0), 0), pend = mine.wallets.reduce((a, w) => a + (w.pendingUsdc || 0), 0);
                    const goal = 1e6, need = Math.max(0, goal - held);
                    // Months to the goal by restaking each month's USDC at this yield and price (no new money).
                    let months = null;
                    if (yieldNow && px && need > 0) { let t = held; for (let m = 1; m <= 240; m++) { t *= 1 + yieldNow / 12; if (t >= goal) { months = m; break; } } }
                    $('tg-goal').innerHTML = [
                        ['You hold', mil(held), `${mil(staked)} staked${pend ? ` · ${usd(pend)} USDC to claim` : ''}`],
                        ['Still to get', need ? mil(need) : 'Reached', need && px ? `About ${usd(need * px)} at today’s price` : ''],
                        ['Progress', (Math.min(1, held / goal) * 100).toFixed(0) + '%', `<div class="tc-prog" style="margin-top: 4px;"><b style="width: ${Math.min(100, held / goal * 100)}%"></b></div>`],
                        ['By restaking rewards only', months ? (months < 24 ? months + ' months' : (months / 12).toFixed(1) + ' years') : '–', yieldNow ? `If the ${(yieldNow * 100).toFixed(0)}% yield and today’s price held (they won’t exactly)` : ''],
                    ].map(([k, v, s2]) => `<div class="tc-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s2}</div></div>`).join('');
                };
                document.querySelectorAll('input[name="tg-range"]').forEach(r => r.onchange = () => { tgRange = Number(r.value); renderGrowth(); });
                const loadGrowth = () => {
                    const g = window.opticGet ? window.opticGet('/api/titn/growth') : fetch('/api/titn/growth').then(r => r.json());
                    g.then(j => { if (j) { growth = j; if (data) renderGrowth(); } }).catch(() => { });
                    const m = window.opticGet ? window.opticGet('/api/titn/summary') : fetch('/api/titn/summary').then(r => r.json());
                    m.then(j => { if (j && !j.error) { mine = j; if (data && growth) renderGrowth(); } }).catch(() => { });
                };

                const b64ToBytes = (v) => { v = v.replace(/-/g, '+').replace(/_/g, '/'); const raw = atob(v + '='.repeat((4 - v.length % 4) % 4)); return Uint8Array.from(raw, c => c.charCodeAt(0)); };
                const deviceName = () => { const u = navigator.userAgent; return (/iPhone|iPad/.test(u) ? 'iPhone/iPad' : /Android/.test(u) ? 'Android' : /Windows/.test(u) ? 'Windows' : /Mac/.test(u) ? 'Mac' : 'Device') + ' · ' + (/Edg\//.test(u) ? 'Edge' : /Chrome\//.test(u) ? 'Chrome' : /Firefox\//.test(u) ? 'Firefox' : /Safari\//.test(u) ? 'Safari' : 'browser'); };
                const zone = () => { try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch (e) { return 'UTC'; } };
                const save = (body) => fetch('/api/titn/watch/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }).then(r => { if (!r.ok) throw new Error('HTTP ' + r.status); });
                const renderAlert = () => {
                    const a = alerts || { on: false, min: 1e6, liquidity: false, devices: 0 };
                    const canPush = window.isSecureContext && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
                    $('tc-alert').innerHTML = `<div class="tc-alert"><label style="font-weight: 600;"><input type="checkbox" id="tc-al-on"${a.on ? ' checked' : ''}> 🔔 Alert me when TITN leaves the treasury or is claimed from vesting</label>
                        <div style="margin-top: 6px;">At least <input type="number" id="tc-al-min" min="10000" step="100000" value="${a.min}"> TITN · <label><input type="checkbox" id="tc-al-liq"${a.liquidity ? ' checked' : ''}> include moves into the treasury’s own liquidity</label></div>
                        <div class="tc-note" style="margin: 6px 0 0;">${a.on ? (a.devices ? `On: a notification goes to your ${a.devices} device${a.devices === 1 ? '' : 's'} within about 15 minutes of a move, at any time of day, and it shows in the bell for 3 days.` : 'On: moves show in the bell. To get notifications too, turn them on for this device.') : 'Off.'}
                        ${a.on && !a.devices && canPush ? ' <button type="button" class="btn btn-primary" id="tc-al-push">Turn on notifications on this device</button>' : ''}${msg ? '<br>' + msg : ''}</div></div>`;
                    const push = () => save({ on: $('tc-al-on').checked, min: Number($('tc-al-min').value) || 1e6, liquidity: $('tc-al-liq').checked })
                        .then(() => { alerts = { ...a, on: $('tc-al-on').checked, min: Number($('tc-al-min').value) || 1e6, liquidity: $('tc-al-liq').checked }; msg = ''; renderAlert(); if (window.opticReminders && window.opticReminders.refreshTitn) window.opticReminders.refreshTitn(); })
                        .catch(e => { msg = 'Could not save: ' + esc(e.message); renderAlert(); });
                    $('tc-al-on').onchange = push; $('tc-al-min').onchange = push; $('tc-al-liq').onchange = push;
                    if ($('tc-al-push')) $('tc-al-push').onclick = async () => {
                        try {
                            if (await Notification.requestPermission() !== 'granted') throw new Error('notifications weren’t allowed');
                            const st = await fetch('/api/push/status').then(r => r.json());
                            const reg = await navigator.serviceWorker.register('/sw.js', { scope: '/' });
                            await navigator.serviceWorker.ready;
                            const j = (await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: b64ToBytes(st.publicKey) })).toJSON();
                            const r = await fetch('/api/push/subscribe', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ endpoint: j.endpoint, keys: j.keys, device: deviceName(), timeZone: zone() }) });
                            if (!r.ok) throw new Error('HTTP ' + r.status);
                            alerts = { ...a, devices: a.devices + 1 }; msg = 'Notifications are on for this device.';
                        } catch (e) { msg = 'Could not turn notifications on: ' + esc(e.message); }
                        renderAlert();
                    };
                };
                $('tc-hide-liq').onchange = () => data && render();
                $('tc-big').onchange = () => data && render();

                const load = (fresh) => {
                    if (fresh) $('tc-status').textContent = 'Checking the chain for new transfers…';
                    const u = '/api/titn/watch' + (fresh ? '?fresh=1' : '');
                    const get = !fresh && window.opticGet ? window.opticGet(u) : fetch(u).then(r => r.json());
                    return get.then(j => {
                        if (!j || j.error) throw new Error(j && j.error || 'no answer');
                        data = j.data; alerts = j.alerts; prices = j.prices || []; $('tc-status').textContent = ''; render();
                    }).catch(e => { if (!data) $('tc-status').textContent = 'Could not read the TITN treasury: ' + e.message; });
                };
                // ---- Next payout: the fee splitter's waiting USDC and your cut ----
                const ago = (t) => { const m = Math.round((Date.now() - new Date(t)) / 60000); return m < 60 ? m + ' min ago' : m < 2880 ? Math.round(m / 60) + ' h ago' : Math.round(m / 1440) + ' days ago'; };
                const cents = (v) => '$' + Number(v || 0).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: v < 1 ? 4 : 2 });
                const renderNext = (j) => {
                    $('tn-splitter').innerHTML = addr(j.splitter, 'splitter');
                    $('tn-pct').textContent = j.stakersPct + '%';
                    const last = (j.payouts || [])[0];
                    $('tn-tiles').innerHTML = [
                        ['Waiting in the splitter', usd(j.waiting), `${usd(j.toStakers)} of it goes to stakers`],
                        ['Your cut of it', cents(j.myCut), j.myStaked ? `${n0(j.myStaked)} TITN staked = ${(j.share * 100).toFixed(3)}% of ${mil(j.totalStaked)}` : 'No TITN staked in your wallets'],
                        ['Ready to harvest now', cents(j.harvest), 'already in the staking contract'],
                        ['Last payout', last ? ago(last.time) : '—', last ? `${usd(last.stakers)} to stakers · your cut ${cents(last.stakers * j.share)}` : ''],
                        ['Payouts come about every', j.medianGapHours ? (j.medianGapHours < 1 ? Math.round(j.medianGapHours * 60) + ' min' : j.medianGapHours + ' h') : '—', 'typical gap between recent payouts'],
                    ].map(([k, v, s2]) => `<div class="tc-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s2}</div></div>`).join('');
                    $('tn-list').innerHTML = (j.payouts || []).length ? `<div class="data-table-wrap"><table class="data-table"><thead><tr><th>When</th><th style="text-align: right;">Paid out</th><th style="text-align: right;">To stakers</th><th style="text-align: right;">Your cut (at today's stake)</th></tr></thead><tbody>${j.payouts.slice(0, 20).map(p => `<tr><td>${txl(p.tx, esc(when(p.time)))}</td><td style="text-align: right;">${usd(p.total)}</td><td style="text-align: right;">${usd(p.stakers)}</td><td style="text-align: right;">${cents(p.mine)}</td></tr>`).join('')}</tbody></table></div>` : '<p class="tc-note">No payouts found.</p>';
                };
                const loadNext = () => fetch('/api/titn/next-payout').then(r => r.json()).then(j => { if (j && !j.error) renderNext(j); else $('tn-tiles').innerHTML = `<p class="tc-note">${esc(j && j.error || 'Could not read the fee splitter.')}</p>`; }).catch(() => { });
                loadNext();
                setInterval(() => { if (!document.hidden) loadNext(); }, 60000);

                window.addEventListener('optic-fresh', () => { load(false); loadGrowth(); });
                loadGrowth();
                load(false);
            })();
        </script>
""";
}
