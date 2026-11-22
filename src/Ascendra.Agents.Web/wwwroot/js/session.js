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

window.ascendraChat = (() => {
    function scrollToBottom(element) {
        if (!element) return;

        const scroll = () => {
            element.scrollTop = element.scrollHeight;
        };

        scroll();
        window.requestAnimationFrame(scroll);
    }

    return { scrollToBottom };
})();

// Los seguimientos se presentan siempre dentro de Ascendra y, si el agente
// acepta el permiso del navegador, también como notificación del sistema.
// No se almacena nada adicional en el navegador.
window.ascendraReminders = (() => {
    let audioContext = null;
    let lastRingAt = 0;

    async function armSound() {
        const AudioContext = window.AudioContext || window.webkitAudioContext;
        if (!AudioContext) return false;

        try {
            audioContext ??= new AudioContext();
            if (audioContext.state === "suspended") await audioContext.resume();
            return audioContext.state === "running";
        } catch {
            // El indicador visual sigue funcionando si el navegador bloquea audio.
            return false;
        }
    }

    // Los navegadores permiten que el sitio prepare audio solamente después de
    // una interacción real. De esta forma el timbre puede sonar más adelante,
    // sin pedir permisos ni mostrar un diálogo al agente.
    const armOnInteraction = () => { void armSound(); };
    window.addEventListener("pointerdown", armOnInteraction, { passive: true });
    window.addEventListener("keydown", armOnInteraction, { passive: true });

    function chime(context, startAt, frequency) {
        const oscillator = context.createOscillator();
        const gain = context.createGain();
        oscillator.type = "sine";
        oscillator.frequency.setValueAtTime(frequency, startAt);
        gain.gain.setValueAtTime(0.0001, startAt);
        gain.gain.exponentialRampToValueAtTime(0.065, startAt + 0.012);
        gain.gain.exponentialRampToValueAtTime(0.0001, startAt + 0.31);
        oscillator.connect(gain).connect(context.destination);
        oscillator.start(startAt);
        oscillator.stop(startAt + 0.33);
    }

    function ring() {
        const now = Date.now();
        if (now - lastRingAt < 8000) return;
        lastRingAt = now;

        const context = audioContext;
        if (!context || context.state !== "running") return;

        try {
            const startAt = context.currentTime + 0.03;
            chime(context, startAt, 880);
            chime(context, startAt + 0.19, 1047);
        } catch {
            // No interrumpimos el recordatorio si el dispositivo no permite audio.
        }
    }

    async function enable() {
        if (!("Notification" in window)) return "unsupported";
        if (Notification.permission !== "default") return Notification.permission;

        try {
            return await Notification.requestPermission();
        } catch {
            return "denied";
        }
    }

    function notify(title, body, tag) {
        if (!("Notification" in window) || Notification.permission !== "granted") return;

        try {
            const notification = new Notification(title, {
                body,
                tag: `ascendra-reminder-${tag}`,
                renotify: true,
            });
            notification.onclick = () => window.focus();
        } catch {
            // El aviso dentro de Ascendra sigue disponible aunque el navegador
            // tenga bloqueadas las notificaciones del sistema.
        }
    }

    return { enable, notify, ring };
})();

// Un despliegue sustituye el proceso que aloja el circuito interactivo. En vez
// de mostrar el mensaje técnico de Blazor o recargar contra un 502, esperamos
// a que la nueva instancia responda y recuperamos la vista automáticamente.
window.ascendraReconnect = (() => {
    let activeProcess = null;

    const wait = (milliseconds) => new Promise((resolve) => window.setTimeout(resolve, milliseconds));
    const modal = () => document.getElementById("components-reconnect-modal");
    const message = () => document.getElementById("components-reconnect-message");

    function show(text) {
        const reconnectModal = modal();
        if (reconnectModal) reconnectModal.classList.add("ascendra-reconnect--visible");
        const reconnectMessage = message();
        if (reconnectMessage) reconnectMessage.textContent = text;
    }

    function hide() {
        modal()?.classList.remove("ascendra-reconnect--visible");
    }

    async function serviceIsReady() {
        try {
            const response = await fetch("/health", {
                cache: "no-store",
                credentials: "same-origin",
                headers: { "Cache-Control": "no-cache" },
            });
            return response.ok;
        } catch {
            return false;
        }
    }

    function startReconnectionProcess() {
        let cancelled = false;

        (async () => {
            let attempt = 0;
            while (!cancelled) {
                attempt += 1;
                show(attempt <= 4
                    ? `Reconectando de forma segura… intento ${attempt}`
                    : "La actualización sigue en curso. Conservamos tu sesión.");

                try {
                    const rejoined = await window.Blazor.reconnect();
                    if (cancelled) return;
                    if (rejoined) {
                        hide();
                        return;
                    }

                    // La nueva instancia ya está disponible, pero el circuito
                    // anterior pertenecía al proceso reemplazado. Una carga
                    // completa restablece la vista con la misma cookie.
                    if (await serviceIsReady()) {
                        show("Actualización lista. Restaurando tu vista…");
                        await wait(250);
                        window.location.reload();
                        return;
                    }
                } catch {
                    // Render aún está iniciando la siguiente instancia.
                }

                await wait(Math.min(1500 + attempt * 500, 5000));
            }
        })();

        return {
            cancel: () => {
                cancelled = true;
                hide();
            },
        };
    }

    function start() {
        if (!window.Blazor) {
            window.setTimeout(start, 25);
            return;
        }

        window.Blazor.start({
            reconnectionHandler: {
                onConnectionDown: () => activeProcess ??= startReconnectionProcess(),
                onConnectionUp: () => {
                    activeProcess?.cancel();
                    activeProcess = null;
                    hide();
                },
            },
        });
    }

    return { start };
})();
