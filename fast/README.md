# PostNL Route Analyse Fast

Parallelle C# / Blazor versie van de route-analyse app.

## Run lokaal

```bash
PATH="$HOME/.dotnet:$PATH" dotnet run --project fast/Postnl.RouteAnalyse.Fast
```

Open daarna:

```text
http://localhost:5198/fast
```

## Data

De runtime leest bestaande precompute-cache uit `.cache/`:

- `postnl_csv_*.parquet` voor stops/trips
- `agg_weighted_edges_full.parquet`, `agg_weighted_edges_eigen.parquet`, `agg_weighted_edges_charter.parquet`
- `agg_road_heatmap_full.parquet`, `agg_road_heatmap_eigen.parquet`, `agg_road_heatmap_charter.parquet`
- optioneel `hdv_chargers.parquet`

Excel, CSV, geocoding en OSRM blijven offline Python precompute-stappen. De C# app doet geen netwerkcalls tijdens API-requests.

## API

Alle endpoints staan onder `/fast/api` en lokaal ook onder `/api` voor reverse-proxy setups die het prefix strippen:

- `GET /fast/api/metadata`
- `POST /fast/api/summary`
- `POST /fast/api/map/stops`
- `POST /fast/api/map/roads`
- `POST /fast/api/dashboard`
- `POST /fast/api/simulation`

## Tests

```bash
PATH="$HOME/.dotnet:$PATH" dotnet test fast/Postnl.RouteAnalyse.Fast.slnx
```
