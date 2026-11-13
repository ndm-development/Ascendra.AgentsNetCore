window.ascendraCharts = {
  render: (id, data, layout, config, dotNetReference) => {
    const target = document.getElementById(id);
    if (!target || !window.Plotly) return;
    const theme = window.ascendraCharts.themeFor(target);
    target.__ascendraBaseData = data;
    const themedData = window.ascendraCharts.dataForTheme(data, target);
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
    return window.Plotly.react(target, themedData, baseLayout, { responsive: true, displayModeBar: false, ...config })
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
    if (!window.Plotly) return;
    document.querySelectorAll('.plotly-chart').forEach(target => {
      const theme = window.ascendraCharts.themeFor(target);
      const themedData = window.ascendraCharts.dataForTheme(target.__ascendraBaseData || target.data, target);
      const barIndices = themedData.map((trace, index) => trace?.type === 'bar' ? index : -1).filter(index => index >= 0);
      if (barIndices.length) {
        const bars = barIndices.map(index => themedData[index]);
        window.Plotly.restyle(target, {
          'marker.color': bars.map(trace => trace.marker?.color),
          'marker.line.color': bars.map(trace => trace.marker?.line?.color),
          'textfont.color': bars.map(trace => trace.textfont?.color)
        }, barIndices);
      }
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
