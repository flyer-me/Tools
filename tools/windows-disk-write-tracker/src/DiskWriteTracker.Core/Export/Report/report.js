/* DiskWriteTracker 报表前端：读取内嵌 JSON，在浏览器里完成筛选、排序、绘图与导出。
   无外部依赖、无网络请求。数据键名与 ReportData.cs 一一对应：
   d=日期 k=磁盘标识 s=起始累计 e=结束累计 x=日增量 m=方法 q=数据质量 g=缺失天数 n=样本数 */
(function () {
  'use strict';

  var dataEl = document.getElementById('dwt-data');
  if (!dataEl) { return; }

  var D;
  try { D = JSON.parse(dataEl.textContent); } catch (e) { return; }

  var app = document.getElementById('app');
  if (!app) { return; }

  var QUALITY_ORDER = ['reliable', 'fallback', 'low_confidence', 'anomaly', 'missing'];
  var PAGE_SIZE = 200;
  var MAX_POINTS = 400;
  var MAX_TABLE_ROWS = 3000;

  /* ---------------- 工具 ---------------- */

  function pad2(n) { return (n < 10 ? '0' : '') + n; }

  function parseDate(s) {
    var p = String(s).split('-');
    return new Date(Date.UTC(+p[0], +p[1] - 1, +p[2]));
  }

  function fmtDate(dt) {
    return dt.getUTCFullYear() + '-' + pad2(dt.getUTCMonth() + 1) + '-' + pad2(dt.getUTCDate());
  }

  function addDays(s, n) {
    var dt = parseDate(s);
    return fmtDate(new Date(dt.getTime() + n * 86400000));
  }

  function daysBetween(a, b) {
    return Math.round((parseDate(b) - parseDate(a)) / 86400000);
  }

  function dateRange(from, to) {
    var out = [];
    if (!from || !to || from > to) { return out; }
    var span = daysBetween(from, to);
    if (span > 4000) { from = addDays(to, -4000); span = 4000; }
    var dt = parseDate(from);
    for (var i = 0; i <= span; i++) {
      out.push(fmtDate(new Date(dt.getTime() + i * 86400000)));
    }
    return out;
  }

  function fmtBytes(n) {
    if (n === null || n === undefined) { return '—'; }
    var v = Number(n);
    if (!isFinite(v)) { return '—'; }
    var neg = v < 0;
    if (neg) { v = -v; }
    var units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
    var u = 0;
    while (v >= 1000 && u < units.length - 1) { v /= 1000; u++; }
    var s = v.toFixed(3);
    if (s.indexOf('.') >= 0) { s = s.replace(/\.?0+$/, ''); }
    return (neg ? '-' : '') + s + ' ' + units[u];
  }

  function fmtInt(n) {
    if (n === null || n === undefined) { return '—'; }
    return String(n).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  }

  function esc(s) {
    return String(s === null || s === undefined ? '' : s)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function el(tag, cls, text) {
    var n = document.createElement(tag);
    if (cls) { n.className = cls; }
    if (text !== undefined && text !== null) { n.textContent = String(text); }
    return n;
  }

  function qLabel(q) { return D.dict.quality[q] || q; }
  function mLabel(m) { return D.dict.method[m] || m; }

  var diskByKey = {};
  var colorList = [];
  D.disks.forEach(function (d) {
    diskByKey[d.key] = d;
    colorList.push({ key: d.key, model: d.model, color: d.color });
  });
  D.rows.forEach(function (r, i) { r._i = i; });

  /* ---------------- 状态 ---------------- */

  var allFrom = D.rows.length ? D.rows[0].d : D.range.from;
  var allTo = D.rows.length ? D.rows[D.rows.length - 1].d : D.range.to;

  var S = {
    disks: {},
    quality: {},
    from: allFrom,
    to: allTo,
    mode: 'bar',
    hidden: {},
    sort: [{ col: 'd', dir: -1 }],
    page: 1
  };

  D.disks.forEach(function (d) { S.disks[d.key] = true; });
  QUALITY_ORDER.forEach(function (q) { S.quality[q] = true; });

  function selectedDiskCount() {
    return Object.keys(S.disks).filter(function (k) { return S.disks[k]; }).length;
  }

  function isFiltered() {
    return selectedDiskCount() !== D.disks.length
      || S.from !== allFrom || S.to !== allTo
      || QUALITY_ORDER.some(function (q) { return !S.quality[q]; });
  }

  function visibleRows() {
    var from = S.from, to = S.to;
    return D.rows.filter(function (r) {
      return S.disks[r.k] && S.quality[r.q] && r.d >= from && r.d <= to;
    });
  }

  /* ---------------- 筛选条 ---------------- */

  function buildFilters() {
    var diskHost = document.getElementById('diskFilter');
    D.disks.forEach(function (d) {
      var lab = el('label', 'chip');
      var cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.checked = true;
      cb.addEventListener('change', function () {
        S.disks[d.key] = cb.checked;
        lab.classList.toggle('off', !cb.checked);
        render();
      });
      var dot = el('span', 'dot');
      dot.style.background = d.color;
      lab.appendChild(cb);
      lab.appendChild(dot);
      lab.appendChild(document.createTextNode(d.model));
      diskHost.appendChild(lab);
    });

    var qHost = document.getElementById('qualityFilter');
    QUALITY_ORDER.forEach(function (q) {
      var lab = el('label', 'chip');
      var cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.checked = true;
      cb.addEventListener('change', function () {
        S.quality[q] = cb.checked;
        lab.classList.toggle('off', !cb.checked);
        render();
      });
      lab.appendChild(cb);
      lab.appendChild(document.createTextNode(qLabel(q)));
      qHost.appendChild(lab);
    });

    var fromEl = document.getElementById('from');
    var toEl = document.getElementById('to');
    fromEl.min = allFrom; fromEl.max = allTo; fromEl.value = S.from;
    toEl.min = allFrom; toEl.max = allTo; toEl.value = S.to;

    fromEl.addEventListener('change', function () {
      if (!fromEl.value) { return; }
      S.from = fromEl.value > S.to ? S.to : fromEl.value;
      fromEl.value = S.from;
      render();
    });
    toEl.addEventListener('change', function () {
      if (!toEl.value) { return; }
      S.to = toEl.value < S.from ? S.from : toEl.value;
      toEl.value = S.to;
      render();
    });

    document.getElementById('btnReset').addEventListener('click', function () {
      D.disks.forEach(function (d) { S.disks[d.key] = true; });
      QUALITY_ORDER.forEach(function (q) { S.quality[q] = true; });
      S.from = allFrom; S.to = allTo; S.page = 1;
      S.hidden = {};
      S.sort = [{ col: 'd', dir: -1 }];
      buildFiltersFresh();
      render();
    });

    document.getElementById('btnCsv').addEventListener('click', exportCsv);

    document.querySelectorAll('#modeSeg button').forEach(function (b) {
      b.addEventListener('click', function () {
        S.mode = b.getAttribute('data-mode');
        document.querySelectorAll('#modeSeg button').forEach(function (x) {
          x.classList.toggle('on', x === b);
        });
        drawChart(currentRows);
      });
    });
  }

  function buildFiltersFresh() {
    document.querySelectorAll('#diskFilter .chip').forEach(function (c, i) {
      var on = S.disks[D.disks[i].key];
      c.classList.toggle('off', !on);
      c.querySelector('input').checked = on;
    });
    document.querySelectorAll('#qualityFilter .chip').forEach(function (c, i) {
      var on = S.quality[QUALITY_ORDER[i]];
      c.classList.toggle('off', !on);
      c.querySelector('input').checked = on;
    });
    document.getElementById('from').value = S.from;
    document.getElementById('to').value = S.to;
  }

  /* ---------------- 卡片 ---------------- */

  function renderCards(rows) {
    var host = document.getElementById('cards');
    host.textContent = '';

    var todaySum = 0, yestSum = 0, weekSum = 0;
    var weekFrom = addDays(D.today, -6);
    rows.forEach(function (r) {
      if (r.d === D.today) { todaySum += r.x; }
      if (r.d === D.yesterday) { yestSum += r.x; }
      if (r.d >= weekFrom && r.d <= D.today) { weekSum += r.x; }
    });

    var cum = 0, bad = 0, seen = 0;
    D.disks.forEach(function (d) {
      if (!S.disks[d.key]) { return; }
      seen++;
      cum += (d.cumulativeBytes || 0);
      if (d.anomalyDays > 0 || d.quality === 'anomaly' || d.quality === 'missing') { bad++; }
    });

    var items = [
      { k: '今日总写入', v: fmtBytes(todaySum), n: D.today, cls: '' },
      { k: '昨日总写入', v: fmtBytes(yestSum), n: D.yesterday, cls: '' },
      { k: '近 7 日均值', v: fmtBytes(Math.round(weekSum / 7)), n: '合计 ' + fmtBytes(weekSum), cls: '' },
      { k: '累计写入', v: fmtBytes(cum), n: '全生命周期，不受日期筛选影响', cls: '' },
      { k: '异常磁盘', v: bad + ' / ' + seen, n: bad > 0 ? '存在回退或缺数，见明细' : '未发现异常',
        cls: bad > 0 ? 'k-bad' : 'k-ok' }
    ];

    items.forEach(function (it) {
      var card = el('article', 'card ' + it.cls);
      var k = el('div', 'k', it.k);
      if (isFiltered()) {
        var tag = el('span', 'filtered-tag', '筛选');
        k.appendChild(tag);
      }
      card.appendChild(k);
      card.appendChild(el('div', 'v', it.v));
      card.appendChild(el('div', 'n', it.n));
      host.appendChild(card);
    });
  }

  /* ---------------- 磁盘清单 ---------------- */

  function renderDiskStrip(rows) {
    var host = document.getElementById('diskstrip');
    host.textContent = '';

    var perDisk = {};
    rows.forEach(function (r) {
      if (!perDisk[r.k]) { perDisk[r.k] = { week: 0 }; }
      if (r.d >= addDays(D.today, -6) && r.d <= D.today) { perDisk[r.k].week += r.x; }
    });

    D.disks.forEach(function (d) {
      if (!S.disks[d.key]) { return; }
      var box = el('div', 'diskitem');

      var bar = el('div', 'barcolor');
      bar.style.background = d.color;
      box.appendChild(bar);

      var left = el('div');
      left.appendChild(el('div', 'dm', d.model));
      var parts = [d.bus, 'SN ' + d.serial];
      if (d.percentageUsed !== null && d.percentageUsed !== undefined) { parts.push('已用寿命 ' + d.percentageUsed + '%'); }
      if (d.temperatureC !== null && d.temperatureC !== undefined) { parts.push(d.temperatureC + '°C'); }
      left.appendChild(el('div', 'ds', parts.join(' · ')));
      box.appendChild(left);

      var v = el('div', 'dv', fmtBytes(d.cumulativeBytes));
      box.appendChild(v);

      var badge = el('span', 'badge q-' + d.quality, qLabel(d.quality));
      box.appendChild(badge);

      host.appendChild(box);
    });
  }

  /* ---------------- 图表 ---------------- */

  function bucketize() {
    var dates = dateRange(S.from, S.to);
    if (!dates.length) { return { weekly: false, items: [], index: {} }; }

    var weekly = dates.length > MAX_POINTS;
    var index = {};
    var items = [];

    if (!weekly) {
      dates.forEach(function (d, i) {
        index[d] = i;
        items.push({ label: d.slice(5), full: d, hasData: false, values: {} });
      });
    } else {
      for (var i = 0; i < dates.length; i += 7) {
        var chunk = dates.slice(i, i + 7);
        var idx = items.length;
        items.push({
          label: chunk[0].slice(5),
          full: chunk[0] + ' ~ ' + chunk[chunk.length - 1],
          hasData: false,
          values: {}
        });
        chunk.forEach(function (d) { index[d] = idx; });
      }
    }

    return { weekly: weekly, items: items, index: index };
  }

  function aggSeries(rows) {
    var b = bucketize();
    var keys = D.disks.filter(function (d) { return S.disks[d.key] && !S.hidden[d.key]; });

    rows.forEach(function (r) {
      var i = b.index[r.d];
      if (i === undefined) { return; }
      var it = b.items[i];
      if (!S.disks[r.k]) { return; }
      it.hasData = true;
      it.values[r.k] = (it.values[r.k] || 0) + r.x;
    });

    return { buckets: b, keys: keys };
  }

  function niceScale(max, want) {
    if (!(max > 0)) { return { ticks: [0], top: 1 }; }
    var raw = max / want;
    var mag = Math.pow(10, Math.floor(Math.log(raw) / Math.LN10));
    var norm = raw / mag;
    var step = (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * mag;
    // 关键：顶刻度必须向上取整到 step 的整数倍，否则最大值会超出绘图区
    var top = Math.ceil(max / step) * step;
    var ticks = [];
    for (var v = 0; v <= top + step * 1e-6; v += step) { ticks.push(v); }
    return { ticks: ticks, top: top };
  }

  function drawChart(rows) {
    var host = document.getElementById('chart');
    var cap = document.getElementById('cap');
    var agg = aggSeries(rows);
    var items = agg.buckets.items;
    var series = agg.keys;

    host.textContent = '';

    if (!items.length || !series.length) {
      var box = el('div', 'emptybox', items.length ? '未选择任何磁盘，图表为空。' : '所选日期区间没有可绘制的数据。');
      host.appendChild(box);
      renderLegend(series);
      cap.textContent = '';
      return;
    }

    var max = 0;
    items.forEach(function (it) {
      series.forEach(function (s) {
        var v = it.values[s.key];
        if (v && v > max) { max = v; }
      });
    });

    // 全零时不画空坐标系，直接说明原因（明细表仍然可用）
    if (max <= 0) {
      host.appendChild(el('div', 'emptybox',
        '区间内所有日增量均为 0，暂无可绘制趋势；下方明细表仍然可用。'));
      renderLegend(series);
      cap.textContent = '仅有基线样本。明天再采样一次，就会出现第一天的真实增量。';
      return;
    }

    // 逻辑宽度与容器等宽，viewBox 不缩放，11px 刻度在任何屏幕上都保持 11px
    var W = Math.max(560, Math.min(2000, host.clientWidth || 960));
    var H = 320;
    var m = { l: 74, r: 16, t: 16, b: 46 };
    var plotW = W - m.l - m.r;
    var plotH = H - m.t - m.b;

    var scale = niceScale(max, 4);
    var ticks = scale.ticks;
    var top = scale.top;
    var band = plotW / items.length;

    // 低可信/异常的日-盘组合先建索引，避免每根柱都全表扫描
    var lowTrust = {};
    rows.forEach(function (r) {
      if (r.q === 'anomaly' || r.q === 'low_confidence') { lowTrust[r.d + '|' + r.k] = true; }
    });

    function Y(v) { return m.t + plotH * (1 - v / top); }

    var parts = [];
    parts.push('<svg viewBox="0 0 ' + W + ' ' + H + '" preserveAspectRatio="xMidYMid meet" role="img" aria-label="每日写入增量趋势">');

    ticks.forEach(function (t) {
      parts.push('<line class="grid" x1="' + m.l + '" y1="' + Y(t).toFixed(1) + '" x2="' + (m.l + plotW) + '" y2="' + Y(t).toFixed(1) + '"/>');
      parts.push('<text x="' + (m.l - 8) + '" y="' + (Y(t) + 4).toFixed(1) + '" text-anchor="end">' + esc(fmtBytes(t)) + '</text>');
    });

    parts.push('<line class="axis" x1="' + m.l + '" y1="' + (m.t + plotH) + '" x2="' + (m.l + plotW) + '" y2="' + (m.t + plotH) + '"/>');

    var labelEvery = Math.max(1, Math.ceil(items.length / 12));
    items.forEach(function (it, i) {
      if (i % labelEvery === 0) {
        parts.push('<text x="' + (m.l + i * band + band / 2).toFixed(1) + '" y="' + (H - m.b + 20) + '" text-anchor="middle">' + esc(it.label) + '</text>');
      }
      if (!it.hasData) {
        parts.push('<circle class="nodata" cx="' + (m.l + i * band + band / 2).toFixed(1) + '" cy="' + (m.t + plotH + 9) + '" r="2.5"/>');
      }
    });

    if (S.mode === 'bar') {
      // 天数少时柱子会被撑得很宽，按 64px 封顶并把整组居中
      var rawBw = (band - 3) / Math.max(1, series.length);
      var bw = Math.max(1.5, Math.min(64, rawBw));
      var groupW = bw * series.length;
      var offset = Math.max(1.5, (band - groupW) / 2);
      items.forEach(function (it, i) {
        series.forEach(function (s, j) {
          var v = it.values[s.key];
          if (v === undefined || v <= 0) { return; }
          var h = Math.max(1, plotH * (v / top));
          var x = m.l + i * band + offset + j * bw;
          var lt = lowTrust[it.full.split(' ~ ')[0] + '|' + s.key];
          parts.push('<rect x="' + x.toFixed(1) + '" y="' + (m.t + plotH - h).toFixed(1) +
            '" width="' + Math.max(1, bw - 0.7).toFixed(1) + '" height="' + h.toFixed(1) +
            '" fill="' + esc(s.color) + '"' + (lt ? ' fill-opacity="0.45"' : '') + '/>');
        });
      });
    } else {
      series.forEach(function (s) {
        var segs = [];
        var cur = [];
        items.forEach(function (it, i) {
          var v = it.values[s.key];
          if (v === undefined) {
            if (cur.length) { segs.push(cur); cur = []; }
            return;
          }
          var cx = m.l + i * band + band / 2;
          cur.push([cx, Y(v)]);
        });
        if (cur.length) { segs.push(cur); }
        segs.forEach(function (seg) {
          if (seg.length === 1) {
            parts.push('<circle cx="' + seg[0][0].toFixed(1) + '" cy="' + seg[0][1].toFixed(1) + '" r="2.5" fill="' + esc(s.color) + '"/>');
            return;
          }
          var d = seg.map(function (p, i2) {
            return (i2 === 0 ? 'M' : 'L') + p[0].toFixed(1) + ' ' + p[1].toFixed(1);
          }).join(' ');
          parts.push('<path d="' + d + '" fill="none" stroke="' + esc(s.color) + '" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>');
        });
      });
    }

    parts.push('</svg>');
    host.innerHTML = parts.join('');

    renderLegend(series);

    cap.textContent = agg.buckets.weekly
      ? '已按周聚合（区间超过 ' + MAX_POINTS + ' 天）。空心刻度表示该周无采样。'
      : '半透明柱表示当日数据质量为异常或低可信；空心刻度表示该日无采样。点击图例可临时隐藏某块盘。';

    bindHover(host, m, band, items, series, W);
  }

  function renderLegend(series) {
    var host = document.getElementById('legend');
    host.textContent = '';
    D.disks.forEach(function (d) {
      if (!S.disks[d.key]) { return; }
      var span = el('span', S.hidden[d.key] ? 'off' : '');
      var i = el('i');
      i.style.background = d.color;
      span.appendChild(i);
      span.appendChild(document.createTextNode(d.model));
      span.addEventListener('click', function () {
        S.hidden[d.key] = !S.hidden[d.key];
        drawChart(currentRows);
      });
      host.appendChild(span);
    });
    void series;
  }

  function bindHover(host, m, band, items, series, W) {
    var tip = document.getElementById('tip');
    var svg = host.querySelector('svg');
    if (!svg) { return; }

    host.onmousemove = function (e) {
      var rect = svg.getBoundingClientRect();
      if (!rect.width) { return; }
      var lx = (e.clientX - rect.left) * (W / rect.width);
      var idx = Math.floor((lx - m.l) / band);
      if (idx < 0 || idx >= items.length) { tip.hidden = true; return; }

      var it = items[idx];
      var lines = ['<b>' + esc(it.full) + '</b>'];
      var any = false;
      series.forEach(function (s) {
        var v = it.values[s.key];
        if (v === undefined) {
          lines.push('<i style="background:' + esc(s.color) + '"></i>' + esc(s.model) + '：无采样');
        } else {
          any = true;
          lines.push('<i style="background:' + esc(s.color) + '"></i>' + esc(s.model) + '：' + esc(fmtBytes(v)));
        }
      });
      if (!any) { tip.hidden = true; return; }

      tip.innerHTML = lines.join('<br>');
      tip.hidden = false;
      var box = host.getBoundingClientRect();
      var left = e.clientX - box.left + 14;
      tip.style.left = Math.max(4, Math.min(box.width - tip.offsetWidth - 4, left)) + 'px';
      tip.style.top = Math.max(4, e.clientY - box.top - 12) + 'px';
    };

    host.onmouseleave = function () { tip.hidden = true; };
  }

  /* ---------------- 表格 ---------------- */

  function cellValue(r, col) {
    var d = diskByKey[r.k] || {};
    switch (col) {
      case 'd': return r.d;
      case 'model': return d.model || '';
      case 'x': return r.x;
      case 's': return r.s;
      case 'e': return r.e;
      case 'm': return mLabel(r.m);
      case 'q': return QUALITY_ORDER.indexOf(r.q);
      case 'note': return r.note || '';
      default: return '';
    }
  }

  function sortRows(rows) {
    var spec = S.sort;
    return rows.slice().sort(function (a, b) {
      for (var i = 0; i < spec.length; i++) {
        var col = spec[i].col;
        var av = cellValue(a, col);
        var bv = cellValue(b, col);
        var r = 0;
        if (typeof av === 'number' && typeof bv === 'number') {
          r = av - bv;
        } else {
          var as = (av === null || av === undefined) ? '' : String(av);
          var bs = (bv === null || bv === undefined) ? '' : String(bv);
          r = as.localeCompare(bs, 'zh-Hans-CN');
        }
        if (r !== 0) { return r * spec[i].dir; }
      }
      return a._i - b._i;
    });
  }

  function renderTable(rows) {
    var tbody = document.querySelector('#tbl tbody');
    var hint = document.getElementById('rowHint');
    var pager = document.getElementById('pager');

    tbody.textContent = '';
    var sorted = sortRows(rows);
    var paged = sorted.length > MAX_TABLE_ROWS;
    var pageCount = paged ? Math.ceil(sorted.length / PAGE_SIZE) : 1;
    if (S.page > pageCount) { S.page = pageCount; }
    var view = paged ? sorted.slice((S.page - 1) * PAGE_SIZE, S.page * PAGE_SIZE) : sorted;

    hint.textContent = rows.length ? ('共 ' + rows.length + ' 条' + (paged ? '，分页显示' : '')) : '无匹配记录';

    var frag = document.createDocumentFragment();
    view.forEach(function (r) {
      var d = diskByKey[r.k] || {};
      var tr = document.createElement('tr');
      if (r.q === 'anomaly' || r.q === 'missing') { tr.className = 'row-anomaly'; }

      tr.appendChild(el('td', '', r.d));
      tr.appendChild(el('td', '', d.model || r.k));

      var tdX = el('td', 'num x', fmtBytes(r.x));
      tr.appendChild(tdX);
      tr.appendChild(el('td', 'num', fmtBytes(r.s)));
      tr.appendChild(el('td', 'num', fmtBytes(r.e)));
      tr.appendChild(el('td', '', mLabel(r.m)));

      var tdQ = el('td');
      tdQ.appendChild(el('span', 'badge q-' + r.q, qLabel(r.q)));
      tr.appendChild(tdQ);

      var note = r.note || '';
      if (r.g > 0) { note = (note ? note + '；' : '') + '中间缺 ' + r.g + ' 天'; }
      tr.appendChild(el('td', 'note', note));

      frag.appendChild(tr);
    });
    tbody.appendChild(frag);

    updateSortMarks();

    if (!paged) {
      pager.hidden = true;
      pager.textContent = '';
      return;
    }

    pager.hidden = false;
    pager.textContent = '';
    var prev = el('button', '', '上一页');
    var next = el('button', '', '下一页');
    prev.disabled = S.page <= 1;
    next.disabled = S.page >= pageCount;
    prev.addEventListener('click', function () { S.page--; renderTable(rows); });
    next.addEventListener('click', function () { S.page++; renderTable(rows); });
    pager.appendChild(prev);
    pager.appendChild(el('span', '', S.page + ' / ' + pageCount));
    pager.appendChild(next);
  }

  function updateSortMarks() {
    document.querySelectorAll('#tbl thead th').forEach(function (th) {
      var col = th.getAttribute('data-col');
      var idx = -1;
      for (var i = 0; i < S.sort.length; i++) {
        if (S.sort[i].col === col) { idx = i; break; }
      }
      th.classList.toggle('sorted', idx >= 0);
      var ar = th.querySelector('.ar');
      if (ar) { ar.textContent = idx < 0 ? '↕' : (S.sort[idx].dir > 0 ? '↑' : '↓'); }
      var rank = th.querySelector('.rank');
      if (rank) { rank.textContent = (idx > 0) ? String(idx + 1) : ''; }
    });
  }

  function bindSort() {
    document.querySelectorAll('#tbl thead th').forEach(function (th) {
      th.addEventListener('click', function (e) {
        var col = th.getAttribute('data-col');
        if (!col) { return; }
        var found = -1;
        for (var i = 0; i < S.sort.length; i++) {
          if (S.sort[i].col === col) { found = i; break; }
        }

        if (e.shiftKey) {
          if (found >= 0) {
            S.sort[found].dir *= -1;
          } else {
            S.sort.push({ col: col, dir: 1 });
          }
        } else if (found === 0 && S.sort.length === 1) {
          if (S.sort[0].dir === 1) { S.sort[0].dir = -1; }
          else { S.sort = [{ col: 'd', dir: -1 }]; }
        } else {
          S.sort = [{ col: col, dir: found === 0 && S.sort[0].dir === 1 ? -1 : 1 }];
        }

        S.page = 1;
        renderTable(currentRows);
      });
    });
  }

  /* ---------------- 导出 ---------------- */

  function csvCell(v) {
    var s = (v === null || v === undefined) ? '' : String(v);
    if (/[",\r\n]/.test(s)) { s = '"' + s.replace(/"/g, '""') + '"'; }
    if (/^[=+\-@\t]/.test(s)) { s = "'" + s; }
    return s;
  }

  function toCsv(rows) {
    var head = ['日期', '磁盘', '型号', '序列号', '总线', '日增量(bytes)', '日增量',
      '起始累计(bytes)', '结束累计(bytes)', '方法', '数据质量', '备注'];
    var out = [head.join(',')];
    rows.forEach(function (r) {
      var d = diskByKey[r.k] || {};
      out.push([
        r.d, d.model || '', d.serial || '', d.bus || '',
        r.x, fmtBytes(r.x),
        r.s === null || r.s === undefined ? '' : r.s,
        r.e === null || r.e === undefined ? '' : r.e,
        mLabel(r.m), qLabel(r.q), r.note || ''
      ].map(csvCell).join(','));
    });
    return '\ufeff' + out.join('\r\n') + '\r\n';
  }

  function stamp() {
    var n = new Date();
    return n.getFullYear() + pad2(n.getMonth() + 1) + pad2(n.getDate()) + '-' +
      pad2(n.getHours()) + pad2(n.getMinutes()) + pad2(n.getSeconds());
  }

  function exportCsv() {
    var rows = sortRows(visibleRows());
    if (!rows.length) {
      alert('当前筛选没有可导出的记录，请放宽条件后重试。');
      return;
    }
    var text = toCsv(rows);
    var name = 'diskwrite-' + stamp() + '.csv';
    try {
      var blob = new Blob([text], { type: 'text/csv;charset=utf-8' });
      var url = URL.createObjectURL(blob);
      var a = document.createElement('a');
      a.href = url;
      a.download = name;
      document.body.appendChild(a);
      a.click();
      setTimeout(function () { URL.revokeObjectURL(url); a.remove(); }, 0);
    } catch (e) {
      fallbackCopy(text, 'CSV');
    }
  }

  function fallbackCopy(text, kind) {
    var ta = document.createElement('textarea');
    ta.value = text;
    ta.style.position = 'fixed';
    ta.style.left = '-9999px';
    document.body.appendChild(ta);
    ta.select();
    var ok = false;
    try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
    if (ok) {
      alert(kind + ' 内容已复制到剪贴板，请粘贴到记事本后另存为 UTF-8 文件。');
      ta.remove();
      return;
    }
    ta.style.position = 'static';
    ta.style.left = '';
    ta.style.width = '100%';
    ta.style.height = '220px';
    var wrap = el('div', '', '当前浏览器不允许直接下载，请全选下面的文本手动复制：');
    wrap.appendChild(ta);
    document.body.appendChild(wrap);
    ta.select();
  }

  /* ---------------- 数据质量 & 页脚 ---------------- */

  function renderQuality() {
    var q = D.dataQuality;
    var host = document.getElementById('dq');
    host.textContent = '';

    host.appendChild(el('h2', '', '数据质量说明'));

    var ul = el('ul');
    if (q.notes && q.notes.length) {
      q.notes.forEach(function (n) { ul.appendChild(el('li', '', n)); });
    } else {
      ul.appendChild(el('li', '', '尚无日统计记录，明细为空。'));
    }
    host.appendChild(ul);

    var kv = el('div', 'kv');
    function add(k, v) {
      var d = el('div');
      d.appendChild(document.createTextNode(k));
      d.appendChild(el('b', '', v));
      kv.appendChild(d);
    }
    add('样本条数', fmtInt(q.sampleCount));
    add('覆盖天数', fmtInt(q.dayCount));
    add('最早日期', q.firstDate || '—');
    add('最新日期', q.lastDate || '—');
    add('异常日', fmtInt(q.anomalyDays));
    add('缺失日', fmtInt(q.missingDays));
    add('跨期累计', fmtInt(q.gapFilled));
    add('回退钳制', fmtInt(q.clamped));
    add('来源切换', fmtInt(q.sourceSwitches));
    host.appendChild(kv);

    host.appendChild(el('p', 'qnote',
      '数据来自硬盘固件上报的累计写入计数器，不是软件层估算。日增量 = 当日最后一条有效样本 − 前一个有效日期的最后一条样本；计数回退不出负数，跨期会标注。'));

    var cfg = D.config || {};
    var kv2 = el('div', 'kv');
    function add2(k, v) {
      var d = el('div');
      d.appendChild(document.createTextNode(k));
      d.appendChild(el('b', '', v));
      kv2.appendChild(d);
    }
    add2('采样时间', '每天 ' + (cfg.dailyTime || '00:05'));
    add2('数据目录', D.dataDirectory);
    add2('保留天数', fmtInt(cfg.retentionDays));
    add2('计划任务', cfg.taskName || '—');
    add2('引擎版本', D.engineVersion || '—');
    host.appendChild(kv2);
  }

  function renderStale() {
    var host = document.getElementById('stale');
    var last = D.dataQuality && D.dataQuality.lastDate;
    if (!last) { return; }
    var gap = daysBetween(last, D.today);
    if (gap <= 1) { return; }

    var msg = document.createDocumentFragment();
    msg.appendChild(document.createTextNode('数据已 ' + gap + ' 天没有更新（最新 ' + last + '）。'));
    var code = el('b', '', '双击 DiskWriteTracker.cmd → 选 1');
    msg.appendChild(document.createTextNode('可以立即采样并刷新本页：'));
    msg.appendChild(code);
    msg.appendChild(document.createTextNode('。'));
    host.appendChild(msg);
    host.hidden = false;
  }

  /* ---------------- 主循环 ---------------- */

  var currentRows = [];

  function renderEmpty(rows) {
    var host = document.getElementById('chart');
    host.textContent = '';
    var msg;
    if (!D.rows.length) {
      msg = '当前区间只有基线样本，还没有可用的日增量。明天再采样一次就会出现第一天增量。';
    } else if (!rows.length) {
      msg = '当前筛选没有匹配的日记录。试试放宽日期范围，或勾选更多磁盘 / 数据质量。';
    } else {
      msg = '区间内所有日增量均为 0，暂无可绘制趋势；下方明细表仍然可用。';
    }
    host.appendChild(el('div', 'emptybox', msg));
    document.getElementById('legend').textContent = '';
    document.getElementById('cap').textContent = '';
  }

  function render() {
    app.hidden = false;
    document.body.classList.toggle('is-filtered', isFiltered());

    currentRows = visibleRows();
    renderCards(currentRows);
    renderDiskStrip(currentRows);

    if (!currentRows.length) {
      renderEmpty(currentRows);
    } else {
      drawChart(currentRows);
    }

    renderTable(currentRows);
    renderQuality();
  }

  /* ---------------- 启动 ---------------- */

  buildFilters();
  bindSort();
  renderStale();
  render();

  var resizeTimer = null;
  window.addEventListener('resize', function () {
    if (resizeTimer) { clearTimeout(resizeTimer); }
    resizeTimer = setTimeout(function () {
      if (currentRows.length) { drawChart(currentRows); }
    }, 150);
  });
})();
