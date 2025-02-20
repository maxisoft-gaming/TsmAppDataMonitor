using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

var config = new Config(args);
var state = State.Load();
var processor = new FileProcessor(config, state);
var monitor = new FileMonitor(config, processor);

monitor.Start();
await Task.Delay(-1); // Keep application running

public class Config
{
    public string FilePath { get; }
    public string OutputDir { get; }
    public int QuietPeriod { get; } = 30;
    public int PollInterval { get; } = 5;

    public Config(string[] args)
    {
        // Simplified argument parsing
        FilePath = GetPathFromArgs(args) ??
                   Environment.GetEnvironmentVariable("TSM_MONITOR_PATH") ??
                   State.Load()?.LastFilePath ?? "";

        if (string.IsNullOrEmpty(FilePath))
            throw new ArgumentException("File path not specified");

        OutputDir = Path.GetFullPath(Environment.GetEnvironmentVariable("TSM_OUTPUT_DIR") ?? ".");
        Directory.CreateDirectory(OutputDir);
    }

    private string? GetPathFromArgs(string[] args)
    {
        // Add proper command line parsing if needed
        return args.FirstOrDefault(File.Exists);
    }
}

public record State(
    string LastHash,
    string LastFilePath,
    DateTime LastProcessed
)
{
    private const string StateFile = "appstate.json";

    public void Save()
    {
        File.WriteAllText(StateFile, JsonSerializer.Serialize(this, StateJsonContext.Default.State));
    }

    public static State? Load() =>
        File.Exists(StateFile) ? 
            JsonSerializer.Deserialize(File.ReadAllText(StateFile), StateJsonContext.Default.State) : 
            null;
}

[JsonSerializable(typeof(State))]
public partial class StateJsonContext : JsonSerializerContext
{
}


public class FileProcessor
{
    private readonly Config _config;
    private State? _state;

    public FileProcessor(Config config, State? state)
    {
        _config = config;
        _state = state;
    }

    public async Task ProcessAsync()
    {
        try
        {
            var fileInfo = new FileInfo(_config.FilePath);
            if (!fileInfo.Exists || fileInfo.Length == 0) return;

            using var stream = new FileStream(
                _config.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            var hash = await ComputeHashAsync(stream);
            if (hash == _state?.LastHash) return;

            var outputPath = GenerateOutputPath();
            stream.Seek(0, SeekOrigin.Begin);
            await CompressFileAsync(stream, outputPath);

            _state = new State(hash, _config.FilePath, DateTime.UtcNow);
            _state.Save();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Processing error: {ex.Message}");
        }
    }

    private async Task<string> ComputeHashAsync(Stream stream)
    {
        using var sha = SHA256.Create();
        var hashBytes = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes);
    }

    private string GenerateOutputPath() =>
        Path.Combine(
            _config.OutputDir,
            $"{Path.GetFileName(_config.FilePath)}_{DateTime.Now:yyyy_MM_dd_HHmmss}.brotli"
        );

    private async Task CompressFileAsync(Stream input, string outputPath)
    {
        using var output = new FileStream(outputPath, FileMode.CreateNew);
        using var compressor = new BrotliStream(output, CompressionLevel.Fastest);
        await input.CopyToAsync(compressor);
    }
}

public class FileMonitor : IDisposable
{
    private readonly Config _config;
    private readonly FileProcessor _processor;
    private readonly Timer _pollTimer;
    private readonly Timer _debounceTimer;
    private readonly FileSystemWatcher? _watcher;
    private DateTime _lastWrite = DateTime.MinValue;
    private long _lastSize = -1;

    public FileMonitor(Config config, FileProcessor processor)
    {
        _config = config;
        _processor = processor;

        _debounceTimer = new Timer(_ => _processor.ProcessAsync().Wait());

        try
        {
            _watcher = new FileSystemWatcher(
                Path.GetDirectoryName(_config.FilePath)!,
                Path.GetFileName(_config.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += OnFileChanged;
        }
        catch
        {
            Console.WriteLine("Filesystem watcher initialization failed");
        }

        _pollTimer = new Timer(CheckFilePoll, null,
            TimeSpan.FromSeconds(_config.PollInterval),
            TimeSpan.FromSeconds(_config.PollInterval));
    }

    public void Start()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e) =>
        HandleChange();

    private void CheckFilePoll(object? state)
    {
        try
        {
            var fi = new FileInfo(_config.FilePath);
            if (fi.LastWriteTime > _lastWrite || fi.Length != _lastSize)
            {
                HandleChange();
                _lastWrite = fi.LastWriteTime;
                _lastSize = fi.Length;
            }
        }
        catch
        {
            Console.WriteLine("Polling check failed");
        }
    }

    private void HandleChange() =>
        _debounceTimer.Change(
            TimeSpan.FromSeconds(_config.QuietPeriod),
            Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _watcher?.Dispose();
        _pollTimer.Dispose();
        _debounceTimer.Dispose();
    }
}