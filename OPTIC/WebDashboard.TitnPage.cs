// My TITN page: THORWallet holdings, stake, pending and received rewards across the user's saved 0x... wallets.
public static partial class WebDashboard
{
    static void AppendTitnCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(TitnPageHtml);
    }

    const string TitnPageHtml = """
        <style>
            .ti { grid-column: 1 / -1; }
            .ti .card-content { display: flex; flex-direction: column; gap: 14px; }
            .ti-note { font-size: 13px; color: var(--text-secondary); margin: 0; max-width: 1100px; line-height: 1.5; }
            .ti-tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 10px; }
            .ti-tile { border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px; background: var(--surface); }
            .ti-tile .k { font-size: 12px; color: var(--text-secondary); }
            .ti-tile .v { font-size: 20px; font-weight: 700; margin-top: 2px; font-variant-numeric: tabular-nums; }
            .ti-tile .s { font-size: 12px; color: var(--text-muted); margin-top: 2px; }
            .ti td.num, .ti th.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
            .ti h3 { margin: 6px 0 0; font-size: 15px; }
            .ti-row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
            .ti select, .ti button { font-size: 13px; }
            .ti-mono { font-family: var(--font-mono, monospace); font-size: 12px; }
            @media (max-width: 640px) { .ti .ti-hide-sm { display: none; } }
        </style>
        <div class="card ti">
            <h2>My TITN</h2>
            <div class="card-content">
                <p class="ti-note">THORWallet (TITN) in your saved Ethereum-style wallets on Base and Arbitrum. Rewards are the payments from THORWallet's reward contracts: USDC from TITN staking (and TGT staking before the merge), TITN airdrop awards (paid through the LiquidRewards contract), and raffle prizes. Each is income on the day received, including airdrops and prizes: USDC at $1.00, TITN at that day's price (DefiLlama). TITN coming back from staking is your own stake, not income. Add or label wallets on <a href="/page/my-wallets">My Wallets</a>.</p>
                <div class="ti-row">
                    <label for="ti-year" style="font-size: 13px;">Year</label>
                    <select id="ti-year"></select>
                    <button type="button" id="ti-refresh">Refresh from chain</button>
                    <span id="ti-status" style="font-size: 13px; color: var(--text-secondary);"></span>
                </div>
                <div class="ti-tiles" id="ti-tiles"></div>
                <h3>Wallets</h3>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Wallet</th><th class="num">TITN held</th><th class="num">TITN staked</th><th class="num">Value now</th><th class="num">Pending USDC</th></tr></thead><tbody id="ti-wallets"></tbody></table></div>
                <h3>Rewards by year</h3>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Year</th><th>Source</th><th class="num">Payments</th><th class="num">Amount</th><th class="num">Income (USD)</th></tr></thead><tbody id="ti-years"></tbody></table></div>
                <h3 id="ti-list-title">Reward payments</h3>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Date (UTC)</th><th class="ti-hide-sm">Wallet</th><th>Source</th><th class="num">Amount</th><th class="num ti-hide-sm">Price</th><th class="num">USD</th><th class="ti-hide-sm">Tx</th></tr></thead><tbody id="ti-list"></tbody></table></div>
                <h3>Received from other addresses</h3>
                <p class="ti-note">Plain USDC or TITN transfers into your wallets from addresses that are not THORWallet reward contracts or your own wallets. None of them is income: USDC from anyone but your own wallets is USDC you bought (MoonPay, Coinbase or a swap), and TITN delivered by a cross-chain solver is TITN you bought, costed from what you paid (Tax Report section 4a).</p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>From</th><th>Network</th><th class="num">Transfers</th><th class="num">Amount</th><th>Treated as</th><th class="ti-hide-sm">Dates</th></tr></thead><tbody id="ti-other"></tbody></table></div>
            </div>
        </div>
        <script>
            (function () {
                const $ = (id) => document.getElementById(id);
                const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
                const usd = (v) => v == null ? '–' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
                const num = (v, d = 2) => Number(v || 0).toLocaleString('en-US', { maximumFractionDigits: d });
                const short = (a) => a.slice(0, 6) + '…' + a.slice(-4);
                const scan = (chain, kind, v) => (chain === 'arbitrum' ? 'https://arbiscan.io/' : chain === 'bsc' ? 'https://bscscan.com/' : chain === 'ethereum' ? 'https://etherscan.io/' : 'https://basescan.org/') + kind + '/' + v;
                const W = window.opticWallets;
                const name = (a) => (W && W.label(a)) || short(a);
                let data = null;
                const tile = (k, v, s) => `<div class="ti-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s || ''}</div></div>`;
                const render = () => {
                    const yr = $('ti-year').value;
                    const price = data.price?.usd || 0;
                    const held = data.wallets.reduce((s, w) => s + w.titnBalance, 0), staked = data.wallets.reduce((s, w) => s + w.titnStaked, 0);
                    const pending = data.wallets.reduce((s, w) => s + w.pendingUsdc, 0);
                    const inYear = data.rewards.filter(r => !yr || String(r.time).startsWith(yr));
                    const yrUsd = inYear.reduce((s, r) => s + (r.usd || 0), 0);
                    const allUsd = data.rewards.reduce((s, r) => s + (r.usd || 0), 0);
                    $('ti-tiles').innerHTML =
                        tile('TITN staked', num(staked, 0), usd(staked * price) + ' now') +
                        tile('TITN held (not staked)', num(held, 0), usd(held * price) + ' now') +
                        tile('TITN price', price ? '$' + price.toFixed(5) : '–', esc(data.price?.day || '') + ' · DefiLlama') +
                        tile('TGT price', data.tgtPrice?.usd ? '$' + Number(data.tgtPrice.usd).toFixed(5) : '–', esc(data.tgtPrice?.day || '') + ' · last traded') +
                        tile('Pending staking rewards', usd(pending), 'USDC not yet claimed (not income until received)') +
                        tile(yr ? `Rewards received in ${esc(yr)}` : 'Rewards received', usd(yrUsd), `${inYear.length} payments`) +
                        tile('All rewards to date', usd(allUsd), `${data.rewards.length} payments`);
                    $('ti-wallets').innerHTML = data.wallets.map(w => `<tr><td>${esc(w.label || short(w.address))}<div class="ti-mono" style="color: var(--text-muted);"><a href="${scan('base', 'address', w.address)}" target="_blank" rel="noopener">${esc(short(w.address))}</a></div></td><td class="num">${num(w.titnBalance, 0)}</td><td class="num">${num(w.titnStaked, 0)}</td><td class="num">${usd((w.titnBalance + w.titnStaked) * price)}</td><td class="num">${usd(w.pendingUsdc)}</td></tr>`).join('')
                        || '<tr><td colspan="5" style="text-align: center; color: var(--text-secondary);">No 0x... wallets saved. Add them on My Wallets.</td></tr>';
                    const groups = {};
                    for (const r of data.rewards) { const k = String(r.time).slice(0, 4) + '|' + r.source + '|' + r.symbol; (groups[k] = groups[k] || { n: 0, amt: 0, usd: 0, missing: 0 }); groups[k].n++; groups[k].amt += r.amount; groups[k].usd += r.usd || 0; if (r.usd == null) groups[k].missing++; }
                    const years = [...new Set(Object.keys(groups).map(k => k.slice(0, 4)))].sort().reverse();
                    $('ti-years').innerHTML = years.map(y => {
                        const rows = Object.entries(groups).filter(([k]) => k.startsWith(y));
                        const tot = rows.reduce((s, [, g]) => s + g.usd, 0);
                        return rows.map(([k, g]) => { const [, src, sym] = k.split('|'); return `<tr><td>${y}</td><td>${esc(src)}</td><td class="num">${g.n}</td><td class="num">${num(g.amt, sym === 'USDC' ? 2 : 0)} ${sym}</td><td class="num">${usd(g.usd)}${g.missing ? ` <span class="tr-flag">${g.missing} unpriced</span>` : ''}</td></tr>`; }).join('')
                            + `<tr class="tr-total"><td>${y}</td><td><strong>Total</strong></td><td></td><td></td><td class="num"><strong>${usd(tot)}</strong></td></tr>`;
                    }).join('') || '<tr><td colspan="5" style="text-align: center; color: var(--text-secondary);">No rewards found.</td></tr>';
                    $('ti-list-title').textContent = yr ? `Reward payments in ${yr}` : 'Reward payments';
                    $('ti-list').innerHTML = inYear.slice().reverse().map(r => `<tr><td>${esc(String(r.time).slice(0, 16).replace('T', ' '))}</td><td class="ti-hide-sm">${esc(name(r.wallet))}</td><td>${esc(r.source)}</td><td class="num">${num(r.amount, r.symbol === 'USDC' ? 6 : 2)} ${r.symbol}</td><td class="num ti-hide-sm" title="${esc(r.priceSource)}">${r.priceUsd == null ? '–' : r.symbol === 'USDC' ? '$1.00' : '$' + Number(r.priceUsd).toFixed(5)}</td><td class="num">${usd(r.usd)}</td><td class="ti-hide-sm ti-mono"><a href="${scan(r.chain, 'tx', r.tx)}" target="_blank" rel="noopener">${esc(r.tx.slice(0, 10))}…</a></td></tr>`).join('')
                        || '<tr><td colspan="7" style="text-align: center; color: var(--text-secondary);">None in this year.</td></tr>';
                    $('ti-other').innerHTML = (data.unclassified || []).map(u => `<tr><td class="ti-mono"><a href="${scan(u.chain, 'address', u.from)}" target="_blank" rel="noopener">${esc(u.name || short(u.from))}</a></td><td>${u.chain === 'arbitrum' ? 'Arbitrum' : 'Base'}</td><td class="num">${u.count}</td><td class="num">${num(u.amount, 2)} ${u.symbol}</td><td>${esc(u.treatedAs || '')}</td><td class="ti-hide-sm">${esc(String(u.first).slice(0, 10))} – ${esc(String(u.last).slice(0, 10))}</td></tr>`).join('')
                        || '<tr><td colspan="6" style="text-align: center; color: var(--text-secondary);">None.</td></tr>';
                };
                const load = async (refresh) => {
                    $('ti-status').textContent = 'Reading Base and Arbitrum…';
                    try {
                        const r = await fetch('/api/titn/summary' + (refresh ? '?refresh=1' : ''));
                        const j = await r.json();
                        if (!r.ok) throw new Error(j.error || r.statusText);
                        data = j;
                        const years = [...new Set(j.rewards.map(x => String(x.time).slice(0, 4)))].sort().reverse();
                        const cur = $('ti-year').value || String(new Date().getUTCFullYear());
                        $('ti-year').innerHTML = '<option value="">All years</option>' + years.map(y => `<option value="${y}">${y}</option>`).join('');
                        $('ti-year').value = years.includes(cur) ? cur : '';
                        render();
                        $('ti-status').textContent = 'Updated ' + new Date(j.updatedUtc).toLocaleString();
                    } catch (e) { $('ti-status').textContent = 'Error: ' + e.message; }
                };
                $('ti-year').addEventListener('change', render);
                $('ti-refresh').addEventListener('click', () => load(true));
                load(false);
            })();
        </script>
        """;
}
