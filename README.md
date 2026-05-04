# PostNL Laadinfrastructuur Planner

Hoofdapp voor depot-, rit- en corridoranalyse voor laadinfrastructuur. De planner helpt bepalen waar laadcapaciteit nodig is, hoeveel vermogen per locatie passend is en wanneer publieke corridor-lading nodig wordt.

## Lokaal starten

```bash
PATH="$HOME/.dotnet:$PATH" dotnet run --project src/Postnl.LaadinfrastructuurPlanner
```

Open daarna:

```text
http://localhost:5198
```

## Mappen

- `src/Postnl.LaadinfrastructuurPlanner`: de planner-app.
- `tests/Postnl.LaadinfrastructuurPlanner.Tests`: controles op data-inlees, filters, kaartlagen, depots, wegvlakken en laadscenario's.
- `.cache/planner`: lokale werkset die automatisch wordt ververst wanneer brondata wijzigt.

## Data

De planner leest de PostNL ritbestanden, locatiekoppelingen en laadlocaties uit de projectdata-map op deze Mac. Grote bronbestanden en lokale werksets blijven buiten Git.

Gebruik deze variabelen wanneer de data op een andere plek staat:

```bash
POSTNL_ORIGINAL_CSV_DIR=/pad/naar/rittendata
POSTNL_EXTERNAL_CACHE_DIR=/pad/naar/cache-backup
```

## Testen

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test Postnl.LaadinfrastructuurPlanner.slnx
```
