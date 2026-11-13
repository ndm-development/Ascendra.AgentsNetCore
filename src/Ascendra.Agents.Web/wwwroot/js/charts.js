window.ascendraCharts = {
  render: (id, data, layout, config, dotNetReference) => {
    const target = document.getElementById(id);
    if (!target || !window.Plotly) return;
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
    clearHover();
    return window.Plotly.react(target, themedData, themedLayout, target.__ascendraConfig)
      .then(attachInteractions);
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
    return { ink: '#111827', axis: '#111827', muted: '#172235', grid: 'rgba(47,68,94,.16)', hoverBackground: 'rgba(14, 27, 45, .94)', hoverBorder: 'rgba(210, 228, 250, .38)', hoverInk: '#f7fbff' };
  },
  dataForTheme: (data, target) => {
    if (!target.closest('.theme-pink')) return data;
    const statusPalette = {
      Vigentes: '#99bbed',
      Activas: '#8ecba7',
      Incumplidas: '#ae90dc',
      Robadas: '#e2bd47',
      Duplicadas: '#9c86cf'
    };
    const recoveryPalette = {
      '#1f9b72': '#8ecba7',
      '#c38d25': '#e2bd47',
      '#c74857': '#ae90dc',
      'rgba(124,145,169,.48)': 'rgba(161, 151, 180, .45)'
    };
    return data.map(trace => {
      if (trace?.type !== 'bar' || !trace.marker) return trace;
      const statusColor = statusPalette[trace.name];
      const color = statusColor
        ? statusColor
        : Array.isArray(trace.marker.color)
          ? trace.marker.color.map(value => recoveryPalette[value] ?? value)
          : trace.marker.color;
      return {
        ...trace,
        textfont: { ...(trace.textfont || {}), color: '#281735' },
        marker: { ...trace.marker, color, line: { ...(trace.marker.line || {}), color: 'rgba(255,255,255,.72)' } }
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
        target.__ascendraClearHover?.();
        const themedData = window.ascendraCharts.dataForTheme(target.__ascendraBaseData, target);
        const themedLayout = window.ascendraCharts.layoutForTheme(target.__ascendraBaseLayout, target);
        return window.Plotly.react(
          target,
          themedData,
          themedLayout,
          target.__ascendraConfig || { responsive: true, displayModeBar: false }
        ).then(() => target.__ascendraRebindInteractions?.());
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
