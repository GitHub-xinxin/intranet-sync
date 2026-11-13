(function () {
  function $(id) { return document.getElementById(id); }

  function setVal(path, value) {
    var el = $(path);
    if (!el) return;
    if (el.type === "checkbox") el.checked = !!value;
    else el.value = value == null ? "" : value;
  }

  function getVal(path) {
    var el = $(path);
    if (!el) return null;
    if (el.type === "checkbox") return el.checked;
    if (el.type === "number") return el.value === "" ? null : Number(el.value);
    return el.value;
  }

  function fill(cfg) {
    setVal("sql.server", cfg.sql && cfg.sql.server);
    setVal("sql.database", cfg.sql && cfg.sql.database);
    setVal("sql.userId", cfg.sql && cfg.sql.userId);
    setVal("sql.password", "");
    setVal("sql.integratedSecurity", cfg.sql && cfg.sql.integratedSecurity);

    setVal("view.viewName", cfg.view && cfg.view.viewName);
    var pk = (cfg.view && cfg.view.primaryKeyColumn) || "";
    if (pk === "Id") pk = "";
    setVal("view.primaryKeyColumn", pk);
    var inc = (cfg.view && cfg.view.incrementalColumn) || "updated_at";
    if (inc === "UpdatedAt") inc = "updated_at";
    setVal("view.incrementalColumn", inc);
    setVal("view.pdfUrlColumns", normalizePdfColumns(cfg.view && cfg.view.pdfUrlColumns).join(","));
    setVal("view.pageSize", cfg.view && cfg.view.pageSize);

    setVal("cloud.apiBaseUrl", cfg.cloud && cfg.cloud.apiBaseUrl);
    setVal("cloud.ossTokenUrl", cfg.cloud && cfg.cloud.ossTokenUrl);
    setVal("cloud.serviceToken", "");
    setVal("cloud.tenantId", cfg.cloud && cfg.cloud.tenantId);

    setVal("oss.endpoint", cfg.oss && cfg.oss.endpoint);
    setVal("oss.bucket", cfg.oss && cfg.oss.bucket);
    setVal("oss.prefix", cfg.oss && cfg.oss.prefix);
    setVal("oss.useSts", cfg.oss && cfg.oss.useSts);
    setVal("oss.accessKeyId", cfg.oss && cfg.oss.accessKeyId);
    setVal("oss.accessKeySecret", "");
    setVal("oss.publicBaseUrl", cfg.oss && cfg.oss.publicBaseUrl);
    setVal("oss.useSignedUrl", !cfg.oss || cfg.oss.useSignedUrl !== false);
    setVal("oss.signedUrlExpireSeconds", (cfg.oss && cfg.oss.signedUrlExpireSeconds) || 86400);

    var maxMb = cfg.pdf && cfg.pdf.maxBytes ? Math.round(cfg.pdf.maxBytes / (1024 * 1024)) : 50;
    setVal("pdf.maxMb", maxMb);
    setVal("pdf.maxConcurrency", cfg.pdf && cfg.pdf.maxConcurrency);
    setVal("pdf.authMode", (cfg.pdf && cfg.pdf.authMode) || "anonymous");
    setVal("sync.intervalSeconds", cfg.sync && cfg.sync.intervalSeconds);
    setVal("sync.enabled", cfg.sync && cfg.sync.enabled);
    setVal("sync.failBatchOnPdfError", cfg.sync && cfg.sync.failBatchOnPdfError);
  }

  function normalizePdfColumns(cols) {
    var list = [];
    if (typeof cols === "string")
      list = cols.split(",");
    else if (Object.prototype.toString.call(cols) === "[object Array]")
      list = cols.slice();
    var out = [];
    var seen = {};
    for (var i = 0; i < list.length; i++) {
      var name = (list[i] == null ? "" : String(list[i])).trim();
      if (!name) continue;
      var key = name.toLowerCase();
      if (seen[key]) continue;
      seen[key] = true;
      out.push(name);
    }
    if (!out.length || (out.length === 2 && out[0] === "PdfUrl" && out[1] === "AttachUrl"))
      return ["PDF"];
    return out;
  }

  function collect() {
    var pdfCols = normalizePdfColumns(getVal("view.pdfUrlColumns") || "");
    var maxMb = getVal("pdf.maxMb") || 50;
    return {
      sql: {
        server: getVal("sql.server"),
        database: getVal("sql.database"),
        userId: getVal("sql.userId"),
        password: getVal("sql.password") || "********",
        integratedSecurity: getVal("sql.integratedSecurity")
      },
      view: {
        viewName: getVal("view.viewName"),
        primaryKeyColumn: getVal("view.primaryKeyColumn"),
        incrementalColumn: getVal("view.incrementalColumn"),
        pdfUrlColumns: pdfCols,
        pageSize: getVal("view.pageSize") || 300
      },
      cloud: {
        apiBaseUrl: getVal("cloud.apiBaseUrl"),
        ossTokenUrl: getVal("cloud.ossTokenUrl"),
        serviceToken: getVal("cloud.serviceToken") || "********",
        tenantId: getVal("cloud.tenantId")
      },
      oss: {
        endpoint: getVal("oss.endpoint"),
        bucket: getVal("oss.bucket"),
        prefix: getVal("oss.prefix"),
        useSts: getVal("oss.useSts"),
        accessKeyId: getVal("oss.accessKeyId"),
        accessKeySecret: getVal("oss.accessKeySecret") || "********",
        publicBaseUrl: getVal("oss.publicBaseUrl"),
        useSignedUrl: getVal("oss.useSignedUrl"),
        signedUrlExpireSeconds: getVal("oss.signedUrlExpireSeconds") || 86400
      },
      pdf: {
        maxBytes: maxMb * 1024 * 1024,
        maxConcurrency: getVal("pdf.maxConcurrency") || 3,
        authMode: getVal("pdf.authMode")
      },
      sync: {
        intervalSeconds: getVal("sync.intervalSeconds") || 180,
        enabled: getVal("sync.enabled"),
        failBatchOnPdfError: getVal("sync.failBatchOnPdfError")
      },
      admin: {
        bindAddress: "127.0.0.1",
        port: 8787,
        adminPassword: "********"
      }
    };
  }

  function api(path, method, body) {
    var opts = { method: method || "GET", headers: { "Content-Type": "application/json" } };
    if (body !== undefined) opts.body = JSON.stringify(body);
    return fetch(path, opts).then(function (r) {
      return r.json().then(function (j) {
        if (!r.ok) throw new Error((j && j.error) || r.statusText);
        return j;
      });
    });
  }

  function updateSyncButtons(running) {
    var startBtn = $("btnStart");
    var stopBtn = $("btnStop");
    if (!startBtn || !stopBtn) return;
    if (running) {
      startBtn.classList.add("is-hidden");
      stopBtn.classList.remove("is-hidden");
    } else {
      stopBtn.classList.add("is-hidden");
      startBtn.classList.remove("is-hidden");
    }
  }

  /** Display UTC / ISO timestamps as Beijing time (UTC+8). */
  function toBeijing(value) {
    if (value == null || value === "") return "-";
    var d = value instanceof Date ? value : new Date(value);
    if (isNaN(d.getTime())) return String(value);
    var bj = new Date(d.getTime() + 8 * 60 * 60 * 1000);
    function pad(n) { return n < 10 ? "0" + n : String(n); }
    return bj.getUTCFullYear() + "-" +
      pad(bj.getUTCMonth() + 1) + "-" +
      pad(bj.getUTCDate()) + " " +
      pad(bj.getUTCHours()) + ":" +
      pad(bj.getUTCMinutes()) + ":" +
      pad(bj.getUTCSeconds());
  }

  function formatFailures(list) {
    return (list || []).map(function (item) {
      var copy = {};
      for (var k in item) {
        if (!Object.prototype.hasOwnProperty.call(item, k)) continue;
        if (k === "failedAtUtc") {
          copy.failedAt = toBeijing(item[k]);
          continue;
        }
        copy[k] = item[k];
      }
      return copy;
    });
  }

  function refreshStatus() {
    return api("/api/status").then(function (s) {
      var running = !!s.running;
      var dot = $("statusDot");
      var title = $("statusTitle");
      var body = $("statusBody");

      if (dot) {
        dot.className = "status-dot " + (running ? "is-running" : "is-stopped");
      }
      if (title) {
        title.textContent = running ? "同步运行中" : "同步已停止";
      }
      if (body) {
        body.textContent =
          "定时启用: " + (s.enabled ? "是" : "否") +
          "\n上次运行: " + toBeijing(s.lastRunUtc) +
          "\n水位: " + ((s.watermark && s.watermark.lastWatermark) || "-") +
          "\n" + ((s.lastResult && s.lastResult.message) || "");
      }
      updateSyncButtons(running);
    }).catch(function (e) {
      var dot = $("statusDot");
      var title = $("statusTitle");
      var body = $("statusBody");
      if (dot) dot.className = "status-dot is-error";
      if (title) title.textContent = "状态获取失败";
      if (body) body.textContent = e.message;
      updateSyncButtons(false);
    });
  }

  function refreshLogs() {
    return Promise.all([
      api("/api/logs"),
      api("/api/failures")
    ]).then(function (pair) {
      $("logs").textContent = (pair[0] || []).map(function (l) {
        return toBeijing(l.utc) + " [" + l.level + "] " + l.message;
      }).join("\n");
      $("failures").textContent = JSON.stringify(formatFailures(pair[1] || []), null, 2);
    });
  }

  function show(el, obj) {
    $(el).textContent = typeof obj === "string" ? obj : JSON.stringify(obj, null, 2);
  }

  document.addEventListener("DOMContentLoaded", function () {
    api("/api/config").then(fill).then(refreshStatus).then(refreshLogs);

    $("btnSave").onclick = function () {
      api("/api/config", "POST", collect())
        .then(function (c) { show("actionResult", c); return refreshStatus(); })
        .catch(function (e) { show("actionResult", e.message); });
    };
    $("btnTestSql").onclick = function () {
      api("/api/config", "POST", collect())
        .then(function () { return api("/api/sql/test", "POST", {}); })
        .then(function (r) { show("sqlTestResult", r); })
        .catch(function (e) { show("sqlTestResult", e.message); });
    };
    $("btnStart").onclick = function () {
      api("/api/sync/start", "POST", {})
        .then(function (r) { show("actionResult", r); return refreshStatus(); })
        .catch(function (e) { show("actionResult", e.message); });
    };
    $("btnStop").onclick = function () {
      api("/api/sync/stop", "POST", {})
        .then(function (r) { show("actionResult", r); return refreshStatus(); })
        .catch(function (e) { show("actionResult", e.message); });
    };
    $("btnRun").onclick = function () {
      show("actionResult", "running…");
      api("/api/sync/run", "POST", {})
        .then(function (r) { show("actionResult", r); return Promise.all([refreshStatus(), refreshLogs()]); })
        .catch(function (e) { show("actionResult", e.message); });
    };

    $("btnClearLogs").onclick = function () {
      if (!window.confirm("确认清除运行日志（内存最近日志 + sync.log）？")) return;
      api("/api/logs/clear", "POST", {})
        .then(function (r) { show("actionResult", r); return refreshLogs(); })
        .catch(function (e) { show("actionResult", e.message); });
    };

    $("btnClearFailures").onclick = function () {
      if (!window.confirm("确认清除全部 PDF 失败记录？")) return;
      api("/api/failures/clear", "POST", {})
        .then(function (r) { show("actionResult", r); return refreshLogs(); })
        .catch(function (e) { show("actionResult", e.message); });
    };

    setInterval(function () { refreshStatus(); refreshLogs(); }, 10000);
  });
})();
