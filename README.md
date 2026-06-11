# Laadinfrastructuur Planner

Hoofdapp voor depot-, rit- en corridoranalyse voor laadinfrastructuur. De planner helpt bepalen waar laadcapaciteit nodig is, hoeveel vermogen per locatie passend is en wanneer publieke corridor-lading nodig wordt.

## Lokaal starten

```bash
PATH="$HOME/.dotnet:$PATH" dotnet run --project src/LaadinfrastructuurPlanner
```

Open daarna:

```text
http://localhost:5198
```

## Mappen

- `src/LaadinfrastructuurPlanner`: de planner-app.
- `tests/LaadinfrastructuurPlanner.Tests`: controles op data-inlees, filters, kaartlagen, depots, wegvlakken en laadscenario's.
- `.cache/planner`: lokale werkset die automatisch wordt ververst wanneer brondata wijzigt.

## Data

De planner leest ritbestanden, locatiekoppelingen en laadlocaties uit een lokale werkset. Grote bronbestanden, klantdata en lokale werksets blijven buiten Git.

Via de app kan een eigen dataset worden geupload als CSV of parquet. Voor CSV verwacht de planner kolommen voor voertuig, rit, start/eind, afstand en bij voorkeur `lat`/`lon`. Zonder `lat`/`lon` is een lokale locatiekoppeling nodig.

Gebruik deze variabelen wanneer de data op een andere plek staat:

```bash
ROUTE_ANALYSIS_ORIGINAL_CSV_DIR=/pad/naar/rittendata
ROUTE_ANALYSIS_EXTERNAL_CACHE_DIR=/pad/naar/cache-backup
ROUTE_ANALYSIS_REPO_ROOT=/pad/naar/repo            # overschrijft de afgeleide repo-root
ROUTE_ANALYSIS_DRIVE_DATA_DIR=/pad/naar/drive-data # map met wagenpark- en charter-Excels
ROUTE_ANALYSIS_FLEET_EXCEL_PATH=/pad/naar/ev_wagenpark_standplaatsen.xlsx
ROUTE_ANALYSIS_CHARTER_FLEET_EXCEL_PATH="/pad/naar/Standplaatsen charters.xlsx"
ROUTE_ANALYSIS_ZE_ZONES_PATH=/pad/naar/zez_pc6.csv # of .xlsx/.zip
```

Machine-specifieke standaardpaden (Drive-datamap en ZE-zone fallbacks) staan in
`appsettings.json` onder `RouteAnalysis:DriveDataDir` en `RouteAnalysis:ZeZonesFallbackPaths`;
omgevingsvariabelen gaan altijd voor.

## Toegang

De productie-URL loopt achter Cloudflare Access. Gebruikers moeten eerst met e-mail inloggen voordat de planner of API bereikbaar is.

Optioneel kan de app zelf ook een e-mailallowlist afdwingen:

```bash
ROUTE_ANALYSIS_ALLOWED_EMAILS=info@example.com
```

## Testen

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test LaadinfrastructuurPlanner.slnx
```

Elke pull request en elke push naar `main` draait dezelfde testset via GitHub
Actions, plus een Release publish-smoke. Main niet mergen als die check rood is.

## Publiceren

```bash
PATH="$HOME/.dotnet:$PATH" dotnet publish src/LaadinfrastructuurPlanner -c Release -o .deploy/route-analyse
```
