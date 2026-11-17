window.ascendraGlassSelect = (() => {
    const listeners = new WeakMap();

    const unwatch = (element) => {
        const listener = listeners.get(element);
        if (!listener) return;

        document.removeEventListener("pointerdown", listener, true);
        listeners.delete(element);
    };

    const watch = (element, callback) => {
        unwatch(element);

        const listener = (event) => {
            if (!element.contains(event.target)) {
                callback.invokeMethodAsync("CloseFromOutside").catch(() => { });
            }
        };

        listeners.set(element, listener);
        // The opening click completes before outside-click listening begins.
        // Confirm the listener is still current so a very fast close cannot
        // leave a detached handler registered on the document.
        window.setTimeout(() => {
            if (listeners.get(element) === listener) {
                document.addEventListener("pointerdown", listener, true);
            }
        }, 0);
    };

    return { watch, unwatch };
})();
