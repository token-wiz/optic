// Market > OPT Sell Plan: how much OPT to sell each day, through which Osmosis pools, to get the most dollars for the OPT you
// have (in your wallets now, unbonding, and optionally locked OPT once it unlocks). Built from the live pools:
//   - What a sale returns comes from Osmosis's router quotes into USDC at many sizes (/api/osmosis/sell-model).
//   - After a sale the pools hold extra OPT until other traders buy it back. The plan assumes they keep buying at their pace of
//     the last 30 days; OPT you sell faster than that stays in the pools and lowers the price of your next sale.
//   - Each daily size is simulated to the last OPT; the plan is the one that returns the most while finishing by your date.
//   - The amount can be all your OPT, just what is unbonding, or any amount from a start date (a "what if").
//   - "All my OPT" also counts OPT still to come: node and staking payouts from the Distro Wallet to your Optio wallets,
//     expected at each wallet's pace over the last 30 days, every day up to the finish date; and unclaimed staking rewards.
//   - "Start this plan" saves it. After that, each time the page opens it reads your OPT sales on Osmosis since the start,
//     re-plans what is left to the same finish date and says how much to sell today, and shows progress against the plan.
//   - While a plan is followed, notifications can be turned on for each device (Web Push, see WebDashboard.WebPush.cs).
public static partial class WebDashboard
{
    static void AppendSellPlanPage(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(SellPlanHtml);
    }

    const string SellPlanHtml = """
        <style>
            .sp { grid-column: 1 / -1; }
            .sp .sp-note { font-size: 13px; color: var(--text-secondary); margin: 0 0 8px; max-width: 1040px; }
            .sp h3 { margin: 18px 0 6px; font-size: 16px; }
            .sp .num { text-align: right; white-space: nowrap; }
            .sp .sp-plan { border: 2px solid var(--success, #107c10); border-radius: 10px; padding: 12px 14px; margin: 10px 0; }
            .sp .sp-plan .big { font-size: 22px; font-weight: 700; line-height: 1.25; }
            .sp .sp-today { border-radius: 10px; padding: 10px 14px; margin: 10px 0; border: 1px solid var(--border); }
            .sp .sp-today.go { border-color: var(--success, #107c10); }
            .sp .sp-today.wait { border-color: var(--warning, #f7b500); background: rgba(247, 181, 0, 0.08); }
            .sp .sp-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 8px; margin-bottom: 10px; }
            .sp .sp-tile { border: 1px solid var(--border); border-radius: 8px; padding: 8px 10px; }
            .sp .sp-tile .k { font-size: 12px; color: var(--text-secondary); }
            .sp .sp-tile .v { font-size: 18px; font-weight: 700; }
            .sp .sp-tile .s { font-size: 12px; color: var(--text-muted); }
            .sp tr.is-pick td { background: rgba(16, 124, 16, 0.10); font-weight: 600; }
            .sp .sp-controls { display: flex; gap: 12px; flex-wrap: wrap; align-items: flex-end; margin-bottom: 6px; }
            .sp .sp-controls label { font-size: 13px; }
            .sp details summary { cursor: pointer; font-size: 13px; margin: 6px 0; }
            .sp .sp-track { border: 2px solid var(--accent-primary, #0078d4); border-radius: 10px; padding: 12px 14px; margin: 10px 0 14px; }
            .sp .sp-track .big { font-size: 24px; font-weight: 700; line-height: 1.25; }
            .sp .sp-badge { display: inline-block; padding: 2px 8px; border-radius: 10px; font-size: 12px; font-weight: 600; vertical-align: middle; }
            .sp .sp-badge.ok { background: rgba(16, 124, 16, 0.15); color: var(--success, #107c10); }
            .sp .sp-badge.ahead { background: rgba(0, 120, 212, 0.15); color: var(--accent-primary, #0078d4); }
            .sp .sp-badge.behind { background: rgba(197, 15, 31, 0.12); color: var(--danger, #c50f1f); }
            .sp .sp-bar { height: 10px; border-radius: 5px; background: var(--border); overflow: hidden; position: relative; margin: 6px 0 2px; }
            .sp .sp-bar > div { position: absolute; top: 0; bottom: 0; left: 0; }
            .sp .sp-chart { width: 100%; height: 190px; display: block; }
            .sp .sp-chart text { font-size: 11px; fill: var(--text-secondary); }
            .sp .sp-btns { display: flex; gap: 8px; flex-wrap: wrap; margin-top: 8px; }
        </style>
        <div class="card sp">
            <h2>OPT Sell Plan</h2>
            <div class="card-content">
                <p class="sp-note">How much of your OPT to sell each day, and through which Osmosis pools, to get the most dollars. The OPT pools are small, so selling a lot at once pushes the price down; the plan sells at about the pace other traders buy OPT back, so the pools refill between your sales.</p>
                <div id="sp-track"></div>
                <div id="sp-push"></div>
                <div class="sp-controls">
                    <div class="form-group" style="margin-bottom: 0;"><label for="sp-what">Plan for</label>
                        <select id="sp-what"><option value="mine">All my OPT</option><option value="unbonding">My unbonding OPT</option><option value="custom">Any amount (simulate)</option></select></div>
                    <div class="form-group" id="sp-custom-box" style="margin-bottom: 0;" hidden><label for="sp-amount">OPT to sell</label><input type="text" id="sp-amount" inputmode="decimal" value="8,000,000" style="width: 140px; text-align: right;"></div>
                    <div class="form-group" id="sp-start-box" style="margin-bottom: 0;" hidden><label for="sp-start">Start selling</label><input type="date" id="sp-start"></div>
                    <div class="form-group" style="margin-bottom: 0;"><label for="sp-by">Finish by</label><input type="date" id="sp-by"></div>
                    <div class="form-group" style="margin-bottom: 0;"><label for="sp-scenario">If the price…</label>
                        <select id="sp-scenario"><option value="0">stays where it is</option><option value="30">keeps its last-30-days trend</option><option value="90">keeps its last-90-days trend</option></select></div>
                    <label style="display: inline-flex; gap: 6px; align-items: center;"><input type="checkbox" id="sp-incoming" checked> Include OPT still to come from node and staking payouts</label>
                    <label style="display: inline-flex; gap: 6px; align-items: center;"><input type="checkbox" id="sp-locked"> Include locked OPT when it unlocks</label>
                </div>
                <p class="sp-note" id="sp-status">Reading your wallets and the Osmosis pools…</p>
                <div class="sp-grid" id="sp-have"></div>
                <div id="sp-today"></div>
                <div id="sp-plan"></div>

                <h3>How each day's sale splits across the pools</h3>
                <div id="sp-split"></div>

                <h3>Your schedule</h3>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>When</th><th class="num">Sell per day</th><th class="num">OPT</th><th class="num">Expected</th><th>From</th></tr></thead><tbody id="sp-sched"></tbody></table></div>
                <details><summary>Every day of the plan</summary><div class="data-table-wrap"><table class="data-table"><thead><tr><th>Date</th><th class="num">OPT</th><th class="num">Expected</th><th class="num">Per OPT</th><th>From</th></tr></thead><tbody id="sp-days"></tbody></table></div></details>

                <h3>Other daily sizes</h3>
                <p class="sp-note">Every daily size was played through to the last OPT. Smaller sales get a better price but take longer, and the longer it takes, the more a falling price costs you. Sizes that would finish after your date are greyed out.</p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th class="num">Sell per day</th><th class="num">Days</th><th>Finishes</th><th class="num">Total</th><th class="num">Avg per OPT</th><th class="num">vs best</th></tr></thead><tbody id="sp-alts"></tbody></table></div>

                <h3>What the plan is based on</h3>
                <div class="sp-grid" id="sp-market"></div>
                <div id="sp-weekday" class="sp-note"></div>
                <p class="sp-note">Quotes are Osmosis's router into USDC right now (fees included). "Bought back per day" is the OPT other traders bought on Osmosis over the last 30 days; if they buy less, sell less. This is a plan from today's pools, not a promise of prices. Before selling, the <a href="/page/sell-planner">Sell Planner</a> shows the tax effect, and <a href="/page/osmosis-sell">Sell OPT on Osmosis</a> compares every pool for one sale. To sell OPT from an Optio wallet, send it to your Osmosis account first (Keplr: Send → Osmosis).</p>
            </div>
        </div>
        <script>
            // The shared cache of last visit's answers (as on My Wallets and the Dashboard).
            window.opticGet = window.opticGet || (() => { /* Last visit's answers (saved in this browser per user) are returned at once and replaced when the fresh ones arrive; each arrival fires 'optic-fresh' so the page can redraw. */ const mem = {}, key = (u) => 'optic.cache.' + ((window.opticUser || {}).name || '') + '.' + u; let timer = null; const get = (u) => { if (mem[u]) return mem[u]; let old = null; try { old = JSON.parse(localStorage.getItem(key(u)) || 'null'); } catch (e) { } const live = fetch(u).then(r => r.ok ? r.json() : null).then(j => { if (j) { try { localStorage.setItem(key(u), JSON.stringify({ t: Date.now(), j })); } catch (e) { } } return j; }); if (!old || !old.j) return mem[u] = live; get.cachedAt = Math.min(get.cachedAt || Infinity, old.t); get.waiting = (get.waiting || 0) + 1; live.catch(() => null).then(j => { mem[u] = Promise.resolve(j || old.j); get.waiting--; clearTimeout(timer); timer = setTimeout(() => window.dispatchEvent(new Event('optic-fresh')), 300); }); return mem[u] = Promise.resolve(old.j); }; return get; })();
            (function () {
                const $ = (id) => document.getElementById(id);
                const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
                const usd = (v) => v == null ? '–' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                const n0 = (v) => Number(v || 0).toLocaleString('en-US', { maximumFractionDigits: 0 });
                const big = (v) => v >= 1e6 ? (v / 1e6).toFixed(v >= 1e7 ? 1 : 2).replace(/\.?0+$/, '') + 'M' : v >= 1e3 ? Math.round(v / 1e3) + 'K' : n0(v);
                const px = (v) => v == null ? '–' : '$' + Number(v).toLocaleString('en-US', { maximumSignificantDigits: 4 });
                const pct = (v) => (v * 100).toFixed(1) + '%';
                const dayMs = 86400000;
                // Days are your own calendar days (this device's time zone). Each day is kept as midnight UTC of that date, so the
                // date arithmetic and labels below don't shift with the time zone.
                const localDay = (d) => d.toLocaleDateString('en-CA');
                const today = Date.parse(localDay(new Date()) + 'T00:00:00Z');
                const fmtDay = (t) => new Date(t).toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric', timeZone: 'UTC' });
                const fmtDate = (t) => new Date(t).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' });

                let model = null, tranches = null;

                // What selling x OPT at once returns now (USD), from the quote grid; straight lines between grid sizes.
                const curve = () => {
                    const pts = [[0, 0]].concat(model.quotes.filter(q => q.usd != null).map(q => [q.opt, q.usd])).sort((a, b) => a[0] - b[0]);
                    return (x) => {
                        if (x <= 0) return 0;
                        for (let i = 1; i < pts.length; i++) if (x <= pts[i][0]) { const [x0, y0] = pts[i - 1], [x1, y1] = pts[i]; return y0 + (y1 - y0) * (x - x0) / (x1 - x0); }
                        return pts[pts.length - 1][1]; // beyond the deepest quote the pools are empty: nothing more
                    };
                };

                // Play one daily size through: OPT becomes sellable on its date; each day sells up to `size`; OPT not yet bought
                // back by other traders (backlog) lowers the next sale; the price moves by the chosen trend.
                const simulate = (size, U, drift, tr) => {
                    const queue = tr.map(t => ({ ...t, left: t.opt })).sort((a, b) => a.t - b.t);
                    const total = queue.reduce((n, t) => n + t.opt, 0);
                    const B = Math.max(0, model.buyFlowPerDay || 0);
                    let backlog = 0, sold = 0, usdSum = 0, day = today;
                    const rows = [];
                    for (let guard = 0; guard < 2000 && sold < total - 0.5; guard++, day += dayMs) {
                        const ready = queue.filter(t => t.t <= day && t.left > 0.5);
                        if (!ready.length) { backlog = Math.max(0, backlog - B); continue; }
                        let q = Math.min(size, ready.reduce((n, t) => n + t.left, 0));
                        const from = [];
                        let need = q;
                        for (const t of ready) { if (need <= 0) break; const take = Math.min(t.left, need); t.left -= take; need -= take; if (take > 0.5) from.push(t.from); }
                        const factor = Math.pow(1 + drift, (day - today) / dayMs / 30);
                        const got = Math.max(0, U(backlog + q) - U(backlog)) * factor;
                        rows.push({ t: day, opt: q, usd: got, from: [...new Set(from)].join(' + ') });
                        usdSum += got; sold += q;
                        backlog = Math.max(0, backlog + q - B);
                    }
                    return { size, total: usdSum, days: rows.length, end: rows.length ? rows[rows.length - 1].t : today, rows, opt: sold };
                };

                // Every daily size played through; the pick returns the most while finishing by the deadline (the fastest if none can).
                const choose = (tr, deadline, drift, U) => {
                    const total = tr.reduce((n, t) => n + t.opt, 0);
                    const sizes = [10e3, 25e3, 50e3, 75e3, 100e3, 150e3, 200e3, 250e3, 300e3, 400e3, 500e3, 750e3, 1e6, 1.5e6, 2e6, 3e6, 5e6, 1e7, 2e7, 5e7].filter(s => s <= Math.max(total, 10e3) * 1.0001 || s === 10e3);
                    if (!sizes.includes(total) && total <= 5e7) sizes.push(total); // all at once
                    // The smallest daily amount that still finishes by the date (selling every day from when the OPT comes free).
                    const first = Math.min(...tr.map(t => t.t));
                    if (deadline && deadline >= first) {
                        const need = Math.ceil(total / Math.max(1, Math.round((deadline - first) / dayMs) + 1) / 1000) * 1000;
                        for (const f of [1, 1.1, 1.25, 1.5]) { const v = Math.ceil(need * f / 1000) * 1000; if (v < total && !sizes.includes(v)) sizes.push(v); }
                    }
                    const runs = sizes.sort((a, b) => a - b).map(s => simulate(s, U, drift, tr));
                    const best = runs.reduce((a, b) => b.total > a.total ? b : a);
                    const feasible = runs.filter(r => !deadline || r.end <= deadline);
                    const pick = feasible.length ? feasible.reduce((a, b) => b.total > a.total ? b : a) : runs.reduce((a, b) => b.end < a.end ? b : a);
                    return { runs, best, pick, late: !feasible.length, first, total };
                };
                const driftFor = (scen) => scen === '30' ? (model.change30 || 0) : scen === '90' ? Math.pow(1 + (model.change90 || 0), 1 / 3) - 1 : 0;

                const dateVal = (id) => { const v = $(id).value; return v ? Date.parse(v + 'T00:00:00Z') : null; };
                const isoDay = (t) => new Date(t).toISOString().slice(0, 10);
                const render = () => {
                    if (!model || !allTranches) return;
                    const what = $('sp-what').value;
                    $('sp-custom-box').hidden = $('sp-start-box').hidden = what !== 'custom';
                    const deadline = dateVal('sp-by');
                    tranches = what === 'custom'
                        ? [{ opt: Number(String($('sp-amount').value).replace(/[^0-9.]/g, '')) || 0, t: Math.max(today, dateVal('sp-start') || today), from: 'Simulated amount', left: 0 }]
                        : allTranches.filter(t => what === 'unbonding' ? /unbonding/.test(t.from) : (!t.locked || $('sp-locked').checked) && incomingOk(t, $('sp-incoming').checked, deadline));
                    tranches = tranches.map(t => ({ ...t, left: t.opt }));
                    const total = tranches.reduce((n, t) => n + t.opt, 0);
                    const nowOpt = tranches.filter(t => t.t <= today).reduce((n, t) => n + t.opt, 0);
                    const later = tranches.filter(t => t.t > today);
                    const lockedLater = tranches.filter(t => t.locked).reduce((n, t) => n + t.opt, 0);
                    $('sp-have').innerHTML =
                        tile('Sellable now', n0(nowOpt) + ' OPT', model.spot ? `≈ ${usd(nowOpt * model.spot)} at the market price` : '') +
                        tile('Coming free later', n0(later.reduce((n, t) => n + t.opt, 0)) + ' OPT', later.length ? later.slice(0, 3).map(t => `${big(t.opt)} on ${fmtDate(t.t)}`).join(', ') + (later.length > 3 ? '…' : '') : 'nothing unbonding') +
                        (what === 'mine' && incoming.length ? tile('Still to come from payouts', n0(incoming.reduce((n, w) => n + w.node + w.staking, 0)) + ' OPT a day',
                            $('sp-incoming').checked ? `${n0(tranches.filter(t => t.incoming).reduce((n, t) => n + t.opt, 0))} OPT by ${deadline ? fmtDate(deadline) : 'the end'} at the last 30 days' pace (${incoming.map(w => `${esc(w.wallet)}: node ${n0(w.node)}, staking ${n0(w.staking)}`).join('; ')})` : 'not included (tick the box to plan for it)') : '') +
                        tile('Market price now', px(model.spot), model.median7 ? `7-day median ${px(model.median7)}` : '') +
                        tile('Bought back per day', n0(model.buyFlowPerDay) + ' OPT', `by other traders, last 30 days (they sold ${n0(model.sellFlowPerDay)}/day)`);
                    if (total < 1) { $('sp-status').textContent = 'You have no OPT to sell: none is in your wallets, unbonding' + ($('sp-locked').checked ? ' or locked.' : '. Tick "Include locked OPT" to plan for locked OPT.'); $('sp-plan').innerHTML = $('sp-today').innerHTML = ''; $('sp-sched').innerHTML = $('sp-days').innerHTML = $('sp-alts').innerHTML = ''; renderTrack(); return; }

                    const drift = driftFor($('sp-scenario').value);
                    const U = curve();
                    const onTime = (r) => !deadline || r.end <= deadline;
                    const { runs, best, pick, late, first } = choose(tranches, deadline, drift, U);
                    shown = { pick, deadline };
                    const once = runs.find(r => r.size === Math.max(...runs.map(x => x.size)));
                    const quote = model.quotes.filter(q => q.usd != null).reduce((a, b) => Math.abs(Math.log(b.opt / pick.size)) < Math.abs(Math.log(a.opt / pick.size)) ? b : a);
                    const poolName = (id) => model.pools[id] ? `${model.pools[id]} (pool ${id})` : `pool ${id}`;
                    const routeText = (quote.routes || []).map(r => `${Math.round(r.share * 100)}% via ${r.pools.map(poolName).join(' → ')}`).join('; ');
                    const daily = Math.min(pick.size, total);
                    const steps = (r) => r.pools.map((id, i) => `<div>${i + 1}. ${esc(poolName(id))}</div>`).join('');
                    $('sp-split').innerHTML = `<p class="sp-note">For ${n0(daily)} OPT a day, Osmosis's router uses ${quote.routes.length > 1 ? 'these pools together' : 'this route'} (from its live quote for ${n0(quote.opt)} OPT). One <strong>OPT → USDC</strong> swap in the Osmosis app does this split for you; to do it by hand, make one swap per row.</p>
                        <div class="data-table-wrap"><table class="data-table"><thead><tr><th class="num">Share</th><th class="num">OPT per day</th><th>Pools, in order</th></tr></thead><tbody>${(quote.routes || []).map(r => `<tr><td class="num">${Math.round(r.share * 100)}%</td><td class="num">${n0(daily * r.share)}</td><td style="font-size: 12px;">${steps(r)}</td></tr>`).join('')}</tbody></table></div>`;
                    const split = pick.size >= 150e3;
                    $('sp-status').textContent = `${n0(total)} OPT to sell. Pools and quotes read ${new Date(model.readUtc).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}.`;
                    $('sp-plan').innerHTML = `<div class="sp-plan">
                        <div style="font-size: 13px; color: var(--text-secondary);">Recommended plan</div>
                        <div class="big">Sell ${n0(Math.min(pick.size, total))} OPT a day for ${pick.days} day${pick.days === 1 ? '' : 's'} → about ${usd(pick.total)}</div>
                        <div style="font-size: 13px; margin-top: 4px;">${pick.days > 1 ? `From ${fmtDate(pick.rows[0].t)} to ${fmtDate(pick.end)}` : `On ${fmtDate(pick.rows[0].t)}`} · average ${px(pick.total / pick.opt)} per OPT${model.spot ? ', ' + pct(1 - pick.total / pick.opt / model.spot) + ' below today’s price' : ''}${once && once !== pick ? ` · selling everything at once would get only ${usd(once.total)}` : ''}.</div>
                        <div style="font-size: 13px; margin-top: 6px;"><strong>Where:</strong> on Osmosis, swap <strong>OPT → USDC</strong>; the router splits it the best way: ${esc(routeText || 'through the deepest pools')}.${split ? ` Do it as <strong>two swaps of about ${n0(pick.size / 2)} OPT</strong> a few hours apart, so arbitrage can refill the pools in between.` : ''}</div>
                        <div style="font-size: 12px; color: var(--text-secondary); margin-top: 6px;">Why this size: ${deadline ? `it's the plan that returns the most while finishing by ${fmtDate(deadline)}. ` : ''}Other traders buy back about ${n0(model.buyFlowPerDay)} OPT a day; ${pick.size > model.buyFlowPerDay * 1.05 ? `<strong>this pace is faster than that</strong>, so OPT builds up in the pools and each later sale gets a little less. A later finish date would help.` : `selling at or below that pace lets the pools refill between your sales.`}${best !== pick ? ` With no deadline, ${n0(best.size)} a day over ${best.days} days would get ${usd(best.total - pick.total)} more.` : ''}</div>
                        ${late ? `<div style="font-size: 13px; margin-top: 6px; color: var(--danger, #c50f1f);"><strong>This can't finish by ${fmtDate(deadline)}</strong>${first > deadline ? ' (the OPT is not free until ' + fmtDate(first) + ')' : ''}; shown is the fastest plan.</div>` : ''}
                        <div class="sp-btns"><button type="button" class="btn btn-primary" id="sp-start-plan">${saved ? 'Replace my saved plan with this one' : 'Start this plan'}</button>
                        <span class="sp-note" style="margin: 0; align-self: center;">${saved ? 'Your saved plan is followed at the top of the page.' : 'Saves it, so each day this page tells you how much to sell and whether you are keeping up.'}</span></div>
                    </div>`;
                    $('sp-start-plan').addEventListener('click', startPlan);
                    // Today: sell, or wait if the price is well below its recent level (usually right after someone else's big sale).
                    const ratio = model.spot && model.median7 ? model.spot / model.median7 : 1;
                    const firstDay = pick.rows[0];
                    const wd = (model.weekday || []).filter(w => w.vsWeek != null);
                    const todayName = new Date(today).toLocaleDateString('en-US', { weekday: 'long', timeZone: 'UTC' });
                    const todayW = wd.find(w => w.day === todayName);
                    $('sp-today').innerHTML = saved ? '' : firstDay && firstDay.t > today
                        ? `<div class="sp-today"><strong>Nothing to sell yet.</strong> Your first OPT comes free on ${fmtDate(firstDay.t)}; check back that day.</div>`
                        : ratio < 0.9
                            ? `<div class="sp-today wait"><strong>Today: wait.</strong> The price (${px(model.spot)}) is ${pct(1 - ratio)} below its 7-day median (${px(model.median7)}), which usually follows someone else's big sale. It has recovered within 2–7 days before; check again tomorrow.</div>`
                            : `<div class="sp-today go"><strong>Today: sell ${n0(firstDay.opt)} OPT</strong>${firstDay.opt >= 150e3 ? ` (two swaps of ${n0(firstDay.opt / 2)})` : ''} for about ${usd(firstDay.usd)}. The price is ${ratio >= 1 ? pct(ratio - 1) + ' above' : pct(1 - ratio) + ' below'} its 7-day median.${todayW && todayW.vsWeek < -0.02 ? ` ${todayName}s have tended to be about ${pct(-todayW.vsWeek)} weaker; tomorrow may be a little better.` : ''}</div>`;
                    // Schedule grouped into runs with the same daily amount and source.
                    const groups = [];
                    for (const r of pick.rows) {
                        const g = groups[groups.length - 1];
                        if (g && Math.abs(g.per - r.opt) < 1 && g.from === r.from && r.t - g.end <= dayMs) { g.end = r.t; g.opt += r.opt; g.usd += r.usd; g.n++; }
                        else groups.push({ start: r.t, end: r.t, per: r.opt, opt: r.opt, usd: r.usd, from: r.from, n: 1 });
                    }
                    $('sp-sched').innerHTML = groups.map(g => `<tr><td>${g.n > 1 ? `${fmtDate(g.start)} – ${fmtDate(g.end)} (${g.n} days)` : fmtDay(g.start)}</td><td class="num">${n0(g.per)}</td><td class="num">${n0(g.opt)}</td><td class="num">${usd(g.usd)}</td><td>${esc(g.from)}</td></tr>`).join('')
                        + `<tr class="tr-total"><td><strong>Total</strong></td><td></td><td class="num"><strong>${n0(pick.opt)}</strong></td><td class="num"><strong>${usd(pick.total)}</strong></td><td></td></tr>`;
                    $('sp-days').innerHTML = pick.rows.map(r => `<tr><td>${fmtDay(r.t)}</td><td class="num">${n0(r.opt)}</td><td class="num">${usd(r.usd)}</td><td class="num">${px(r.usd / r.opt)}</td><td>${esc(r.from)}</td></tr>`).join('');
                    $('sp-alts').innerHTML = runs.slice().sort((a, b) => a.size - b.size).map(r => `<tr class="${r === pick ? 'is-pick' : ''}"${onTime(r) ? '' : ' style="opacity: 0.5;"'}><td class="num">${r.size >= total ? 'All at once' : n0(r.size)}</td><td class="num">${r.days}</td><td>${fmtDate(r.end)}</td><td class="num">${usd(r.total)}</td><td class="num">${px(r.total / Math.max(1, r.opt))}</td><td class="num">${r === best ? 'best' : '−' + usd(best.total - r.total)}</td></tr>`).join('');
                    $('sp-market').innerHTML = model.quotes.filter(q => q.usd != null && [10e3, 100e3, 250e3, 1e6].includes(q.opt)).map(q => tile(`Sell ${big(q.opt)} OPT now`, usd(q.usd), `${pct(1 - q.usd / q.opt / model.spot)} below market`)).join('')
                        + tile('Price trend', model.change30 != null ? (model.change30 >= 0 ? '+' : '') + pct(model.change30) : '–', `last 30 days · ${model.change90 != null ? (model.change90 >= 0 ? '+' : '') + pct(model.change90) : '–'} over 90 days`);
                    renderTrack();
                    $('sp-weekday').innerHTML = wd.length ? 'By weekday, the Osmosis price has been: ' + wd.map(w => `${w.day.slice(0, 3)} ${w.vsWeek >= 0 ? '+' : ''}${pct(w.vsWeek)}`).join(' · ') + ' against its weekly median. Small and not reliable on its own; use it to choose between two days, not to wait a week.' : '';
                };
                // ---- Notifications on selling days (Web Push) ----
                const b64ToBytes = (v) => { v = v.replace(/-/g, '+').replace(/_/g, '/'); const raw = atob(v + '='.repeat((4 - v.length % 4) % 4)); return Uint8Array.from(raw, c => c.charCodeAt(0)); };
                const hourOpts = (h) => Array.from({ length: 22 }, (_, i) => `<option value="${i}"${i === h ? ' selected' : ''}>${new Date(Date.UTC(2000, 0, 1, i)).toLocaleTimeString('en-US', { hour: 'numeric', timeZone: 'UTC' })}</option>`).join('');
                const zone = () => { try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch (e) { return 'UTC'; } };
                const deviceName = () => { const u = navigator.userAgent; return (/iPhone|iPad/.test(u) ? 'iPhone/iPad' : /Android/.test(u) ? 'Android' : /Windows/.test(u) ? 'Windows' : /Mac/.test(u) ? 'Mac' : 'Device') + ' · ' + (/Edg\//.test(u) ? 'Edge' : /Chrome\//.test(u) ? 'Chrome' : /Firefox\//.test(u) ? 'Firefox' : /Safari\//.test(u) ? 'Safari' : 'browser'); };
                let pushMsg = '';
                const renderPush = async () => {
                    const box = $('sp-push');
                    if (!saved) { box.innerHTML = ''; return; }
                    const wrap = (inner) => `<div class="sp-tile" style="margin: -4px 0 14px;"><div class="k">🔔 Notifications on selling days</div><div style="font-size: 13px; margin-top: 4px;">${inner}</div>${pushMsg ? `<div class="sp-note" style="margin: 6px 0 0;">${pushMsg}</div>` : ''}</div>`;
                    const ios = /iPhone|iPad/.test(navigator.userAgent), standalone = window.navigator.standalone || window.matchMedia('(display-mode: standalone)').matches;
                    if (!window.isSecureContext) { box.innerHTML = wrap('Notifications need a secure connection: open OPTIC with https:// and a certificate this device trusts (see Admin → HTTPS).'); return; }
                    if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) {
                        box.innerHTML = wrap(ios && !standalone ? 'On iPhone or iPad: tap Share → <strong>Add to Home Screen</strong>, open OPTIC from the new Home Screen icon, then turn notifications on here (iOS 16.4 or later).' : 'This browser can’t receive push notifications.');
                        return;
                    }
                    if (Notification.permission === 'denied') { box.innerHTML = wrap('Notifications are blocked for OPTIC in this browser’s site settings. Allow them there, then reload this page.'); return; }
                    let st = null, sub = null;
                    try { st = await fetch('/api/push/status').then(r => r.ok ? r.json() : null); } catch (e) { }
                    try { const reg = await navigator.serviceWorker.getRegistration('/'); sub = reg ? await reg.pushManager.getSubscription() : null; } catch (e) { }
                    if (!st) { box.innerHTML = wrap('Could not read the notification settings.'); return; }
                    const devices = st.devices || [];
                    const on = !!sub && devices.some(d => d.endpoint === sub.endpoint);
                    const others = devices.filter(d => !sub || d.endpoint !== sub.endpoint).length;
                    const settings = `at <select id="sp-push-hour">${hourOpts(st.hour)}</select> <label style="display: inline-flex; gap: 4px; align-items: center; margin-left: 6px;"><input type="checkbox" id="sp-push-evening"${st.evening ? ' checked' : ''}> remind me again at 6 PM if I haven't sold</label>`;
                    box.innerHTML = wrap(on
                        ? `<strong>On for this device</strong>, ${settings}${others ? ` · also on ${others} other device${others === 1 ? '' : 's'}` : ''}${st.lastReceived ? `<div class="sp-note" style="margin: 4px 0 0;">Last message received by a device: ${esc(st.lastReceived)}</div>` : ''}<div class="sp-note" style="margin: 4px 0 0;">On days the sale is two swaps, you also get a reminder for the second swap about 3 hours after your first one, whatever the time.</div><div class="sp-btns"><button type="button" class="btn btn-secondary" id="sp-push-off">Turn off on this device</button></div>`
                        : `Get a notification on days you have OPT to sell, ${settings}${others ? ` (already on for ${others} other device${others === 1 ? '' : 's'})` : ''}<div class="sp-btns"><button type="button" class="btn btn-primary" id="sp-push-on">Turn on notifications</button></div>`);
                    const settingsBody = () => ({ hour: Number($('sp-push-hour').value), timeZone: zone(), evening: $('sp-push-evening').checked });
                    const saveSettings = () => { if (on) fetch('/api/push/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(settingsBody()) }); };
                    $('sp-push-hour').addEventListener('change', saveSettings);
                    $('sp-push-evening').addEventListener('change', saveSettings);
                    if ($('sp-push-on')) $('sp-push-on').addEventListener('click', async () => {
                        pushMsg = '';
                        $('sp-push-on').disabled = true;
                        try {
                            if (await Notification.requestPermission() !== 'granted') { pushMsg = 'Notifications weren’t allowed.'; return renderPush(); }
                            let reg;
                            try { reg = await navigator.serviceWorker.register('/sw.js', { scope: '/' }); }
                            catch (e) { pushMsg = 'The browser refused to install OPTIC’s notification helper (' + esc(e.message) + '). This usually means OPTIC’s certificate isn’t trusted on this device; see Admin → HTTPS.'; return renderPush(); }
                            await navigator.serviceWorker.ready;
                            const s = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: b64ToBytes(st.publicKey) });
                            const j = s.toJSON();
                            const r = await fetch('/api/push/subscribe', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ endpoint: j.endpoint, keys: j.keys, device: deviceName(), ...settingsBody() }) });
                            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || 'HTTP ' + r.status);
                            pushMsg = 'Notifications are on. An admin can send a test from Admin → Notifications.';
                        } catch (e) { pushMsg = 'Could not turn notifications on: ' + esc(e.message); }
                        renderPush();
                    });
                    if ($('sp-push-off')) $('sp-push-off').addEventListener('click', async () => {
                        try { await fetch('/api/push/unsubscribe', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ endpoint: sub.endpoint }) }); await sub.unsubscribe(); } catch (e) { }
                        pushMsg = 'Notifications are off on this device.';
                        renderPush();
                    });
                };

                // ---- Following a saved plan ----
                let saved = null, sales = null, salesInfo = null, shown = null, sentForecast = '';
                const todayIso = isoDay(today);
                const parseDay = (d) => Date.parse(d + 'T00:00:00Z');

                const startPlan = async () => {
                    if (!shown || !shown.pick.rows.length) return;
                    if (saved && !confirm('Replace the plan you are following (started ' + fmtDate(parseDay(saved.start)) + ')? Progress is then counted from today.')) return;
                    const p = shown.pick;
                    const body = {
                        start: todayIso, finishBy: isoDay(shown.deadline || p.end), goalOpt: p.opt, goalUsd: p.total,
                        mode: $('sp-what').value, scenario: $('sp-scenario').value, includeLocked: $('sp-locked').checked, includeIncoming: $('sp-incoming').checked,
                        schedule: p.rows.map(r => ({ d: isoDay(r.t), opt: Math.round(r.opt * 100) / 100, usd: Math.round(r.usd * 100) / 100 })),
                    };
                    const r = await fetch('/api/sell-plan/saved', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
                    const j = await r.json().catch(() => ({}));
                    if (!r.ok) { alert(j.error || 'Could not save the plan.'); return; }
                    saved = j.plan; sales = null; salesInfo = null; render(); loadSales(); renderPush();
                    if (window.opticReminders && window.opticReminders.refreshSell) window.opticReminders.refreshSell();
                    window.scrollTo({ top: 0, behavior: 'smooth' });
                };
                const stopPlan = async () => {
                    if (!confirm('Stop following this plan? The page goes back to planning only.')) return;
                    await fetch('/api/sell-plan/saved', { method: 'DELETE' });
                    saved = null; sales = null; salesInfo = null; render(); renderPush();
                    if (window.opticReminders && window.opticReminders.refreshSell) window.opticReminders.refreshSell();
                };
                // Sales: first straight from the server's saved history (instant), then again once it has checked Osmosis for newer ones.
                let checking = false;
                const loadSales = () => {
                    if (!saved) return;
                    const plan = saved;
                    const url = '/api/osmosis/opt-sales?since=' + isoDay(parseDay(plan.start) - dayMs);
                    const take = (j) => {
                        if (saved !== plan) return;
                        // A plan started today counts only the sales made after it was started.
                        const cut = plan.createdUtc && localDay(new Date(plan.createdUtc)) === plan.start ? Date.parse(plan.createdUtc) : new Date(plan.start + 'T00:00:00').getTime();
                        sales = (j.sales || []).filter(s => Date.parse(s.time) >= cut); salesInfo = { ...j, checking }; render();
                    };
                    const get = (u) => fetch(u).then(r => r.json().then(j => { if (!r.ok) throw new Error(j.error || 'HTTP ' + r.status); return j; }));
                    checking = true;
                    if (!sales) get(url + '&update=0').then(j => { if (checking) take(j); }).catch(() => { });
                    else render();
                    get(url).then(j => { checking = false; take(j); if (window.opticReminders && window.opticReminders.refreshSell) window.opticReminders.refreshSell(); })
                        .catch(e => { checking = false; salesInfo = { ...(salesInfo || {}), checking: false, error: e.message }; render(); });
                };

                // What is left of the saved plan, as OPT by the date it can be sold: the OPT you hold now (sold OPT is already gone
                // from the balances) plus what you sold today (so today's target stays today's), up to the plan's amount still unsold.
                const leftTranches = (soldBefore, soldToday) => {
                    let cap = Math.max(0, saved.goalOpt - soldBefore);
                    const startT = saved.schedule.length ? parseDay(saved.schedule[0].d) : today;
                    let src;
                    if (saved.mode === 'custom') src = [{ opt: cap, t: Math.max(today, startT), from: 'Planned amount' }];
                    else {
                        src = allTranches.filter(t => (!t.locked || saved.includeLocked) && incomingOk(t, saved.includeIncoming !== false, parseDay(saved.finishBy))).map(t => ({ ...t }));
                        // Unbonding only: before it completes, other OPT you hold isn't part of this plan.
                        if (saved.mode === 'unbonding' && today < startT) src = src.filter(t => /unbonding/.test(t.from));
                        if (soldToday > 0) src.push({ opt: soldToday, t: today, from: 'Sold today' });
                    }
                    src.sort((a, b) => a.t - b.t);
                    const out = [];
                    for (const t of src) { if (cap <= 0.5) break; const take = Math.min(t.opt, cap); out.push({ ...t, opt: take }); cap -= take; }
                    return { out, short: cap > 0.5 ? cap : 0 };
                };

                const renderTrack = () => {
                    const box = $('sp-track');
                    if (!saved) { box.innerHTML = ''; return; }
                    const sched = saved.schedule.map(r => ({ t: parseDay(r.d), opt: r.opt, usd: r.usd }));
                    const finishBy = parseDay(saved.finishBy);
                    const head = `<div style="font-size: 13px; color: var(--text-secondary);">Your plan · started ${fmtDate(parseDay(saved.start))} · sell ${n0(saved.goalOpt)} OPT by ${fmtDate(finishBy)} for about ${usd(saved.goalUsd)}</div>`;
                    if (!sales) {
                        box.innerHTML = `<div class="sp-track">${head}<p class="sp-note" id="sp-track-status" style="margin-top: 8px;">${salesInfo && salesInfo.error ? 'Could not read your Osmosis sales: ' + esc(salesInfo.error) : 'Checking Osmosis for your newest sales… (the first check can take a minute)'}</p>
                            ${salesInfo && salesInfo.error ? '<div class="sp-btns"><button type="button" class="btn btn-secondary" id="sp-refresh">Try again</button></div>' : ''}</div>`;
                        if ($('sp-refresh')) $('sp-refresh').addEventListener('click', loadSales);
                        return;
                    }
                    const byDay = {};
                    for (const s of sales) { const k = localDay(new Date(s.time)); const d = byDay[k] || (byDay[k] = { opt: 0, usd: 0, n: 0 }); d.opt += s.opt; d.usd += s.usd || 0; d.n++; }
                    const soldTotal = sales.reduce((n, s) => n + s.opt, 0);
                    const usdTotal = sales.reduce((n, s) => n + (s.usd || 0), 0);
                    const soldToday = byDay[todayIso] ? byDay[todayIso].opt : 0;
                    const usdToday = byDay[todayIso] ? byDay[todayIso].usd : 0;
                    const soldBefore = soldTotal - soldToday;
                    const plannedBefore = sched.filter(r => r.t < today).reduce((n, r) => n + r.opt, 0);
                    const plannedThrough = sched.filter(r => r.t <= today).reduce((n, r) => n + r.opt, 0);
                    // What the plan expected the OPT sold so far to bring (its dollars for the same amount, in plan order).
                    let expUsd = 0, acc = 0;
                    for (const r of sched) { if (acc >= soldTotal) break; const take = Math.min(r.opt, soldTotal - acc); expUsd += r.opt ? r.usd * take / r.opt : 0; acc += take; }
                    if (acc < soldTotal && saved.goalOpt) expUsd += (soldTotal - acc) * saved.goalUsd / saved.goalOpt;

                    // Re-plan what is left to the same finish date with today's pools.
                    const left = leftTranches(soldBefore, soldToday);
                    const leftOpt = left.out.reduce((n, t) => n + t.opt, 0);
                    const re = leftOpt > 0.5 ? choose(left.out, finishBy, driftFor(saved.scenario), curve()) : null;
                    const firstRow = re && re.pick.rows[0];
                    const target = firstRow && firstRow.t === today ? firstRow.opt : 0;
                    const leftToday = Math.max(0, target - soldToday);
                    const next = re ? re.pick.rows.find(r => r.t > today) : null;
                    // Keep the server's copy of this re-plan up to date (for notifications and the bell).
                    const fcRows = re ? re.pick.rows.slice(0, 120).map(r => ({ d: isoDay(r.t), opt: Math.round(r.opt) })) : [];
                    if (!fcRows.length || fcRows[0].d > todayIso) fcRows.unshift({ d: todayIso, opt: 0 }); // nothing to sell today is an answer too
                    const fc = JSON.stringify(fcRows);
                    if (fc !== sentForecast) { sentForecast = fc; fetch('/api/sell-plan/forecast', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"rows":' + fc + '}' }).then(() => { if (window.opticReminders && window.opticReminders.refreshSell) window.opticReminders.refreshSell(); }).catch(() => { }); }
                    const projected = usdTotal + (re ? re.pick.total - (target > 0 ? Math.min(soldToday, target) / target * firstRow.usd : 0) : 0);
                    const done = saved.goalOpt - soldTotal <= Math.max(1, saved.goalOpt * 0.001);

                    // Status: compared with what the plan had sold by the end of yesterday (today is still open).
                    const tol = Math.max(1000, saved.goalOpt * 0.01);
                    const status = done ? ['ok', 'Goal reached'] : soldTotal > plannedThrough + tol ? ['ahead', `Ahead by ${n0(soldTotal - plannedThrough)} OPT`]
                        : soldTotal >= plannedBefore - tol ? ['ok', 'On track'] : ['behind', `Behind by ${n0(plannedBefore - soldTotal)} OPT`];
                    const willLate = re && re.late;

                    // Today.
                    const ratio = model.spot && model.median7 ? model.spot / model.median7 : 1;
                    const quotes = model.quotes.filter(q => q.usd != null);
                    const qt = leftToday > 0 && quotes.length ? quotes.reduce((a, b) => Math.abs(Math.log(b.opt / leftToday)) < Math.abs(Math.log(a.opt / leftToday)) ? b : a) : null;
                    const poolName = (id) => model.pools && model.pools[id] ? `${model.pools[id]} (${id})` : `pool ${id}`;
                    let todayHtml;
                    if (done) todayHtml = `<div class="big">Done: all ${n0(saved.goalOpt)} OPT sold</div>`;
                    else if (!re) todayHtml = `<div class="big">Nothing left to sell</div><div style="font-size: 13px;">None of your OPT is available for the rest of this plan${left.short ? ` (${n0(left.short)} OPT of the plan is no longer in your wallets)` : ''}.</div>`;
                    else if (!target) todayHtml = `<div class="big">Today: nothing to sell</div><div style="font-size: 13px;">${next ? `Next: sell <strong>${n0(next.opt)} OPT on ${fmtDay(next.t)}</strong>${firstRow.t > today ? ' (when your OPT comes free)' : ''}.` : ''}${soldToday > 0 ? ` You sold ${n0(soldToday)} OPT today for ${usd(usdToday)}.` : ''}</div>`;
                    else if (leftToday <= Math.max(500, target * 0.02)) todayHtml = `<div class="big">Done for today ✓</div><div style="font-size: 13px;">You sold ${n0(soldToday)} OPT today${soldToday > target * 1.02 ? `, more than today's ${n0(target)}` : ''}, for ${usd(usdToday)}.${next ? ` Next: <strong>${n0(next.opt)} OPT on ${fmtDay(next.t)}</strong>.` : ''}</div>`;
                    else todayHtml = `<div class="big">Today: sell ${n0(leftToday)} OPT</div>
                        <div style="font-size: 13px;">${soldToday > 0 ? `You've sold ${n0(soldToday)} of today's ${n0(target)} so far. ` : ''}Expect about ${usd(firstRow.usd * leftToday / target)}. On Osmosis swap <strong>OPT → USDC</strong>${leftToday >= 150e3 ? `, as two swaps of about ${n0(leftToday / 2)} a few hours apart` : ''}${qt && (qt.routes || []).length ? '; the router splits it ' + qt.routes.map(r => Math.round(r.share * 100) + '% via ' + r.pools.map(poolName).join(' → ')).join(', ') : ''}.</div>
                        ${ratio < 0.9 ? `<div style="font-size: 13px; margin-top: 4px;"><strong>The price is low today</strong> (${px(model.spot)}, ${pct(1 - ratio)} below its 7-day median). If you can, wait a day; whatever you don't sell is spread over the days left.</div>` : ''}`;

                    // Chart: OPT sold so far against the plan.
                    const t0 = parseDay(saved.start);
                    const t1 = Math.max(finishBy, sched.length ? sched[sched.length - 1].t + dayMs : finishBy, today + dayMs);
                    const yMax = Math.max(saved.goalOpt, soldTotal) * 1.05 || 1;
                    const W = 600, H = 190, L = 60, R = 8, T = 8, B = 22;
                    const X = (t) => L + (W - L - R) * (t - t0) / Math.max(dayMs, t1 - t0);
                    const Y = (v) => T + (H - T - B) * (1 - v / yMax);
                    let cum = 0;
                    const planPts = [[t0, 0]];
                    for (const r of sched) { planPts.push([r.t, cum]); cum += r.opt; planPts.push([r.t + dayMs, cum]); }
                    planPts.push([t1, cum]);
                    let cumA = 0;
                    const actPts = [[t0, 0]];
                    for (let d = t0; d <= today; d += dayMs) { const v = byDay[isoDay(d)]; actPts.push([d, cumA]); cumA += v ? v.opt : 0; actPts.push([d + dayMs, cumA]); }
                    const path = (pts) => pts.map((q, i) => (i ? 'L' : 'M') + X(q[0]).toFixed(1) + ' ' + Y(q[1]).toFixed(1)).join(' ');
                    const tickVals = [0, 0.5, 1].map(f => yMax / 1.05 * f);
                    const ticks = tickVals.map(v => `<line x1="${L}" x2="${W - R}" y1="${Y(v)}" y2="${Y(v)}" stroke="var(--border)" stroke-width="1" vector-effect="non-scaling-stroke"/>`).join('');
                    // Labels are plain text over the chart, so they don't stretch with it on narrow screens.
                    const lab = (x, y, text, align) => `<span style="position: absolute; left: ${x / W * 100}%; top: ${y / H * 100}%; transform: translate(${align === 'end' ? '-100%' : align === 'middle' ? '-50%' : '0'}, -50%); font-size: 11px; color: var(--text-secondary); white-space: nowrap;">${text}</span>`;
                    const labels = tickVals.map(v => lab(L - 4, Y(v), big(v), 'end')).join('') + lab(L, H - 8, fmtDate(t0).replace(/, \d{4}$/, ''), 'start')
                        + lab(Math.min(Math.max(X(today), L + 60), W - R - 60), H - 8, 'today', 'middle') + lab(W - R, H - 8, fmtDate(t1 - dayMs).replace(/, \d{4}$/, ''), 'end');
                    const chart = `<div style="position: relative;"><svg class="sp-chart" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" role="img" aria-label="OPT sold against the plan">${ticks}
                        <line x1="${X(finishBy + dayMs)}" x2="${X(finishBy + dayMs)}" y1="${T}" y2="${H - B}" stroke="var(--danger, #c50f1f)" stroke-dasharray="3 3"/>
                        <line x1="${X(today)}" x2="${X(today)}" y1="${T}" y2="${H - B}" stroke="var(--text-muted)" stroke-dasharray="2 3"/>
                        <path d="${path(planPts)}" fill="none" stroke="var(--text-muted)" stroke-width="2" stroke-dasharray="6 4" vector-effect="non-scaling-stroke"/>
                        <path d="${path(actPts)}" fill="none" stroke="var(--accent-primary, #0078d4)" stroke-width="3" vector-effect="non-scaling-stroke"/>
                        </svg>${labels}</div>
                        <div class="sp-note">Blue: OPT you sold. Dashed grey: the plan. Red line: finish by ${fmtDate(finishBy)}.</div>`;

                    // Day by day, newest first: planned, sold, dollars, and the running difference from the plan.
                    const plannedOn = {};
                    for (const r of sched) plannedOn[isoDay(r.t)] = r;
                    const days = [];
                    let runP = 0, runS = 0;
                    for (let d = t0; d <= today; d += dayMs) {
                        const k = isoDay(d), pl = plannedOn[k], so = byDay[k];
                        runP += pl ? pl.opt : 0; runS += so ? so.opt : 0;
                        if (pl || so) days.push({ d, planned: pl ? pl.opt : 0, sold: so ? so.opt : 0, usd: so ? so.usd : 0, diff: runS - runP });
                    }
                    const dayRows = days.reverse().map(r => `<tr><td>${fmtDay(r.d)}${r.d === today ? ' (today)' : ''}</td><td class="num">${r.planned ? n0(r.planned) : '–'}</td><td class="num">${r.sold ? n0(r.sold) : '–'}</td><td class="num">${r.sold ? usd(r.usd) : '–'}</td><td class="num">${r.sold ? px(r.usd / r.sold) : '–'}</td><td class="num"${r.diff < -tol && r.d < today ? ' style="color: var(--danger, #c50f1f);"' : ''}>${r.diff >= 0 ? '+' : '−'}${n0(Math.abs(r.diff))}</td></tr>`).join('');
                    const saleRows = sales.slice().reverse().map(s => `<tr><td>${new Date(s.time).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })}</td><td class="num">${n0(s.opt)}</td><td>${esc(s.received)}${s.estimated ? ' <span class="sp-note">(valued at the OPT price then)</span>' : ''}</td><td class="num">${usd(s.usd)}</td><td>${esc(s.wallet)}</td><td><a href="https://www.mintscan.io/osmosis/tx/${encodeURIComponent(s.hash)}" target="_blank" rel="noopener">${esc(s.hash.slice(0, 8))}…</a></td></tr>`).join('');
                    const pctDone = Math.min(1, soldTotal / saved.goalOpt), pctPlan = Math.min(1, plannedBefore / saved.goalOpt);
                    const asOf = salesInfo && salesInfo.asOf ? new Date(salesInfo.asOf).toLocaleString('en-US', { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }) : null;
                    const daysLeft = Math.max(0, Math.round((finishBy - today) / dayMs) + 1);

                    box.innerHTML = `<div class="sp-track">${head}
                        <div style="margin: 8px 0 4px;">${todayHtml}</div>
                        <div style="margin-top: 10px;"><span class="sp-badge ${status[0]}">${status[1]}</span>${willLate && !done ? ` <span class="sp-badge behind">Can’t finish by ${fmtDate(finishBy)} with the OPT available</span>` : ''}
                            <span style="font-size: 13px; margin-left: 6px;">${n0(soldTotal)} of ${n0(saved.goalOpt)} OPT sold (${pct(pctDone)}); the plan had ${n0(plannedBefore)} sold by yesterday.</span></div>
                        <div class="sp-bar" title="Sold ${pct(pctDone)}; plan by yesterday ${pct(pctPlan)}"><div style="width: ${pctPlan * 100}%; background: var(--text-muted); opacity: 0.35;"></div><div style="width: ${pctDone * 100}%; background: var(--accent-primary, #0078d4);"></div></div>
                        <div class="sp-grid" style="margin-top: 10px;">
                            ${tile('Received so far', usd(usdTotal), soldTotal ? `avg ${px(usdTotal / soldTotal)} per OPT · the plan expected ${usd(expUsd)} for this much (${usdTotal >= expUsd ? '+' : '−'}${usd(Math.abs(usdTotal - expUsd))})` : 'no sales yet')}
                            ${tile('Left to sell', n0(Math.max(0, saved.goalOpt - soldTotal)) + ' OPT', re && !done ? `about ${n0(re.pick.size)} a day, finishing ${fmtDate(re.pick.end)}` : '')}
                            ${tile('Expected in total', usd(projected), `sold so far + the rest at today's pools · the plan said ${usd(saved.goalUsd)}`)}
                            ${tile('Days left', daysLeft, `to ${fmtDate(finishBy)}, counting today`)}
                        </div>
                        ${chart}
                        <details${days.length ? ' open' : ''}><summary>Day by day (${days.length} day${days.length === 1 ? '' : 's'})</summary><div class="data-table-wrap"><table class="data-table"><thead><tr><th>Date</th><th class="num">Planned</th><th class="num">Sold</th><th class="num">Received</th><th class="num">Per OPT</th><th class="num">Ahead/behind (total)</th></tr></thead><tbody>${dayRows || '<tr><td colspan="6" class="sp-note">Nothing was planned or sold yet.</td></tr>'}</tbody></table></div></details>
                        <details><summary>Each sale (${sales.length})</summary><div class="data-table-wrap"><table class="data-table"><thead><tr><th>When</th><th class="num">OPT</th><th>Received</th><th class="num">USD</th><th>Account</th><th>Tx</th></tr></thead><tbody>${saleRows || '<tr><td colspan="6" class="sp-note">No OPT sold on Osmosis since ' + fmtDate(t0) + '.</td></tr>'}</tbody></table></div></details>
                        <p class="sp-note" style="margin-top: 6px;">Sales are swaps out of OPT in the Osmosis accounts in My Wallets${asOf ? `, checked ${asOf}` : ''}${checking ? ' (checking for newer ones now)' : ''}. Today's amount is re-planned every day from what is left, the OPT you hold and today's pools, so selling less one day spreads the rest over the days left.${((salesInfo && salesInfo.notes) || []).length ? ' ' + salesInfo.notes.map(esc).join(' ') : ''}${salesInfo && salesInfo.error ? ' The last check failed: ' + esc(salesInfo.error) : ''}</p>
                        <div class="sp-btns"><button type="button" class="btn btn-secondary" id="sp-refresh"${checking ? ' disabled' : ''}>${checking ? 'Checking Osmosis for new sales…' : 'Check for new sales'}</button><button type="button" class="btn btn-secondary" id="sp-stop">Stop following this plan</button></div>
                    </div>`;
                    $('sp-refresh').addEventListener('click', loadSales);
                    $('sp-stop').addEventListener('click', stopPlan);
                };

                const tile = (k, v, s) => `<div class="sp-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s || ''}</div></div>`;

                // Your OPT: free in Optio wallets and on Osmosis now; unbonding on its date; locked OPT after it unlocks and unbonds.
                // Last visit's answers (saved in this browser) come back at once and are replaced when the fresh ones arrive.
                const cached = (u) => window.opticGet ? window.opticGet(u).then(j => { if (!j) throw new Error('no answer'); return j; }) : fetch(u).then(r => r.ok ? r.json() : Promise.reject(new Error('HTTP ' + r.status)));
                const loadMine = async () => {
                    const W = window.opticWallets;
                    if (!W) { setTimeout(loadMine, 200); return; }
                    const list = [], incomingNew = [];
                    const name = (a) => W.label(a) || (a.slice(0, 10) + '…');
                    await Promise.all(W.listSaved().filter(w => W.valid(w.a)).map(w => cached('/api/staking/positions?address=' + encodeURIComponent(w.a)).then(j => {
                        if (!j || !j.totals) return;
                        const t = j.totals, n = name(w.a);
                        if (t.balanceOpt > 1) list.push({ opt: t.balanceOpt, t: today, from: n });
                        if (t.rewardsOpt > 1) list.push({ opt: t.rewardsOpt, t: today, from: n + ' (unclaimed staking rewards, claim first)', incoming: true, rewards: true });
                        for (const u of t.unbondingEntries || []) if (u.opt > 1) list.push({ opt: u.opt, t: Math.max(today, Date.parse(u.completes.slice(0, 10) + 'T00:00:00Z') + dayMs), from: n + ' (unbonding)' });
                        for (const l of j.locks || []) if (l.opt > 1) list.push({ opt: l.opt, t: Math.max(today, Date.parse(l.unlockDate + 'T00:00:00Z') + Math.round(t.unbondingDays || 21) * dayMs + dayMs), from: n + ' (locked)', locked: true });
                    }).catch(() => { })).concat(W.listSaved().filter(w => W.valid(w.a)).map(w => cached('/api/distributions/address?address=' + encodeURIComponent(w.a)).then(j => {
                        // Payouts from the Distro Wallet: each kind's daily pace over the last 30 full days, expected from tomorrow on.
                        if (!j || !j.rows) return;
                        const from = today - 30 * dayMs, sum = { node: 0, staking: 0 };
                        for (const r of j.rows) { const t = Date.parse(r.timeUtc); if (t >= from && t < today && sum[r.kind] != null) sum[r.kind] += r.amountOpt; }
                        const node = sum.node / 30, staking = sum.staking / 30;
                        if (node + staking < 1) return;
                        const n = name(w.a);
                        incomingNew.push({ wallet: n, node, staking });
                        for (let d = today + dayMs; d <= today + 400 * dayMs; d += dayMs) list.push({ opt: node + staking, t: d, from: n + ' (node + staking payouts, expected)', incoming: true });
                    }).catch(() => { }))));
                    await cached('/api/evm/balances').then(j => {
                        for (const w of (j && j.wallets) || []) for (const c of w.chains) if (c.chain === 'osmosis') for (const b of c.balances) if (b.symbol === 'OPT' && b.amount > 1) list.push({ opt: b.amount, t: today, from: name(w.address) });
                    }).catch(() => { });
                    allTranches = list; incoming = incomingNew;
                    applyLocked();
                };
                let allTranches = null, incoming = [];
                const applyLocked = () => render();
                // Expected payouts count only when ticked, and only up to the finish date (unclaimed rewards are already there).
                const incomingOk = (t, on, deadline) => !t.incoming || (on && (t.rewards || !deadline || t.t <= deadline));
                // Finish by the end of this year; start a simulation today.
                $('sp-by').value = new Date().getFullYear() + '-12-31';
                $('sp-start').value = isoDay(today);
                for (const id of ['sp-what', 'sp-by', 'sp-start', 'sp-amount']) $(id).addEventListener('change', render);
                $('sp-amount').addEventListener('keydown', (e) => { if (e.key === 'Enter') render(); });

                fetch('/api/sell-plan/saved').then(r => r.ok ? r.json() : null).then(j => {
                    if (!j || !j.plan) return;
                    saved = j.plan;
                    $('sp-what').value = saved.mode; $('sp-by').value = saved.finishBy; $('sp-scenario').value = saved.scenario; $('sp-locked').checked = !!saved.includeLocked; $('sp-incoming').checked = saved.includeIncoming !== false;
                    if (saved.mode === 'custom') { $('sp-amount').value = n0(saved.goalOpt); if (saved.schedule.length) $('sp-start').value = saved.schedule[0].d; }
                    render(); loadSales(); renderPush();
                }).catch(() => { });
                const loadModel = () => cached('/api/osmosis/sell-model').then(j => { if (j.error) throw new Error(j.error); model = j; render(); })
                    .catch(e => { if (!model) $('sp-status').textContent = 'Could not read the Osmosis pools: ' + e.message; });
                loadModel();
                // Fresh answers replaced last visit's: work it all out again.
                window.addEventListener('optic-fresh', () => { loadModel(); loadMine(); });
                $('sp-scenario').addEventListener('change', render);
                $('sp-locked').addEventListener('change', applyLocked);
                $('sp-incoming').addEventListener('change', render);
                loadMine();
            })();
        </script>
        """;
}
