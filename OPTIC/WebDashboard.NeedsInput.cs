// Needs Your Input: everything OPTIC will not guess, in one list, each with a way to answer it.
//   - OPT sent or received in your Optio wallets and Osmosis account that is not classified (same answers as the Tax Report,
//     saved to that wallet's tax labels), and gifts received with no giver's basis.
//   - Other crypto (Ethereum, Base, Arbitrum, BNB Chain) moved to or from an outside address that the cost engine cannot
//     place, saved to titn-labels.json and used by the lots on the next load.
public static partial class WebDashboard
{
    static void AppendNeedsInputCard(System.Text.StringBuilder sb)
    {
        AppendMyWalletsScript(sb);
        sb.Append(NeedsInputHtml);
    }

    const string NeedsInputHtml = """
        <style>
            .ni { grid-column: 1 / -1; }
            .ni h3 { margin: 18px 0 6px; font-size: 16px; }
            .ni .ni-note { font-size: 13px; color: var(--text-secondary); margin: 0 0 8px; max-width: 1040px; }
            .ni select, .ni input, .ni textarea { font-size: 12px; padding: 3px 6px; min-height: 0; }
            .ni input.ni-usd { width: 100px; text-align: right; }
            .ni textarea { width: 200px; min-height: 28px; resize: vertical; }
            .ni .ni-ok { color: var(--success, #107c10); font-weight: 600; }
            .ni .ni-count { display: inline-block; font-size: 12px; font-weight: 600; border-radius: 10px; padding: 1px 9px; background: var(--warning); color: #000; margin-left: 6px; }
            .ni .ni-mono { font-family: monospace; font-size: 11px; }
            @media (max-width: 640px) { .ni textarea { width: 140px; } }
        </style>
        <div class="card ni">
            <h2>Needs Your Input <span id="ni-total"></span></h2>
            <div class="card-content">
                <p class="ni-note">Everything OPTIC won't guess: transfers to or from addresses it can't identify, and receipts with no known cost. Until you answer, a send is not counted as a sale and a receipt has no cost basis; nothing is treated as zero. Answers are saved to your account and used by the Tax Report, Sales &amp; Lots and Profit &amp; Loss.</p>
                <div style="display: flex; gap: 8px; flex-wrap: wrap; align-items: center; margin-bottom: 6px;">
                    <label for="ni-year" style="font-size: 13px;">Year</label>
                    <select id="ni-year"><option value="">All years</option></select>
                    <label style="font-size: 13px; display: inline-flex; gap: 6px; align-items: center;"><input type="checkbox" id="ni-show-done"> Show what I've answered</label>
                </div>
                <div id="ni-status" class="ni-note">Reading your wallets…</div>

                <h3>OPT not classified <span id="ni-opt-count"></span></h3>
                <p class="ni-note">OPT sent from or received in your Optio wallets and Osmosis account, to or from an address that isn't one of your wallets or a gift wallet. Moves between your own wallets are recognised automatically.</p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Date (UTC)</th><th>Wallet</th><th>Sent / received</th><th>Other party</th><th style="text-align: right;">OPT</th><th style="text-align: right;">Value that day</th><th>What was it?</th><th>USD (sale price or cost)</th><th>Date sold</th><th>Note</th><th></th></tr></thead><tbody id="ni-opt"></tbody></table></div>

                <h3>Gifts received with no giver's basis <span id="ni-gift-count"></span></h3>
                <p class="ni-note">A gift carries over the giver's cost and holding period. Ask them what they paid (or what it was worth when they earned it) and when.</p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Date (UTC)</th><th>Wallet</th><th>From</th><th style="text-align: right;">OPT</th><th>Giver's cost (USD)</th><th>When they got it</th><th>Note</th><th></th></tr></thead><tbody id="ni-gift"></tbody></table></div>

                <h3>Other crypto not classified <span id="ni-evm-count"></span></h3>
                <p class="ni-note">Tokens, ETH, BNB and SOL moved between your Ethereum, Base, Arbitrum, BNB Chain or Solana wallets and an address OPTIC can't identify. A receipt stays at unknown cost and a send stays out of your sales until you answer. Answers apply to the whole transaction.</p>
                <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Date (UTC)</th><th>Wallet</th><th>Network</th><th>Sent / received</th><th>Other party</th><th style="text-align: right;">Amount</th><th style="text-align: right;">Value that day</th><th>What was it?</th><th>USD (sale price or cost)</th><th>Note</th><th></th></tr></thead><tbody id="ni-evm"></tbody></table></div>
                <div id="ni-evm-notes" class="ni-note" style="margin-top: 6px;"></div>
            </div>
        </div>
        <script>
        (() => {
            const $ = (id) => document.getElementById(id);
            const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
            const W = window.opticWallets;
            const short = (a) => a && a.length > 20 ? a.slice(0, 10) + '…' + a.slice(-6) : (a || '');
            const wName = (a) => W.label(a) || short(a);
            const fmtTime = (v) => String(v || '').replace('T', ' ').replace(/\.\d+/, '').replace('Z', '').slice(0, 16);
            const fmtQty = (v, d = 2) => Number(v).toLocaleString('en-US', { maximumFractionDigits: d });
            const fmtUsd = (v) => v == null ? '–' : '$' + Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
            const getJson = async (url, opts) => { const r = await fetch(url, opts); const j = await r.json(); if (!r.ok) throw new Error(j.error || url + ' failed'); return j; };
            const yearSel = $('ni-year');
            const thisYear = new Date().getUTCFullYear();
            for (let y = thisYear; y >= 2023; y--) yearSel.insertAdjacentHTML('beforeend', `<option value="${y}">${y}</option>`);
            const inYear = (t) => !yearSel.value || String(t).startsWith(yearSel.value);

            // Same treatments as the Tax Report (keep in step with OUT_KINDS / IN_KINDS there).
            const OPT_OUT = [['exchange-sale', 'Deposited to exchange and sold there'], ['sale', 'Sold to a person (cash or other crypto)'], ['node', 'Bought a node with it'], ['node-expense', 'Paid a node expense with it'], ['payment', 'Paid for something else'], ['worthless', 'Lost / stuck on exchange'], ['exchange-held', 'Deposited to exchange, not sold'], ['own', 'Moved to my own wallet'], ['gift', 'Gift'], ['loan', 'Loan (expected back)'], ['ignore', 'Leave out']];
            const OPT_IN = [['purchase', 'Bought (exchange or person)'], ['own', 'From my own wallet or exchange account'], ['gift-in', 'Gift received'], ['income', 'Other income (payment received)'], ['loan-back', 'Loan repaid to me'], ['ignore', 'Leave out']];
            const EVM_OUT = [['sale', 'Sold (enter what you got, in USD)'], ['own', 'Moved to my own wallet or exchange account'], ['gift-out', 'Gift'], ['ignore', 'Leave out']];
            const EVM_IN = [['purchase', 'Bought (enter what you paid)'], ['own', 'From my own wallet or exchange account'], ['gift-in', 'Gift received (enter the giver’s cost)'], ['income', 'Income (payment or reward)'], ['ignore', 'Leave out']];
            const select = (opts, chosen) => `<select data-f="kind"><option value="">Choose…</option>${opts.map(([k, l]) => `<option value="${k}"${k === chosen ? ' selected' : ''}>${esc(l)}</option>`).join('')}</select>`;
            const scanTx = (chain, tx) => (chain === 'sovr' ? 'https://sovrscan.com/transactions/' : chain === 'osmosis' ? 'https://www.mintscan.io/osmosis/tx/' : chain === 'solana' ? 'https://solscan.io/tx/' : chain === 'bsc' ? 'https://bscscan.com/tx/' : chain === 'arbitrum' ? 'https://arbiscan.io/tx/' : chain === 'ethereum' ? 'https://etherscan.io/tx/' : 'https://basescan.org/tx/') + tx;
            const scanAddr = (chain, a) => (chain === 'sovr' ? 'https://sovrscan.com/accounts/' : chain === 'osmosis' ? 'https://www.mintscan.io/osmosis/address/' : chain === 'solana' ? 'https://solscan.io/account/' : chain === 'bsc' ? 'https://bscscan.com/address/' : chain === 'arbitrum' ? 'https://arbiscan.io/address/' : chain === 'ethereum' ? 'https://etherscan.io/address/' : 'https://basescan.org/address/') + a;
            const NET = { ethereum: 'Ethereum', base: 'Base', arbitrum: 'Arbitrum', bsc: 'BNB Chain', solana: 'Solana' };

            let opt = null, evm = null, evmLabels = {}, gifts = {};
            const keyOf = (e) => `${e.txHash}|${e.direction}|${e.counterparty}`;

            // ---------------------------------------------------------------- OPT (Optio wallets and Osmosis account)
            const loadOpt = async () => {
                const wallets = W.listAll().map(w => w.a);
                const optio = wallets.filter(a => W.valid(a));
                const [giftSenders, ...per] = await Promise.all([
                    getJson('/api/tax/gift-senders').catch(() => ({})),
                    ...wallets.map(async a => {
                        if (W.validOsmo(a)) {
                            const [o, s] = await Promise.all([getJson('/api/osmosis/ledger?address=' + encodeURIComponent(a) + '&peers=' + encodeURIComponent(optio.join(','))), getJson('/api/tax/labels?address=' + encodeURIComponent(a))]);
                            return { a, entries: o.entries || [], labels: { ...(o.labels || {}), ...(s || {}) }, links: o.links || [] };
                        }
                        const [l, s] = await Promise.all([getJson('/api/wallet/ledger?address=' + encodeURIComponent(a)), getJson('/api/tax/labels?address=' + encodeURIComponent(a))]);
                        return { a, entries: l.entries || [], labels: s || {} };
                    }),
                ]);
                gifts = giftSenders || {};
                // IBC moves between an Optio wallet and your Osmosis account: point the Optio side at the Osmosis account.
                for (const o of per.filter(x => x.links)) for (const lk of o.links) {
                    const wd = per.find(x => x.a === lk.wallet);
                    if (wd) for (const e of wd.entries) if (e.txHash === lk.optioTxHash && e.direction === lk.direction && !per.some(x => x.a === e.counterparty)) e.counterparty = o.a;
                }
                opt = per;
            };
            const own = () => new Set(W.listAll().map(w => w.a));
            const optItems = () => {
                const mine = own(), items = [], giftRows = [];
                for (const w of opt || []) for (const e of w.entries) {
                    if (e.category !== 'wallet' || !inYear(e.timeUtc)) continue;
                    const lab = w.labels[keyOf(e)] || {};
                    const auto = mine.has(e.counterparty) ? 'own' : e.direction === 'in' && gifts[e.counterparty] != null ? 'gift-in' : null;
                    const kind = (lab.kind && lab.kind !== 'unclassified' ? lab.kind : null) || auto;
                    if (kind === 'gift-in' && (lab.usd === undefined || lab.usd === null || lab.usd === '')) giftRows.push({ w: w.a, e, lab, done: false });
                    else if (kind === 'gift-in') giftRows.push({ w: w.a, e, lab, done: true });
                    if (!kind) items.push({ w: w.a, e, lab, done: false });
                    else if (lab.kind && lab.kind !== 'unclassified' && !auto) items.push({ w: w.a, e, lab, done: true });
                }
                const byTime = (x, y) => String(y.e.timeUtc).localeCompare(String(x.e.timeUtc));
                return { items: items.sort(byTime), giftRows: giftRows.sort(byTime) };
            };
            const renderOpt = () => {
                if (!opt) return;
                const show = $('ni-show-done').checked;
                const { items, giftRows } = optItems();
                const todo = items.filter(x => !x.done), gtodo = giftRows.filter(x => !x.done);
                $('ni-opt-count').innerHTML = todo.length ? `<span class="ni-count">${todo.length}</span>` : '<span class="ni-ok">✓ none</span>';
                $('ni-gift-count').innerHTML = gtodo.length ? `<span class="ni-count">${gtodo.length}</span>` : '<span class="ni-ok">✓ none</span>';
                const valueOf = (e) => e.priceUsd != null ? e.amountOpt * e.priceUsd : null;
                $('ni-opt').innerHTML = items.filter(x => show || !x.done).map(x => `<tr data-w="${esc(x.w)}" data-key="${esc(keyOf(x.e))}">
                    <td>${esc(fmtTime(x.e.timeUtc))}</td><td>${esc(wName(x.w))}</td><td>${x.e.direction === 'out' ? 'Sent' : 'Received'}</td>
                    <td class="ni-mono" title="${esc(x.e.counterparty)}">${esc(short(x.e.counterparty))}${x.e.memo ? `<div style="font-family: inherit; color: var(--text-secondary);">${esc(x.e.memo)}</div>` : ''}</td>
                    <td class="amount-cell">${fmtQty(x.e.amountOpt)}</td><td class="amount-cell">${fmtUsd(valueOf(x.e))}</td>
                    <td>${select(x.e.direction === 'out' ? OPT_OUT : OPT_IN, x.lab.kind)}</td>
                    <td><input type="number" step="any" min="0" class="ni-usd" data-f="usd" value="${esc(x.lab.usd ?? '')}" placeholder="${valueOf(x.e) != null ? valueOf(x.e).toFixed(2) : ''}"></td>
                    <td>${x.e.direction === 'out' ? `<input type="date" data-f="date" value="${esc(x.lab.date || '')}">` : ''}</td>
                    <td><textarea data-f="note" placeholder="e.g. sold on Coinstore, trade #…">${esc(x.lab.note || '')}</textarea></td>
                    <td><button type="button" data-save-opt>${x.done ? 'Update' : 'Save'}</button>${x.done ? ' <span class="ni-ok">✓</span>' : ''}</td></tr>`).join('')
                    || `<tr><td colspan="11" style="text-align: center; color: var(--text-secondary);">Nothing to answer${yearSel.value ? ' in ' + yearSel.value : ''}.</td></tr>`;
                $('ni-gift').innerHTML = giftRows.filter(x => show || !x.done).map(x => `<tr data-w="${esc(x.w)}" data-key="${esc(keyOf(x.e))}">
                    <td>${esc(fmtTime(x.e.timeUtc))}</td><td>${esc(wName(x.w))}</td><td>${esc(gifts[x.e.counterparty] || short(x.e.counterparty))}</td><td class="amount-cell">${fmtQty(x.e.amountOpt)}</td>
                    <td><input type="number" step="any" min="0" class="ni-usd" data-f="usd" value="${esc(x.lab.usd ?? '')}" placeholder="their cost"></td>
                    <td><input type="date" data-f="date" value="${esc(x.lab.date || '')}"></td>
                    <td><textarea data-f="note">${esc(x.lab.note || '')}</textarea></td>
                    <td><button type="button" data-save-gift>${x.done ? 'Update' : 'Save'}</button>${x.done ? ' <span class="ni-ok">✓</span>' : ''}</td></tr>`).join('')
                    || `<tr><td colspan="8" style="text-align: center; color: var(--text-secondary);">Nothing to answer${yearSel.value ? ' in ' + yearSel.value : ''}.</td></tr>`;
            };
            const fields = (tr) => Object.fromEntries([...tr.querySelectorAll('[data-f]')].map(el => [el.dataset.f, el.value]));
            const saveOptLabel = async (tr, patch) => {
                const w = tr.dataset.w, key = tr.dataset.key, wd = opt.find(x => x.a === w);
                const next = { ...(wd.labels[key] || {}) };
                for (const [k, v] of Object.entries(patch)) { if (v === '' || v == null) delete next[k]; else next[k] = k === 'usd' ? Number(v) : v; }
                const saved = await getJson('/api/tax/labels?address=' + encodeURIComponent(w), { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ [key]: Object.keys(next).length ? next : null }) });
                wd.labels = { ...wd.labels, ...(saved || {}) };
                if (!saved || !(key in saved)) { if (Object.keys(next).length) wd.labels[key] = next; else delete wd.labels[key]; }
            };

            // ---------------------------------------------------------------- other crypto (0x... wallets)
            const loadEvm = async () => {
                if (!W.listSaved().some(w => W.validEvm(w.a) || W.validSolana(w.a))) { evm = { unclassified: [], notes: [] }; return; }
                [evm, evmLabels] = await Promise.all([getJson('/api/titn/lots'), getJson('/api/titn/labels').catch(() => ({}))]);
            };
            const renderEvm = () => {
                if (!evm) return;
                const show = $('ni-show-done').checked;
                const todo = (evm.unclassified || []).filter(u => inYear(u.time)).sort((x, y) => String(y.time).localeCompare(String(x.time)));
                const done = show ? Object.entries(evmLabels).filter(([tx, l]) => l && l.kind) : [];
                $('ni-evm-count').innerHTML = todo.length ? `<span class="ni-count">${todo.length}</span>` : '<span class="ni-ok">✓ none</span>';
                const dec = (a) => a === 'ETH' || a === 'BNB' ? 6 : 2;
                $('ni-evm').innerHTML = todo.map(u => {
                    const cp = String(u.counterparty || '').split(',')[0];
                    return `<tr data-tx="${esc(u.tx)}">
                    <td>${esc(fmtTime(u.time))}</td><td>${esc(wName(u.wallet))}</td><td>${esc(NET[u.chain] || u.chain)}</td><td>${u.direction === 'in' ? 'Received' : 'Sent'}</td>
                    <td class="ni-mono"><a href="${scanAddr(u.chain, cp)}" target="_blank" rel="noopener" title="${esc(u.counterparty)}">${esc(short(cp))}</a></td>
                    <td class="amount-cell">${fmtQty(u.qty, dec(u.asset))} ${esc(u.asset)}</td><td class="amount-cell">${fmtUsd(u.valueThatDay)}</td>
                    <td>${select(u.direction === 'in' ? EVM_IN : EVM_OUT, '')}</td>
                    <td><input type="number" step="any" min="0" class="ni-usd" data-f="usd" placeholder="${u.valueThatDay != null ? Number(u.valueThatDay).toFixed(2) : ''}"></td>
                    <td><textarea data-f="note"></textarea></td>
                    <td style="white-space: nowrap;"><button type="button" data-save-evm>Save</button> <a href="${scanTx(u.chain, u.tx)}" target="_blank" rel="noopener" class="ni-mono">tx</a></td></tr>`;
                }).join('') + done.map(([tx, l]) => `<tr data-tx="${esc(tx)}"><td colspan="7" class="ni-mono">${esc(tx.slice(0, 18))}… <span class="ni-ok">✓ answered</span></td><td>${esc((EVM_IN.concat(EVM_OUT).find(k => k[0] === l.kind) || [l.kind, l.kind])[1])}</td><td class="amount-cell">${fmtUsd(l.usd)}</td><td style="font-size: 12px;">${esc(l.note || '')}</td><td><button type="button" data-clear-evm>Clear</button></td></tr>`).join('')
                    || `<tr><td colspan="11" style="text-align: center; color: var(--text-secondary);">Nothing to answer${yearSel.value ? ' in ' + yearSel.value : ''}.</td></tr>`;
                const notes = (evm.notes || []).filter(n => /not entered|Provisional|unknown/i.test(n));
                const unknown = (evm.holdings || []).filter(h => h.unknownCostQty > (h.asset === 'ETH' || h.asset === 'BNB' ? 0.00001 : 0.01));
                $('ni-evm-notes').innerHTML = (unknown.length ? `<div><strong>Held now with no known cost:</strong> ${unknown.map(h => `${fmtQty(h.unknownCostQty, dec(h.asset))} ${esc(h.asset)}`).join(', ')}. Answer the receipts above to give them a cost.</div>` : '') + notes.map(n => `<div>${esc(n)}</div>`).join('');
            };

            const renderAll = () => {
                renderOpt(); renderEvm();
                const n = (opt ? optItems().items.filter(x => !x.done).length + optItems().giftRows.filter(x => !x.done).length : 0) + (evm ? (evm.unclassified || []).filter(u => inYear(u.time)).length : 0);
                $('ni-total').innerHTML = opt && evm ? (n ? `<span class="ni-count">${n} to answer</span>` : '<span class="ni-ok" style="font-size: 14px;">✓ nothing to answer</span>') : '';
            };

            document.querySelector('.ni').addEventListener('click', async (ev) => {
                const b = ev.target.closest('button');
                if (!b) return;
                const tr = b.closest('tr');
                try {
                    if (b.hasAttribute('data-save-opt')) {
                        const f = fields(tr);
                        if (!f.kind) { $('ni-status').textContent = 'Choose what it was first.'; return; }
                        await saveOptLabel(tr, f);
                    } else if (b.hasAttribute('data-save-gift')) {
                        const f = fields(tr);
                        await saveOptLabel(tr, { kind: 'gift-in', usd: f.usd, date: f.date, note: f.note });
                    } else if (b.hasAttribute('data-save-evm')) {
                        const f = fields(tr);
                        if (!f.kind) { $('ni-status').textContent = 'Choose what it was first.'; return; }
                        const saved = await getJson('/api/titn/labels', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ tx: tr.dataset.tx, kind: f.kind, usd: f.usd === '' ? null : Number(f.usd), note: f.note }) });
                        evmLabels[saved.tx] = saved;
                        evm.unclassified = (evm.unclassified || []).filter(u => u.tx !== tr.dataset.tx);
                    } else if (b.hasAttribute('data-clear-evm')) {
                        await getJson('/api/titn/labels', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ tx: tr.dataset.tx, kind: null }) });
                        delete evmLabels[tr.dataset.tx];
                        $('ni-status').textContent = 'Cleared. Reload the page to see it listed again.';
                        renderAll();
                        return;
                    } else return;
                    $('ni-status').textContent = 'Saved. The Tax Report and Sales & Lots use it the next time they load.';
                    renderAll();
                } catch (e) { $('ni-status').textContent = 'Could not save: ' + e.message; }
            });
            yearSel.addEventListener('change', renderAll);
            $('ni-show-done').addEventListener('change', renderAll);

            (async () => {
                if (!W.listSaved().length) { $('ni-status').innerHTML = 'Save your wallets on <a href="/page/my-wallets">My Wallets</a> first.'; return; }
                $('ni-status').textContent = 'Reading your wallets. The first time can take a few minutes…';
                const errors = [];
                await Promise.all([
                    loadOpt().then(renderAll).catch(e => { opt = []; errors.push('OPT: ' + e.message); }),
                    loadEvm().then(renderAll).catch(e => { evm = { unclassified: [], notes: [] }; errors.push('Other crypto: ' + e.message); }),
                ]);
                renderAll();
                $('ni-status').textContent = errors.length ? 'Some wallets could not be read: ' + errors.join('; ') : '';
            })();
        })();
        </script>
        """;
}
