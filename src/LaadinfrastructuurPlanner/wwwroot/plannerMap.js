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
          weight: line.uniqueWagens || 1,
          passes: line.passes || 0
        },
        geometry: {
          type: "LineString",
          coordinates: [
            [line.lon1, line.lat1],
            [line.lon2, line.lat2]
          ]
        }
      }))
    };
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
          shortageMwh: location.shortageMwh || 0,
          recommendation: location.recommendation || ""
        },
        geometry: { type: "Point", coordinates: [location.lon, location.lat] }
      }))
    };
  }

  function addLayers() {
    ensureSource("stop-heat");
    ensureSource("stop-markers");
    ensureSource("road-lines");
    ensureSource("road-heat");
    ensureSource("overnight-locations");
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
        const feature = event.features?.[0];
        const coords = feature?.geometry?.coordinates;
        if (!coords || coords.length < 2 || !dotNetRef) return;
        dotNetRef.invokeMethodAsync(
          "SelectRoadAsync",
          Number(coords[0][1]),
          Number(coords[0][0]),
          Number(coords[coords.length - 1][1]),
          Number(coords[coords.length - 1][0])
        );
      });
      map.on("mouseenter", "road-lines", () => { map.getCanvas().style.cursor = "pointer"; });
      map.on("mouseleave", "road-lines", () => { map.getCanvas().style.cursor = ""; });
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
          "circle-color": ["case", [">", ["get", "shortageMwh"], 0], "#dc2626", "#f9bc13"],
          "circle-opacity": 0.9,
          "circle-stroke-width": 2,
          "circle-stroke-color": "#2e2343"
        }
      });

      map.on("click", "overnight-locations", (event) => {
        const feature = event.features?.[0];
        const depotId = feature?.properties?.depotId;
        if (!depotId || !dotNetRef) return;
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

    update: async (filter, chargerFilter, overnightFilter, options) => {
      if (!map) return;
      if (!isStyleReady()) {
        window.setTimeout(() => window.routePlannerMap.update(filter, chargerFilter, overnightFilter, options), 100);
        return;
      }
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
        const chargersPromise = options.showChargers
          ? postJson("map/chargers", chargerFilter, signal)
          : Promise.resolve({ status: "skipped", markers: [] });
        const overnightPromise = options.showOvernight
          ? postJson("overnight/locations", overnightFilter, signal)
          : Promise.resolve({ status: "skipped", locations: [] });
        const [stops, roads, chargers, overnight] = await Promise.all([stopsPromise, roadsPromise, chargersPromise, overnightPromise]);

        setData("stop-heat", featureCollection(stops.heatPoints));
        setData("stop-markers", featureCollection(stops.markers, true));
        setData("road-lines", lineCollection(roads.lines));
        setData("road-heat", featureCollection(roads.heatPoints));
        setData("chargers", chargerCollection(chargers.markers));
        setData("overnight-locations", overnightCollection(overnight.locations));

        setVisibility("stop-heat", !!options.showStopHeat);
        setVisibility("stop-markers", !!options.showMarkers);
        setVisibility("road-lines", !!options.showRoads && roads.status === "ok");
        setVisibility("road-heat", !!options.showRoadHeat && roads.status === "ok");
        setVisibility("chargers", !!options.showChargers && chargers.status === "ok");
        setVisibility("overnight-locations", !!options.showOvernight && overnight.status === "ok");

        if (stops.markers?.length) {
          const bounds = new maplibregl.LngLatBounds();
          stops.markers.slice(0, 200).forEach((p) => bounds.extend([p.lon, p.lat]));
          if (!bounds.isEmpty()) map.fitBounds(bounds, { padding: 48, maxZoom: 10, duration: 450 });
        }

        const notes = [];
        if (options.showChargers && chargers.status === "ok") notes.push(`${chargers.markers?.length || 0} laders`);
        if (options.showOvernight && overnight.status === "ok") notes.push(`${overnight.locations?.length || 0} vaste stilstandlocaties`);
        if (roads.status === "cache_missing") notes.push("weglaag niet beschikbaar");
        if (options.showChargers && chargers.status === "cache_missing") notes.push("laadlocaties niet beschikbaar");
        if (options.showOvernight && overnight.status !== "ok") notes.push("vaste stilstandlocaties niet beschikbaar");
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

    selectRoad: async (lat1, lon1, lat2, lon2) => {
      if (dotNetRef) {
        await dotNetRef.invokeMethodAsync("SelectRoadAsync", lat1, lon1, lat2, lon2);
      }
    }
  };
})();
