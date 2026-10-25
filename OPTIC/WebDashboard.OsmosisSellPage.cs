// Market > Sell OPT on Osmosis: pick an amount, see every OPT pool's live quote and the best way to sell. Data from
// /api/osmosis/opt-sell (Osmosis's routing service); your OPT available to sell from your saved wallets.
public static partial class WebDashboard
{
    static void AppendOsmosisSellPage(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(OsmosisSellHtml);
    }

    const string OsmosisSellHtml = """
        <style>
            .os { grid-column: 1 / -1; }
            .os .os-note { font-size: 13px; color: var(--text-secondary); margin: 0 0 8px; max-width: 1040px; }
            .os h3 { margin: 18px 0 6px; font-size: 16px; }
            .os .num { text-align: right; white-space: nowrap; }
            .os .os-best { border: 2px solid var(--success, #107c10); border-radius: 10px; padding: 10px 14px; margin: 10px 0 6px; }
            .os .os-best .big { font-size: 22px; font-weight: 700; }
            .os tr.is-best td { background: rgba(16, 124, 16, 0.10); }
            .os .os-tag { display: inline-block; font-size: 11px; font-weight: 700; border-radius: 9px; padding: 1px 8px; background: var(--success, #107c10); color: #fff; margin-left: 6px; }
            .os .os-muted { color: var(--text-muted); }
            .os .os-bar { display: flex; gap: 8px; flex-wrap: wrap; align-items: flex-end; }
            .os .os-bar input { width: 170px; text-align: right; }
            .os .os-bar button.os-preset { font-size: 12px; padding: 4px 10px; }
        </style>
        <div class="card os">
            <h2>Sell OPT on Osmosis</h2>
            <div class="card-content">
                <p class="os-note">Every Osmosis pool that trades OPT, with a live quote for the amount you want to sell, and the way that puts the most dollars in your wallet. Quotes come from Osmosis's own routing service (the same as the Osmosis app) and change with every trade; swap fees are already taken out.</p>
                <div class="os-bar">
                    <div class="form-group" style="margin-bottom: 0;"><label for="os-amt">OPT to sell</label><input type="text" id="os-amt" inputmode="decimal" value="100,000"></div>
                    <button type="button" id="os-go">Get quotes</button>
                    <button type="button" class="os-preset" data-amt="10000">10K</button>
                    <button type="button" class="os-preset" data-amt="100000">100K</button>
                    <button type="button" class="os-preset" data-amt="1000000">1M</button>
                    <button type="button" class="os-preset" id="os-mine" hidden>All I can sell now</button>
                </div>
                <p class="os-note" id="os-have" style="margin-top: 6px;"></p>
                <p class="os-note" id="os-status"></p>
                <div id="os-best"></div>

                <h3>All OPT pools</h3>
                <p class="os-note">Each pool on its own for your amount. "In dollars" is what you would hold in USDC/USDT afterwards: for a pool that pays another token (like SOVR) it includes swapping that token on to USDC.</p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Pool</th><th>Pair</th><th class="num">Liquidity</th><th class="num">Fee</th><th class="num">You receive</th><th class="num">In dollars</th><th class="num">Per OPT</th><th class="num">Below market</th><th>Notes</th></tr></thead><tbody id="os-pools"></tbody></table></div>

                <h3>How much you can sell</h3>
                <p class="os-note">The best route into USDC at other sizes: the more you sell at once, the lower the average price. Selling in smaller pieces over time (as arbitrage refills the pools) usually gets more than one large sale.</p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th class="num">Sell</th><th class="num">You get (USDC)</th><th class="num">Per OPT</th><th class="num">Below market</th></tr></thead><tbody id="os-ladder"></tbody></table></div>

                <p class="os-note" style="margin-top: 12px;">To sell OPT held on Optio, first send it to your Osmosis account over IBC (in Keplr: Send → Osmosis). OPT that is staked or locked can't be sold until it's unstaked or unlocked. Before a sale, the <a href="/page/sell-planner">Sell Planner</a> and <a href="/page/pnl">Profit &amp; Loss</a> show the gain or loss it would make. <span id="os-source"></span></p>
            </div>
        </div>
        <script>
            (function () {
                const $ = (id) => document.getElementById(id);
                const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
                const usd = (v) => v == null ? '–' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                const n0 = (v) => Number(v || 0).toLocaleString('en-US', { maximumFractionDigits: 0 });
                const nq = (v) => Number(v || 0).toLocaleString('en-US', { maximumFractionDigits: v < 1 ? 6 : v < 100 ? 4 : 2 });
                const px = (v) => v == null ? '–' : '$' + Number(v).toLocaleString('en-US', { maximumSignificantDigits: 4 });
                const below = (avg, spot) => avg == null || !spot ? '–' : ((1 - avg / spot) * 100).toFixed(1) + '%';
                const parse = (s) => Number(String(s).replace(/[^0-9.]/g, ''));

                let seq = 0;
                const run = async () => {
                    const opt = parse($('os-amt').value);
                    if (!(opt > 0)) { $('os-status').textContent = 'Enter how much OPT you want to sell.'; return; }
                    $('os-amt').value = n0(opt);
                    const my = ++seq;
                    $('os-status').textContent = 'Asking Osmosis for quotes…';
                    $('os-go').disabled = true;
                    try {
                        const r = await fetch('/api/osmosis/opt-sell?opt=' + opt);
                        const j = await r.json();
                        if (my !== seq) return;
                        if (!r.ok) throw new Error(j.error || 'HTTP ' + r.status);
                        render(j);
                    } catch (e) { if (my === seq) { $('os-status').textContent = 'Could not get quotes: ' + e.message; $('os-best').innerHTML = ''; } }
                    finally { if (my === seq) $('os-go').disabled = false; }
                };

                const render = (j) => {
                    const spot = j.spot;
                    $('os-status').textContent = `Market price on Osmosis now: ${px(spot)} per OPT, so ${n0(j.sellOpt)} OPT is worth ${usd(j.spotValue)} before fees and price impact. Quotes read ${new Date(j.readUtc).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}.`;
                    // Options: the router, and each pool alone (in dollars after any follow-on swap).
                    const opts = [];
                    if (j.router && j.router.usd != null) opts.push({ key: 'router', name: 'Osmosis best route into USDC', usd: j.router.usd, detail: j.router.route, avg: j.router.avgPrice });
                    for (const p of j.pools) if (p.cashUsd != null) opts.push({ key: 'pool' + p.id, name: `Pool ${p.id} (${p.pair})`, usd: p.cashUsd, detail: p.stable ? `sell straight into ${p.token}` : `sell for ${p.token}, ${p.cashNote}`, avg: p.avgPrice });
                    opts.sort((a, b) => b.usd - a.usd);
                    const best = opts[0], next = opts[1];
                    $('os-best').innerHTML = best ? `<div class="os-best">
                        <div style="font-size: 13px; color: var(--text-secondary);">Best way to sell ${n0(j.sellOpt)} OPT right now</div>
                        <div class="big">${esc(best.name)}: ${usd(best.usd)}</div>
                        <div style="font-size: 13px;">${px(best.avg)} per OPT, ${below(best.avg, spot)} below the market price · ${esc(best.detail)}</div>
                        ${next ? `<div style="font-size: 12px; color: var(--text-secondary); margin-top: 4px;">Next best: ${esc(next.name)} at ${usd(next.usd)} (${usd(best.usd - next.usd)} less).</div>` : ''}
                        ${best.key === 'router' ? '<div style="font-size: 12px; color: var(--text-secondary);">In the Osmosis app, swap OPT → USDC and it uses this route automatically.</div>' : `<div style="font-size: 12px; color: var(--text-secondary);">Pick this pool in the Osmosis app's swap route, or swap OPT → USDC and compare the quote.</div>`}
                    </div>` : '<p class="os-note">No pool could quote this amount.</p>';
                    $('os-pools').innerHTML = j.pools.slice().sort((a, b) => (b.cashUsd ?? -1) - (a.cashUsd ?? -1) || b.liquidityUsd - a.liquidityUsd).map(p => {
                        const isBest = best && best.key === 'pool' + p.id;
                        return `<tr class="${isBest ? 'is-best' : ''}${p.error ? ' os-muted' : ''}"><td><a href="https://app.osmosis.zone/pool/${encodeURIComponent(p.id)}" target="_blank" rel="noopener">${esc(p.id)}</a>${isBest ? '<span class="os-tag">Best</span>' : ''}</td>
                            <td><strong>${esc(p.pair)}</strong><div style="font-size: 11px;" class="os-muted">${esc(p.type)} · ${n0(p.optInPool)} OPT + ${nq(p.otherInPool)} ${esc(p.token)}</div></td>
                            <td class="num">${usd(p.liquidityUsd)}</td><td class="num">${(p.fee * 100).toFixed(2)}%</td>
                            <td class="num">${p.receive != null ? nq(p.receive) + ' ' + esc(p.token) : '–'}</td>
                            <td class="num"><strong>${usd(p.cashUsd)}</strong></td><td class="num">${px(p.avgPrice)}</td><td class="num">${below(p.avgPrice, spot)}</td>
                            <td style="font-size: 12px;">${esc(p.error || (p.stable ? '' : p.cashNote || ''))}</td></tr>`;
                    }).join('') + (j.router && j.router.usd != null ? `<tr class="${best && best.key === 'router' ? 'is-best' : ''}"><td>Router${best && best.key === 'router' ? '<span class="os-tag">Best</span>' : ''}</td><td><strong>OPT → USDC, best route</strong><div style="font-size: 11px;" class="os-muted">${esc(j.router.route)}</div></td><td class="num">–</td><td class="num">–</td><td class="num">${nq(j.router.usd)} USDC</td><td class="num"><strong>${usd(j.router.usd)}</strong></td><td class="num">${px(j.router.avgPrice)}</td><td class="num">${below(j.router.avgPrice, spot)}</td><td style="font-size: 12px;">May split across pools</td></tr>` : '');
                    $('os-ladder').innerHTML = (j.ladder || []).map(l => `<tr><td class="num">${n0(l.opt)} OPT</td><td class="num">${l.error ? '<span class="os-muted">' + esc(l.error.slice(0, 80)) + '</span>' : usd(l.usd)}</td><td class="num">${px(l.avgPrice)}</td><td class="num">${below(l.avgPrice, spot)}</td></tr>`).join('');
                    $('os-source').textContent = `Source: ${j.source}.`;
                };

                // OPT you could sell now: in your Optio wallets (not staked or locked) and in your Osmosis accounts.
                const loadMine = async () => {
                    const W = window.opticWallets;
                    if (!W) { setTimeout(loadMine, 200); return; }
                    let optio = 0, osmo = 0;
                    const jobs = W.listSaved().filter(w => W.valid(w.a)).map(w => fetch('/api/staking/positions?address=' + encodeURIComponent(w.a)).then(r => r.ok ? r.json() : null).then(j => { if (j && j.totals) optio += j.totals.balanceOpt || 0; }).catch(() => { }));
                    jobs.push(fetch('/api/evm/balances').then(r => r.ok ? r.json() : null).then(j => { for (const w of (j && j.wallets) || []) for (const c of w.chains) if (c.chain === 'osmosis') for (const b of c.balances) if (b.symbol === 'OPT') osmo += b.amount; }).catch(() => { }));
                    await Promise.all(jobs);
                    const total = optio + osmo;
                    if (total < 1) { $('os-have').textContent = 'None of your OPT is free to sell right now (it is staked, locked or unbonding).'; return; }
                    $('os-have').textContent = `You can sell ${n0(total)} OPT now: ${n0(osmo)} already on Osmosis and ${n0(optio)} in your Optio wallets (send it to Osmosis first). Staked and locked OPT isn't included.`;
                    $('os-mine').hidden = false;
                    $('os-mine').dataset.amt = Math.floor(total);
                };

                $('os-go').addEventListener('click', run);
                $('os-amt').addEventListener('keydown', (e) => { if (e.key === 'Enter') run(); });
                document.querySelectorAll('.os-preset').forEach(b => b.addEventListener('click', () => { if (!b.dataset.amt) return; $('os-amt').value = n0(b.dataset.amt); run(); }));
                const q = new URLSearchParams(location.search).get('opt');
                if (q) $('os-amt').value = q;
                loadMine();
                run();
            })();
        </script>
        """;
}
