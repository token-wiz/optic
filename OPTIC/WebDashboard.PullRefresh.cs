// Pull down to refresh, for OPTIC opened from a phone's Home Screen (standalone web app), where the browser's own
// pull-to-refresh isn't there. At the top of the page, pulling down past the spinner reloads the page.
public static partial class WebDashboard
{
    const string PullRefreshHtml = """

                <div id="ptr" aria-hidden="true"><span id="ptr-icon">↻</span></div>
                <style>
                    #ptr { position: fixed; top: env(safe-area-inset-top, 0); left: 50%; transform: translate(-50%, -48px); z-index: 3000; width: 38px; height: 38px; border-radius: 50%;
                        background: var(--bg-secondary, #fff); box-shadow: 0 2px 10px rgba(0, 0, 0, 0.25); display: flex; align-items: center; justify-content: center;
                        font-size: 22px; font-weight: 700; color: var(--accent-primary, #0078d4); opacity: 0; pointer-events: none; }
                    #ptr.go #ptr-icon { animation: ptr-spin 0.8s linear infinite; }
                    @keyframes ptr-spin { to { transform: rotate(360deg); } }
                </style>
                <script>
                    (function () {
                        const standalone = window.navigator.standalone === true || window.matchMedia('(display-mode: standalone)').matches;
                        if (!standalone || !('ontouchstart' in window)) return;
                        const el = document.getElementById('ptr'), icon = document.getElementById('ptr-icon');
                        const PULL = 75;
                        let y0 = null, dy = 0, busy = false;
                        // Only when the page is at the top and the touch isn't inside something that scrolls on its own (a table, the bell list).
                        const insideScroller = (t) => { for (let n = t; n && n !== document.body; n = n.parentElement) if (n.scrollTop > 0) return true; return false; };
                        const reset = () => { el.style.transition = 'transform 0.2s, opacity 0.2s'; el.style.opacity = 0; el.style.transform = 'translate(-50%, -48px)'; };
                        document.addEventListener('touchstart', (e) => {
                            y0 = null;
                            if (busy || window.scrollY > 0 || e.touches.length !== 1 || insideScroller(e.target)) return;
                            y0 = e.touches[0].clientY; dy = 0; el.style.transition = 'none';
                        }, { passive: true });
                        document.addEventListener('touchmove', (e) => {
                            if (y0 == null) return;
                            if (window.scrollY > 0) { y0 = null; reset(); return; }
                            dy = Math.max(0, e.touches[0].clientY - y0);
                            const p = Math.min(1, dy / PULL);
                            el.style.opacity = p;
                            el.style.transform = `translate(-50%, ${Math.min(dy, PULL) - 40}px)`;
                            icon.style.transform = `rotate(${p * 300}deg)`;
                        }, { passive: true });
                        document.addEventListener('touchend', () => {
                            if (y0 == null) return;
                            y0 = null;
                            if (dy >= PULL) { busy = true; icon.style.transform = ''; el.classList.add('go'); location.reload(); }
                            else reset();
                            dy = 0;
                        }, { passive: true });
                    })();
                </script>
""";
}
