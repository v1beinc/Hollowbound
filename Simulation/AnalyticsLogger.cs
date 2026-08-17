using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Hollowbound.Simulation;

public sealed class AnalyticsLogger : IDisposable
{
    private readonly JsonSerializerOptions _jsonOptions = new();
    private StreamWriter? _writer;
    private long _lastSnapshotTick = -1;
    private int _recordsSinceFlush;

    public string LogPath { get; }

    public AnalyticsLogger()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                root = AppContext.BaseDirectory;

            var directory = Path.Combine(root, "Hollowbound", "logs");
            Directory.CreateDirectory(directory);
            LogPath = Path.Combine(directory, $"simulation-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.jsonl");
            _writer = new StreamWriter(LogPath, false, new UTF8Encoding(false), 16 * 1024);
        }
        catch
        {
            LogPath = string.Empty;
        }
    }

    public void LogEvent(string kind, EmergentSimulationWorld world, float timeScale, bool paused, string? detail = null)
    {
        if (kind is "run_started" or "new_world")
            _lastSnapshotTick = -1;
        WriteRecord(kind, world, timeScale, paused, detail);
    }

    public void MaybeWriteSnapshot(EmergentSimulationWorld world, float timeScale, bool paused)
    {
        if (world.Tick == _lastSnapshotTick || world.Tick - _lastSnapshotTick < 100)
            return;

        _lastSnapshotTick = world.Tick;
        WriteRecord("snapshot", world, timeScale, paused, null);
    }

    private void WriteRecord(string kind, EmergentSimulationWorld world, float timeScale, bool paused, string? detail)
    {
        if (_writer is null)
            return;

        try
        {
            var alive = world.Agents.Count(agent => agent.Alive);
            var record = new
            {
                timestamp_utc = DateTimeOffset.UtcNow,
                kind,
                detail,
                seed = world.Seed,
                tick = world.Tick,
                population = alive,
                food_stockpile = world.FoodStockpile,
                wall_blocks = world.Map.WallCells.Count,
                passages = world.WallBlocksRemoved,
                births = world.Births,
                deaths = world.Deaths,
                stored_piles = world.FoodStorage.Count,
                speed = timeScale,
                paused,
                catching_up = world.IsCatchingUp,
            };
            _writer.WriteLine(JsonSerializer.Serialize(record, _jsonOptions));
            _recordsSinceFlush++;
            if (kind != "snapshot" || _recordsSinceFlush >= 10)
            {
                _writer.Flush();
                _recordsSinceFlush = 0;
            }
        }
        catch
        {
            _writer.Dispose();
            _writer = null;
        }
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
    }
}
