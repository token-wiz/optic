// Exchange Flows page: wallets ranked by how much OPT they sent to exchanges (Coinstore, Osmosis) and did not get back.
public static partial class WebDashboard
{
    static void AppendExchangeFlowsCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        AppendWalletHistoryScript(sb);
        sb.Append(ExchangeFlowsHtml);
    }

    const string ExchangeFlowsHtml = """
        <style>
            .xf { grid-column: 1 / -1; }
            .xf .card-content { display: flex; flex-direction: column; gap: 12px; }
            .xf-note { font-size: 13px; color: var(--text-secondary); margin: 0; max-width: 1100px; line-height: 1.5; }
            .xf-row { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; font-size: 13px; }
            .xf-row input[type=search] { flex: 1 1 240px; max-width: 360px; font-size: 13px; padding: 5px 8px; min-height: 0; }
            .xf-row select { font-size: 13px; padding: 4px 6px; min-height: 0; }
            .xf-tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 10px; }
            .xf-tile { border: 1px solid var(--border); border-radius: 8px; padding: 10px 12px; background: var(--surface); }
            .xf-tile .k { font-size: 12px; color: var(--text-secondary); }
            .xf-tile .v { font-size: 19px; font-weight: 700; margin-top: 2px; font-variant-numeric: tabular-nums; }
            .xf-tile .s { font-size: 12px; color: var(--text-muted); margin-top: 2px; }
            .xf td.num, .xf th.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
            .xf th[data-sort] { cursor: pointer; user-select: none; }
            .xf th[data-dir="desc"]::after { content: ' ▼'; font-size: 9px; }
            .xf th[data-dir="asc"]::after { content: ' ▲'; font-size: 9px; }
            .xf-sub { display: block; font-size: 11px; color: var(--text-muted); white-space: nowrap; }
            .xf-net { font-weight: 700; }
            .xf-btn { font-size: 12px; padding: 4px 12px; background: var(--surface); color: var(--text-primary); border: 1px solid var(--border); box-shadow: none; text-transform: none; letter-spacing: 0; font-weight: 600; min-height: 0; }
            .xf table.data-table { min-width: 980px; }
            .nm-tag { display: inline-block; font-weight: 700; border-radius: 6px; padding: 1px 7px; line-height: 1.45; overflow-wrap: anywhere; }
            .nm-named { background: color-mix(in srgb, var(--accent-primary) 16%, transparent); color: var(--accent-primary); }
            .nm-shared { background: color-mix(in srgb, #8250df 16%, transparent); color: #8250df; }
            .nm-mine { background: #f5a623; color: #1f1300; box-shadow: 0 0 0 1px #c47f00 inset; }
            tr.xf-mine > td { background: color-mix(in srgb, #f5a623 9%, transparent); }
            tr.xf-mine > td:first-child { box-shadow: inset 4px 0 0 #f5a623; }
            @media (max-width: 640px) {
                .xf-tiles { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
                .xf-tile .v { font-size: 16px; }
                .xf .data-table-wrap { overflow: visible; max-height: none !important; }
                .xf table.data-table { min-width: 0; display: block; }
                .xf table.data-table thead { display: none; }
                .xf table.data-table tbody { display: block; }
                .xf table.data-table tr { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 6px 12px; padding: 10px 12px; margin: 0 0 8px; border: 1px solid var(--border); border-radius: 8px; }
                .xf table.data-table td { display: block; border: none; padding: 0; text-align: left !important; white-space: normal; overflow-wrap: anywhere; }
                .xf table.data-table td::before { content: attr(data-label); display: block; font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.04em; color: var(--text-muted); }
                .xf table.data-table td.xf-wide { grid-column: 1 / -1; }
                .xf table.data-table td.xf-rank { grid-column: 1 / -1; font-weight: 700; }
                .xf table.data-table td.xf-rank::before, .xf table.data-table td.xf-wide::before { display: none; }
                .xf table.data-table td.xf-zero { display: none; }
                .xf table.data-table tr.xf-mine { border: 2px solid #f5a623; }
                .xf table.data-table tr.xf-mine > td { background: none; box-shadow: none; }
            }
        </style>
        <div class="card xf">
            <div class="card-content">
                <p class="xf-note">Wallets ranked by the OPT they sent to exchanges and did not get back. <strong>Coinstore</strong>: deposits are transfers to its deposit address (with the customer&#39;s account number as the memo) and withdrawals are paid from its hot wallet. <strong>Osmosis</strong>: OPT reaches Osmosis by IBC transfer into an escrow account on Optio, and comes back out of the same escrow. A withdrawal can go to a different wallet than the one that deposited, so &ldquo;back&rdquo; counts only what came back to the same wallet. <strong>Likely MEXC</strong>: MEXC gave each customer an own deposit address on Optio; OPTIC finds the addresses that forward nearly everything to one collector wallet within minutes (checked hourly) and counts what each wallet paid into them. Nothing on chain names the exchange, so it is marked likely.</p>
                <div id="xf-status" class="xf-note">Reading every deposit and withdrawal from the chain… the first time can take a few minutes.</div>
                <div id="xf-tiles" class="xf-tiles" hidden></div>
                <div class="xf-row">
                    <input type="search" id="xf-search" placeholder="Search name, email or address" autocomplete="off" spellcheck="false">
                    <label style="display: flex; gap: 6px; align-items: center;"><input type="checkbox" id="xf-named"> Named only</label>
                    <label style="display: flex; gap: 6px; align-items: center;">Sort <select id="xf-sort">
                        <option value="net">Kept on exchanges</option><option value="sent">Sent to exchanges</option><option value="coinstoreSent">Sent to Coinstore</option><option value="osmosisSent">Sent to Osmosis</option><option value="mexcSent">Sent to likely MEXC</option><option value="back">Came back</option><option value="lastSent">Last sent</option>
                    </select></label>
                    <span id="xf-count" style="color: var(--text-secondary);"></span>
                </div>
                <div class="data-table-wrap"><table class="data-table"><thead><tr>
                    <th class="num">#</th>
                    <th>Wallet</th>
                    <th class="num" data-sort="net">Kept on exchanges (OPT)</th>
                    <th class="num" data-sort="coinstoreSent">To Coinstore</th>
                    <th class="num" data-sort="coinstoreBack">Back from Coinstore</th>
                    <th class="num" data-sort="osmosisSent">To Osmosis</th>
                    <th class="num" data-sort="osmosisBack">Back from Osmosis</th>
                    <th class="num" data-sort="mexcSent" title="Sent to per-customer deposit addresses that are swept to the likely MEXC collector">To likely MEXC</th>
                    <th class="num" data-sort="deposits">Deposits</th>
                    <th class="num" data-sort="lastSent">Last sent</th>
                </tr></thead><tbody id="xf-body"></tbody></table></div>
                <div class="xf-row"><button type="button" id="xf-more" class="xf-btn" hidden>Show more</button><button type="button" id="xf-refresh" class="xf-btn">Refresh from chain</button></div>
                <p id="xf-foot" class="xf-note"></p>
            </div>
        </div>
        <script>
        (() => {
            const $ = (id) => document.getElementById(id);
            const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
            const fmt = (v) => Math.round(v || 0).toLocaleString('en-US');
            const fmtBig = (v) => Math.abs(v) >= 1e9 ? (v / 1e9).toFixed(2) + 'B' : Math.abs(v) >= 1e6 ? (v / 1e6).toFixed(1) + 'M' : fmt(v);
            const day = (v) => v ? String(v).slice(0, 10) : '–';
            const short = (a) => a && a.length > 24 ? a.slice(0, 12) + '…' + a.slice(-6) : (a || '');
            let data = null, names = {}, globalNames = {}, sortKey = 'net', sortDir = 'desc', limit = 100;
            const nameOf = (a) => names[a] || (globalNames[a] ? globalNames[a].name + (globalNames[a].email ? ' · ' + globalNames[a].email : '') : '') || (window.opticWallets?.label(a) ? 'My wallet · ' + window.opticWallets.label(a) : '');

            // Name tags: your own wallets stand out (amber), names you gave are blue, shared names (genesis list) purple.
            const isMine = (a) => !!window.opticWallets?.list().some(x => x.a === a);
            const tag = (a) => {
                const n = nameOf(a);
                if (!n) return '';
                const cls = isMine(a) ? 'nm-mine' : names[a] ? 'nm-named' : 'nm-shared';
                return `<span class="nm-tag ${cls}">${isMine(a) ? '★ ' + esc(names[a] || window.opticWallets.label(a) || 'My wallet') : esc(n)}</span><br>`;
            };
            const render = () => {
                if (!data) return;
                const q = $('xf-search').value.trim().toLowerCase(), namedOnly = $('xf-named').checked;
                let rows = data.wallets.map((w, i) => ({ ...w, rank: i + 1 }));
                if (namedOnly) rows = rows.filter(w => nameOf(w.address));
                if (q) rows = rows.filter(w => w.address.toLowerCase().includes(q) || nameOf(w.address).toLowerCase().includes(q));
                rows.sort((a, b) => { const x = a[sortKey] ?? '', y = b[sortKey] ?? ''; return (x < y ? -1 : x > y ? 1 : 0) * (sortDir === 'asc' ? 1 : -1); });
                $('xf-count').textContent = `${rows.length.toLocaleString('en-US')} wallet(s)`;
                document.querySelectorAll('.xf th[data-sort]').forEach(th => th.dataset.dir = th.dataset.sort === sortKey ? sortDir : '');
                $('xf-body').innerHTML = rows.slice(0, limit).map(w => {
                    const n = nameOf(w.address), z = (v) => v ? '' : ' xf-zero';
                    return `<tr${isMine(w.address) ? ' class="xf-mine"' : ''}>
                        <td class="num xf-rank" data-label="Rank">#${w.rank}</td>
                        <td class="xf-wide" data-label="Wallet">${tag(w.address)}<a class="address-cell" href="/page/address-distributions?address=${encodeURIComponent(w.address)}" style="color: inherit;" title="${esc(w.address)}">${esc(short(w.address))}</a>${w.coinstoreAccounts ? `<span class="xf-sub">${w.coinstoreAccounts} Coinstore account${w.coinstoreAccounts === 1 ? '' : 's'}</span>` : ''}<br><button type="button" class="wh-btn" data-wh-open="${esc(w.address)}" data-wh-subtitle="Exchange Flows #${w.rank} · kept on exchanges ${fmt(w.net)} OPT">History</button></td>
                        <td class="num xf-net xf-wide" data-label="Kept on exchanges (OPT)">${fmt(w.net)}<span class="xf-sub">sent ${fmt(w.sent)} · back ${fmt(w.back)}</span></td>
                        <td class="num${z(w.coinstoreSent)}" data-label="To Coinstore">${fmt(w.coinstoreSent)}</td>
                        <td class="num${z(w.coinstoreBack)}" data-label="Back from Coinstore">${fmt(w.coinstoreBack)}</td>
                        <td class="num${z(w.osmosisSent)}" data-label="To Osmosis">${fmt(w.osmosisSent)}</td>
                        <td class="num${z(w.osmosisBack)}" data-label="Back from Osmosis">${fmt(w.osmosisBack)}</td>
                        <td class="num${z(w.mexcSent)}" data-label="To likely MEXC">${fmt(w.mexcSent)}${w.mexcBack ? `<span class="xf-sub">${fmt(w.mexcBack)} back</span>` : ''}</td>
                        <td class="num" data-label="Deposits">${w.deposits}${w.returns ? `<span class="xf-sub">${w.returns} back</span>` : ''}</td>
                        <td class="num" data-label="Last sent">${day(w.lastSent)}<span class="xf-sub">first ${day(w.firstSent)}</span></td>
                    </tr>`;
                }).join('') || '<tr><td colspan="10" style="text-align: center; color: var(--text-secondary);">No wallets match.</td></tr>';
                $('xf-more').hidden = rows.length <= limit;
                $('xf-more').textContent = `Show more (${(rows.length - limit).toLocaleString('en-US')} left)`;
            };

            const load = async (refresh) => {
                $('xf-refresh').disabled = true;
                if (refresh) $('xf-status').textContent = 'Reading every deposit and withdrawal from the chain…';
                try {
                    const [d, n, g] = await Promise.all([
                        fetch('/api/exchange-flows' + (refresh ? '?refresh=1' : '')).then(async r => { const j = await r.json(); if (!r.ok) throw new Error(j.error || ('HTTP ' + r.status)); return j; }),
                        fetch('/api/address-book').then(r => r.ok ? r.json() : {}).catch(() => ({})),
                        fetch('/api/address-book/global').then(r => r.ok ? r.json() : {}).catch(() => ({})),
                    ]);
                    data = d; names = n || {}; globalNames = g || {};
                    const t = d.totals, tile = (k, v, s) => `<div class="xf-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s || ''}</div></div>`;
                    $('xf-tiles').hidden = false;
                    $('xf-tiles').innerHTML =
                        tile('Sent to Coinstore', fmtBig(t.coinstoreSent) + ' OPT', `${fmtBig(t.coinstoreBack)} came back to the same wallets`) +
                        tile('Sent to Osmosis', fmtBig(t.osmosisSent) + ' OPT', `${fmtBig(t.osmosisBack)} came back to the same wallets`) +
                        tile('Sent to likely MEXC', fmtBig(t.mexcSent) + ' OPT', `into ${d.venues.mexc.depositAddresses} per-customer deposit addresses`) +
                        tile('Kept on exchanges', fmtBig(t.coinstoreSent + t.osmosisSent + t.mexcSent - t.coinstoreBack - t.osmosisBack - t.mexcBack) + ' OPT', `by ${t.wallets.toLocaleString('en-US')} wallets`) +
                        tile('Paid out to other wallets', fmtBig(t.backToOthers) + ' OPT', 'withdrawals to wallets that never deposited');
                    $('xf-status').textContent = `As of ${String(d.asOfUtc).replace('T', ' ').slice(0, 16)} UTC · refreshed hourly`;
                    $('xf-foot').innerHTML = `Likely MEXC collector ${esc(d.venues.mexc.collectors.join(', '))}: ${d.venues.mexc.depositAddresses} deposit addresses, ${d.venues.mexc.customers} depositing wallets (to block ${Number(d.venues.mexc.scannedToHeight).toLocaleString('en-US')}). Coinstore deposit address ${esc(d.venues.coinstore.depositCollector)}, hot wallet ${esc(d.venues.coinstore.hotWallet)}. Osmosis escrow accounts: ${d.venues.osmosis.map(o => `${esc(o.channel)} ${esc(short(o.escrow))}`).join(', ')}.${d.truncated.length ? ` <strong>${d.truncated.length} exchange address(es) hit the 20,000-transaction cap; older transfers may be missing.</strong>` : ''}`;
                    render();
                } catch (error) {
                    $('xf-status').textContent = 'Could not read exchange flows: ' + error.message;
                } finally { $('xf-refresh').disabled = false; }
            };

            $('xf-search').addEventListener('input', () => { limit = 100; render(); });
            $('xf-named').addEventListener('change', () => { limit = 100; render(); });
            $('xf-sort').addEventListener('change', (e) => { sortKey = e.target.value; sortDir = 'desc'; render(); });
            document.querySelectorAll('.xf th[data-sort]').forEach(th => th.addEventListener('click', () => {
                if (sortKey === th.dataset.sort) sortDir = sortDir === 'asc' ? 'desc' : 'asc'; else { sortKey = th.dataset.sort; sortDir = 'desc'; }
                $('xf-sort').value = [...$('xf-sort').options].some(o => o.value === sortKey) ? sortKey : $('xf-sort').value;
                render();
            }));
            $('xf-more').addEventListener('click', () => { limit += 100; render(); });
            $('xf-refresh').addEventListener('click', () => load(true));
            load(false);
        })();
        </script>
        """;
}
