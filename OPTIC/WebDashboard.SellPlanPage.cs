// Market > OPT Sell Plan: how much OPT to sell each day, through which Osmosis pools, to get the most dollars for the OPT you
// have (in your wallets now, unbonding, and optionally locked OPT once it unlocks). Built from the live pools:
//   - What a sale returns comes from Osmosis's router quotes into USDC at many sizes (/api/osmosis/sell-model).
//   - After a sale the pools hold extra OPT until other traders buy it back. The plan assumes they keep buying at their pace of
//     the last 30 days; OPT you sell faster than that stays in the pools and lowers the price of your next sale.
//   - Each daily size is simulated to the last OPT; the plan is the one that returns the most while finishing by your date.
//   - The amount can be all your OPT, just what is unbonding, or any amount from a start date (a "what if").
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
        </style>
        <div class="card sp">
            <h2>OPT Sell Plan</h2>
            <div class="card-content">
                <p class="sp-note">How much of your OPT to sell each day, and through which Osmosis pools, to get the most dollars. The OPT pools are small, so selling a lot at once pushes the price down; the plan sells at about the pace other traders buy OPT back, so the pools refill between your sales.</p>
                <div class="sp-controls">
                    <div class="form-group" style="margin-bottom: 0;"><label for="sp-what">Plan for</label>
                        <select id="sp-what"><option value="mine">All my OPT</option><option value="unbonding">My unbonding OPT</option><option value="custom">Any amount (simulate)</option></select></div>
                    <div class="form-group" id="sp-custom-box" style="margin-bottom: 0;" hidden><label for="sp-amount">OPT to sell</label><input type="text" id="sp-amount" inputmode="decimal" value="8,000,000" style="width: 140px; text-align: right;"></div>
                    <div class="form-group" id="sp-start-box" style="margin-bottom: 0;" hidden><label for="sp-start">Start selling</label><input type="date" id="sp-start"></div>
                    <div class="form-group" style="margin-bottom: 0;"><label for="sp-by">Finish by</label><input type="date" id="sp-by"></div>
                    <div class="form-group" style="margin-bottom: 0;"><label for="sp-scenario">If the price…</label>
                        <select id="sp-scenario"><option value="0">stays where it is</option><option value="30">keeps its last-30-days trend</option><option value="90">keeps its last-90-days trend</option></select></div>
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
            (function () {
                const $ = (id) => document.getElementById(id);
                const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
                const usd = (v) => v == null ? '–' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                const n0 = (v) => Number(v || 0).toLocaleString('en-US', { maximumFractionDigits: 0 });
                const big = (v) => v >= 1e6 ? (v / 1e6).toFixed(v >= 1e7 ? 1 : 2).replace(/\.?0+$/, '') + 'M' : v >= 1e3 ? Math.round(v / 1e3) + 'K' : n0(v);
                const px = (v) => v == null ? '–' : '$' + Number(v).toLocaleString('en-US', { maximumSignificantDigits: 4 });
                const pct = (v) => (v * 100).toFixed(1) + '%';
                const dayMs = 86400000;
                const today = new Date(new Date().toISOString().slice(0, 10) + 'T00:00:00Z').getTime();
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
                const simulate = (size, U, drift) => {
                    const queue = tranches.map(t => ({ ...t })).sort((a, b) => a.t - b.t);
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

                const dateVal = (id) => { const v = $(id).value; return v ? Date.parse(v + 'T00:00:00Z') : null; };
                const isoDay = (t) => new Date(t).toISOString().slice(0, 10);
                const render = () => {
                    if (!model || !allTranches) return;
                    const what = $('sp-what').value;
                    $('sp-custom-box').hidden = $('sp-start-box').hidden = what !== 'custom';
                    tranches = what === 'custom'
                        ? [{ opt: Number(String($('sp-amount').value).replace(/[^0-9.]/g, '')) || 0, t: Math.max(today, dateVal('sp-start') || today), from: 'Simulated amount', left: 0 }]
                        : allTranches.filter(t => what === 'unbonding' ? /unbonding/.test(t.from) : !t.locked || $('sp-locked').checked);
                    tranches = tranches.map(t => ({ ...t, left: t.opt }));
                    const deadline = dateVal('sp-by');
                    const total = tranches.reduce((n, t) => n + t.opt, 0);
                    const nowOpt = tranches.filter(t => t.t <= today).reduce((n, t) => n + t.opt, 0);
                    const later = tranches.filter(t => t.t > today);
                    const lockedLater = tranches.filter(t => t.locked).reduce((n, t) => n + t.opt, 0);
                    $('sp-have').innerHTML =
                        tile('Sellable now', n0(nowOpt) + ' OPT', model.spot ? `≈ ${usd(nowOpt * model.spot)} at the market price` : '') +
                        tile('Coming free later', n0(later.reduce((n, t) => n + t.opt, 0)) + ' OPT', later.length ? later.slice(0, 3).map(t => `${big(t.opt)} on ${fmtDate(t.t)}`).join(', ') + (later.length > 3 ? '…' : '') : 'nothing unbonding') +
                        tile('Market price now', px(model.spot), model.median7 ? `7-day median ${px(model.median7)}` : '') +
                        tile('Bought back per day', n0(model.buyFlowPerDay) + ' OPT', `by other traders, last 30 days (they sold ${n0(model.sellFlowPerDay)}/day)`);
                    if (total < 1) { $('sp-status').textContent = 'You have no OPT to sell: none is in your wallets, unbonding' + ($('sp-locked').checked ? ' or locked.' : '. Tick "Include locked OPT" to plan for locked OPT.'); $('sp-plan').innerHTML = $('sp-today').innerHTML = ''; $('sp-sched').innerHTML = $('sp-days').innerHTML = $('sp-alts').innerHTML = ''; return; }

                    const scen = $('sp-scenario').value;
                    const drift = scen === '30' ? (model.change30 || 0) : scen === '90' ? Math.pow(1 + (model.change90 || 0), 1 / 3) - 1 : 0;
                    const U = curve();
                    const sizes = [10e3, 25e3, 50e3, 75e3, 100e3, 150e3, 200e3, 250e3, 300e3, 400e3, 500e3, 750e3, 1e6, 1.5e6, 2e6, 3e6, 5e6, 1e7, 2e7, 5e7].filter(s => s <= Math.max(total, 10e3) * 1.0001 || s === 10e3);
                    if (!sizes.includes(total) && total <= 5e7) sizes.push(total); // all at once
                    // The smallest daily amount that still finishes by the date (selling every day from when the OPT comes free).
                    const first = Math.min(...tranches.map(t => t.t));
                    if (deadline && deadline >= first) {
                        const need = Math.ceil(total / Math.max(1, Math.round((deadline - first) / dayMs) + 1) / 1000) * 1000;
                        for (const f of [1, 1.1, 1.25, 1.5]) { const v = Math.ceil(need * f / 1000) * 1000; if (v < total && !sizes.includes(v)) sizes.push(v); }
                    }
                    const runs = sizes.sort((a, b) => a - b).map(s => simulate(s, U, drift));
                    const best = runs.reduce((a, b) => b.total > a.total ? b : a);
                    const onTime = (r) => !deadline || r.end <= deadline;
                    // The plan: the most dollars among the plans that finish by the date (the fastest one if none can).
                    const feasible = runs.filter(onTime);
                    const pick = feasible.length ? feasible.reduce((a, b) => b.total > a.total ? b : a) : runs.reduce((a, b) => b.end < a.end ? b : a);
                    const late = !feasible.length;
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
                    </div>`;
                    // Today: sell, or wait if the price is well below its recent level (usually right after someone else's big sale).
                    const ratio = model.spot && model.median7 ? model.spot / model.median7 : 1;
                    const firstDay = pick.rows[0];
                    const wd = (model.weekday || []).filter(w => w.vsWeek != null);
                    const todayName = new Date().toLocaleDateString('en-US', { weekday: 'long', timeZone: 'UTC' });
                    const todayW = wd.find(w => w.day === todayName);
                    $('sp-today').innerHTML = firstDay && firstDay.t > today
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
                    $('sp-weekday').innerHTML = wd.length ? 'By weekday, the Osmosis price has been: ' + wd.map(w => `${w.day.slice(0, 3)} ${w.vsWeek >= 0 ? '+' : ''}${pct(w.vsWeek)}`).join(' · ') + ' against its weekly median. Small and not reliable on its own; use it to choose between two days, not to wait a week.' : '';
                };
                const tile = (k, v, s) => `<div class="sp-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s || ''}</div></div>`;

                // Your OPT: free in Optio wallets and on Osmosis now; unbonding on its date; locked OPT after it unlocks and unbonds.
                const loadMine = async () => {
                    const W = window.opticWallets;
                    if (!W) { setTimeout(loadMine, 200); return; }
                    const list = [];
                    const name = (a) => W.label(a) || (a.slice(0, 10) + '…');
                    await Promise.all(W.listSaved().filter(w => W.valid(w.a)).map(w => fetch('/api/staking/positions?address=' + encodeURIComponent(w.a)).then(r => r.ok ? r.json() : null).then(j => {
                        if (!j || !j.totals) return;
                        const t = j.totals, n = name(w.a);
                        if (t.balanceOpt > 1) list.push({ opt: t.balanceOpt, t: today, from: n });
                        for (const u of t.unbondingEntries || []) if (u.opt > 1) list.push({ opt: u.opt, t: Math.max(today, Date.parse(u.completes.slice(0, 10) + 'T00:00:00Z') + dayMs), from: n + ' (unbonding)' });
                        for (const l of j.locks || []) if (l.opt > 1) list.push({ opt: l.opt, t: Math.max(today, Date.parse(l.unlockDate + 'T00:00:00Z') + Math.round(t.unbondingDays || 21) * dayMs + dayMs), from: n + ' (locked)', locked: true });
                    }).catch(() => { })));
                    await fetch('/api/evm/balances').then(r => r.ok ? r.json() : null).then(j => {
                        for (const w of (j && j.wallets) || []) for (const c of w.chains) if (c.chain === 'osmosis') for (const b of c.balances) if (b.symbol === 'OPT' && b.amount > 1) list.push({ opt: b.amount, t: today, from: name(w.address) });
                    }).catch(() => { });
                    allTranches = list;
                    applyLocked();
                };
                let allTranches = null;
                const applyLocked = () => render();
                // Finish by the end of this year; start a simulation today.
                $('sp-by').value = new Date().getUTCFullYear() + '-12-31';
                $('sp-start').value = isoDay(today);
                for (const id of ['sp-what', 'sp-by', 'sp-start', 'sp-amount']) $(id).addEventListener('change', render);
                $('sp-amount').addEventListener('keydown', (e) => { if (e.key === 'Enter') render(); });

                fetch('/api/osmosis/sell-model').then(r => r.json().then(j => { if (!r.ok) throw new Error(j.error || 'HTTP ' + r.status); return j; }))
                    .then(j => { model = j; render(); })
                    .catch(e => { $('sp-status').textContent = 'Could not read the Osmosis pools: ' + e.message; });
                $('sp-scenario').addEventListener('change', render);
                $('sp-locked').addEventListener('change', applyLocked);
                loadMine();
            })();
        </script>
        """;
}
