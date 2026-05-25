# Stuurgroep 1 juni - power profiles

## 1. 24-uurs vermogensprofiel per standplaats
- Geimplementeerd: aparte `route_actions`-laag die `wait_task_available`, `wait_after`, `wait_action` en `pause` als laadvensters bewaart.
- Geimplementeerd: API voor standplaats x uur heatmap, top-5 profiel en locatieprofiel.
- Berekening: per truck `Batterijcapaciteit (kWh) / stilstanduren`; de heatmap telt deze individuele kW-vragen gelijktijdig op.
- Output: `out/nieuwegein/nieuwegein_hourly_profile.csv`, `top5_own_current_heatmap.csv`, `top5_own_current_heatmap.parquet` en `nieuwegein_power_report.png`.

## 2. Eigen vs charter
- Geimplementeerd: `vehicle_class` met `own`, `charter`, `unknown` in `route_actions`.
- Mapping gebruikt carrier/eigenaartekst uit de ritdata; fleet Excel wordt in diagnostics gebruikt om matchgraad zichtbaar te maken.
- Charterlocaties zonder bruikbare geocode blijven als `unknown_location:*` meetellen in diagnostics.

## 3. Locatieprofiel Nieuwegein
- Geimplementeerd: drill-down API met unieke voertuigen per dag, eigen/charter split, ritten/events, gemiddelde verblijfsduur, piek-kW en 24-uurs profiel.
- Nieuwegein wordt herkend op `Groteweerd`, `Nieuwegein` of de ingestelde focusalias. In de huidige data is de exportlocatie `Mobilisatiedok 4, 3439 JG Nieuwegein`.

## 4. E-truck instroom-overlay
- Geimplementeerd: lineaire scenario-overlay en cherry-pick modus in de service.
- Madeleine-input is vastgelegd als rekenaantallen: 2026 = 3 trekkers/1 bakwagen, 2027 = 10/16, 2028 = 21/25, 2029 = 38/25, 2030 = 56/25, 2031 = 75/25.
- Output: `out/nieuwegein/nieuwegein_scenarios_2027_2030.csv`.

## 5. Data-kwaliteit en sanity checks
- Geimplementeerd: diagnostics API met total actions, laadvensters, ontbrekende locaties, onbekende voertuigklasse, routes zonder wait/pause venster en fleet-match tellingen.
- De UI toont de belangrijkste diagnostics prominent onder de 24-uurs heatmap.

## Aannames en twijfels
- Vermogensvraag: elke truck vraagt standaard 590 kWh op de locatie, gedeeld door de stilstanduren. De 590 kWh komt uit de linker paneelparameter `Batterijcapaciteit (kWh)`.
- Cut-off tijden en gewicht/ladingprofielen zijn niet meegenomen.
- Fleet Excel-match is diagnostisch; de primaire `own`/`charter` classificatie blijft gebaseerd op ritdata omdat die voor alle route-acties aanwezig is.

## 6. Review door data-scientist / wiskundige (2026-05-25)
- **Site-cap + anomaly_flag**: aggregate vermogen per uur per site nu hard begrensd op `SiteLimitMw` (default 1.4 MW); overschrijdingen worden gemarkeerd via `anomaly_flag` kolom in alle CSV-exports en doorgepropageerd naar `PowerHourlyCell`, `PowerDailyMetric`, `PowerHeatmapCell`, scenario-cellen.
- **Minimum dwell**: default `MinDwellMin` opgehoogd van 0 naar 15 minuten; voorkomt division-by-small-number explosies bij korte dwell-tijden.
- **DataQualityReport**: nieuwe endpoint `GET /api/data-quality` met null-rates, time-inversions, dubbele trip-ids, negative/zero distances, implausible speed (>120 km/h), long-dwell outliers. Telt absolute aantallen en percentages per regel.
- **kWh/km per klasse + seizoen**: `RouteAnalysisDefaults.VehicleEnergyAssumptions` differentieert nu trekker (winter 1.60, summer 1.30), bakwagen (winter 1.00, summer 0.85), unknown (winter 1.30, summer 1.10). Helper `ResolveKwhPerKm(class, date)` beschikbaar voor consumers; SQL-pipeline override is follow-up.
- **Sidecar meta.json**: elke CSV/Parquet export in `out/nieuwegein/` krijgt nu een `<name>.meta.json` met run-parameters, SHA256, software-versie, generated_at, energy assumptions en scenario inflows. Auditbaar voor reviewers.
- **Sensitivity sweep**: nieuwe endpoint `POST /api/power/sensitivity` met sweep over {energy 0.85-1.60, SoC bands, fleet low/base/high} en P10/P50/P90 voor peak_mw en shortage_mwh.
- **Property-based tests**: nieuwe `PhysicsConstraintsTests` met o.a. `PowerProfileNeverExceedsSiteLimit`, `ShortageMwhIsNeverNegative`, `ScenarioPercolatesNoPhysicallyImpossibleValues`, `DataQualityReportReturnsKnownIssueCodes`.
- **Coordinaten-precisie**: location_id format `auto:%.3f:%.3f` → `auto:%.4f:%.4f` (~11 m i.p.v. ~111 m); voorkomt dat twee depots binnen 100 m onder één auto-cluster ID vallen.
- **S-curve fleet rollout**: `RouteAnalysisOptions.FleetRolloutMode = "scurve"` activeert logistische groei (`L/(1+e^(-k(t-t0)))`) ipv lineair; parameters `FleetRolloutK` (default 1.1) en `FleetRolloutT0Year` (default 2029).
- **Fleet-match endpoint**: `GET /api/fleet/match` geeft per voertuig in trip-data of het in fleet-Excel zit; lijst van "unknown" voertuigen voor handmatige review.
- **Geocoding override**: optioneel `data/fleet_geocoded.csv` of `ROUTE_ANALYSIS_GEOCODING_OVERRIDE` env var; overschrijft Nominatim resultaten met handmatig geverifieerde coordinaten. Template: `scripts/fleet_geocoded_template.csv`.
- **Corridor hotspots**: nieuwe endpoint `POST /api/corridors/hotspots` alloceert corridor-shortage MWh aan top-N drukste wegsegmenten via grid-bucketed (`mid_lat,mid_lon` op 0.01° ≈ 1.1 km) Voronoi-achtige clustering.
- **Backtest gap**: tool's `1.2 kWh/km` is **niet** gevalideerd tegen reëel meterverbruik. Template `scripts/backtest_template.csv` toegevoegd voor PostNL pilot-data; MAPE-berekening volgt zodra meterdata beschikbaar is.
- De 2026 instroom is omgerekend naar 3 trekker-equivalenten omdat 6 trekkers pas in september instromen.
