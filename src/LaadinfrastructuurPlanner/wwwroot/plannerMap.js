window.routePlannerMap = (() => {
  let map;
  let abortController;
  let dotNetRef;

  function apiUrl(path) {
    return new URL(`/api/${path}`, window.location.origin).toString();
  }

  function status(text) {
    const el = document.getElementById("map-status");
    if (el) el.textContent = text;
  }

  function setLegendCollapsed(legend, button, collapsed) {
    legend.classList.toggle("is-collapsed", collapsed);
    button.textContent = collapsed ? "+" : "-";
    button.title = collapsed ? "Legenda tonen" : "Legenda minimaliseren";
    button.setAttribute("aria-label", button.title);
    button.setAttribute("aria-expanded", collapsed ? "false" : "true");
  }

  function wireLegendToggle() {
    const legend = document.querySelector(".map-legend");
    const button = legend?.querySelector("[data-legend-toggle]");
    if (!legend || !button || button.dataset.legendFallback === "true") {
      return;
    }

    button.dataset.legendFallback = "true";
    button.addEventListener("click", (event) => {
      event.preventDefault();
      setLegendCollapsed(legend, button, !legend.classList.contains("is-collapsed"));
    });
  }

  function emptyFeatureCollection() {
    return { type: "FeatureCollection", features: [] };
  }

  function ensureSource(id) {
    if (!map.getSource(id)) {
      map.addSource(id, { type: "geojson", data: emptyFeatureCollection() });
    }
  }

  function setData(id, data) {
    ensureSource(id);
    map.getSource(id).setData(data || emptyFeatureCollection());
  }

  function featureCollection(points, markerMode = false) {
    return {
      type: "FeatureCollection",
      features: (points || []).map((p) => ({
        type: "Feature",
        properties: markerMode
          ? {
              weight: p.uniqueWagens || p.weight || 1,
              label: p.name || p.address || "",
              stops: p.stops || 0,
              trips: p.trips || 0
            }
          : { weight: p.weight || 1 },
        geometry: { type: "Point", coordinates: [p.lon, p.lat] }
      }))
    };
  }

  function lineCollection(lines) {
    return {
      type: "FeatureCollection",
      features: (lines || []).map((line) => ({
        type: "Feature",
        properties: {
          segmentId: line.segmentId || "",
          weight: line.passes || line.uniqueWagens || 1,
          vehicles: line.uniqueWagens || 1,
          passes: line.passes || 0,
          direction: line.direction || "",
          bearing: line.bearing || 0,
          lengthKm: line.lengthKm || 0,
          rawSegments: line.rawSegments || 1,
          radiusKm: line.selectionRadiusKm || 3
        },
        geometry: {
          type: "LineString",
          coordinates: line.coordinates?.length
            ? line.coordinates.map((point) => [point.lon, point.lat])
            : [
                [line.lon1, line.lat1],
                [line.lon2, line.lat2]
              ]
        }
      }))
    };
  }

  function roadBreakDemandCollection(lines, roadLines = []) {
    return {
      type: "FeatureCollection",
      features: (lines || [])
        .map((line) => {
          const roadCoordinates = nearestRoadCoordinates(line, roadLines);
          if (!roadCoordinates) return null;
          return {
            type: "Feature",
            properties: {
              segmentId: line.segmentId || "",
              weight: line.passages || 0,
              peakMw: line.peakMw || 0,
              totalKwh: line.totalKwh || 0,
              vehicles: line.vehicles || 0,
              passages: line.passages || 0,
              direction: line.direction || "",
              routeQuality: line.routeQuality || "",
              radiusKm: line.selectionRadiusKm || 3
            },
            geometry: {
              type: "LineString",
              coordinates: roadCoordinates
            }
          };
        })
        .filter(Boolean)
    };
  }

  function coordinatesForLine(line) {
    return line.coordinates?.length
      ? line.coordinates.map((point) => [point.lon, point.lat])
      : [
          [line.lon1, line.lat1],
          [line.lon2, line.lat2]
        ];
  }

  function nearestRoadCoordinates(line, roadLines) {
    if (!roadLines?.length) return null;
    const source = coordinatesForLine(line);
    const center = lineCenter(source);
    let best = { km: Number.POSITIVE_INFINITY, coords: null };
    for (const road of roadLines) {
      const coords = coordinatesForLine(road);
      for (let i = 1; i < coords.length; i += 1) {
        const km = distancePointToSegmentKm(center[1], center[0], coords[i - 1][1], coords[i - 1][0], coords[i][1], coords[i][0]);
        if (km < best.km) {
          best = { km, coords };
        }
      }
    }

    return best.km <= 2 ? best.coords : null;
  }

  function lineCenter(coords) {
    const first = coords[0];
    const last = coords[coords.length - 1] || first;
    return [(first[0] + last[0]) / 2, (first[1] + last[1]) / 2];
  }

  function distancePointToSegmentKm(pointLat, pointLon, lat1, lon1, lat2, lon2) {
    const meanLat = toRadians((pointLat + lat1 + lat2) / 3);
    const kmPerDegreeLat = 111.32;
    const kmPerDegreeLon = Math.max(1, kmPerDegreeLat * Math.cos(meanLat));
    const px = pointLon * kmPerDegreeLon;
    const py = pointLat * kmPerDegreeLat;
    const ax = lon1 * kmPerDegreeLon;
    const ay = lat1 * kmPerDegreeLat;
    const bx = lon2 * kmPerDegreeLon;
    const by = lat2 * kmPerDegreeLat;
    const dx = bx - ax;
    const dy = by - ay;
    const lengthSquared = dx * dx + dy * dy;
    if (lengthSquared <= 0) {
      const x = px - ax;
      const y = py - ay;
      return Math.sqrt(x * x + y * y);
    }
    const t = Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / lengthSquared));
    const closestX = ax + t * dx;
    const closestY = ay + t * dy;
    const x = px - closestX;
    const y = py - closestY;
    return Math.sqrt(x * x + y * y);
  }

  function toRadians(value) {
    return value * Math.PI / 180;
  }

  function chargerCollection(chargers) {
    return {
      type: "FeatureCollection",
      features: (chargers || []).map((charger) => ({
        type: "Feature",
        properties: {
          id: charger.id,
          label: charger.name || "Laadlocatie",
          operator: charger.operator || "",
          address: `${charger.address || ""}, ${charger.postcode || ""} ${charger.town || ""}`.trim(),
          maxPowerKw: charger.maxPowerKw || 0,
          connectors: charger.connectors || 0,
          access: charger.access || "",
          dedicated: charger.dedicated || "",
          twentyfourSeven: charger.twentyfourSeven || "",
          connectorType: charger.connectorType || ""
        },
        geometry: { type: "Point", coordinates: [charger.lon, charger.lat] }
      }))
    };
  }

  function overnightCollection(locations) {
    return {
      type: "FeatureCollection",
      features: (locations || []).map((location) => ({
        type: "Feature",
        properties: {
          depotId: location.depotId,
          label: location.address || location.depotId,
          weight: location.uniqueVehicles || 1,
          events: location.events || 0,
          totalMwh: location.totalMwh || 0,
          publicDemandMwh: location.shortageMwh || 0,
          recommendation: location.recommendation || ""
        },
        geometry: { type: "Point", coordinates: [location.lon, location.lat] }
      }))
    };
  }

  function standplaatsCollection(depots) {
    return {
      type: "FeatureCollection",
      features: (depots || []).map((depot) => ({
        type: "Feature",
        properties: {
          depotId: depot.depotId,
          name: depot.name,
          regio: depot.regio || "",
          typeLocatie: depot.typeLocatie || "",
          vehicles: depot.vehicles || 0,
          matchedInTrips: depot.matchedInTrips || 0,
          vehicleList: JSON.stringify(depot.vehicleList || [])
        },
        geometry: { type: "Point", coordinates: [depot.lon, depot.lat] }
      }))
    };
  }

  function showStandplaatsPopup(feature, sourceLabel, disclaimer) {
    if (!feature) return;
    const p = feature.properties || {};
    let vehicles = [];
    try { vehicles = JSON.parse(p.vehicleList || "[]"); } catch { vehicles = []; }
    const rows = vehicles.slice(0, 80).map((v) => {
      const trips = Number(v.tripsInData || 0);
      const tripsLabel = trips > 0 ? `${trips.toLocaleString("nl-NL")} ritten` : "geen ritten in ritdata";
      const identifier = v.kenteken || v.vlootnummer || "";
      const secondary = v.kenteken ? v.vlootnummer : v.typeLocatie;
      const vehicleType = v.merk || v.soortVoertuig || v.soortBrandstof || "";
      return `<tr><td><strong>${escapeHtml(identifier)}</strong></td><td>${escapeHtml(secondary || "")}</td><td>${escapeHtml(vehicleType)}</td><td>${escapeHtml(tripsLabel)}</td></tr>`;
    }).join("");
    const hiddenCount = vehicles.length - Math.min(vehicles.length, 80);
    const hiddenNote = hiddenCount > 0 ? `<p class="standplaats-popup__hidden">+${hiddenCount} meer voertuig(en) niet getoond</p>` : "";
    new maplibregl.Popup({ closeButton: true, maxWidth: "460px" })
      .setLngLat(feature.geometry.coordinates)
      .setHTML(`
        <div class="standplaats-popup">
          <strong>${escapeHtml(p.name || sourceLabel)}</strong>
          <span>${escapeHtml(sourceLabel)}${p.regio ? " · " + escapeHtml(p.regio) : ""}</span>
          <span>${Number(p.vehicles || 0).toLocaleString("nl-NL")} voertuigen · ${Number(p.matchedInTrips || 0).toLocaleString("nl-NL")} met ritten in data</span>
          <table class="standplaats-popup__table">
            <thead><tr><th>Voertuig</th><th>Inzet/vloot</th><th>Type</th><th>Activiteit</th></tr></thead>
            <tbody>${rows}</tbody>
          </table>
          ${hiddenNote}
          <p class="standplaats-popup__disclaimer">${escapeHtml(disclaimer)}</p>
        </div>
      `)
      .addTo(map);
  }

  function addLayers() {
    ensureSource("stop-heat");
    ensureSource("selection-heat");
    ensureSource("stop-markers");
    ensureSource("road-lines");
    ensureSource("road-selection");
    ensureSource("road-break-demand");
    ensureSource("road-heat");
    ensureSource("overnight-locations");
    ensureSource("fleet-standplaatsen");
    ensureSource("charter-standplaatsen");
    ensureSource("chargers");

    if (!map.getLayer("stop-heat")) {
      map.addLayer({
        id: "stop-heat",
        type: "heatmap",
        source: "stop-heat",
        paint: {
          "heatmap-weight": ["interpolate", ["linear"], ["get", "weight"], 1, 0.15, 500, 1],
          "heatmap-intensity": 0.8,
          "heatmap-radius": 18,
          "heatmap-opacity": 0.72,
          "heatmap-color": [
            "interpolate",
            ["linear"],
            ["heatmap-density"],
            0,
            "rgba(46,35,67,0)",
            0.25,
            "#3b82f6",
            0.5,
            "#f9bc13",
            0.75,
            "#f6a119",
            1,
            "#dc2626"
          ]
        }
      });
    }

    if (!map.getLayer("selection-heat")) {
      map.addLayer({
        id: "selection-heat",
        type: "heatmap",
        source: "selection-heat",
        paint: {
          "heatmap-weight": ["interpolate", ["linear"], ["get", "weight"], 1, 0.2, 250, 1],
          "heatmap-intensity": 1.1,
          "heatmap-radius": 19,
          "heatmap-opacity": 0.78,
          "heatmap-color": [
            "interpolate",
            ["linear"],
            ["heatmap-density"],
            0,
            "rgba(20,83,45,0)",
            0.25,
            "#22c55e",
            0.5,
            "#f9bc13",
            0.75,
            "#f97316",
            1,
            "#dc2626"
          ]
        },
        layout: { visibility: "none" }
      });
    }

    if (!map.getLayer("road-heat")) {
      map.addLayer({
        id: "road-heat",
        type: "heatmap",
        source: "road-heat",
        paint: {
          "heatmap-weight": ["interpolate", ["linear"], ["get", "weight"], 1, 0.2, 100, 1],
          "heatmap-radius": 11,
          "heatmap-opacity": 0.55,
          "heatmap-color": [
            "interpolate",
            ["linear"],
            ["heatmap-density"],
            0,
            "rgba(30,58,138,0)",
            0.35,
            "#3b82f6",
            0.65,
            "#dc2626",
            1,
            "#7f1d1d"
          ]
        }
      });

      map.on("click", "stop-markers", (event) => {
        const feature = event.features?.[0];
        const coords = feature?.geometry?.coordinates;
        if (!coords || !dotNetRef) return;
        clearRoadSelection();
        dotNetRef.invokeMethodAsync(
          "SelectStopLocationAsync",
          Number(coords[1]),
          Number(coords[0]),
          String(feature.properties?.label || "")
        );
      });
      map.on("mouseenter", "stop-markers", () => { map.getCanvas().style.cursor = "pointer"; });
      map.on("mouseleave", "stop-markers", () => { map.getCanvas().style.cursor = ""; });
    }

    if (!map.getLayer("road-lines")) {
      map.addLayer({
        id: "road-lines",
        type: "line",
        source: "road-lines",
        paint: {
          "line-color": ["interpolate", ["linear"], ["get", "weight"], 1, "#ddd6fe", 50, "#4c1d95"],
          "line-width": ["interpolate", ["linear"], ["get", "weight"], 1, 1.2, 50, 5],
          "line-opacity": 0.82
        }
      });

      map.on("click", "road-lines", (event) => {
        selectRoadFeature(event.features?.[0]);
      });
      map.on("mouseenter", "road-lines", () => { map.getCanvas().style.cursor = "pointer"; });
      map.on("mouseleave", "road-lines", () => { map.getCanvas().style.cursor = ""; });
    }

    if (!map.getLayer("road-selection")) {
      map.addLayer({
        id: "road-selection",
        type: "line",
        source: "road-selection",
        paint: {
          "line-color": "#f9bc13",
          "line-width": ["interpolate", ["linear"], ["get", "weight"], 1, 4, 50, 9],
          "line-opacity": 0.94
        }
      });
    }

    if (!map.getLayer("road-lines-hitbox")) {
      map.addLayer({
        id: "road-lines-hitbox",
        type: "line",
        source: "road-lines",
        paint: {
          "line-color": "#000000",
          "line-width": 18,
          "line-opacity": 0
        }
      });

      map.on("click", "road-lines-hitbox", (event) => {
        selectRoadFeature(event.features?.[0]);
      });
      map.on("mouseenter", "road-lines-hitbox", () => { map.getCanvas().style.cursor = "pointer"; });
      map.on("mouseleave", "road-lines-hitbox", () => { map.getCanvas().style.cursor = ""; });
    }

    if (map.getLayer("road-selection") && map.getLayer("road-lines-hitbox")) {
      map.moveLayer("road-selection", "road-lines-hitbox");
    }

    if (!map.getLayer("road-break-demand-casing")) {
      map.addLayer({
        id: "road-break-demand-casing",
        type: "line",
        source: "road-break-demand",
        paint: {
          "line-color": "#ffffff",
          "line-width": ["interpolate", ["linear"], ["get", "passages"], 1, 6, 25, 10, 100, 14, 500, 18],
          "line-opacity": 0.88
        }
      });
    }

    if (!map.getLayer("road-break-demand")) {
      map.addLayer({
        id: "road-break-demand",
        type: "line",
        source: "road-break-demand",
        paint: {
          "line-color": ["interpolate", ["linear"], ["get", "passages"], 1, "#facc15", 10, "#fb923c", 50, "#f97316", 100, "#dc2626", 500, "#7f1d1d"],
          "line-width": ["interpolate", ["linear"], ["get", "passages"], 1, 3, 25, 6, 100, 10, 500, 14],
          "line-opacity": 0.96
        }
      });

      map.on("click", "road-break-demand", (event) => {
        selectRoadFeature(event.features?.[0]);
      });
      map.on("mouseenter", "road-break-demand", () => { map.getCanvas().style.cursor = "pointer"; });
      map.on("mouseleave", "road-break-demand", () => { map.getCanvas().style.cursor = ""; });
    }

    if (!map.getLayer("stop-markers")) {
      map.addLayer({
        id: "stop-markers",
        type: "circle",
        source: "stop-markers",
        paint: {
          "circle-radius": ["interpolate", ["linear"], ["get", "weight"], 1, 4, 80, 14],
          "circle-color": "#1f77b4",
          "circle-opacity": 0.72,
          "circle-stroke-width": 1,
          "circle-stroke-color": "#ffffff"
        }
      });
    }

    if (!map.getLayer("overnight-locations")) {
      map.addLayer({
        id: "overnight-locations",
        type: "circle",
        source: "overnight-locations",
        paint: {
          "circle-radius": ["interpolate", ["linear"], ["get", "weight"], 5, 7, 100, 18],
          "circle-color": "#f9bc13",
          "circle-opacity": 0.9,
          "circle-stroke-width": 2,
          "circle-stroke-color": "#2e2343"
        }
      });

      map.on("click", "overnight-locations", (event) => {
        const feature = event.features?.[0];
        const depotId = feature?.properties?.depotId;
        if (!depotId || !dotNetRef) return;
        clearRoadSelection();
        dotNetRef.invokeMethodAsync("SelectDepotAsync", depotId);
      });
      map.on("mouseenter", "overnight-locations", () => { map.getCanvas().style.cursor = "pointer"; });
      map.on("mouseleave", "overnight-locations", () => { map.getCanvas().style.cursor = ""; });
    }

    if (!map.getLayer("chargers")) {
      map.addLayer({
        id: "chargers",
        type: "circle",
        source: "chargers",
        paint: {
          "circle-radius": ["interpolate", ["linear"], ["get", "maxPowerKw"], 50, 5, 400, 10],
          "circle-color": "#16a34a",
          "circle-opacity": 0.88,
          "circle-stroke-width": 2,
          "circle-stroke-color": "#ffffff"
        }
      });

      map.on("click", "chargers", (event) => {
        const feature = event.features?.[0];
        if (!feature) return;
        const p = feature.properties || {};
        new maplibregl.Popup({ closeButton: true, maxWidth: "320px" })
          .setLngLat(feature.geometry.coordinates)
          .setHTML(`
            <div class="charger-popup">
              <strong>${escapeHtml(p.label || "Laadlocatie")}</strong>
              <span>${escapeHtml(p.address || "")}</span>
              <span>${Number(p.maxPowerKw || 0).toFixed(0)} kW · ${p.connectors || 0} laadpunten · ${escapeHtml(p.connectorType || "")}</span>
              <span>${escapeHtml(p.access || "Onbekend")}${p.dedicated === "Ja" ? " · gereserveerd" : ""}${p.twentyfourSeven === "Ja" ? " · 24/7" : ""}</span>
            </div>
          `)
          .addTo(map);
      });
      map.on("mouseenter", "chargers", () => { map.getCanvas().style.cursor = "pointer"; });
      map.on("mouseleave", "chargers", () => { map.getCanvas().style.cursor = ""; });
    }

    if (!map.getLayer("fleet-standplaatsen")) {
      map.addLayer({
        id: "fleet-standplaatsen",
        type: "circle",
        source: "fleet-standplaatsen",
        paint: {
          "circle-radius": ["interpolate", ["linear"], ["get", "vehicles"], 1, 6, 30, 18],
          "circle-color": "#2563eb",
          "circle-opacity": 0.85,
          "circle-stroke-width": 2,
          "circle-stroke-color": "#ffffff"
        }
      });

      map.on("click", "fleet-standplaatsen", (event) => {
        showStandplaatsPopup(event.features?.[0], "PostNL-wagenparkstandplaats", "Standplaats is ruw gegeocodeerd op plaatsnaam.");
      });
      map.on("mouseenter", "fleet-standplaatsen", () => { map.getCanvas().style.cursor = "pointer"; });
      map.on("mouseleave", "fleet-standplaatsen", () => { map.getCanvas().style.cursor = ""; });
    }

    if (!map.getLayer("charter-standplaatsen")) {
      map.addLayer({
        id: "charter-standplaatsen",
        type: "circle",
        source: "charter-standplaatsen",
        paint: {
          "circle-radius": ["interpolate", ["linear"], ["get", "vehicles"], 1, 5, 30, 16],
          "circle-color": "#7c3aed",
          "circle-opacity": 0.82,
          "circle-stroke-width": 2,
          "circle-stroke-color": "#ffffff"
        }
      });

      map.on("click", "charter-standplaatsen", (event) => {
        showStandplaatsPopup(event.features?.[0], "Charterstandplaats", "Charterstandplaats uit aparte Excel; bedoeld als fysiek shift-startpunt.");
      });
      map.on("mouseenter", "charter-standplaatsen", () => { map.getCanvas().style.cursor = "pointer"; });
      map.on("mouseleave", "charter-standplaatsen", () => { map.getCanvas().style.cursor = ""; });
    }

    if (map.getLayer("selection-heat") && map.getLayer("stop-markers")) {
      map.moveLayer("selection-heat", "stop-markers");
    }
    if (map.getLayer("road-break-demand-casing")) {
      map.moveLayer("road-break-demand-casing");
    }
    if (map.getLayer("road-break-demand")) {
      map.moveLayer("road-break-demand");
    }
    if (map.getLayer("road-selection")) {
      map.moveLayer("road-selection");
    }
  }

  function selectRoadFeature(feature) {
    const coords = feature?.geometry?.coordinates;
    if (!coords || coords.length < 2 || !dotNetRef) return;
    setData("road-selection", {
      type: "FeatureCollection",
      features: [{
        type: "Feature",
        properties: feature.properties || {},
        geometry: {
          type: "LineString",
          coordinates: coords
        }
      }]
    });
    dotNetRef.invokeMethodAsync(
      "SelectRoadAsync",
      Number(coords[0][1]),
      Number(coords[0][0]),
      Number(coords[coords.length - 1][1]),
      Number(coords[coords.length - 1][0]),
      Number(feature.properties?.radiusKm || 3)
    );
  }

  function clearRoadSelection() {
    if (!map || !isStyleReady()) return;
    setData("road-selection", emptyFeatureCollection());
  }

  async function postJson(path, payload, signal) {
    const response = await fetch(apiUrl(path), {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
      signal
    });
    if (!response.ok) {
      throw new Error(`${path}: ${response.status}`);
    }
    return response.json();
  }

  function setVisibility(layer, visible) {
    if (map.getLayer(layer)) {
      map.setLayoutProperty(layer, "visibility", visible ? "visible" : "none");
    }
  }

  function isStyleReady() {
    return typeof map.isStyleLoaded === "function" ? map.isStyleLoaded() : map.loaded();
  }

  function escapeHtml(value) {
    return String(value)
      .replaceAll("&", "&amp;")
      .replaceAll("<", "&lt;")
      .replaceAll(">", "&gt;")
      .replaceAll('"', "&quot;")
      .replaceAll("'", "&#039;");
  }

  return {
    init: (elementId, callbacks) => {
      if (map) return;
      dotNetRef = callbacks;
      wireLegendToggle();
      map = new maplibregl.Map({
        container: elementId,
        center: [5.3, 52.1],
        zoom: 7,
        style: {
          version: 8,
          sources: {
            osm: {
              type: "raster",
              tiles: ["https://tile.openstreetmap.org/{z}/{x}/{y}.png"],
              tileSize: 256,
              attribution: "OpenStreetMap"
            }
          },
          layers: [{ id: "osm", type: "raster", source: "osm" }]
        }
      });
      map.addControl(new maplibregl.NavigationControl({ visualizePitch: false }), "top-right");
      map.on("load", addLayers);
    },

    update: async (filter, chargerFilter, overnightFilter, breakDemandFilter, options) => {
      if (!map) return;
      if (!isStyleReady()) {
        window.setTimeout(() => window.routePlannerMap.update(filter, chargerFilter, overnightFilter, breakDemandFilter, options), 100);
        return;
      }
      wireLegendToggle();
      addLayers();

      if (abortController) abortController.abort();
      abortController = new AbortController();
      const signal = abortController.signal;
      status("Kaart bijwerken...");

      try {
        const stopsPromise = postJson("map/stops", filter, signal);
        const roadsPromise = options.showRoads || options.showRoadHeat
          ? postJson("map/roads", filter, signal)
          : Promise.resolve({ status: "skipped", lines: [], heatPoints: [] });
        const breakDemandPromise = options.showRoadBreakDemand
          ? postJson("roads/break-demand", breakDemandFilter, signal)
          : Promise.resolve({ status: "skipped", lines: [] });
        const breakRoadsPromise = options.showRoadBreakDemand
          ? postJson("map/roads", { ...filter, roadThreshold: 1, roadTopPercent: 25 }, signal)
          : Promise.resolve({ status: "skipped", lines: [], heatPoints: [] });
        const chargersPromise = options.showChargers
          ? postJson("map/chargers", chargerFilter, signal)
          : Promise.resolve({ status: "skipped", markers: [] });
        const overnightPromise = options.showOvernight
          ? postJson("overnight/locations", overnightFilter, signal)
          : Promise.resolve({ status: "skipped", locations: [] });
        const standplaatsenPromise = options.showStandplaatsen
          ? fetch(apiUrl("fleet/standplaatsen"), { signal }).then((r) => r.ok ? r.json() : { status: "error", depots: [] })
          : Promise.resolve({ status: "skipped", depots: [] });
        const charterStandplaatsenPromise = options.showCharterStandplaatsen
          ? fetch(apiUrl("fleet/charter-standplaatsen"), { signal }).then((r) => r.ok ? r.json() : { status: "error", depots: [] })
          : Promise.resolve({ status: "skipped", depots: [] });
        const [stops, roads, breakDemand, breakRoads, chargers, overnight, standplaatsen, charterStandplaatsen] = await Promise.all([stopsPromise, roadsPromise, breakDemandPromise, breakRoadsPromise, chargersPromise, overnightPromise, standplaatsenPromise, charterStandplaatsenPromise]);
        const breakDemandFeatures = roadBreakDemandCollection(breakDemand.lines, breakRoads.lines);

        setData("stop-heat", featureCollection(stops.heatPoints));
        setData("stop-markers", featureCollection(stops.markers, true));
        setData("road-lines", lineCollection(roads.lines));
        setData("road-break-demand", breakDemandFeatures);
        setData("road-heat", featureCollection(roads.heatPoints));
        setData("chargers", chargerCollection(chargers.markers));
        setData("overnight-locations", overnightCollection(overnight.locations));
        setData("fleet-standplaatsen", standplaatsCollection(standplaatsen.depots));
        setData("charter-standplaatsen", standplaatsCollection(charterStandplaatsen.depots));

        setVisibility("stop-heat", !!options.showStopHeat);
        setVisibility("stop-markers", !!options.showMarkers);
        setVisibility("road-lines", !!options.showRoads && roads.status === "ok");
        setVisibility("road-selection", (!!options.showRoads && roads.status === "ok") || (!!options.showRoadBreakDemand && breakDemand.status === "ok"));
        setVisibility("road-lines-hitbox", !!options.showRoads && roads.status === "ok");
        setVisibility("road-break-demand-casing", !!options.showRoadBreakDemand && breakDemand.status === "ok");
        setVisibility("road-break-demand", !!options.showRoadBreakDemand && breakDemand.status === "ok");
        setVisibility("road-heat", !!options.showRoadHeat && roads.status === "ok");
        setVisibility("chargers", !!options.showChargers && chargers.status === "ok");
        setVisibility("overnight-locations", !!options.showOvernight && overnight.status === "ok");
        setVisibility("fleet-standplaatsen", !!options.showStandplaatsen && standplaatsen.status === "ok");
        setVisibility("charter-standplaatsen", !!options.showCharterStandplaatsen && charterStandplaatsen.status === "ok");

        if (stops.markers?.length) {
          const bounds = new maplibregl.LngLatBounds();
          stops.markers.slice(0, 200).forEach((p) => bounds.extend([p.lon, p.lat]));
          if (!bounds.isEmpty()) map.fitBounds(bounds, { padding: 48, maxZoom: 10, duration: 450 });
        }

        const notes = [];
        if (options.showRoads && roads.status === "ok") notes.push(`${roads.lines?.length || 0} wegvlakken`);
        if (options.showRoadBreakDemand && breakDemand.status === "ok") notes.push(`${breakDemandFeatures.features.length} pauzelaadvraag-wegvlakken`);
        if (options.showRoadHeat && roads.status === "ok") notes.push(`${roads.heatPoints?.length || 0} wegdruktepunten`);
        if (options.showChargers && chargers.status === "ok") notes.push(`${chargers.markers?.length || 0} laders`);
        if (options.showOvernight && overnight.status === "ok") notes.push(`${overnight.locations?.length || 0} vaste stilstandlocaties`);
        if (options.showStandplaatsen && standplaatsen.status === "ok") notes.push(`${standplaatsen.depots?.length || 0} standplaatsen`);
        if (options.showCharterStandplaatsen && charterStandplaatsen.status === "ok") notes.push(`${charterStandplaatsen.depots?.length || 0} charterstandplaatsen`);
        if (roads.status === "cache_missing") notes.push("weglaag niet beschikbaar");
        if (options.showRoadBreakDemand && breakDemand.status === "cache_missing") notes.push("pauzelaadvraag niet beschikbaar");
        if (options.showChargers && chargers.status === "cache_missing") notes.push("laadlocaties niet beschikbaar");
        if (options.showOvernight && overnight.status !== "ok") notes.push("vaste stilstandlocaties niet beschikbaar");
        if (options.showStandplaatsen && standplaatsen.status !== "ok") notes.push("standplaatsen niet beschikbaar");
        if (options.showCharterStandplaatsen && charterStandplaatsen.status !== "ok") notes.push("charterstandplaatsen niet beschikbaar");
        status(notes.length ? `Kaart geladen · ${notes.join(" · ")}` : "Kaart geladen");
      } catch (error) {
        if (error.name !== "AbortError") {
          console.error(error);
          status("Kaart kon niet worden geladen");
        }
      }
    },

    selectDepot: async (depotId) => {
      if (dotNetRef) {
        await dotNetRef.invokeMethodAsync("SelectDepotAsync", depotId);
      }
    },

    selectRoad: async (lat1, lon1, lat2, lon2, radiusKm = 3) => {
      if (dotNetRef) {
        await dotNetRef.invokeMethodAsync("SelectRoadAsync", lat1, lon1, lat2, lon2, radiusKm);
      }
    },

    clearRoadSelection,

    scrollToSelectionDetail: (elementId) => {
      const scroll = () => {
        const el = document.getElementById(elementId || "selection-detail");
        if (!el) return;
        const top = window.scrollY + el.getBoundingClientRect().top;
        const contextOffset = Math.min(window.innerHeight * 0.42, 360);
        window.scrollTo({
          top: Math.max(0, top - contextOffset),
          behavior: "smooth"
        });
      };
      window.requestAnimationFrame(() => window.setTimeout(scroll, 0));
    },

    updateSelectionHeat: (points) => {
      if (!map || !isStyleReady()) return;
      addLayers();
      setData("selection-heat", featureCollection(points || []));
      setVisibility("selection-heat", (points || []).length > 0);
    },

    clearSelectionHeat: () => {
      if (!map || !isStyleReady()) return;
      setData("selection-heat", emptyFeatureCollection());
      setVisibility("selection-heat", false);
    }
  };
})();
