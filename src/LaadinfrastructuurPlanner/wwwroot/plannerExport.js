window.routePlannerExport = (() => {
  async function download(request) {
    const response = await fetch(new URL("/api/export/report", window.location.origin).toString(), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request)
    });

    if (!response.ok) {
      let message = `Export mislukt (${response.status})`;
      try {
        const error = await response.json();
        if (error?.message) message = error.message;
      } catch {
        const text = await response.text();
        if (text) message = text;
      }
      throw new Error(message);
    }

    const blob = await response.blob();
    const disposition = response.headers.get("content-disposition") || "";
    const filename = filenameFromDisposition(disposition) || "route-analyse-export.zip";
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  }

  function filenameFromDisposition(disposition) {
    const utf8 = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
    if (utf8?.[1]) return decodeURIComponent(utf8[1].replaceAll('"', ""));
    const ascii = /filename="?([^";]+)"?/i.exec(disposition);
    return ascii?.[1] || "";
  }

  return { download };
})();
