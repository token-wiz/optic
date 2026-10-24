public static partial class WebDashboard
{
    const string LiquidityReviewScript = """
        <script>
        (() => {
            const $ = (id) => document.getElementById(id);
            const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
            const num = (v) => v == null || v === '' ? null : Number(v);
            const fmtOpt = (v) => v == null ? '–' : Number(v).toLocaleString('en-US', { maximumFractionDigits: 2 });
            const fmtUsd = (v) => v == null ? '–' : (v < 0 ? '-$' : '$') + Math.abs(Number(v)).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
            const fmtPrice = (v) => v == null ? '–' : '$' + Number(v).toLocaleString('en-US', { maximumFractionDigits: 6 });
            const fmtPct = (v, d = 1) => v == null || !isFinite(v) ? '–' : (v * 100).toLocaleString('en-US', { maximumFractionDigits: d }) + '%';
            const fmtTime = (v) => v ? String(v).replace('T', ' ').replace(/\.\d+/, '').replace('Z', '').slice(0, 16) : '–';
            const short = (a) => a && a.length > 20 ? a.slice(0, 10) + '…' + a.slice(-6) : (a || '');
            const sum = (xs, f) => xs.reduce((s, x) => s + (Number(f(x)) || 0), 0);
            const api = async (url, opts = {}) => {
                const r = await fetch(url, opts);
                const text = await r.text();
                let body = null;
                try { body = text ? JSON.parse(text) : null; } catch { body = { error: text }; }
                if (!r.ok) {
                    const e = new Error(body?.errors ? body.errors.join('\n') : body?.error || ('HTTP ' + r.status));
                    e.body = body;
                    throw e;
                }
                return body;
            };
            const post = (url, data) => api(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data ?? {}) });
            const postText = (url, text) => api(url, { method: 'POST', headers: { 'Content-Type': 'text/csv' }, body: text });
            const msg = (el, text, ok) => { $(el).innerHTML = text ? `<div class="${ok ? 'lr-ok' : 'lr-err'}">${esc(text)}</div>` : ''; };

            const OUTCOME = {
                SupportedHistoricalEstimate: ['Supported estimate', 'ok'],
                PartialCapacity: ['Partial capacity', 'warn'],
                InsufficientData: ['Insufficient data', 'muted'],
                InsufficientDepthData: ['No depth data', 'muted'],
                VerifiedVenueUnavailable: ['Venue unavailable', 'bad'],
                NoExecutableRouteWithinVerifiedCoverage: ['No route in coverage', 'bad'],
                UnsupportedPoolType: ['Unsupported pool', 'muted'],
                ConflictingEvidence: ['Conflicting evidence', 'warn'],
            };
            const METHOD = { AcceptReference: 'Reference value', HistoricalExecutionEvidence: 'Modeled sale proceeds', ObservedTransaction: 'Observed transaction', IndependentAppraisal: 'Independent appraisal', Other: 'Other', AlternativeMarketPrice: 'Alternative market price', TimingApproximation: 'Timing approximation', ImportedValuation: 'Imported valuation' };
            const methodText = (m) => METHOD[m] || m;
            const badge = (text, cls, title) => `<span class="lr-badge ${cls}"${title ? ` title="${esc(title)}"` : ''}>${esc(text)}</span>`;

            // ------------------------------------------------------------ state
            const isPhone = () => window.matchMedia('(max-width: 640px)').matches;
            const pageSize = () => isPhone() ? 25 : 200;
            const state = { address: '', from: '', to: '', rows: [], shown: 200, selected: new Set(), reviews: null, data: null };
            const form = $('lr-form'), addressInput = $('lr-address'), yearSelect = $('lr-year');
            const thisYear = new Date().getUTCFullYear();
            yearSelect.innerHTML = Array.from({ length: thisYear - 2023 }, (_, i) => thisYear - i).map(y => `<option value="${y}">${y}</option>`).join('') + '<option value="custom">Custom…</option>';
            yearSelect.value = String(thisYear - 1);
            const params = new URLSearchParams(location.search);
            addressInput.value = params.get('address') || window.opticWallets?.primary() || '';
            if (params.get('year')) yearSelect.value = params.get('year');
            yearSelect.addEventListener('change', () => document.querySelectorAll('.lr-custom').forEach(el => el.hidden = yearSelect.value !== 'custom'));

            const period = () => {
                if (yearSelect.value === 'custom') return { from: $('lr-from').value, to: $('lr-to').value };
                return { from: yearSelect.value + '-01-01', to: yearSelect.value + '-12-31' };
            };

            // ------------------------------------------------------------ tabs
            const tabs = [...document.querySelectorAll('.lr-tab')];
            const showTab = (name) => {
                for (const t of tabs) t.setAttribute('aria-selected', String(t.dataset.tab === name));
                for (const p of document.querySelectorAll('.lr-panel')) p.hidden = p.id !== 'lr-tab-' + name;
                const loaders = { network: loadNetwork, markets: loadMarkets, approvals: loadApprovals };
                loaders[name]?.();
                try { localStorage.setItem('lr-tab', name); } catch { }
            };
            tabs.forEach(t => t.addEventListener('click', () => showTab(t.dataset.tab)));

            // ------------------------------------------------------------ receipts
            const load = async () => {
                const address = addressInput.value.trim().toLowerCase();
                const { from, to } = period();
                if (!from || !to) { $('lr-status').textContent = 'Choose a period.'; return; }
                Object.assign(state, { address, from, to, shown: pageSize() });
                state.selected.clear();
                $('lr-load').disabled = true;
                $('lr-status').textContent = 'Loading receipts from the chain…';
                try {
                    state.data = await api(`/api/valuation/receipts?address=${encodeURIComponent(address)}&from=${from}&to=${to}`);
                    state.rows = state.data.receipts;
                    $('lr-status').innerHTML = `${state.rows.length.toLocaleString('en-US')} receipts from ${esc(from)} to ${esc(to)}. Times are UTC; daily summaries use ${esc(state.data.reportingTimeZone)}.`;
                    $('lr-tiles').hidden = false; $('lr-tabs').hidden = false;
                    renderTiles(); renderRule(); renderReceipts(); renderReceiptCharts();
                    $('lr-exp-receipts').href = `/api/valuation/export/receipts.csv?address=${encodeURIComponent(address)}&from=${from}&to=${to}`;
                    for (const f of [$('lr-scen-form'), $('lr-an-form')]) { f.from.value = from; f.to.value = to; }
                    let tab = 'receipts'; try { tab = localStorage.getItem('lr-tab') || 'receipts'; } catch { }
                    showTab(tab);
                    history.replaceState(null, '', `?address=${encodeURIComponent(address)}&year=${encodeURIComponent(yearSelect.value)}`);
                } catch (e) {
                    $('lr-status').innerHTML = `<span class="lr-err">${esc(e.message)}</span>`;
                } finally {
                    $('lr-load').disabled = false;
                }
            };
            form.addEventListener('submit', (e) => { e.preventDefault(); load(); });

            const renderTiles = () => {
                const rows = state.rows, c = state.data.coverage;
                const node = rows.filter(r => r.receipt.category === 'Node'), stk = rows.filter(r => r.receipt.category === 'Staking');
                const refKnown = rows.filter(r => r.receipt.reference.valueUsd != null);
                const analyzed = rows.filter(r => r.analysis);
                const unconfirmed = rows.filter(r => r.receipt.classification !== 'Confirmed').length;
                const tile = (k, v, s) => `<div class="lr-tile"><div class="k">${k}</div><div class="v">${v}</div><div class="s">${s || ''}</div></div>`;
                $('lr-tiles').innerHTML =
                    tile('Receipts', rows.length.toLocaleString('en-US'), `${node.length} node · ${stk.length} staking`) +
                    tile('OPT received', fmtOpt(sum(rows, r => r.receipt.quantityOpt)), `${fmtOpt(sum(node, r => r.receipt.quantityOpt))} node · ${fmtOpt(sum(stk, r => r.receipt.quantityOpt))} staking`) +
                    tile('Reference value', fmtUsd(sum(refKnown, r => r.receipt.reference.valueUsd)), refKnown.length < rows.length ? `${rows.length - refKnown.length} unpriced (not zero)` : 'CoinMarketCap close') +
                    tile('Market analysis', `${analyzed.length} / ${rows.length}`, analyzed.length ? `${analyzed.filter(r => r.analysis.outcome === 'SupportedHistoricalEstimate').length} with a supported estimate` : 'Not run yet') +
                    tile('Approved values', `${c.resolved} / ${c.receipts}`, `${fmtUsd(c.knownValueUsd)} known · ${fmtOpt(c.unvaluedOpt)} OPT unvalued`) +
                    tile('Report status', c.unresolved === 0 && c.receipts > 0 ? badge('Final', 'ok') : badge('Draft', 'warn'), unconfirmed ? `${unconfirmed} classifications to confirm` : 'All classifications confirmed');
            };

            // The payout-size rule: confirmed by the user once, then applied to every day it decides.
            const renderRule = () => {
                const rule = state.data.classificationRule;
                const dist = state.rows.filter(r => r.receipt.sender && !/^Paid by the distribution module/.test(r.receipt.classificationBasis || ''));
                const bySplit = dist.filter(r => /^Smaller same-day/.test(r.receipt.classificationBasis || ''));
                // Payouts the rule cannot decide (single or same-size days) and the user has not labelled.
                const open = dist.filter(r => r.receipt.classification !== 'Confirmed' && !/^Smaller same-day/.test(r.receipt.classificationBasis || ''));
                const single = open.filter(r => /^Only payout/.test(r.receipt.classificationBasis || '')).length;
                const equal = open.filter(r => /^Same-size/.test(r.receipt.classificationBasis || '')).length;
                $('lr-rule').innerHTML = `<h3>Node or staking</h3>
                    <p class="lr-note">The chain does not label payouts. Rule: on a day with more than one payout from the distribution account, the smaller payout(s) are staking/participation rewards and the larger are node rewards (used when the smaller is at most 85% of the larger). ${bySplit.length} of ${dist.length} payouts in this period are decided by it.</p>
                    ${rule ? `<p class="lr-ok">Rule confirmed by ${esc(rule.confirmedBy)} on ${esc(fmtTime(rule.confirmedUtc))} UTC. <button type="button" class="lr-small" id="lr-rule-revoke">Revoke</button></p>`
                           : `<p><button type="button" class="btn-quiet" id="lr-rule-confirm">Confirm this rule for my payouts</button> <span class="lr-note">Until confirmed, these labels count as inferred.</span></p>`}
                    ${open.length ? `<p class="lr-note"><strong>${open.length} payout(s) still need you to decide:</strong> ${single} on days with a single payout and ${equal} on days with payouts of about the same size. The rule cannot tell these apart. Filter <em>Classification: Not confirmed</em>, select them and use <em>Confirm as node</em> or <em>Confirm as staking</em>.</p>` : ''}`;
                $('lr-rule-confirm')?.addEventListener('click', async () => {
                    if (!confirm('Confirm: on a day with more than one payout from the distribution account, the smaller payout(s) are staking/participation rewards and the larger are node rewards. This is recorded with your name and the date, and you can revoke it.')) return;
                    try { await post('/api/valuation/receipts/rule', { confirm: true }); await load(); } catch (e) { alert(e.message); }
                });
                $('lr-rule-revoke')?.addEventListener('click', async () => {
                    if (!confirm('Revoke the rule? Rule-based labels go back to inferred; labels you set one by one are kept.')) return;
                    try { await api('/api/valuation/receipts/rule', { method: 'DELETE' }); await load(); } catch (e) { alert(e.message); }
                });
            };

            const reviewState = (r) => r.selected ? 'approved' : r.proposedReviews?.length ? 'proposed' : 'none';
            const outcomeGroup = (o) => !o ? 'none' : o === 'SupportedHistoricalEstimate' || o === 'PartialCapacity' ? o : o === 'VerifiedVenueUnavailable' || o === 'NoExecutableRouteWithinVerifiedCoverage' ? 'blocked' : 'insufficient';
            const filtered = () => {
                const cat = $('lr-f-cat').value, cls = $('lr-f-cls').value, rev = $('lr-f-rev').value, out = $('lr-f-out').value;
                return state.rows.filter(r =>
                    (!cat || r.receipt.category === cat) &&
                    (!cls || (cls === 'Confirmed' ? r.receipt.classification === 'Confirmed' : r.receipt.classification !== 'Confirmed')) &&
                    (!rev || reviewState(r) === rev) &&
                    (!out || outcomeGroup(r.analysis?.outcome) === out));
            };
            for (const id of ['lr-f-cat', 'lr-f-cls', 'lr-f-rev', 'lr-f-out']) $(id).addEventListener('change', () => { state.shown = pageSize(); renderReceipts(); });

            const networkCell = (n) => {
                const x = n.node.optPerDay != null ? n.node : n.combined.optPerDay != null ? n.combined : null;
                if (!x) return `<span class="lr-sub" title="${esc(n.node.missingReason || '')}">none recorded</span>`;
                const label = x === n.node ? 'node' : 'node + staking';
                return `${fmtOpt(x.optPerDay)}<span class="lr-sub">${label}, ${esc(x.basis)} · ${esc(x.verification)}</span>`;
            };

            const renderReceipts = () => {
                const rows = filtered();
                $('lr-count').textContent = `${rows.length.toLocaleString('en-US')} shown`;
                $('lr-body').innerHTML = rows.slice(0, state.shown).map(r => {
                    const x = r.receipt, a = r.analysis, s = r.selected;
                    const o = a ? OUTCOME[a.outcome] || [a.outcome, 'muted'] : null;
                    const cls = x.classification === 'Confirmed' ? '' : ` <span class="lr-badge warn" title="Inferred from payout size; confirm it">${x.classification === 'Uncertain' ? 'uncertain' : 'inferred'}</span>`;
                    const warn = [];
                    if (x.reference.valueUsd == null) warn.push('no reference price');
                    if (a?.usdIncomplete) warn.push('USD conversion missing');
                    if (x.classification !== 'Confirmed') warn.push('classification not confirmed');
                    return `<tr data-id="${esc(x.id)}">
                        <td class="lr-check"><input type="checkbox" class="lr-sel" aria-label="Select" ${state.selected.has(x.id) ? 'checked' : ''}></td>
                        <td class="lr-when" style="white-space: nowrap;"><button type="button" class="lr-small lr-open" title="Details">▸</button> ${esc(fmtTime(x.timestampUtc))}<span class="lr-sub">${esc(r.reportingDay)} local</span></td>
                        <td title="${esc(x.classificationBasis || '')}">${esc(x.category)}${cls}</td>
                        <td class="num">${fmtOpt(x.quantityOpt)}</td>
                        <td class="address-cell lr-hide-m" title="${esc(x.wallet)}">${esc(short(x.wallet))}</td>
                        <td class="num sep">${fmtUsd(x.reference.valueUsd)}<span class="lr-sub">${fmtPrice(x.reference.unitPriceUsd)}/OPT</span></td>
                        <td class="lr-hide-m"><span class="lr-sub" style="display: inline;">${esc(x.reference.source ? `${x.reference.source.provider} ${x.reference.source.interval?.startsWith('close of the 1-hour') ? '1h' : '1d'}` : x.reference.status)}</span></td>
                        <td class="sep">${o ? badge(o[0], o[1]) : '<span class="lr-sub">not analyzed</span>'}</td>
                        <td class="num${a ? '' : ' lr-hide-m'}">${a ? (a.modeledOutputUsd != null ? fmtUsd(a.modeledOutputUsd) : a.modeledOutputQuote != null ? fmtOpt(a.modeledOutputQuote) + ' ' + esc(a.quoteAsset) : '–') : '–'}</td>
                        <td class="num${a ? '' : ' lr-hide-m'}">${a ? `${fmtPct(a.modeledFillRatio, 0)}<span class="lr-sub">${fmtOpt(a.residualOpt)} OPT left</span>` : '–'}</td>
                        <td class="num sep">${networkCell(r.network)}</td>
                        <td class="num sep">${s ? fmtUsd(s.valueUsd) : '–'}</td>
                        <td>${s ? badge('Approved', 'ok', `${methodText(s.method)} · ${s.reviewer}`) : r.proposedReviews?.length ? badge('Proposed', 'warn') : badge('Not reviewed', 'muted')}${warn.length ? `<span class="lr-sub">${esc(warn.join(' · '))}</span>` : ''}</td>
                    </tr>`;
                }).join('') || '<tr><td colspan="13" style="text-align: center; color: var(--text-secondary);">No receipts match.</td></tr>';
                $('lr-more').hidden = rows.length <= state.shown;
                $('lr-more').textContent = `Show more (${Math.min(rows.length, state.shown)} of ${rows.length})`;
                $('lr-all').checked = rows.length > 0 && rows.slice(0, state.shown).every(r => state.selected.has(r.receipt.id));
                renderSelection();
            };
            $('lr-more').addEventListener('click', () => { state.shown += isPhone() ? 25 : 300; renderReceipts(); });

            const detailHtml = (r) => {
                const x = r.receipt, a = r.analysis, src = x.reference.source;
                const list = (xs) => xs?.length ? `<ul>${xs.map(t => `<li>${esc(t)}</li>`).join('')}</ul>` : '–';
                return `<tr class="lr-detail"><td></td><td colspan="12"><div class="lr-grid2">
                    <dl>
                        <dt>Receipt id</dt><dd>${esc(x.id)} <span class="lr-sub">identity: ${esc(x.identityBasis)}</span></dd>
                        <dt>Transaction</dt><dd>${esc(x.txHash || 'none (source row ' + x.sourceRow + ' of ' + x.sourceFile + ')')}${x.eventIndex != null ? ` · event ${x.eventIndex}.${x.ordinal}` : ''}</dd>
                        <dt>Quantity</dt><dd>${esc(x.baseUnits)} ${esc(x.denom)} (${x.decimals} decimals)</dd>
                        <dt>From</dt><dd>${esc(x.sender || '–')}</dd>
                        <dt>Node or staking</dt><dd>${esc(x.category)} · ${esc(x.classification)} · ${esc(x.classificationBasis || '')}</dd>
                        <dt>Reference price</dt><dd>${src ? `${esc(src.provider)} · ${esc(src.field)} · ${esc(src.interval)} · candle ${esc(fmtTime(src.observedUtc))} UTC · ${esc(src.quoteCurrency)}${src.fallback ? ' · ' + esc(src.fallback) : ''}<span class="lr-sub">${esc(src.endpoint || '')}</span>` : 'No reference price (left unvalued, not zero)'}</dd>
                        <dt>Network that day</dt><dd>Node: ${r.network.node.optPerDay != null ? fmtOpt(r.network.node.optPerDay) + ' (' + esc(r.network.node.basis) + ')' : esc(r.network.node.missingReason)}<br>Combined: ${r.network.combined.optPerDay != null ? fmtOpt(r.network.combined.optPerDay) + ' (' + esc(r.network.combined.basis) + ')' : esc(r.network.combined.missingReason)}</dd>
                        <dt>Approved value</dt><dd>${r.selected ? `${fmtUsd(r.selected.valueUsd)} · ${esc(methodText(r.selected.method))} · review ${esc(r.selected.reviewId.slice(0, 8))} v${r.selected.version} by ${esc(r.selected.reviewer)} on ${esc(fmtTime(r.selected.reviewedUtc))}` : 'None'}</dd>
                    </dl>
                    <dl>${a ? `
                        <dt>Outcome</dt><dd>${esc(a.outcome)} · independent counterfactual</dd>
                        <dt>Requested / filled</dt><dd>${fmtOpt(a.requestedOpt)} / ${fmtOpt(a.executableOpt)} OPT · residual ${fmtOpt(a.residualOpt)}</dd>
                        <dt>Modeled output</dt><dd>${a.modeledOutputQuote != null ? fmtOpt(a.modeledOutputQuote) + ' ' + esc(a.quoteAsset) : '–'} · USD ${a.modeledOutputUsd != null ? fmtUsd(a.modeledOutputUsd) : 'not converted'}</dd>
                        <dt>Execution price</dt><dd>${fmtPrice(a.effectiveExecutionPriceUsd)} per filled OPT</dd>
                        <dt>Execution shortfall</dt><dd>${a.executionShortfall != null ? fmtPct(a.executionShortfall) + ' (modeled, not a tax discount)' : '–'}</dd>
                        <dt>Net of costs</dt><dd>${a.estimatedTransactionCostsUsd != null ? `${fmtUsd(a.estimatedNetWalletProceedsUsd)} after ${fmtUsd(a.estimatedTransactionCostsUsd)} costs (shown separately)` : '–'}</dd>
                        <dt>Capacity</dt><dd>${(a.capacity || []).map(c => `${fmtPct(c.threshold, 0)}: ${c.capacityOpt != null ? fmtOpt(c.capacityOpt) + ' OPT' : 'unknown'}`).join(' · ') || '–'}</dd>
                        <dt>Routes</dt><dd>${list(a.routes)}</dd>
                        <dt>Snapshots</dt><dd>${list(a.snapshotIds)}</dd>
                        <dt>Assumptions</dt><dd>${list(a.assumptions)}</dd>
                        <dt>Notes</dt><dd>${list(a.notes)}</dd>` : '<dt>Analysis</dt><dd>Not run. Select the receipt and choose “Analyze market capacity”.</dd>'}
                    </dl></div></td></tr>`;
            };

            $('lr-body').addEventListener('click', (e) => {
                const tr = e.target.closest('tr[data-id]');
                if (!tr) return;
                if (e.target.classList.contains('lr-open')) {
                    const next = tr.nextElementSibling;
                    if (next?.classList.contains('lr-detail')) { next.remove(); e.target.textContent = '▸'; return; }
                    const r = state.rows.find(x => x.receipt.id === tr.dataset.id);
                    tr.insertAdjacentHTML('afterend', detailHtml(r));
                    e.target.textContent = '▾';
                }
            });
            $('lr-body').addEventListener('change', (e) => {
                if (!e.target.classList.contains('lr-sel')) return;
                const id = e.target.closest('tr').dataset.id;
                if (e.target.checked) state.selected.add(id); else state.selected.delete(id);
                renderSelection();
            });
            $('lr-all').addEventListener('change', (e) => {
                for (const r of filtered().slice(0, state.shown)) e.target.checked ? state.selected.add(r.receipt.id) : state.selected.delete(r.receipt.id);
                renderReceipts();
            });
            const renderSelection = () => {
                const n = state.selected.size;
                $('lr-actions').hidden = n === 0;
                const rows = state.rows.filter(r => state.selected.has(r.receipt.id));
                $('lr-sel-count').textContent = `${n} selected · ${fmtOpt(sum(rows, r => r.receipt.quantityOpt))} OPT · reference ${fmtUsd(sum(rows, r => r.receipt.reference.valueUsd))}`;
            };
            $('lr-actions').addEventListener('click', async (e) => {
                const act = e.target.dataset.act;
                if (!act) return;
                const ids = [...state.selected];
                if (act === 'clear') { state.selected.clear(); renderReceipts(); return; }
                if (act === 'node' || act === 'staking') {
                    await post('/api/valuation/receipts/classify', { receiptIds: ids, category: act, status: 'Confirmed' });
                    for (const r of state.rows) if (state.selected.has(r.receipt.id)) { r.receipt.category = act === 'node' ? 'Node' : 'Staking'; r.receipt.classification = 'Confirmed'; }
                    renderTiles(); renderReceipts();
                    return;
                }
                if (act === 'analyze') {
                    const rows = state.rows.filter(r => state.selected.has(r.receipt.id));
                    const days = rows.map(r => r.receipt.timestampUtc.slice(0, 10)).sort();
                    showTab('scenarios');
                    await runAnalysis({ from: days[0], to: days[days.length - 1], receiptIds: ids });
                    return;
                }
                if (act === 'review') openReview(ids);
            });

            // ------------------------------------------------------------ charts (plain SVG)
            const svgBars = (el, days, series, opts = {}) => {
                const W = Math.max(320, Math.min(900, $(el).clientWidth || 900)), H = W < 600 ? 220 : 240, L = W < 600 ? 52 : 64, R = 12, T = 12, B = 30;
                if (!days.length) { $(el).innerHTML = '<p class="lr-note">No data in this period.</p>'; return; }
                const max = Math.max(1, ...series.flatMap(s => s.values.filter(v => v != null)));
                const y = (v) => T + (H - T - B) * (1 - v / max);
                const slot = (W - L - R) / days.length;
                let out = `<svg class="lr-chart" viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(opts.label || '')}">`;
                for (let i = 0; i <= 4; i++) { const v = max * i / 4; out += `<line x1="${L}" x2="${W - R}" y1="${y(v)}" y2="${y(v)}" stroke="var(--border)"/><text x="${L - 6}" y="${y(v) + 4}" font-size="11" text-anchor="end" fill="var(--text-muted)">${esc(opts.fmt ? opts.fmt(v) : fmtOpt(v))}</text>`; }
                (opts.missing || []).forEach((m, i) => { if (m) out += `<rect x="${L + i * slot}" y="${T}" width="${slot}" height="${H - T - B}" fill="var(--text-muted)" opacity="0.08"/>`; });
                series.filter(s => s.type === 'bar').forEach((s, si, bars) => {
                    const bw = Math.max(1, (slot * 0.8) / bars.length);
                    s.values.forEach((v, i) => { if (v != null && v > 0) out += `<rect x="${L + i * slot + slot * 0.1 + si * bw}" y="${y(v)}" width="${bw}" height="${H - B - y(v)}" fill="${s.color}"><title>${esc(days[i])}: ${esc(s.name)} ${esc(opts.fmt ? opts.fmt(v) : fmtOpt(v))}</title></rect>`; });
                });
                for (const s of series.filter(s => s.type === 'line')) {
                    let path = '', pen = false;
                    s.values.forEach((v, i) => { if (v == null) { pen = false; return; } path += `${pen ? 'L' : 'M'}${L + (i + 0.5) * slot},${y(v)} `; pen = true; });
                    if (path) out += `<path d="${path}" fill="none" stroke="${s.color}" stroke-width="2"${s.dash ? ' stroke-dasharray="5 4"' : ''}/>`;
                }
                const step = Math.max(1, Math.ceil(days.length / (W < 600 ? 4 : 10)));
                days.forEach((d, i) => { if (i % step === 0) out += `<text x="${L + i * slot}" y="${H - 10}" font-size="11" fill="var(--text-muted)">${esc(W < 600 && d.length > 7 ? d.slice(5) : d)}</text>`; });
                out += '</svg><div class="lr-legend">' + series.map(s => `<span><i style="background: ${s.color}"></i>${esc(s.name)}</span>`).join('') + (opts.missing?.some(Boolean) ? '<span><i style="background: var(--text-muted); opacity: .3"></i>No data</span>' : '') + '</div>';
                if (opts.note) out += `<p class="lr-note">${esc(opts.note)}</p>`;
                $(el).innerHTML = out;
            };
            const dayRange = (from, to) => { const out = []; for (let d = new Date(from + 'T00:00:00Z'); d <= new Date(to + 'T00:00:00Z'); d.setUTCDate(d.getUTCDate() + 1)) out.push(d.toISOString().slice(0, 10)); return out; };

            const renderReceiptCharts = () => {
                const days = dayRange(state.from, state.to);
                const by = new Map(days.map(d => [d, { opt: 0, cap: null, analyzed: false }]));
                for (const r of state.rows) {
                    const d = by.get(r.utcDay); if (!d) continue;
                    d.opt += r.receipt.quantityOpt;
                    const c = r.analysis?.capacity?.find(c => Math.abs(c.threshold - 0.05) < 1e-9);
                    if (r.analysis) d.analyzed = true;
                    if (c?.capacityOpt != null) d.cap = Math.max(d.cap ?? 0, c.capacityOpt);
                }
                const xs = days.map(d => by.get(d));
                svgBars('lr-chart-capacity', days, [
                    { name: 'OPT received', type: 'bar', color: 'var(--accent-primary)', values: xs.map(x => x.opt || null) },
                    { name: 'Capacity at 5% (largest single-receipt estimate)', type: 'line', color: 'var(--warning)', values: xs.map(x => x.cap) },
                ], { missing: xs.map(x => x.opt > 0 && !x.analyzed), label: 'Receipts vs capacity' });

                const months = [...new Set(state.rows.map(r => r.utcDay.slice(0, 7)))].sort();
                const mo = months.map(m => {
                    const rs = state.rows.filter(r => r.utcDay.startsWith(m));
                    const an = rs.filter(r => r.analysis?.modeledOutputUsd != null);
                    const sel = rs.filter(r => r.selected);
                    return { ref: sum(rs, r => r.receipt.reference.valueUsd) || null, model: an.length ? sum(an, r => r.analysis.modeledOutputUsd) : null, sel: sel.length ? sum(sel, r => r.selected.valueUsd) : null };
                });
                svgBars('lr-chart-values', months, [
                    { name: 'Reference value', type: 'bar', color: 'var(--accent-primary)', values: mo.map(x => x.ref) },
                    { name: 'Modeled output (analyzed only)', type: 'bar', color: 'var(--warning)', values: mo.map(x => x.model) },
                    { name: 'Approved value', type: 'bar', color: 'var(--success, #107c10)', values: mo.map(x => x.sel) },
                ], { fmt: (v) => '$' + Number(v).toLocaleString('en-US', { maximumFractionDigits: 0 }), label: 'Valuation comparison', note: 'Modeled output covers only analyzed receipts, and each is an independent estimate, so the monthly sum is not an amount that could have been realized all at once.' });
            };

            // ------------------------------------------------------------ network
            const loadNetwork = async () => {
                try {
                    const rows = await api('/api/valuation/distributions');
                    rows.sort((a, b) => String(a.period.effectiveFrom).localeCompare(String(b.period.effectiveFrom)));
                    const daily = rows.filter(r => r.period.origin === 'chain');
                    const other = rows.filter(r => r.period.origin !== 'chain');
                    const line = (r) => { const p = r.period; return `<tr><td>${esc(p.effectiveFrom)}</td><td>${esc(p.effectiveTo)}</td><td>${esc(p.category)}</td><td class="num">${fmtOpt(p.scheduledOptPerDay)}</td><td class="num">${fmtOpt(p.observedOptPerDay)}</td><td class="num">${fmtOpt(p.userAssumedOptPerDay)}</td><td class="num">${fmtOpt(p.emittedOptPerDay)}${p.emissionVerification ? `<span class="lr-sub">${esc(p.emissionVerification)}</span>` : ''}</td><td>${badge(p.verification, p.verification === 'Verified' || p.verification === 'Observed' ? 'ok' : p.verification === 'Conflicting' ? 'bad' : 'warn')}</td><td>${esc(p.origin)}</td><td>${r.owner === 'system' ? 'Everyone' : 'Me'}</td><td class="lr-wide" style="max-width: 320px; font-size: 12px;">${esc(p.notes || '')}${(p.evidence || []).filter(e => e.url).map(e => ` <a href="${esc(e.url)}" target="_blank" rel="noopener">source</a>`).join('')}</td><td class="lr-no-print"><button type="button" class="lr-small" data-del-dist="${esc(p.id)}">Remove</button></td></tr>`; };
                    $('lr-dist-body').innerHTML = other.map(line).join('') +
                        (daily.length ? `<tr><td colspan="12" class="lr-sub">Plus ${daily.length} observed daily totals from the chain (${esc(daily[0].period.effectiveFrom)} to ${esc(daily[daily.length - 1].period.effectiveTo)}), Combined, UTC days.</td></tr>` : '') ||
                        '<tr><td colspan="12" style="text-align: center; color: var(--text-secondary);">No periods recorded. Network figures stay unknown until you add them.</td></tr>';
                    if (state.from) {
                        const s = await api(`/api/valuation/scenarios?address=${encodeURIComponent(state.address)}&from=${state.from}&to=${state.to}`);
                        const days = s.daily.map(d => d.date);
                        const amount = s.daily.map(d => d.node.optPerDay ?? null);
                        const combined = s.daily.map(d => d.combined.optPerDay ?? null);
                        svgBars('lr-chart-network', days, [
                            { name: 'Node distribution (selected basis)', type: 'line', color: 'var(--accent-primary)', values: amount },
                            { name: 'Combined observed (node + staking)', type: 'line', color: 'var(--text-secondary)', values: combined, dash: true },
                            { name: 'Emitted (minted)', type: 'line', color: 'var(--success, #107c10)', values: s.daily.map(d => d.emission?.optPerDay ?? null), dash: true },
                            { name: '10% hypothetical selling', type: 'line', color: 'var(--error)', values: amount.map(v => v == null ? null : v * 0.10), dash: true },
                            { name: '1% hypothetical selling', type: 'line', color: 'var(--warning)', values: amount.map(v => v == null ? null : v * 0.01), dash: true },
                        ], { missing: amount.map((v, i) => v == null && combined[i] == null), label: 'Network vs hypothetical selling' });
                    }
                } catch (e) { msg('lr-import-msg', e.message); }
            };
            $('lr-dist-body').addEventListener('click', async (e) => {
                const id = e.target.dataset.delDist; if (!id) return;
                if (!confirm('Remove this period? It is kept in history.')) return;
                try { await api('/api/valuation/distributions/' + encodeURIComponent(id), { method: 'DELETE' }); loadNetwork(); } catch (err) { msg('lr-import-msg', err.message); }
            });
            $('lr-dist-form').addEventListener('submit', async (e) => {
                e.preventDefault();
                const f = e.target;
                const p = { effectiveFrom: f.effectiveFrom.value, effectiveTo: f.effectiveTo.value, category: f.category.value, verification: f.verification.value, notes: f.notes.value || null, evidence: [] };
                p[f.basis.value] = Number(f.amount.value);
                if (f.basis.value === 'emittedOptPerDay') { p.emissionVerification = f.verification.value; if (p.category !== 'Combined') p.category = 'Combined'; }
                if (/^https?:\/\//.test(f.notes.value.trim())) p.evidence.push({ kind: 'url', url: f.notes.value.trim(), description: 'Source given when entered', userReported: true });
                try { await post('/api/valuation/distributions?scope=' + f.scope.value, p); msg('lr-dist-msg', 'Period added.', true); f.reset(); loadNetwork(); }
                catch (err) { msg('lr-dist-msg', err.message); }
            });
            const readFile = (input) => new Promise((res, rej) => { const file = $(input).files[0]; if (!file) return rej(new Error('Choose a file first.')); const r = new FileReader(); r.onload = () => res({ text: r.result, name: file.name }); r.onerror = () => rej(r.error); r.readAsText(file); });
            $('lr-dist-import').addEventListener('click', async () => {
                try { const f = await readFile('lr-dist-file'); const r = await postText(`/api/valuation/distributions/import?scope=${$('lr-dist-scope').value}&name=${encodeURIComponent(f.name)}`, f.text); msg('lr-import-msg', `Imported ${r.imported} period(s).`, true); loadNetwork(); }
                catch (e) { msg('lr-import-msg', e.message); }
            });
            $('lr-dist-chain').addEventListener('click', async () => {
                try { const r = await post('/api/valuation/distributions/from-chain'); msg('lr-import-msg', `Recorded ${r.imported} observed daily totals. ${r.note}`, true); loadNetwork(); }
                catch (e) { msg('lr-import-msg', e.message); }
            });
            $('lr-supply-chain').addEventListener('click', async () => {
                try { const r = await post('/api/valuation/supply/from-chain'); msg('lr-import-msg', `Recorded ${r.imported} daily supply observations.`, true); }
                catch (e) { msg('lr-import-msg', e.message); }
            });

            // ------------------------------------------------------------ markets
            const loadMarkets = async () => {
                try {
                    const [profile, venues, snaps, valid] = await Promise.all([api('/api/valuation/profile'), api('/api/valuation/venues'), api('/api/valuation/snapshots'), api('/api/valuation/osmosis/validations')]);
                    const pf = $('lr-profile-form');
                    pf.jurisdiction.value = profile.jurisdiction || '';
                    pf.accountsAt.value = (profile.accountsAt || []).join(', ');
                    pf.custodyNote.value = profile.custodyNote || '';
                    $('lr-venue-body').innerHTML = venues.map(v => { const s = v.status; return `<tr><td>${esc(s.venue)}</td><td>${esc(s.kind)}</td><td>${badge(s.state, s.state === 'Available' ? 'ok' : s.state === 'Unknown' ? 'muted' : 'bad')}</td><td>${esc(fmtTime(s.effectiveFromUtc))}${s.implementationVerified ? '' : '<span class="lr-sub">time not verified</span>'}</td><td>${esc(fmtTime(s.effectiveToUtc))}</td><td>${esc(s.jurisdiction || 'all')}</td><td>${esc(s.verification)}</td><td><button type="button" class="lr-small" data-del-venue="${esc(s.id)}">Remove</button></td></tr>`; }).join('')
                        || '<tr><td colspan="8" style="text-align: center; color: var(--text-secondary);">No access history recorded. Without it, every venue counts as unknown access.</td></tr>';
                    $('lr-snap-body').innerHTML = snaps.osmosisPools.map(p => `<tr><td>Osmosis pool</td><td>${esc(p.poolId)} <span class="lr-sub">${esc(p.poolType)}</span></td><td>${esc(fmtTime(p.blockTimeUtc))}</td><td>${esc(p.servedHeight)}</td><td>${p.exists ? `${p.ticks} ticks · taker fee ${esc(p.takerFee ?? 'unknown')}` : 'did not exist yet'}</td><td class="lr-wide" style="font-size: 12px; max-width: 280px;">${esc((p.problems || []).join(' '))}</td><td>${p.owner === 'system' ? 'Everyone' : 'Me'}</td><td><button type="button" class="lr-small" data-del-snap="osmosis-pool|${esc(p.id)}">Remove</button></td></tr>`).join('')
                        + snaps.orderBooks.map(b => `<tr><td>Order book</td><td>${esc(b.venue)} ${esc(b.market)} <span class="lr-sub">${esc(b.quoteAsset)}</span></td><td>${esc(fmtTime(b.observedUtc))}</td><td>–</td><td>${b.levels} bid levels · ${fmtOpt(b.bidDepthOpt)} OPT${b.truncated ? ' · truncated' : ''}</td><td></td><td>${b.owner === 'system' ? 'Everyone' : 'Me'}</td><td><button type="button" class="lr-small" data-del-snap="orderbook|${esc(b.id)}">Remove</button></td></tr>`).join('')
                        + snaps.conversions.map(c => `<tr><td>Conversion</td><td>${esc(c.asset)} → USD</td><td>${esc(c.from)} – ${esc(c.to)}</td><td>–</td><td>${c.days} days</td><td style="font-size: 12px;">${esc(c.source)}</td><td></td><td></td></tr>`).join('')
                        || '<tr><td colspan="8" style="text-align: center; color: var(--text-secondary);">No market snapshots yet.</td></tr>';
                    $('lr-valid-body').innerHTML = valid.map(v => `<tr><td class="address-cell" title="${esc(v.txHash)}">${esc(short(v.txHash))}</td><td>${esc(v.poolId)}</td><td class="num">${esc(v.amountIn)}</td><td class="num">${esc(v.observedOut)}</td><td class="num">${esc(v.modeledOut ?? '–')}</td><td class="num">${v.deviation != null ? fmtPct(v.deviation, 4) : '–'}</td><td class="lr-wide" style="font-size: 12px; max-width: 320px;">${esc((v.notes || []).join(' '))}</td></tr>`).join('')
                        || '<tr><td colspan="7" style="text-align: center; color: var(--text-secondary);">No swaps replayed yet.</td></tr>';
                    renderCoverage(snaps);
                } catch (e) { msg('lr-osmo-msg', e.message); }
            };
            const renderCoverage = (snaps) => {
                if (!state.from) return;
                const days = dayRange(state.from, state.to);
                const rowsDef = [
                    ['Your receipts', d => state.rows.some(r => r.utcDay === d), 'var(--accent-primary)'],
                    ['Osmosis pool state', d => snaps.osmosisPools.some(p => p.exists && String(p.blockTimeUtc || '').startsWith(d)), 'var(--success, #107c10)'],
                    ['Exchange order books', d => snaps.orderBooks.some(b => String(b.observedUtc).startsWith(d)), 'var(--warning)'],
                    ['USD conversions', d => snaps.conversions.some(c => c.from <= d && c.to >= d), 'var(--text-secondary)'],
                ];
                const W = Math.max(320, Math.min(900, $('lr-chart-coverage').clientWidth || 900)), rowH = 26, L = W < 600 ? 112 : 150, H = rowsDef.length * rowH + 26, slot = (W - L - 10) / days.length;
                let out = `<svg class="lr-chart" viewBox="0 0 ${W} ${H}" role="img" aria-label="Market data coverage">`;
                rowsDef.forEach(([name, has, color], i) => {
                    const y0 = i * rowH;
                    out += `<text x="0" y="${y0 + 17}" font-size="12" fill="var(--text-secondary)">${esc(name)}</text><rect x="${L}" y="${y0 + 6}" width="${W - L - 10}" height="${rowH - 12}" fill="var(--border)" opacity="0.4"/>`;
                    days.forEach((d, j) => { if (has(d)) out += `<rect x="${L + j * slot}" y="${y0 + 6}" width="${Math.max(1, slot)}" height="${rowH - 12}" fill="${color}"/>`; });
                });
                const step = Math.max(1, Math.ceil(days.length / (W < 600 ? 3 : 8)));
                days.forEach((d, j) => { if (j % step === 0) out += `<text x="${L + j * slot}" y="${H - 6}" font-size="11" fill="var(--text-muted)">${esc(W < 600 ? d.slice(5) : d)}</text>`; });
                $('lr-chart-coverage').innerHTML = out + '</svg>';
            };
            $('lr-profile-form').addEventListener('submit', async (e) => {
                e.preventDefault();
                const f = e.target;
                try { await post('/api/valuation/profile', { jurisdiction: f.jurisdiction.value.trim() || null, accountsAt: f.accountsAt.value.split(',').map(s => s.trim()).filter(Boolean), custodyNote: f.custodyNote.value.trim() || null }); msg('lr-osmo-msg', 'Access profile saved.', true); }
                catch (err) { msg('lr-osmo-msg', err.message); }
            });
            $('lr-venue-form').addEventListener('submit', async (e) => {
                e.preventDefault();
                const f = e.target, utc = (v) => v ? v + ':00Z' : null;
                const ev = f.evidence.value.trim();
                const v = { venue: f.venue.value.trim(), kind: f.kind.value, state: f.state.value, effectiveFromUtc: utc(f.effectiveFromUtc.value), effectiveToUtc: utc(f.effectiveToUtc.value), jurisdiction: f.jurisdiction.value.trim() || null, verification: f.verification.value, implementationVerified: f.implementationVerified.checked,
                    evidence: ev ? [{ kind: /^https?:/.test(ev) ? 'url' : 'note', url: /^https?:/.test(ev) ? ev : null, description: ev, userReported: f.verification.value === 'UserReported' }] : [{ kind: 'user-statement', description: 'Entered without supporting evidence', userReported: true }] };
                try { await post('/api/valuation/venues?scope=' + f.scope.value, v); f.reset(); loadMarkets(); } catch (err) { msg('lr-osmo-msg', err.message); }
            });
            $('lr-venue-body').addEventListener('click', async (e) => {
                const id = e.target.dataset.delVenue; if (!id) return;
                try { await api('/api/valuation/venues/' + encodeURIComponent(id), { method: 'DELETE' }); loadMarkets(); } catch (err) { msg('lr-osmo-msg', err.message); }
            });
            $('lr-snap-body').addEventListener('click', async (e) => {
                const v = e.target.dataset.delSnap; if (!v) return;
                const [kind, id] = v.split('|');
                try { await api(`/api/valuation/snapshots/${kind}/${encodeURIComponent(id)}`, { method: 'DELETE' }); loadMarkets(); } catch (err) { msg('lr-osmo-msg', err.message); }
            });
            $('lr-osmo-form').addEventListener('submit', async (e) => {
                e.preventDefault();
                const f = e.target;
                const body = { poolIds: f.poolIds.value.split(/[ ,]+/).filter(Boolean) };
                if (f.height.value) body.height = Number(f.height.value); else if (f.atUtc.value) body.atUtc = f.atUtc.value + ':00Z'; else return msg('lr-osmo-msg', 'Give a time or a block height.');
                msg('lr-osmo-msg', 'Reading archive state… this can take a minute.', true);
                try {
                    const r = await post('/api/valuation/osmosis/fetch?scope=' + f.scope.value, body);
                    $('lr-osmo-msg').innerHTML = `<div class="lr-ok">Height ${esc(r.height)}${r.blockTime ? ' (' + esc(fmtTime(r.blockTime)) + ' UTC)' : ''}:</div><ul class="lr-note">${r.results.map(x => `<li>Pool ${esc(x.poolId)}: ${x.ok ? `${esc(x.poolType)}${x.exists ? '' : ', did not exist yet'}${x.problems?.length ? ' · ' + esc(x.problems.join(' ')) : ''}` : `<span class="lr-err">${esc(x.error)}</span>`}</li>`).join('')}</ul>`;
                    loadMarkets();
                } catch (err) { msg('lr-osmo-msg', err.message); }
            });
            $('lr-validate').addEventListener('click', async () => {
                msg('lr-osmo-msg', 'Replaying swap against the previous block…', true);
                try { await post('/api/valuation/osmosis/validate', { txHash: $('lr-validate-hash').value.trim() }); msg('lr-osmo-msg', ''); loadMarkets(); } catch (err) { msg('lr-osmo-msg', err.message); }
            });
            $('lr-book-import').addEventListener('click', async () => {
                try { const f = await readFile('lr-book-file'); const r = await postText('/api/valuation/orderbooks/import?name=' + encodeURIComponent(f.name), f.text); msg('lr-book-msg', `Imported ${r.imported} order book snapshot(s).`, true); loadMarkets(); } catch (e) { msg('lr-book-msg', e.message); }
            });
            $('lr-conv-import').addEventListener('click', async () => {
                try { const f = await readFile('lr-conv-file'); const r = await postText('/api/valuation/conversions/import', f.text); msg('lr-book-msg', `Imported ${r.imported} daily conversion(s).`, true); loadMarkets(); } catch (e) { msg('lr-book-msg', e.message); }
            });

            // ------------------------------------------------------------ scenarios and analysis
            const known = (k, f = fmtOpt) => k?.value != null ? f(k.value) : `<span class="lr-sub" style="display: inline;">unknown: ${esc(k?.missingReason || '')}</span>`;
            $('lr-scen-form').addEventListener('submit', async (e) => {
                e.preventDefault();
                const f = e.target;
                try {
                    const s = await api(`/api/valuation/scenarios?address=${encodeURIComponent(state.address)}&from=${f.from.value}&to=${f.to.value}&basis=${f.basis.value}&fractions=${encodeURIComponent(f.fractions.value.replace(/\s/g, ''))}&dayBasis=${f.dayBasis.value}`);
                    const m = s.metrics;
                    $('lr-scen-out').innerHTML = `
                        <div class="data-table-wrap"><table class="data-table"><tbody>
                            <tr><td>Your node receipts</td><td class="num">${fmtOpt(m.personalNodeOpt)} OPT</td></tr>
                            <tr><td>Your staking / participation receipts</td><td class="num">${fmtOpt(m.personalStakingOpt)} OPT</td></tr>
                            ${m.personalUnclassifiedOpt ? `<tr><td>Unclassified</td><td class="num">${fmtOpt(m.personalUnclassifiedOpt)} OPT</td></tr>` : ''}
                            <tr><td>Actual network node distributions</td><td class="num">${known(m.actualNetworkNodeDistributions)}</td></tr>
                            <tr><td>Your share of node distributions</td><td class="num">${known(m.shareOfNodeDistributions, v => fmtPct(v, 7))}</td></tr>
                            <tr><td>Your share of the selected node amount <span class="lr-sub" style="display: inline;">(${esc(m.selectedBasisSummary)})</span></td><td class="num">${known(m.shareOfSelectedNodeDistribution, v => fmtPct(v, 7))}</td></tr>
                            <tr><td>Your node + staking share of combined distributions</td><td class="num">${known(m.shareOfCombinedDistributions, v => fmtPct(v, 7))}</td></tr>
                            <tr><td>Network amount used for scenarios</td><td class="num">${known(m.selectedNetworkDistribution)}<span class="lr-sub">${esc(m.selectedBasisSummary)}</span></td></tr>
                            <tr><td>Emitted (minted) in period</td><td class="num">${known(m.emitted)}${m.emissionBasis ? `<span class="lr-sub">${esc(m.emissionBasis)}</span>` : ''}</td></tr>
                            <tr><td>Distributed (observed, node + staking)</td><td class="num">${known(m.distributed)}</td></tr>
                            <tr><td>Emitted but not distributed</td><td class="num">${known(m.undistributed)}</td></tr>
                            <tr><td>Net minted / supply growth</td><td class="num">${known(s.supplyGrowth.netMinted)} · ${known(s.supplyGrowth.growth, v => fmtPct(v, 3))}</td></tr>
                        </tbody></table></div>
                        ${m.warnings.length ? `<ul class="lr-note">${m.warnings.map(w => `<li>${esc(w)}</li>`).join('')}</ul>` : ''}
                        <h4 style="margin: 12px 0 4px;">Hypothetical network selling</h4>
                        <div class="data-table-wrap"><table class="data-table"><thead><tr><th>Scenario</th><th class="num">OPT offered per day</th><th class="num">OPT offered in period</th><th class="num">Reference notional (USD)</th><th>Label</th></tr></thead><tbody>
                            ${m.scenarios.map(x => `<tr><td>${fmtPct(x.fraction, 2)} of network distribution</td><td class="num">${x.optOffered != null ? fmtOpt(x.optOffered / m.days) : '–'}</td><td class="num">${x.optOffered != null ? fmtOpt(x.optOffered) : '–'}</td><td class="num">${fmtUsd(x.referenceNotionalUsd)}</td><td style="font-size: 12px;">${esc(x.missingReason || x.label)}</td></tr>`).join('')}
                        </tbody></table></div>
                        <p class="lr-note">${esc(s.referenceNotionalNote)} ${esc(s.supplyGrowth.note)}</p>`;
                } catch (err) { $('lr-scen-out').innerHTML = `<div class="lr-err">${esc(err.message)}</div>`; }
            });

            const runAnalysis = async (extra = {}) => {
                const f = $('lr-an-form');
                const pcts = (s) => s.split(',').map(x => Number(x.trim()) / 100).filter(x => x > 0 && x < 1);
                const body = { address: state.address, from: extra.from || f.from.value, to: extra.to || f.to.value, receiptIds: extra.receiptIds || null, maxAgeHours: Number(f.maxAgeHours.value) || 24, thresholds: pcts(f.thresholds.value), transactionCostsUsd: num(f.txCosts.value), stress: true };
                $('lr-an-out').innerHTML = '<p class="lr-note">Running… each receipt is simulated against the stored market state at its time.</p>';
                try {
                    const r = await post('/api/valuation/analyze', body);
                    const tally = {};
                    for (const x of r.results) tally[x.taxpayerAccessible.outcome] = (tally[x.taxpayerAccessible.outcome] || 0) + 1;
                    const st = r.stress;
                    $('lr-an-out').innerHTML = `
                        <p class="lr-ok">${r.analyzed} receipt(s) analyzed. ${esc(r.note)}</p>
                        <div class="lr-row">${Object.entries(tally).map(([k, n]) => badge(`${(OUTCOME[k] || [k])[0]}: ${n}`, (OUTCOME[k] || [, 'muted'])[1])).join(' ')}</div>
                        ${st ? `<h4 style="margin: 12px 0 4px;">Cumulative stress view</h4><p class="lr-note">${esc(st.label)} ${esc(st.assumptions.join(' '))}${st.complete ? '' : ' Some days had no market state; they are unknown, not zero.'}</p>
                        <div class="data-table-wrap" style="max-height: 280px;"><table class="data-table"><thead><tr><th>Day</th><th class="num">Offered (incl. carried)</th><th class="num">Absorbed</th><th class="num">Output (quote)</th><th class="num">Carried</th><th>Market state</th><th>Notes</th></tr></thead><tbody>
                        ${st.days.map(d => `<tr><td>${esc(d.day)}</td><td class="num">${fmtOpt(d.requestedOpt)}</td><td class="num">${fmtOpt(d.executedOpt)}</td><td class="num">${fmtOpt(d.outputQuote)}</td><td class="num">${fmtOpt(d.carriedInventoryOpt)}</td><td style="font-size: 12px;">${esc(d.stateSource)}</td><td style="font-size: 12px;">${esc(d.notes.join(' '))}</td></tr>`).join('')}
                        </tbody></table></div>` : ''}
                        ${r.scenarios ? `<h4 style="margin: 12px 0 4px;">Hypothetical network selling, each in its own simulation</h4><div class="data-table-wrap"><table class="data-table"><thead><tr><th>Scenario</th><th class="num">Absorbed</th><th class="num">Output (quote)</th><th>Days without network amount</th></tr></thead><tbody>
                        ${r.scenarios.map(s => `<tr><td>${fmtPct(s.fraction, 2)} <span class="lr-sub">${esc(s.label)}</span></td><td class="num">${fmtOpt(sum(s.result.days, d => d.executedOpt))}</td><td class="num">${fmtOpt(sum(s.result.days, d => d.outputQuote))}</td><td>${s.daysWithoutNetworkAmount}</td></tr>`).join('')}
                        </tbody></table></div>` : '<p class="lr-note">Network scenario simulations run for ranges up to 62 days.</p>'}`;
                    await load();
                    showTab('scenarios');
                } catch (err) { $('lr-an-out').innerHTML = `<div class="lr-err">${esc(err.message)}</div>`; }
            };
            $('lr-an-form').addEventListener('submit', (e) => { e.preventDefault(); runAnalysis(); });

            // ------------------------------------------------------------ approvals
            let reviewIds = [];
            const openReview = (ids) => {
                reviewIds = ids;
                const rows = state.rows.filter(r => ids.includes(r.receipt.id));
                const ref = rows.every(r => r.receipt.reference.valueUsd != null) ? sum(rows, r => r.receipt.reference.valueUsd) : null;
                const f = $('lr-review-form');
                f.reset();
                f.selectedValueUsd.value = ref != null ? ref.toFixed(2) : '';
                document.querySelectorAll('.lr-group-only').forEach(el => el.hidden = ids.length < 2);
                $('lr-review-scope').innerHTML = `${ids.length} receipt(s), ${fmtOpt(sum(rows, r => r.receipt.quantityOpt))} OPT, reference value ${fmtUsd(ref)}${ref == null ? ' (some unpriced)' : ''}.`;
                $('lr-review-box').hidden = false;
                msg('lr-review-msg', '');
                showTab('approvals');
                $('lr-review-box').scrollIntoView({ behavior: 'smooth', block: 'start' });
            };
            $('lr-review-cancel').addEventListener('click', () => { $('lr-review-box').hidden = true; });
            $('lr-review-form').method.addEventListener('change', (e) => {
                if (e.target.value !== 'AcceptReference') return;
                const rows = state.rows.filter(r => reviewIds.includes(r.receipt.id));
                if (rows.every(r => r.receipt.reference.valueUsd != null)) $('lr-review-form').selectedValueUsd.value = sum(rows, r => r.receipt.reference.valueUsd).toFixed(2);
            });
            $('lr-review-form').addEventListener('submit', async (e) => {
                e.preventDefault();
                const f = e.target;
                const evidence = f.evidence.value.split('\n').map(l => l.trim()).filter(Boolean).map(l => { const [d, u] = l.split('|').map(s => s.trim()); return { kind: u ? 'url' : 'note', description: d, url: u || null, userReported: f.userReportedOnly.checked }; });
                const draft = { receiptIds: reviewIds, method: f.method.value, selectedValueUsd: Number(f.selectedValueUsd.value), explanation: f.explanation.value, evidence,
                    groupJustification: reviewIds.length > 1 ? f.groupJustification.value : null, allocationMethod: reviewIds.length > 1 ? f.allocationMethod.value : 'single' };
                if (f.method.value === 'AcceptReference') {
                    const rows = state.rows.filter(r => reviewIds.includes(r.receipt.id));
                    draft.selectedValueUsd = sum(rows, r => r.receipt.reference.valueUsd);
                }
                try { await post('/api/valuation/reviews?address=' + encodeURIComponent(state.address), draft); $('lr-review-box').hidden = true; state.selected.clear(); await loadApprovals(); load(); }
                catch (err) { msg('lr-review-msg', err.message); }
            });

            // ------------------------------------------------------------ receipt-date FMV
            const FLAG_TEXT = {
                ReferenceMissing: 'No reference price', ReferenceDailyFallback: 'Daily close only', ReferenceStale: 'Reference stale',
                ConflictingEvidence: 'Prices conflict', ThinTrading: 'Thin trading', NoObservedTrades: 'No trades observed',
                DepositRestriction: 'Deposits restricted', NoContemporaneousMarket: 'No contemporaneous market',
                LargeQuantityEffect: 'Large quantity effect', CurrencyConversionMissing: 'No USD conversion',
            };
            const SOURCE_TEXT = { reference: 'CoinMarketCap reference', 'osmosis-pool': 'Osmosis pool price', 'osmosis-swaps': 'Osmosis day swaps' };
            let fmv = null, fmvTimer = null;
            const excSelected = new Set();

            const fmvPoll = async () => {
                clearTimeout(fmvTimer);
                if (!state.address) return;
                try {
                    const j = await api('/api/valuation/fmv/status?address=' + encodeURIComponent(state.address));
                    const pct = j.total ? Math.round(100 * j.done / j.total) : null;
                    $('lr-fmv-progress').innerHTML = j.stage === 'Not started' ? '' :
                        `<strong>${esc(j.stage)}</strong>${j.running && j.total ? ` · ${j.done} of ${j.total} (${pct}%)` : ''}${j.error ? ` <span class="lr-err">${esc(j.error)}</span>` : ''}
                        ${j.running && j.total ? `<div style="height: 6px; background: var(--border); border-radius: 3px; margin: 6px 0; max-width: 480px;"><div style="height: 6px; width: ${pct}%; background: var(--accent-primary); border-radius: 3px;"></div></div>` : ''}
                        ${(j.log || []).length ? `<details${j.running ? ' open' : ''}><summary style="cursor: pointer;">Details</summary><ul>${j.log.slice(-8).map(l => `<li>${esc(l)}</li>`).join('')}</ul></details>` : ''}`;
                    $('lr-fmv-build').disabled = !!j.running;
                    $('lr-fmv-cancel').hidden = !j.running;
                    if (j.running) fmvTimer = setTimeout(fmvPoll, 2000);
                    else if (j.stage === 'Done' && !fmvPoll.loadedAfterDone) { fmvPoll.loadedAfterDone = true; await loadFmv(); }
                } catch (e) { $('lr-fmv-progress').innerHTML = `<span class="lr-err">${esc(e.message)}</span>`; }
            };

            const loadFmv = async () => {
                if (!state.address) return;
                const [pol, c] = await Promise.all([api('/api/valuation/fmv/policy'), api(`/api/valuation/fmv/candidates?address=${encodeURIComponent(state.address)}&from=${state.from}&to=${state.to}`)]);
                fmv = { pol, c };
                const approvedPolicy = pol.approval && pol.approval.version === pol.policy.version;
                $('lr-fmv-policy').innerHTML = `<details><summary style="cursor: pointer;"><strong>Policy ${esc(pol.policy.version)}</strong> ${approvedPolicy ? badge('Approved', 'ok', `by ${pol.approval.approvedBy} on ${fmtTime(pol.approval.approvedUtc)}`) : badge('Not approved', 'warn')}</summary><p>${esc(pol.description)}</p></details>
                    ${approvedPolicy ? '' : '<button type="button" class="btn-quiet" id="lr-fmv-policy-ok" style="margin-top: 6px;">Approve this policy</button> <span class="lr-note">Needed before approving qualifying candidates in bulk.</span>'}`;
                $('lr-fmv-policy-ok')?.addEventListener('click', async () => {
                    if (!confirm(`Approve policy ${pol.policy.version}? Receipts that pass every check can then be approved in bulk at the candidate price. Flagged receipts still need an explained decision. This is your own method choice, not IRS approval.`)) return;
                    try { await post('/api/valuation/fmv/policy', { confirm: true }); await loadFmv(); } catch (e) { alert(e.message); }
                });
                const t = c.totals;
                const flags = [...c.byTrigger].sort((x, y) => y.count - x.count);
                $('lr-fmv-summary').innerHTML = t.withCandidate === 0 ? '<p class="lr-note">No candidates yet for this period. Press <em>Gather evidence and build candidates</em>.</p>' : `
                    <div class="lr-tiles">
                        <div class="lr-tile"><div class="k">Selected (approved)</div><div class="v">${fmtUsd(t.approvedUsd)}</div><div class="s">${t.approved} of ${t.receipts} receipts</div></div>
                        <div class="lr-tile"><div class="k">Qualifying candidates</div><div class="v">${fmtUsd(t.qualifyingUsd)}</div><div class="s">${t.qualifying} receipts pass every check</div></div>
                        <div class="lr-tile"><div class="k">Exceptions</div><div class="v">${t.exceptions}</div><div class="s">need an explained decision</div></div>
                        <div class="lr-tile"><div class="k">Unresolved</div><div class="v">${fmtOpt(t.unresolvedOpt)} OPT</div><div class="s">reference estimate ${fmtUsd(t.unresolvedReferenceUsd)} (not income yet)</div></div>
                    </div>
                    ${flags.length ? `<p class="lr-note" style="margin-top: 8px;">Flags on unresolved receipts: ${flags.map(f => `<button type="button" class="lr-small" data-flag="${esc(f.trigger)}" title="Show these">${esc(FLAG_TEXT[f.trigger] || f.trigger)} · ${f.count}</button>`).join(' ')}</p>` : ''}
                    <p style="margin-top: 8px;"><button type="button" id="lr-fmv-approve" ${!approvedPolicy || !t.qualifying ? 'disabled' : ''}>Approve ${t.qualifying} qualifying candidates</button>
                    <span class="lr-note">${!approvedPolicy ? 'Approve the policy first.' : t.qualifying ? `Total ${fmtUsd(t.qualifyingUsd)} at the candidate prices.` : 'None waiting.'}</span></p>`;
                $('lr-fmv-approve')?.addEventListener('click', async () => {
                    if (!confirm(`Approve ${t.qualifying} receipts at their policy candidate (${fmtUsd(t.qualifyingUsd)} in total) under ${pol.policy.version}? Each becomes that receipt's income and cost basis. Every approval is recorded and can be revised.`)) return;
                    try { const r = await post(`/api/valuation/fmv/approve-qualifying?address=${encodeURIComponent(state.address)}&from=${state.from}&to=${state.to}`, { confirm: true }); msg('lr-exc-msg', `${r.approved} value(s) approved.`, true); await loadFmv(); load(); }
                    catch (e) { alert(e.message); }
                });
                document.querySelectorAll('#lr-fmv-summary [data-flag]').forEach(b => b.addEventListener('click', () => { $('lr-exc-flag').value = b.dataset.flag; renderExceptions(); $('lr-exc-box').scrollIntoView({ behavior: 'smooth', block: 'start' }); }));
                const flagSelect = $('lr-exc-flag'), keep = flagSelect.value;
                flagSelect.innerHTML = '<option value="">All flags</option>' + flags.map(f => `<option value="${esc(f.trigger)}">${esc(FLAG_TEXT[f.trigger] || f.trigger)} (${f.count})</option>`).join('');
                flagSelect.value = keep;
                renderExceptions();
            };

            const exceptionRows = () => {
                if (!fmv) return [];
                const flag = $('lr-exc-flag').value, cat = $('lr-exc-cat').value;
                return fmv.c.rows.filter(r => !r.selected && r.candidate && !r.candidate.qualifies)
                    .filter(r => (!flag || r.candidate.flags.some(f => f.trigger === flag)) && (!cat || r.category === cat));
            };
            const renderExceptions = () => {
                const rows = exceptionRows();
                $('lr-exc-box').hidden = !fmv || fmv.c.rows.every(r => r.selected || !r.candidate || r.candidate.qualifies);
                for (const id of [...excSelected]) if (!rows.some(r => r.receiptId === id)) excSelected.delete(id);
                $('lr-exc-body').innerHTML = rows.map(r => {
                    const c = r.candidate;
                    const others = c.options.filter(o => o.key !== c.source).map(o => `${esc(o.label)}: ${fmtUsd(o.valueUsd)}`).join('<br>');
                    return `<tr data-id="${esc(r.receiptId)}">
                        <td class="lr-check"><input type="checkbox" class="lr-exc-sel" aria-label="Select" ${excSelected.has(r.receiptId) ? 'checked' : ''}></td>
                        <td class="lr-when">${esc(fmtTime(r.timestampUtc))}</td><td>${esc(r.category)}</td><td class="num">${fmtOpt(r.quantityOpt)}</td>
                        <td class="num">${fmtUsd(r.referenceUsd)}</td>
                        <td class="num">${c.valueUsd != null ? fmtUsd(c.valueUsd) + `<span class="lr-sub">${esc(SOURCE_TEXT[c.source] || c.source)}</span>` : '<span class="lr-sub">none: stays unresolved</span>'}</td>
                        <td class="lr-wide">${c.flags.map(f => badge(FLAG_TEXT[f.trigger] || f.trigger, 'warn', f.detail)).join(' ')}<span class="lr-sub">${esc(c.flags.map(f => f.detail).join(' '))}</span></td>
                        <td class="lr-wide" style="font-size: 12px;">${others || '–'}</td></tr>`;
                }).join('') || '<tr><td colspan="8" style="text-align: center; color: var(--text-secondary);">No exceptions match.</td></tr>';
                $('lr-exc-all').checked = rows.length > 0 && rows.every(r => excSelected.has(r.receiptId));
                $('lr-exc-count').textContent = excSelected.size ? `${excSelected.size} selected` : '';
            };
            $('lr-exc-flag').addEventListener('change', renderExceptions);
            $('lr-exc-cat').addEventListener('change', renderExceptions);
            $('lr-exc-body').addEventListener('change', (e) => {
                if (!e.target.classList.contains('lr-exc-sel')) return;
                const id = e.target.closest('tr').dataset.id;
                if (e.target.checked) excSelected.add(id); else excSelected.delete(id);
                $('lr-exc-count').textContent = excSelected.size ? `${excSelected.size} selected` : '';
            });
            $('lr-exc-all').addEventListener('change', (e) => { for (const r of exceptionRows()) e.target.checked ? excSelected.add(r.receiptId) : excSelected.delete(r.receiptId); renderExceptions(); });
            $('lr-exc-form').addEventListener('submit', async (e) => {
                e.preventDefault();
                const ids = [...excSelected], option = $('lr-exc-option').value, why = $('lr-exc-why').value.trim();
                if (!ids.length) return msg('lr-exc-msg', 'Select receipts first.');
                if (why.length < 20) return msg('lr-exc-msg', 'Explain the decision (at least 20 characters).');
                const label = $('lr-exc-option').selectedOptions[0].textContent;
                if (!confirm(`Approve "${label}" for ${ids.length} receipt(s), each at its own amount, with your explanation? Receipts where that option is not available are skipped and listed.`)) return;
                try {
                    const r = await post('/api/valuation/fmv/decide?address=' + encodeURIComponent(state.address), { receiptIds: ids, option, explanation: why, confirm: true });
                    msg('lr-exc-msg', `${r.approved} decision(s) recorded.${r.errors.length ? ' Skipped: ' + r.errors.slice(0, 5).join(' | ') + (r.errors.length > 5 ? ` and ${r.errors.length - 5} more` : '') : ''}`, !r.errors.length);
                    excSelected.clear(); $('lr-exc-why').value = '';
                    await loadFmv(); load();
                } catch (err) { msg('lr-exc-msg', err.message); }
            });
            $('lr-fmv-build').addEventListener('click', async () => {
                try {
                    fmvPoll.loadedAfterDone = false;
                    await post('/api/valuation/fmv/build', { address: state.address, from: state.from, to: state.to, fetchMarkets: $('lr-fmv-fetch').checked });
                    fmvPoll();
                } catch (e) { $('lr-fmv-progress').innerHTML = `<span class="lr-err">${esc(e.message)}</span>`; }
            });
            $('lr-fmv-cancel').addEventListener('click', async () => { await post('/api/valuation/fmv/cancel?address=' + encodeURIComponent(state.address)); fmvPoll(); });
            $('lr-imp-go').addEventListener('click', async () => {
                try {
                    const f = await readFile('lr-imp-file');
                    const r = await postText(`/api/valuation/fmv/import?address=${encodeURIComponent(state.address)}&name=${encodeURIComponent(f.name)}`, f.text);
                    msg('lr-imp-msg', `Imported ${r.imported}: ${r.approved} approved, ${r.proposed} proposed (file sha256 ${r.sourceSha256.slice(0, 12)}).`, true);
                    await loadFmv(); load();
                } catch (e) { msg('lr-imp-msg', e.body?.errors ? e.body.errors.slice(0, 8).join('\n') : e.message); }
            });
            // A starting file for the import: one row per receipt in the period with its ids filled in and no value.
            $('lr-imp-ids').addEventListener('click', (e) => {
                e.preventDefault();
                const q = (v) => /[",\n]/.test(String(v ?? '')) ? '"' + String(v).replace(/"/g, '""') + '"' : String(v ?? '');
                const lines = ['receipt_id,tx_hash,event_index,value_usd,method,status,reviewer,review_date,explanation,evidence_url,evidence_description,received_utc,category,quantity_opt,reference_value_usd'];
                for (const r of state.rows) lines.push([r.receipt.id, r.receipt.txHash, r.receipt.eventIndex, '', '', 'proposed', '', '', '', '', '', r.receipt.timestampUtc, r.receipt.category, r.receipt.quantityOpt, r.receipt.reference.valueUsd ?? ''].map(q).join(','));
                const a = document.createElement('a');
                a.href = URL.createObjectURL(new Blob([lines.join('\n') + '\n'], { type: 'text/csv' }));
                a.download = `receipts-${state.from}-to-${state.to}.csv`;
                a.click();
                setTimeout(() => URL.revokeObjectURL(a.href), 1000);
            });
            const loadApprovals = async () => {
                fmvPoll(); loadFmv().catch(e => { $('lr-fmv-summary').innerHTML = `<span class="lr-err">${esc(e.message)}</span>`; });
                try {
                    const [rv, versions] = await Promise.all([api('/api/valuation/reviews'), api('/api/valuation/report/versions?address=' + encodeURIComponent(state.address))]);
                    const cls = { Approved: 'ok', Proposed: 'warn', Withdrawn: 'muted', Superseded: 'muted' };
                    $('lr-rev-body').innerHTML = rv.current.slice().reverse().map(r => `<tr><td title="${esc(r.reviewId)}">${esc(r.reviewId.slice(0, 8))}</td><td>${badge(r.status, cls[r.status] || 'muted')}</td><td>${esc(methodText(r.method))}</td><td class="num">${r.receiptIds.length}</td><td class="num">${fmtUsd(r.referenceValueUsdAtReview)}</td><td class="num">${fmtUsd(r.selectedValueUsd)}</td><td>${esc(r.reviewer)}</td><td>${esc(fmtTime(r.reviewedUtc))}</td><td class="lr-wide" style="font-size: 12px; max-width: 320px; white-space: pre-wrap;"><div class="lr-clamp">${esc(r.explanation)}</div></td>
                        <td class="lr-wide" style="white-space: nowrap;">${r.status === 'Proposed' ? `<button type="button" class="lr-small" data-approve="${esc(r.reviewId)}">Approve…</button> ` : ''}${r.status !== 'Withdrawn' ? `<button type="button" class="lr-small" data-withdraw="${esc(r.reviewId)}">Withdraw</button>` : ''}</td></tr>`).join('')
                        || '<tr><td colspan="10" style="text-align: center; color: var(--text-secondary);">No reviews yet. Select receipts on the Receipts tab and choose “Propose a reviewed value”.</td></tr>';
                    $('lr-hist-body').innerHTML = rv.history.slice().reverse().map(r => `<tr><td>${esc(r.reviewId.slice(0, 8))}</td><td class="num">${r.version}</td><td>${esc(r.status)}</td><td class="num">${fmtUsd(r.selectedValueUsd)}</td><td>${esc(r.reviewer)}</td><td>${esc(fmtTime(r.reviewedUtc))}</td></tr>`).join('');
                    $('lr-versions-body').innerHTML = versions.map(v => `<tr><td>${esc(fmtTime(v.createdUtc))}</td><td>${v.year}</td><td>${badge(v.status === 'final' ? 'Final' : 'Draft', v.status === 'final' ? 'ok' : 'warn')}</td><td class="address-cell">${esc(v.reviewFingerprint.slice(0, 12))}</td><td><button type="button" class="lr-small" data-view="${esc(v.id)}">View</button></td></tr>`).join('')
                        || '<tr><td colspan="5" style="text-align: center; color: var(--text-secondary);">No report versions yet.</td></tr>';
                } catch (e) { msg('lr-review-msg', e.message); }
            };
            $('lr-rev-body').addEventListener('click', async (e) => {
                const ap = e.target.dataset.approve, wd = e.target.dataset.withdraw;
                if (!ap && !wd) return;
                try {
                    if (ap) {
                        if (!confirm('Approve this value? It becomes the income and cost basis for these receipts in the reviewed report. This is your own decision, not IRS approval. Earlier versions stay in the history.')) return;
                        await post(`/api/valuation/reviews/${encodeURIComponent(ap)}/approve?address=${encodeURIComponent(state.address)}`, { confirm: true });
                    } else {
                        if (!confirm('Withdraw this review? Its receipts go back to having no approved value from it.')) return;
                        await post(`/api/valuation/reviews/${encodeURIComponent(wd)}/withdraw?address=${encodeURIComponent(state.address)}`, {});
                    }
                    await loadApprovals(); load();
                } catch (err) { alert(err.message); }
            });

            const renderReport = (rep) => {
                const tbl = (head, rows, empty) => `<div class="data-table-wrap"><table class="data-table" style="min-width: 0;"><thead><tr>${head.map(h => `<th${h.startsWith('#') ? ' class="num"' : ''}>${esc(h.replace(/^#/, ''))}</th>`).join('')}</tr></thead><tbody>${rows.join('') || `<tr><td colspan="${head.length}" style="text-align: center; color: var(--text-secondary);">${empty}</td></tr>`}</tbody></table></div>`;
                const inc = rep.income, rc = rep.reconciliation, ev = rep.evidence;
                $('lr-report-view').innerHTML = `<div class="lr-report">
                    <div class="lr-row lr-no-print" style="justify-content: flex-end;"><button type="button" class="btn-quiet" onclick="window.print()">Print / Save as PDF</button></div>
                    <h2>${esc(rep.title)} ${badge(rep.status === 'final' ? 'Final' : 'Draft', rep.status === 'final' ? 'ok' : 'warn')}</h2>
                    <p class="lr-note">Wallet ${esc(rep.wallet)} · prepared ${esc(fmtTime(rep.preparedUtc))} UTC by ${esc(rep.preparedBy)} · reporting time zone ${esc(rep.reportingTimeZone)} · valuation policy ${esc(rep.policy || '–')}${rep.policyApproval ? ` (approved by ${esc(rep.policyApproval.approvedBy)} ${esc(fmtTime(rep.policyApproval.approvedUtc))})` : ' (not approved)'} · fingerprint ${esc(rep.fingerprint.slice(0, 16))}</p>
                    <p class="lr-disclaimer">${esc(rep.disclaimer)}</p>
                    <p><strong>${esc(rep.statusText)}</strong></p>

                    <h3>Income</h3>
                    ${tbl(['Category', '#Receipts', '#OPT', '#Reference (USD)', '#Selected (USD)', '#Adjustment (USD)', '#Unresolved OPT', '#Unresolved reference estimate', '#Unconfirmed category'],
                        rep.categories.map(c => `<tr><td>${esc(c.category)}</td><td class="num">${c.receipts}</td><td class="num">${fmtOpt(c.quantityOpt)}</td><td class="num">${fmtUsd(c.referenceValueUsd)}${c.unpricedReferenceOpt ? `<span class="lr-sub">${fmtOpt(c.unpricedReferenceOpt)} OPT unpriced</span>` : ''}</td><td class="num">${fmtUsd(c.selectedValueUsd)}</td><td class="num">${fmtUsd(c.adjustmentUsd)}</td><td class="num">${fmtOpt(c.unvaluedOpt)}</td><td class="num">${fmtUsd(c.unvaluedReferenceEstimateUsd)}</td><td class="num">${c.uncertainClassification}</td></tr>`), 'No receipts')}
                    <p class="lr-note">Selected values ${fmtUsd(inc.knownSelectedUsd)} (adjustment from reference ${fmtUsd(inc.adjustmentUsd)} on those receipts). Unresolved: ${fmtOpt(inc.unvaluedOpt)} OPT with a reference estimate of ${fmtUsd(inc.unvaluedReferenceEstimateUsd)}, shown separately and not included. ${esc(inc.note)}</p>
                    ${rep.methods?.length ? tbl(['Selected by', '#Receipts', '#Selected (USD)', '#Reference (USD)'], rep.methods.map(m => `<tr><td>${esc(methodText(m.method))}</td><td class="num">${m.receipts}</td><td class="num">${fmtUsd(m.selectedUsd)}</td><td class="num">${fmtUsd(m.referenceUsd)}</td></tr>`), '') : ''}

                    <h3>Missing information</h3>
                    ${rep.missingInformation?.length ? `<ul class="lr-note">${rep.missingInformation.map(m => `<li>${esc(m)}</li>`).join('')}</ul>` : '<p class="lr-note">None.</p>'}

                    <h3>Market and distribution evidence</h3>
                    <p class="lr-note">${ev.candidates} receipt(s) have market evidence under the policy; ${ev.qualifyingUnderPolicy} passed every check. Sources: ${ev.bySource.map(s => `${esc(SOURCE_TEXT[s.source] || s.source)} ${s.receipts}`).join(', ') || '–'}. ${ev.withOsmosisPoolState} had Osmosis pool state just before receipt${ev.largestPriceImpact != null ? `; the largest modeled whole-receipt price impact was ${fmtPct(ev.largestPriceImpact)}` : ''}.
                    Flags: ${ev.flags.map(f => `${esc(FLAG_TEXT[f.trigger] || f.trigger)} ${f.receipts}`).join(', ') || 'none'}.
                    Network: ${ev.network.emittedOpt != null ? `${fmtOpt(ev.network.emittedOpt)} OPT emitted` : `emission recorded for ${ev.network.emittedDaysRecorded} of ${ev.network.days} days`}; ${fmtOpt(ev.network.distributedObservedOpt)} OPT observed distributed on ${ev.network.distributedDaysObserved} day(s). ${esc(ev.network.note)}</p>

                    <h3>Reconciliation</h3>
                    ${tbl(['Item', '#USD'], [
                        `<tr><td>Income at selected values (${rep.year})</td><td class="num">${fmtUsd(rc.incomeSelectedUsd)}</td></tr>`,
                        `<tr><td>Reward lot basis created (${rep.year})</td><td class="num">${fmtUsd(rc.rewardLotBasisCreatedUsd)} ${rc.incomeEqualsBasis ? badge('equal', 'ok') : badge('differs', 'bad')}</td></tr>`,
                        `<tr><td>Known basis in, through ${rep.year}</td><td class="num">${fmtUsd(rc.basisInKnownUsd)}${rc.basisInUnknownOpt ? `<span class="lr-sub">plus ${fmtOpt(rc.basisInUnknownOpt)} OPT of unknown basis</span>` : ''}</td></tr>`,
                        `<tr><td>Basis disposed</td><td class="num">${fmtUsd(rc.basisDisposedUsd)}</td></tr>`,
                        `<tr><td>Basis carried out (own wallets, gifts, exchange holdings)</td><td class="num">${fmtUsd(rc.basisCarriedOutUsd)}</td></tr>`,
                        `<tr><td>Basis paid as network fees</td><td class="num">${fmtUsd(rc.basisFeesUsd)}</td></tr>`,
                        `<tr><td>Basis in unclassified transfers</td><td class="num">${fmtUsd(rc.basisUnresolvedOutUsd)}</td></tr>`,
                        `<tr><td>Basis still held</td><td class="num">${fmtUsd(rc.basisRemainingUsd)}</td></tr>`,
                        `<tr class="tr-total"><td>Difference</td><td class="num">${fmtUsd(rc.differenceUsd)}</td></tr>`,
                        `<tr><td>${rc.dispositionsInYear} disposition(s) in ${rep.year}: proceeds / basis / gain</td><td class="num">${fmtUsd(rc.proceedsUsd)} / ${fmtUsd(rc.basisUsd)} / ${fmtUsd(rc.gainUsd)}</td></tr>`], '')}
                    <p class="lr-note">${esc(rc.note)}</p>

                    <h3>Dispositions in ${rep.year}</h3>
                    ${tbl(['Disposed', 'What', '#OPT', '#Proceeds', '#Basis', '#Gain / (loss)', 'Term'], rep.dispositions.map(d => `<tr><td>${esc(fmtTime(d.disposedUtc))}</td><td>${esc(d.kind || '')}</td><td class="num">${fmtOpt(d.quantityOpt)}</td><td class="num">${d.proceedsUsd == null ? badge('not entered', 'warn') : fmtUsd(d.proceedsUsd)}</td><td class="num">${d.basisUsd == null ? badge('unknown', 'warn') : fmtUsd(d.basisUsd)}</td><td class="num">${d.gainUsd == null ? '–' : fmtUsd(d.gainUsd)}</td><td>${esc(d.term)}</td></tr>`), 'None')}
                    <p class="lr-note">Held at year end: ${fmtOpt(rep.remainingLots.quantityOpt)} OPT, known basis ${fmtUsd(rep.remainingLots.knownBasisUsd)}${rep.remainingLots.unknownBasisOpt ? `, ${fmtOpt(rep.remainingLots.unknownBasisOpt)} OPT with unknown basis` : ''}.</p>

                    <h3>Review and approval history</h3>
                    ${tbl(['Review', 'Version', 'Status', 'Method', '#Selected', '#Receipts', 'Reviewer', 'Date', 'Explanation'], rep.approvals.map(a => `<tr><td>${esc(a.reviewId.slice(0, 8))}</td><td>${a.version}</td><td>${esc(a.status)}</td><td>${esc(methodText(a.method))}</td><td class="num">${fmtUsd(a.selectedValueUsd)}</td><td class="num">${a.receipts}</td><td>${esc(a.reviewer)}</td><td>${esc(fmtTime(a.reviewedUtc))}</td><td class="lr-wide" style="font-size: 12px; white-space: pre-wrap;"><div class="lr-clamp">${esc(a.explanation)}</div></td></tr>`), 'None')}

                    <h3>Assumptions</h3><ul class="lr-note">${rep.assumptions.map(a => `<li>${esc(a)}</li>`).join('')}</ul>
                    <details><summary style="cursor: pointer;">Policy</summary><p class="lr-note">${esc(rep.policyDescription || '')}</p></details>
                </div>`;
            };            $('lr-versions-body').addEventListener('click', async (e) => {
                const id = e.target.dataset.view; if (!id) return;
                try { renderReport(await api('/api/valuation/report/' + encodeURIComponent(id))); } catch (err) { msg('lr-review-msg', err.message); }
            });
            $('lr-report-new').addEventListener('click', async () => {
                const year = state.from.slice(0, 4);
                $('lr-report-view').innerHTML = '<p class="lr-note">Building report…</p>';
                try { const r = await post(`/api/valuation/report?address=${encodeURIComponent(state.address)}&year=${year}`); renderReport(r.report); loadApprovals(); }
                catch (err) { $('lr-report-view').innerHTML = `<div class="lr-err">${esc(err.message)}</div>`; }
            });

            // Card layout on phones: label each cell with its column heading (handles colspans and tables drawn later).
            const labelTables = () => {
                for (const t of document.querySelectorAll('.lr table.data-table')) {
                    const head = t.tHead?.rows[t.tHead.rows.length - 1];
                    if (!head) continue;
                    const names = [];
                    for (const th of head.cells) for (let k = 0; k < (th.colSpan || 1); k++) names.push(th.textContent.trim());
                    for (const tb of t.tBodies) {
                        for (const tr of tb.rows) {
                            let i = 0;
                            for (const td of tr.cells) {
                                if (!td.hasAttribute('data-label')) td.setAttribute('data-label', td.colSpan > 1 ? '' : (names[i] || ''));
                                i += td.colSpan || 1;
                            }
                        }
                        limitRows(t, tb);
                    }
                }
            };
            // Long lists on phones show the first 10 cards and a button for the rest (receipts page themselves).
            const ROW_LIMIT = 10;
            const limitRows = (table, tb) => {
                if (!tb.id || tb.id === 'lr-body') return;
                const wrap = table.closest('.data-table-wrap') || table;
                let btn = wrap.parentElement.querySelector(`button[data-more-for="${tb.id}"]`);
                const rows = [...tb.rows].filter(r => !r.classList.contains('lr-detail'));
                if (!isPhone() || tb.dataset.expanded === '1' || rows.length <= ROW_LIMIT) {
                    rows.forEach(r => { if (r.dataset.limited) { r.style.display = ''; delete r.dataset.limited; } });
                    btn?.remove();
                    return;
                }
                rows.forEach((r, i) => { if (i >= ROW_LIMIT && r.style.display !== 'none') { r.style.display = 'none'; r.dataset.limited = '1'; } });
                if (!btn) {
                    btn = document.createElement('button');
                    btn.type = 'button'; btn.className = 'lr-small'; btn.dataset.moreFor = tb.id; btn.style.margin = '4px 0 8px';
                    btn.addEventListener('click', () => { tb.dataset.expanded = '1'; limitRows(table, tb); });
                    wrap.insertAdjacentElement('afterend', btn);
                }
                btn.textContent = `Show all ${rows.length}`;
            };
            let labelQueued = false;
            new MutationObserver(() => { if (labelQueued) return; labelQueued = true; requestAnimationFrame(() => { labelQueued = false; labelTables(); }); })
                .observe(document.querySelector('.lr'), { childList: true, subtree: true });
            let lastWidth = window.innerWidth;
            window.addEventListener('resize', () => {
                if (Math.abs(window.innerWidth - lastWidth) < 40 || !state.data) return;
                lastWidth = window.innerWidth;
                renderReceiptCharts();
            });

            if (addressInput.value) load();
        })();
        </script>
        """;
}
