window.ascendraSession = (() => {
    const idleTimeoutMs = 2 * 60 * 60 * 1000;
    const keepAliveThrottleMs = 5 * 60 * 1000;
    let active = false;
    let signingOut = false;
    let idleTimer = null;
    let lastActivityAt = 0;
    let lastKeepAliveAt = 0;

    const activityEvents = ["pointerdown", "keydown", "touchstart", "scroll", "focus"];

    const antiforgeryToken = () =>
        document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? "";

    const post = (url) => fetch(url, {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/x-www-form-urlencoded; charset=UTF-8" },
        body: new URLSearchParams({ __RequestVerificationToken: antiforgeryToken() }),
    });

    const removeListeners = () => {
        activityEvents.forEach((eventName) => window.removeEventListener(eventName, registerActivity));
        document.removeEventListener("visibilitychange", onVisibilityChange);
    };

    const scheduleExpiry = () => {
        window.clearTimeout(idleTimer);
        const elapsed = Date.now() - lastActivityAt;
        idleTimer = window.setTimeout(expireSession, Math.max(0, idleTimeoutMs - elapsed) + 25);
    };

    async function expireSession() {
        if (!active || signingOut) return;

        if (Date.now() - lastActivityAt < idleTimeoutMs) {
            scheduleExpiry();
            return;
        }

        signingOut = true;
        stop();
        try {
            await post("/account/logout");
        } catch {
            // La navegación siguiente mantiene al usuario fuera aunque falle una petición transitoria.
        } finally {
            window.location.replace("/login?timeout=1");
        }
    }

    function requestKeepAlive() {
        const now = Date.now();
        if (now - lastKeepAliveAt < keepAliveThrottleMs) return;
        lastKeepAliveAt = now;
        void post("/account/keepalive").catch(() => {
            // No cerramos una sesión activa por una interrupción puntual de red.
        });
    }

    function registerActivity() {
        if (!active || signingOut) return;
        if (Date.now() - lastActivityAt >= idleTimeoutMs) {
            void expireSession();
            return;
        }

        lastActivityAt = Date.now();
        scheduleExpiry();
        requestKeepAlive();
    }

    function onVisibilityChange() {
        if (!document.hidden) registerActivity();
    }

    function start() {
        stop();
        active = true;
        signingOut = false;
        lastActivityAt = Date.now();
        lastKeepAliveAt = Date.now();
        activityEvents.forEach((eventName) => window.addEventListener(eventName, registerActivity, { passive: true }));
        document.addEventListener("visibilitychange", onVisibilityChange);
        scheduleExpiry();
    }

    function stop() {
        active = false;
        window.clearTimeout(idleTimer);
        idleTimer = null;
        removeListeners();
    }

    return { start, stop };
})();

// El estado de lectura es una preferencia local por usuario, no contiene el
// texto de los mensajes. Conserva los avisos ya atendidos entre reconexiones
// del circuito interactivo y actualizaciones visuales de la aplicación.
window.ascendraChatRead = (() => {
    const keyFor = (username) => `ascendra.chat-read.v1.${String(username ?? "").trim().toLowerCase()}`;

    function load(username) {
        try {
            const stored = window.localStorage.getItem(keyFor(username));
            if (!stored) return { hasState: false, ids: [] };

            const parsed = JSON.parse(stored);
            return {
                hasState: true,
                ids: Array.isArray(parsed?.ids)
                    ? parsed.ids.filter((id) => typeof id === "string").slice(-8000)
                    : [],
            };
        } catch {
            return { hasState: false, ids: [] };
        }
    }

    function save(username, ids) {
        try {
            const normalizedIds = Array.isArray(ids)
                ? ids.filter((id) => typeof id === "string").slice(-8000)
                : [];
            window.localStorage.setItem(keyFor(username), JSON.stringify({ ids: normalizedIds }));
        } catch {
            // No interrumpimos el chat si el navegador tiene el almacenamiento restringido.
        }
    }

    return { load, save };
})();
