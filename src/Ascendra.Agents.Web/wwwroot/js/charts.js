window.ascendraCharts = {
  render: (id, data, layout, config, dotNetReference) => {
    const target = document.getElementById(id);
    if (!target || !window.Plotly) return;
    const theme = window.ascendraCharts.themeFor(target);
    const baseLayout = {
      ...layout,
      autosize: true,
      font: { ...(layout.font || {}), color: theme.ink || layout.font?.color || '#111827' },
      title: { ...(layout.title || {}), font: { ...(layout.title?.font || {}), color: theme.axis } },
      xaxis: { ...(layout.xaxis || {}), gridcolor: theme.grid, linecolor: theme.grid, tickfont: { ...(layout.xaxis?.tickfont || {}), color: theme.axis } },
      yaxis: { ...(layout.yaxis || {}), gridcolor: theme.grid, linecolor: theme.grid, tickfont: { ...(layout.yaxis?.tickfont || {}), color: theme.muted } },
      legend: { ...(layout.legend || {}), font: { ...(layout.legend?.font || {}), color: theme.muted } },
      hoverlabel: {
        bgcolor: theme.hoverBackground,
        bordercolor: theme.hoverBorder,
        font: { color: theme.hoverInk, family: '-apple-system, BlinkMacSystemFont, Segoe UI, sans-serif' }
      }
    };
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
    return window.Plotly.react(target, data, baseLayout, { responsive: true, displayModeBar: false, ...config })
      .then(attachClick);
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
  refreshTheme: () => {
    if (!window.Plotly) return;
    document.querySelectorAll('.plotly-chart').forEach(target => {
      const theme = window.ascendraCharts.themeFor(target);
      window.Plotly.relayout(target, {
        'font.color': theme.ink,
        'title.font.color': theme.axis,
        'xaxis.gridcolor': theme.grid,
        'xaxis.linecolor': theme.grid,
        'xaxis.tickfont.color': theme.axis,
        'yaxis.gridcolor': theme.grid,
        'yaxis.linecolor': theme.grid,
        'yaxis.tickfont.color': theme.muted,
        'legend.font.color': theme.muted,
        'hoverlabel.bgcolor': theme.hoverBackground,
        'hoverlabel.bordercolor': theme.hoverBorder,
        'hoverlabel.font.color': theme.hoverInk
      });
    });
  },
  dispose: id => {
    const target = document.getElementById(id);
    if (target?.__ascendraPlotlyClick && typeof target.removeListener === 'function') {
      target.removeListener('plotly_click', target.__ascendraPlotlyClick);
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
