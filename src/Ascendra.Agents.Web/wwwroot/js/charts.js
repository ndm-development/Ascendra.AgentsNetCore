window.ascendraCharts = {
  // Plotly pesa varios megabytes y sólo hace falta en las dos vistas analíticas
  // de agentes. Cargarlo al pintar la primera gráfica deja que Referencias,
  // WhatsApp y la consola de Equipo queden listos de inmediato.
  ensurePlotly: () => {
    if (window.Plotly) return Promise.resolve(window.Plotly);
    if (window.__ascendraPlotlyPromise) return window.__ascendraPlotlyPromise;
    window.__ascendraPlotlyPromise = new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = 'https://cdn.plot.ly/plotly-2.35.2.min.js';
      script.async = true;
      script.onload = () => window.Plotly ? resolve(window.Plotly) : reject(new Error('Plotly no se cargó.'));
      script.onerror = () => reject(new Error('No se pudo cargar Plotly.'));
      document.head.appendChild(script);
    });
    return window.__ascendraPlotlyPromise;
  },
  render: (id, data, layout, config, dotNetReference) => {
    const target = document.getElementById(id);
    if (!target) return Promise.resolve();
    target.__ascendraBaseData = data;
    target.__ascendraBaseLayout = layout;
    target.__ascendraConfig = { responsive: true, displayModeBar: false, ...config };
    const themedData = window.ascendraCharts.dataForTheme(data, target);
    const themedLayout = window.ascendraCharts.layoutForTheme(layout, target);
    const detachClick = () => {
      if (target.__ascendraPlotlyClick && typeof target.removeListener === 'function') {
        target.removeListener('plotly_click', target.__ascendraPlotlyClick);
      }
      target.__ascendraPlotlyClick = null;
    };
    const attachClick = () => {
      detachClick();
      if (!dotNetReference || typeof target.on !== 'function') return;
      target.__ascendraPlotlyClick = event => {
        const point = event?.points?.[0];
        if (!point) return;
        const x = point.x == null ? '' : String(point.x);
        const traceName = point.data?.name ?? point.fullData?.name ?? '';
        dotNetReference.invokeMethodAsync('OnPlotlyClick', { x, traceName: String(traceName) })
          .catch(() => { /* La pantalla pudo cambiar antes de resolver el clic. */ });
      };
      target.on('plotly_click', target.__ascendraPlotlyClick);
    };
    const clearHover = () => {
      try { window.Plotly?.Fx?.unhover?.(target); }
      catch (_) { /* La gráfica pudo ser reemplazada durante el renderizado. */ }
    };
    const detachHover = () => {
      if (target.__ascendraMouseLeave) {
        target.removeEventListener('mouseleave', target.__ascendraMouseLeave);
      }
      target.__ascendraMouseLeave = null;
    };
    const attachHover = () => {
      detachHover();
      target.__ascendraMouseLeave = clearHover;
      target.addEventListener('mouseleave', target.__ascendraMouseLeave);
    };
    const attachInteractions = () => {
      attachClick();
      attachHover();
    };
    target.__ascendraClearHover = clearHover;
    target.__ascendraRebindInteractions = attachInteractions;
    return window.ascendraCharts.ensurePlotly()
      .then(() => window.ascendraCharts.queueRender(target, () => {
        clearHover();
        return window.Plotly.react(target, themedData, themedLayout, target.__ascendraConfig)
          .then(attachInteractions);
      }))
      .catch(() => { /* La pantalla mantiene sus tablas aun si la CDN falla. */ });
  },
  // Mantiene ordenados los renders que puede disparar Blazor al mismo tiempo
  // que se cambia la apariencia. Plotly nunca pinta dos estados en paralelo.
  queueRender: (target, render) => {
    const prior = target.__ascendraRenderQueue || Promise.resolve();
    const next = prior.catch(() => undefined).then(render);
    target.__ascendraRenderQueue = next;
    return next;
  },
  layoutForTheme: (layout, target) => {
    const theme = window.ascendraCharts.themeFor(target);
    return {
      ...(layout || {}),
      autosize: true,
      // Nunca dejamos que Plotly interpole un lienzo blanco entre cambios de tema:
      // las gráficas se dibujan sobre el vidrio de la tarjeta.
      paper_bgcolor: 'rgba(0,0,0,0)',
      plot_bgcolor: 'rgba(0,0,0,0)',
      font: { ...((layout || {}).font || {}), color: theme.ink || layout?.font?.color || '#111827' },
      title: { ...((layout || {}).title || {}), font: { ...((layout || {}).title?.font || {}), color: theme.axis } },
      xaxis: { ...((layout || {}).xaxis || {}), gridcolor: theme.grid, linecolor: theme.grid, tickfont: { ...((layout || {}).xaxis?.tickfont || {}), color: theme.axis } },
      yaxis: { ...((layout || {}).yaxis || {}), gridcolor: theme.grid, linecolor: theme.grid, tickfont: { ...((layout || {}).yaxis?.tickfont || {}), color: theme.muted } },
      legend: { ...((layout || {}).legend || {}), font: { ...((layout || {}).legend?.font || {}), color: theme.muted } },
      hoverlabel: {
        bgcolor: theme.hoverBackground,
        bordercolor: theme.hoverBorder,
        font: { color: theme.hoverInk, family: '-apple-system, BlinkMacSystemFont, Segoe UI, sans-serif' }
      }
    };
  },
  themeFor: target => {
    const shell = target.closest('.app-shell');
    if (shell?.classList.contains('theme-dark')) {
      return { ink: '#edf4ff', axis: '#edf4ff', muted: '#a9bad0', grid: 'rgba(157,180,214,.16)', hoverBackground: 'rgba(14, 27, 45, .94)', hoverBorder: 'rgba(210, 228, 250, .38)', hoverInk: '#f7fbff' };
    }
    if (shell?.classList.contains('theme-pink')) {
      return { ink: '#281735', axis: '#281735', muted: '#765b79', grid: 'rgba(125, 73, 137, .17)', hoverBackground: 'rgba(54, 27, 61, .94)', hoverBorder: 'rgba(255, 213, 239, .55)', hoverInk: '#fff8fd' };
    }
    if (shell?.classList.contains('theme-sierra')) {
      return { ink: '#20362f', axis: '#20362f', muted: '#5b7168', grid: 'rgba(47, 78, 67, .18)', hoverBackground: 'rgba(26, 53, 47, .95)', hoverBorder: 'rgba(233, 211, 157, .56)', hoverInk: '#fffdf7' };
    }
    if (shell?.classList.contains('theme-aurora')) {
      return { ink: '#eefbff', axis: '#effdff', muted: '#c4ddea', grid: 'rgba(158, 218, 238, .19)', hoverBackground: 'rgba(6, 22, 43, .96)', hoverBorder: 'rgba(153, 238, 222, .45)', hoverInk: '#f6ffff' };
    }
    if (shell?.classList.contains('theme-oceano')) {
      return { ink: '#effbff', axis: '#f0fdff', muted: '#c1dde8', grid: 'rgba(151, 219, 230, .19)', hoverBackground: 'rgba(4, 30, 52, .96)', hoverBorder: 'rgba(129, 230, 233, .42)', hoverInk: '#f5ffff' };
    }
    if (shell?.classList.contains('theme-ambar')) {
      return { ink: '#fff4e5', axis: '#fff4e5', muted: '#e3c7a6', grid: 'rgba(234, 189, 132, .18)', hoverBackground: 'rgba(38, 28, 23, .96)', hoverBorder: 'rgba(255, 206, 144, .45)', hoverInk: '#fff9f0' };
    }
    if (shell?.classList.contains('theme-kintsugi')) {
      return { ink: '#2b2119', axis: '#382713', muted: '#6e5b45', grid: 'rgba(139, 91, 8, .22)', hoverBackground: 'rgba(44, 31, 17, .97)', hoverBorder: 'rgba(255, 212, 105, .78)', hoverInk: '#fff8e4' };
    }
    return { ink: '#111827', axis: '#111827', muted: '#172235', grid: 'rgba(47,68,94,.16)', hoverBackground: 'rgba(14, 27, 45, .94)', hoverBorder: 'rgba(210, 228, 250, .38)', hoverInk: '#f7fbff' };
  },
  dataForTheme: (data, target) => {
    const isPink = Boolean(target.closest('.theme-pink'));
    const isSierra = Boolean(target.closest('.theme-sierra'));
    const isAurora = Boolean(target.closest('.theme-aurora'));
    const isOceano = Boolean(target.closest('.theme-oceano'));
    const isAmbar = Boolean(target.closest('.theme-ambar'));
    const isKintsugi = Boolean(target.closest('.theme-kintsugi'));
    const palette = isKintsugi ? {
      status: { 'Esperando pago': '#d99a14', Pagada: '#328a72', 'No pagó': '#c65b58', Robada: '#b96f19', Duplicada: '#7560a8' },
      recovery: { '#1f9b72': '#328a72', '#c38d25': '#d99a14', '#c74857': '#c65b58', 'rgba(124,145,169,.48)': 'rgba(132, 101, 52, .43)' },
      line: 'rgba(255, 247, 219, .92)',
      externalText: '#2b2119',
      heatmap: [[0, '#fff8e6'], [.28, '#f6da8a'], [.57, '#e0ad36'], [.78, '#b8730c'], [1, '#704311']]
    } : isAmbar ? {
      status: { 'Esperando pago': '#dca45d', Pagada: '#d1a264', 'No pagó': '#c8776b', Robada: '#e1b360', Duplicada: '#b796c8' },
      recovery: { '#1f9b72': '#d1a264', '#c38d25': '#e1b360', '#c74857': '#c8776b', 'rgba(124,145,169,.48)': 'rgba(185, 139, 105, .43)' },
      line: 'rgba(255, 239, 214, .68)',
      externalText: '#fff3e2'
    } : isSierra ? {
      status: { 'Esperando pago': '#6d9ed4', Pagada: '#67ad8d', 'No pagó': '#b97878', Robada: '#d2a449', Duplicada: '#9981be' },
      recovery: { '#1f9b72': '#67ad8d', '#c38d25': '#d2a449', '#c74857': '#b97878', 'rgba(124,145,169,.48)': 'rgba(119, 143, 132, .46)' },
      line: 'rgba(255,250,239,.70)',
      externalText: '#20362f'
    } : isPink ? {
      status: { 'Esperando pago': '#99bbed', Pagada: '#8ecba7', 'No pagó': '#ae90dc', Robada: '#e2bd47', Duplicada: '#9c86cf' },
      recovery: { '#1f9b72': '#8ecba7', '#c38d25': '#e2bd47', '#c74857': '#ae90dc', 'rgba(124,145,169,.48)': 'rgba(161, 151, 180, .45)' },
      line: 'rgba(255,255,255,.72)',
      externalText: '#281735'
    } : isAurora ? {
      status: { 'Esperando pago': '#7bbdf4', Pagada: '#65d9c1', 'No pagó': '#bf8bd7', Robada: '#9e97e5', Duplicada: '#89a5e2' },
      recovery: { '#1f9b72': '#65d9c1', '#c38d25': '#8da8f5', '#c74857': '#bf8bd7', 'rgba(124,145,169,.48)': 'rgba(130, 160, 198, .42)' },
      line: 'rgba(218,253,252,.64)',
      externalText: '#effdff'
    } : isOceano ? {
      status: { 'Esperando pago': '#62bddd', Pagada: '#55c9b7', 'No pagó': '#6b93c5', Robada: '#78a8d1', Duplicada: '#789dd4' },
      recovery: { '#1f9b72': '#55c9b7', '#c38d25': '#4fa6cf', '#c74857': '#678bc3', 'rgba(124,145,169,.48)': 'rgba(120, 176, 195, .38)' },
      line: 'rgba(220,253,255,.62)',
      externalText: '#f0fdff'
    } : null;
    if (!palette) return data;
    return data.map(trace => {
      if (trace?.type === 'heatmap' && palette.heatmap) {
        return {
          ...trace,
          colorscale: palette.heatmap,
          colorbar: { ...(trace.colorbar || {}), tickfont: { ...((trace.colorbar || {}).tickfont || {}), color: palette.externalText } }
        };
      }
      if (trace?.type !== 'bar' || !trace.marker) return trace;
      const statusColor = palette.status[trace.name];
      const color = statusColor
        ? statusColor
        : Array.isArray(trace.marker.color)
          ? trace.marker.color.map(value => palette.recovery[value] ?? value)
          : trace.marker.color;
      return {
        ...trace,
        ...(trace.textposition === 'outside' ? { textfont: { ...(trace.textfont || {}), color: palette.externalText } } : {}),
        marker: { ...trace.marker, color, line: { ...(trace.marker.line || {}), color: palette.line } }
      };
    });
  },
  refreshTheme: () => {
    if (!window.Plotly) return Promise.resolve();
    // Un único react evita que restyle y relayout compitan por las capas SVG.
    // También devolvemos la promesa para que Blazor no continúe hasta terminar.
    return Promise.all([...document.querySelectorAll('.plotly-chart')]
      .filter(target => target.__ascendraBaseData && target.__ascendraBaseLayout)
      .map(target => {
        const themedData = window.ascendraCharts.dataForTheme(target.__ascendraBaseData, target);
        const themedLayout = window.ascendraCharts.layoutForTheme(target.__ascendraBaseLayout, target);
        return window.ascendraCharts.queueRender(target, () => {
          target.__ascendraClearHover?.();
          return window.Plotly.react(
            target,
            themedData,
            themedLayout,
            target.__ascendraConfig || { responsive: true, displayModeBar: false }
          ).then(() => target.__ascendraRebindInteractions?.());
        });
      }));
  },
  dispose: id => {
    const target = document.getElementById(id);
    target?.__ascendraClearHover?.();
    if (target?.__ascendraPlotlyClick && typeof target.removeListener === 'function') {
      target.removeListener('plotly_click', target.__ascendraPlotlyClick);
    }
    if (target?.__ascendraMouseLeave) {
      target.removeEventListener('mouseleave', target.__ascendraMouseLeave);
    }
    if (target) {
      target.__ascendraPlotlyClick = null;
      target.__ascendraMouseLeave = null;
      target.__ascendraClearHover = null;
      target.__ascendraRebindInteractions = null;
      target.__ascendraBaseData = null;
      target.__ascendraBaseLayout = null;
      target.__ascendraConfig = null;
      target.__ascendraRenderQueue = null;
    }
    if (target && window.Plotly) window.Plotly.purge(target);
  }
};

window.ascendraClipboard = {
  copy: async text => {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return;
    }
    const area = document.createElement('textarea');
    area.value = text;
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    document.execCommand('copy');
    area.remove();
  }
};
