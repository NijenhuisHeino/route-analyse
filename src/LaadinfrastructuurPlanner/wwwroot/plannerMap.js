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

  function addLayers() {
    ensureSource("stop-heat");
    ensureSource("selection-heat");
    ensureSource("stop-markers");
    ensureSource("road-lines");
    ensureSource("road-heat");
    ensureSource("overnight-locations");
    ensureSource("fleet-standplaatsen");
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
        const feature = event.features?.[0];
        const coords = feature?.geometry?.coordinates;
        if (!coords || coords.length < 2 || !dotNetRef) return;
        dotNetRef.invokeMethodAsync(
          "SelectRoadAsync",
          Number(coords[0][1]),
          Number(coords[0][0]),
          Number(coords[coords.length - 1][1]),
          Number(coords[coords.length - 1][0]),
          Number(feature.properties?.radiusKm || 3)
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
        const feature = event.features?.[0];
        if (!feature) return;
        const p = feature.properties || {};
        let vehicles = [];
        try { vehicles = JSON.parse(p.vehicleList || "[]"); } catch { vehicles = []; }
        const rows = vehicles.slice(0, 50).map((v) => {
          const trips = Number(v.tripsInData || 0);
          const tripsLabel = trips > 0 ? `${trips.toLocaleString("nl-NL")} ritten` : "geen ritten in data";
          return `<tr><td><strong>${escapeHtml(v.kenteken || "")}</strong></td><td>${escapeHtml(v.vlootnummer || "")}</td><td>${escapeHtml(v.merk || "")}</td><td>${escapeHtml(tripsLabel)}</td></tr>`;
        }).join("");
        const hiddenCount = vehicles.length - Math.min(vehicles.length, 50);
        const hiddenNote = hiddenCount > 0 ? `<p class="standplaats-popup__hidden">+${hiddenCount} meer voertuig(en) niet getoond</p>` : "";
        new maplibregl.Popup({ closeButton: true, maxWidth: "420px" })
          .setLngLat(feature.geometry.coordinates)
          .setHTML(`
            <div class="standplaats-popup">
              <strong>${escapeHtml(p.name || "Standplaats")}</strong>
              <span>${escapeHtml(p.typeLocatie || "")}${p.regio ? " · " + escapeHtml(p.regio) : ""}</span>
              <span>${Number(p.vehicles || 0)} voertuigen · ${Number(p.matchedInTrips || 0)} met ritten in data</span>
              <table class="standplaats-popup__table">
                <thead><tr><th>Kenteken</th><th>Vloot</th><th>Merk</th><th>Activiteit</th></tr></thead>
                <tbody>${rows}</tbody>
              </table>
              ${hiddenNote}
              <p class="standplaats-popup__disclaimer">Standplaats is ruw gegeocodeerd op plaatsnaam.</p>
            </div>
          `)
          .addTo(map);
      });
      map.on("mouseenter", "fleet-standplaatsen", () => { map.getCanvas().style.cursor = "pointer"; });
      map.on("mouseleave", "fleet-standplaatsen", () => { map.getCanvas().style.cursor = ""; });
    }

    if (map.getLayer("selection-heat") && map.getLayer("stop-markers")) {
      map.moveLayer("selection-heat", "stop-markers");
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
        const standplaatsenPromise = options.showStandplaatsen
          ? fetch(apiUrl("fleet/standplaatsen"), { signal }).then((r) => r.ok ? r.json() : { status: "error", depots: [] })
          : Promise.resolve({ status: "skipped", depots: [] });
        const [stops, roads, chargers, overnight, standplaatsen] = await Promise.all([stopsPromise, roadsPromise, chargersPromise, overnightPromise, standplaatsenPromise]);

        setData("stop-heat", featureCollection(stops.heatPoints));
        setData("stop-markers", featureCollection(stops.markers, true));
        setData("road-lines", lineCollection(roads.lines));
        setData("road-heat", featureCollection(roads.heatPoints));
        setData("chargers", chargerCollection(chargers.markers));
        setData("overnight-locations", overnightCollection(overnight.locations));
        setData("fleet-standplaatsen", standplaatsCollection(standplaatsen.depots));

        setVisibility("stop-heat", !!options.showStopHeat);
        setVisibility("stop-markers", !!options.showMarkers);
        setVisibility("road-lines", !!options.showRoads && roads.status === "ok");
        setVisibility("road-heat", !!options.showRoadHeat && roads.status === "ok");
        setVisibility("chargers", !!options.showChargers && chargers.status === "ok");
        setVisibility("overnight-locations", !!options.showOvernight && overnight.status === "ok");
        setVisibility("fleet-standplaatsen", !!options.showStandplaatsen && standplaatsen.status === "ok");

        if (stops.markers?.length) {
          const bounds = new maplibregl.LngLatBounds();
          stops.markers.slice(0, 200).forEach((p) => bounds.extend([p.lon, p.lat]));
          if (!bounds.isEmpty()) map.fitBounds(bounds, { padding: 48, maxZoom: 10, duration: 450 });
        }

        const notes = [];
        if (options.showRoads && roads.status === "ok") notes.push(`${roads.lines?.length || 0} wegvlakken`);
        if (options.showRoadHeat && roads.status === "ok") notes.push(`${roads.heatPoints?.length || 0} wegdruktepunten`);
        if (options.showChargers && chargers.status === "ok") notes.push(`${chargers.markers?.length || 0} laders`);
        if (options.showOvernight && overnight.status === "ok") notes.push(`${overnight.locations?.length || 0} vaste stilstandlocaties`);
        if (options.showStandplaatsen && standplaatsen.status === "ok") notes.push(`${standplaatsen.depots?.length || 0} standplaatsen`);
        if (roads.status === "cache_missing") notes.push("weglaag niet beschikbaar");
        if (options.showChargers && chargers.status === "cache_missing") notes.push("laadlocaties niet beschikbaar");
        if (options.showOvernight && overnight.status !== "ok") notes.push("vaste stilstandlocaties niet beschikbaar");
        if (options.showStandplaatsen && standplaatsen.status !== "ok") notes.push("standplaatsen niet beschikbaar");
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
