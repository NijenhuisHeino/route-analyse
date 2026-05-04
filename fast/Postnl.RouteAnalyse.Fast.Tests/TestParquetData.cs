using DuckDB.NET.Data;

namespace Postnl.RouteAnalyse.Fast.Tests;

internal static class TestParquetData
{
    public static void WriteAll(string cacheDir)
    {
        Directory.CreateDirectory(cacheDir);
        WriteStopsParquet(Path.Combine(cacheDir, "postnl_csv_Test.parquet"));
        WriteRoadParquets(cacheDir);
        WriteChargersParquet(Path.Combine(cacheDir, "hdv_chargers.parquet"));
    }

    private static void WriteStopsParquet(string path)
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection,
            """
            CREATE TABLE stops AS
            SELECT * FROM (
                VALUES
                ('W1', 'Eigen vervoer', 'eigen', DATE '2026-01-01', 'T1', 0, 'Origin', 'Depot A', 'Adres A', TIMESTAMP '2026-01-01 08:00:00', TIMESTAMP '2026-01-01 08:00:00', 0.0, 120.0, 10.0, 52.000, 5.000),
                ('W1', 'Eigen vervoer', 'eigen', DATE '2026-01-01', 'T1', 1, 'Stop', 'Hub B', 'Adres B', TIMESTAMP '2026-01-01 12:00:00', TIMESTAMP '2026-01-01 12:00:00', 60.0, 120.0, 20.0, 52.500, 5.500),
                ('W1', 'Eigen vervoer', 'eigen', DATE '2026-01-01', 'T1', 2, 'Destination', 'Depot A', 'Adres A', TIMESTAMP '2026-01-01 18:00:00', TIMESTAMP '2026-01-01 18:00:00', 60.0, 120.0, 20.0, 52.000, 5.000),
                ('W1', 'Eigen vervoer', 'eigen', DATE '2026-01-02', 'T2', 0, 'Origin', 'Depot A', 'Adres A', TIMESTAMP '2026-01-02 06:00:00', TIMESTAMP '2026-01-02 06:00:00', 0.0, 180.0, 5.0, 52.000, 5.000),
                ('W1', 'Eigen vervoer', 'eigen', DATE '2026-01-02', 'T2', 1, 'Stop', 'Hub C', 'Adres C', TIMESTAMP '2026-01-02 11:00:00', TIMESTAMP '2026-01-02 11:00:00', 90.0, 180.0, 15.0, 52.200, 5.200),
                ('W1', 'Eigen vervoer', 'eigen', DATE '2026-01-02', 'T2', 2, 'Destination', 'Depot A', 'Adres A', TIMESTAMP '2026-01-02 15:00:00', TIMESTAMP '2026-01-02 15:00:00', 90.0, 180.0, 15.0, 52.000, 5.000),
                ('W2', 'Uitbesteed vervoer', 'charter', DATE '2026-01-02', 'T3', 0, 'Origin', 'Depot C', 'Adres C', TIMESTAMP '2026-01-02 07:00:00', TIMESTAMP '2026-01-02 07:00:00', 0.0, 200.0, 5.0, 53.000, 6.000),
                ('W2', 'Uitbesteed vervoer', 'charter', DATE '2026-01-02', 'T3', 1, 'Destination', 'Hub D', 'Adres D', TIMESTAMP '2026-01-02 17:00:00', TIMESTAMP '2026-01-02 17:00:00', 200.0, 200.0, 15.0, 51.900, 4.500)
            ) AS t(wagencode, vervoerder, vervoerder_type, trip_date, trip_id, stop_seq, acties, locatie_naam, adres, gepland_start, gepland_eind, afstand_km, afstand_km_trip, dwell_min, lat, lon);
            """);
        Execute(connection, $"COPY stops TO '{SqlPath(path)}' (FORMAT PARQUET);");
    }

    private static void WriteRoadParquets(string cacheDir)
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection,
            """
            CREATE TABLE edges AS
            SELECT * FROM (
                VALUES
                (52.000, 5.000, 52.100, 5.100, 3, 7),
                (52.100, 5.100, 52.200, 5.200, 2, 5)
            ) AS t(lat1, lon1, lat2, lon2, n_wagens, n_passes);
            CREATE TABLE heat AS
            SELECT * FROM (
                VALUES
                (52.000, 5.000, 3.0),
                (52.100, 5.100, 2.0)
            ) AS t(lat, lon, weight);
            """);

        foreach (var variant in new[] { "full", "eigen", "charter" })
        {
            Execute(connection, $"COPY edges TO '{SqlPath(Path.Combine(cacheDir, $"agg_weighted_edges_{variant}.parquet"))}' (FORMAT PARQUET);");
            Execute(connection, $"COPY heat TO '{SqlPath(Path.Combine(cacheDir, $"agg_road_heatmap_{variant}.parquet"))}' (FORMAT PARQUET);");
        }
    }

    private static void WriteChargersParquet(string path)
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        Execute(connection,
            """
            CREATE TABLE chargers AS
            SELECT * FROM (
                VALUES
                (1, 52.100, 5.100, 'Publieke HPC', 'Operator A', 'Straat 1', 'Utrecht', '3500AA', 350.0, 8, 'Publiek', 'Ja', 'Nee', 'CCS', 'Ja', '2025-01-01'),
                (2, 52.200, 5.200, 'Dedicated Depot', 'Operator B', 'Straat 2', 'Amersfoort', '3800AA', 450.0, 4, 'Privaat', 'Nee', 'Ja', 'CCS/MCS', 'Nee', '2025-01-01')
            ) AS t(LocatieID, lat, lon, name, operator, address, town, postcode, max_power_kw, n_connectors, toegankelijkheid, twentyfour_seven, dedicated, ccs_mcs, wachtruimte, in_gebruik_vanaf);
            """);
        Execute(connection, $"COPY chargers TO '{SqlPath(path)}' (FORMAT PARQUET);");
    }

    private static string SqlPath(string path) => path.Replace("'", "''", StringComparison.Ordinal);

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
