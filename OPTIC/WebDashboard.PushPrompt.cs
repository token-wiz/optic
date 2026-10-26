// In the Home Screen web app, on every page load: if the system hasn't been asked yet whether OPTIC may send
// notifications, iOS's own Allow dialog comes up on the first tap (iOS only shows it in answer to a tap; there's no
// dialog of OPTIC's own). If notifications are allowed but this app isn't signed up (e.g. just re-added), it's signed
// up quietly. window.opticPush (state, enable, disable) is shared with the Notifications section of My Settings.
public static partial class WebDashboard
{
    const string PushPromptHtml = """

                <script>
                    (function () {
                        const OFF = 'optic.push.turnedOff';
                        const turnedOff = () => { try { return localStorage.getItem(OFF) === '1'; } catch (e) { return false; } };
                        const zone = () => { try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch (e) { return 'UTC'; } };
                        const deviceName = () => { const u = navigator.userAgent; return (/iPhone|iPad/.test(u) ? 'iPhone/iPad' : /Android/.test(u) ? 'Android' : /Windows/.test(u) ? 'Windows' : /Mac/.test(u) ? 'Mac' : 'Device') + ' · ' + (/Edg\//.test(u) ? 'Edge' : /Chrome\//.test(u) ? 'Chrome' : /Firefox\//.test(u) ? 'Firefox' : /Safari\//.test(u) ? 'Safari' : 'browser'); };
                        const b64ToBytes = (v) => { v = v.replace(/-/g, '+').replace(/_/g, '/'); const raw = atob(v + '='.repeat((4 - v.length % 4) % 4)); return Uint8Array.from(raw, c => c.charCodeAt(0)); };
                        const post = (url, body) => fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
                        const send = (sub) => { const j = sub.toJSON(); return post('/api/push/subscribe', { endpoint: j.endpoint, keys: j.keys, device: deviceName(), timeZone: zone() }); };
                        const ios = /iPhone|iPad/.test(navigator.userAgent) || (/Macintosh/.test(navigator.userAgent) && navigator.maxTouchPoints > 1);
                        const standalone = window.navigator.standalone === true || window.matchMedia('(display-mode: standalone)').matches;
                        const thisSub = async () => { try { const reg = await navigator.serviceWorker.getRegistration('/'); return reg ? await reg.pushManager.getSubscription() : null; } catch (e) { return null; } };

                        // Where this device stands: { support: 'ok' | 'insecure' | 'ios-home-screen' | 'none', permission, on, sub, status (from /api/push/status) }.
                        const state = async () => {
                            if (!window.isSecureContext) return { support: 'insecure' };
                            if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) return { support: ios && !standalone ? 'ios-home-screen' : 'none' };
                            let status = null;
                            try { status = await fetch('/api/push/status').then(r => r.ok ? r.json() : null); } catch (e) { }
                            const sub = await thisSub();
                            let on = Notification.permission === 'granted' && !!sub && !!status && (status.devices || []).some(d => d.endpoint === sub.endpoint);
                            // Permission still granted and the browser still subscribed, but the server lost it: sign it up again.
                            if (!on && status && Notification.permission === 'granted' && sub) {
                                try { if ((await send(sub)).ok) { on = true; status = await fetch('/api/push/status').then(r => r.json()); } } catch (e) { }
                            }
                            return { support: 'ok', permission: Notification.permission, on, sub, status };
                        };
                        const enable = async () => {
                            // Ask first, while still inside the tap that called this (iOS needs that to show its dialog).
                            if (await Notification.requestPermission() !== 'granted') throw new Error('notifications weren’t allowed');
                            const status = await fetch('/api/push/status').then(r => r.ok ? r.json() : null);
                            if (!status || !status.publicKey) throw new Error('could not read the notification settings');
                            let reg;
                            try { reg = await navigator.serviceWorker.register('/sw.js', { scope: '/' }); }
                            catch (e) { throw new Error('the browser refused to install OPTIC’s notification helper (' + e.message + '); OPTIC’s certificate may not be trusted on this device, see Admin → HTTPS'); }
                            await navigator.serviceWorker.ready;
                            const s = await reg.pushManager.getSubscription() || await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: b64ToBytes(status.publicKey) });
                            const r = await send(s);
                            if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || 'HTTP ' + r.status);
                            try { localStorage.removeItem(OFF); } catch (e) { }
                            document.dispatchEvent(new Event('optic-push-changed'));
                        };
                        // With an endpoint: remove that (other) device. Without: turn this device off.
                        const disable = async (endpoint) => {
                            const sub = await thisSub();
                            const ep = endpoint || (sub && sub.endpoint);
                            if (ep) await post('/api/push/unsubscribe', { endpoint: ep });
                            if (sub && (!endpoint || endpoint === sub.endpoint)) { try { await sub.unsubscribe(); } catch (e) { } }
                            // Turned off here on purpose: don't sign this device up again by itself.
                            if (!endpoint || (sub && endpoint === sub.endpoint)) { try { localStorage.setItem(OFF, '1'); } catch (e) { } }
                            document.dispatchEvent(new Event('optic-push-changed'));
                        };
                        window.opticPush = { state, enable, disable, ios, standalone };

                        const check = async () => {
                            // Only in the Home Screen web app, and not after turning notifications off in My Settings.
                            if (!standalone || turnedOff()) return;
                            const s = await state();
                            if (s.on || s.support !== 'ok' || !s.status) return;
                            // Allowed by the system but this app isn't signed up: sign it up, nothing shown.
                            if (s.permission === 'granted') { try { await enable(); } catch (e) { } return; }
                            // Never asked: iOS's Allow dialog on the first tap anywhere. (Once "Don't Allow" is chosen,
                            // only iPhone Settings → Notifications → OPTIC can turn them back on; iOS won't ask again.)
                            if (s.permission !== 'default') return;
                            // A touch that was a scroll doesn't let iOS show the dialog, so keep listening until it has an answer.
                            let asking = false;
                            const ask = () => {
                                if (asking) return;
                                asking = true;
                                enable().catch(() => { }).finally(() => {
                                    asking = false;
                                    if (Notification.permission === 'default') return;
                                    document.removeEventListener('touchend', ask, true);
                                    document.removeEventListener('click', ask, true);
                                });
                            };
                            document.addEventListener('touchend', ask, true);
                            document.addEventListener('click', ask, true);
                        };
                        // iPhone/iPad in Safari: once ever on this device, a tip on adding OPTIC to the Home Screen (iOS has no
                        // way for a page to add itself; the person has to use Share → Add to Home Screen).
                        const homeScreenTip = () => {
                            const KEY = 'optic.homeScreenTip.shown';
                            if (!ios || standalone) return;
                            try { if (localStorage.getItem(KEY)) return; localStorage.setItem(KEY, '1'); } catch (e) { return; }
                            const tip = document.createElement('div');
                            tip.id = 'hs-tip';
                            tip.setAttribute('role', 'dialog');
                            tip.innerHTML = `<style>
                                    #hs-tip { position: fixed; left: 50%; bottom: calc(16px + env(safe-area-inset-bottom, 0px)); transform: translateX(-50%); z-index: 2500; width: min(440px, calc(100vw - 32px));
                                        box-sizing: border-box; padding: 14px 16px; border-radius: 12px; background: var(--bg-secondary, #fff); color: var(--text-primary, #1b1f24);
                                        border: 1px solid var(--border-color, #d5dbe3); box-shadow: 0 6px 24px rgba(0, 0, 0, 0.22); font-size: 14px; line-height: 1.45; display: flex; gap: 12px; align-items: flex-start; }
                                    #hs-tip img { width: 44px; height: 44px; border-radius: 10px; flex: none; }
                                    #hs-tip .hs-note { font-size: 13px; opacity: 0.8; margin-top: 4px; }
                                    #hs-tip .hs-btns { display: flex; justify-content: flex-end; margin-top: 10px; }
                                </style>
                                <img src="/apple-touch-icon.png" alt="">
                                <div style="flex: 1;">
                                    <strong>Add OPTIC to your Home Screen</strong>
                                    <div class="hs-note">It opens like an app, full screen, and can send you notifications. Tap the <strong>Share</strong> button <span aria-hidden="true">(□↑)</span> in Safari (or <strong>•••</strong> then Share), then <strong>Add to Home Screen</strong>.</div>
                                    <div class="hs-btns"><button type="button" class="btn btn-primary" id="hs-ok">Got it</button></div>
                                </div>`;
                            document.body.appendChild(tip);
                            tip.querySelector('#hs-ok').addEventListener('click', () => tip.remove());
                        };
                        const start = () => { check(); homeScreenTip(); };
                        if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
                    })();
                </script>
""";

    // The Notifications section of My Settings: this device on or off, the daily time, the evening reminder,
    // TITN treasury alerts, and every device that gets them (each removable).
    const string SettingsNotificationsHtml = """
                            <div class="st-section">
                                <h3>Notifications</h3>
                                <div id="st-push"><p class="st-note">Loading...</p></div>
                                <div id="st-push-status" class="st-note" style="margin-top: 6px;"></div>
                            </div>
                            <script>
                                (function () {
                                    const $ = (id) => document.getElementById(id);
                                    const esc = (v) => String(v ?? '').replace(/[&<>"']/g, ch => '&#' + ch.charCodeAt(0) + ';');
                                    const zone = () => { try { return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'; } catch (e) { return 'UTC'; } };
                                    const hourOpts = (h) => Array.from({ length: 22 }, (_, i) => `<option value="${i}"${i === h ? ' selected' : ''}>${new Date(Date.UTC(2000, 0, 1, i)).toLocaleTimeString('en-US', { hour: 'numeric', timeZone: 'UTC' })}</option>`).join('');
                                    const post = (url, body) => fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
                                    const say = (t) => { $('st-push-status').textContent = t; };
                                    const render = async () => {
                                        const P = window.opticPush, box = $('st-push');
                                        if (!P) { box.innerHTML = '<p class="st-note">Notifications are not available on this page.</p>'; return; }
                                        const s = await P.state();
                                        let device;
                                        if (s.support === 'insecure') device = 'This device: notifications need a secure connection (https:// with a certificate this device trusts, see Admin → HTTPS).';
                                        else if (s.support === 'ios-home-screen') device = 'This device: on iPhone, notifications only work in the Home Screen app. Tap Share → <strong>Add to Home Screen</strong>, open OPTIC from that icon, then turn them on here (iOS 16.4 or later).';
                                        else if (s.support === 'none') device = 'This device: this browser can’t receive push notifications.';
                                        else if (s.permission === 'denied') device = 'This device: <strong>blocked</strong>. ' + (P.ios ? 'Allow them in iPhone Settings → Notifications → OPTIC, then reopen the app.' : 'Allow them in this browser’s site settings for OPTIC, then reload the page.');
                                        else if (s.on) device = 'This device: <strong>On</strong> <button type="button" class="btn btn-secondary" id="st-push-off" style="margin-left: 8px;">Turn off</button>';
                                        else device = 'This device: <strong>Off</strong> <button type="button" class="btn btn-primary" id="st-push-on" style="margin-left: 8px;">Turn on notifications</button>';
                                        const st = s.status, titn = st && st.titn;
                                        const mine = s.sub && s.sub.endpoint;
                                        const devices = st ? (st.devices || []) : [];
                                        box.innerHTML = `<p style="margin: 0 0 10px;">${device}</p>`
                                            + (st ? `<div class="st-row" style="margin-bottom: 8px;">
                                                    <div class="form-group" style="flex: 0 0 220px;"><label for="st-push-hour">Selling-day reminder at</label><select id="st-push-hour">${hourOpts(st.hour)}</select></div>
                                                </div>
                                                <label class="st-check"><input type="checkbox" id="st-push-evening"${st.evening ? ' checked' : ''}> Remind me again at 6 PM if I haven’t sold</label>`
                                                + (titn ? `<label class="st-check"><input type="checkbox" id="st-push-titn"${titn.on ? ' checked' : ''}> TITN treasury alerts (moves of ${Number(titn.min || 1000000).toLocaleString('en-US')} TITN or more, and fee split changes)</label>` : '')
                                                + `<p class="st-note" style="margin-top: 8px;">Times are in ${esc(st.timeZone || zone())}.${st.lastReceived ? ' Last message received by a device: ' + esc(st.lastReceived) + '.' : ''}</p>`
                                                + (devices.length ? `<div class="data-table-wrap pnl-table-wrap"><table class="data-table"><thead><tr><th>Device</th><th>Added</th><th></th></tr></thead><tbody>${devices.map(d => `<tr><td>${esc(d.device || 'Device')}${d.endpoint === mine ? ' <span class="st-badge">This device</span>' : ''}</td><td>${d.added ? esc(new Date(d.added).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' })) : ''}</td><td><button type="button" class="btn btn-secondary st-push-remove" data-ep="${esc(d.endpoint)}">Remove</button></td></tr>`).join('')}</tbody></table></div>` : '<p class="st-note">No device gets notifications yet.</p>')
                                                : '');
                                        if ($('st-push-on')) $('st-push-on').addEventListener('click', async () => {
                                            $('st-push-on').disabled = true; say('');
                                            try { await P.enable(); say('Notifications are on for this device.'); } catch (e) { say('Could not turn notifications on: ' + (e.message || e)); }
                                            render();
                                        });
                                        if ($('st-push-off')) $('st-push-off').addEventListener('click', async () => { await P.disable(); say('Notifications are off on this device.'); render(); });
                                        box.querySelectorAll('.st-push-remove').forEach(b => b.addEventListener('click', async () => {
                                            if (!confirm('Stop sending notifications to this device?')) return;
                                            await P.disable(b.dataset.ep); say('Device removed.'); render();
                                        }));
                                        const saveTimes = async () => {
                                            const r = await post('/api/push/settings', { hour: Number($('st-push-hour').value), timeZone: zone(), evening: $('st-push-evening').checked });
                                            say(r.ok ? 'Saved.' : 'Could not save.');
                                        };
                                        if ($('st-push-hour')) $('st-push-hour').addEventListener('change', saveTimes);
                                        if ($('st-push-evening')) $('st-push-evening').addEventListener('change', saveTimes);
                                        if ($('st-push-titn')) $('st-push-titn').addEventListener('change', async () => {
                                            const r = await post('/api/titn/watch/settings', { on: $('st-push-titn').checked, min: titn.min, liquidity: titn.liquidity });
                                            say(r.ok ? 'Saved.' : 'Could not save.');
                                        });
                                    };
                                    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', render); else render();
                                })();
                            </script>
""";
}
