window.ascendraCharts = {
  render: (id, data, layout, config, dotNetReference) => {
    const target = document.getElementById(id);
    if (!target || !window.Plotly) return;
    const dark = Boolean(target.closest('.theme-dark'));
    const axisColor = dark ? '#edf4ff' : '#111827';
    const muted = dark ? '#a9bad0' : '#172235';
    const grid = dark ? 'rgba(157,180,214,.16)' : 'rgba(47,68,94,.16)';
    const baseLayout = {
      ...layout,
      autosize: true,
      font: { ...(layout.font || {}), color: dark ? '#edf4ff' : (layout.font?.color || '#111827') },
      title: { ...(layout.title || {}), font: { ...(layout.title?.font || {}), color: axisColor } },
      xaxis: { ...(layout.xaxis || {}), gridcolor: grid, linecolor: grid, tickfont: { ...(layout.xaxis?.tickfont || {}), color: axisColor } },
      yaxis: { ...(layout.yaxis || {}), gridcolor: grid, linecolor: grid, tickfont: { ...(layout.yaxis?.tickfont || {}), color: muted } },
      legend: { ...(layout.legend || {}), font: { ...(layout.legend?.font || {}), color: muted } },
      hoverlabel: {
        bgcolor: 'rgba(14, 27, 45, .94)',
        bordercolor: 'rgba(210, 228, 250, .38)',
        font: { color: '#f7fbff', family: '-apple-system, BlinkMacSystemFont, Segoe UI, sans-serif' }
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
