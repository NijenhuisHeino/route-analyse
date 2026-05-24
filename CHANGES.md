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
- De 2026 instroom is omgerekend naar 3 trekker-equivalenten omdat 6 trekkers pas in september instromen.
