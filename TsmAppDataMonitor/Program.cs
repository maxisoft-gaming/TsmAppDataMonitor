using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var config = new Config(args);
Mutex? mutex = null;
if (config.SingleInstanceEnabled)
{
    mutex = new Mutex(true, "Global\\" + config.InstanceMutexName, out var createdNew);
    if (!createdNew && !mutex.WaitOne(TimeSpan.FromSeconds(5)))
    {
        Console.WriteLine("Another instance is already monitoring this file and output directory");
        return;
    }

    // Keep mutex alive for the application lifetime
    GC.KeepAlive(mutex);
}

try
{
    var state = State.Load();
    var processor = new FileProcessor(config, state);
    var monitor = new FileMonitor(config, processor);
    var iteration = monitor.CheckIterations;
    monitor.Start();

    // monitor the monitor
    while (mutex is null || mutex.WaitOne(TimeSpan.FromSeconds(config.PollInterval)))
    {
        Thread.Sleep((config.PollInterval + config.QuietPeriod) * 2000);
        try
        {
            var newIteration = monitor.CheckIterations;
            if (newIteration <= iteration)
            {
                if (iteration - newIteration > 1L << 40)
                {
                    Console.WriteLine("Overflow detected");
                }
                else
                {
                    Console.Error.WriteLine("Monitor stopped");
                    Environment.Exit(2);
                }
            }

            iteration = newIteration;
        }
        finally
        {
            mutex?.ReleaseMutex();
        }
    }
}
finally
{
    if (mutex is not null)
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}


public class Config
{
    public string FilePath { get; }
    public string OutputDir { get; }
    public int QuietPeriod { get; } = 30;
    public int PollInterval { get; } = 5;

    public bool SingleInstanceEnabled { get; }

    public string InstanceMutexName { get; } = string.Empty;

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

        SingleInstanceEnabled = GetSingleInstanceEnabled();
        if (SingleInstanceEnabled)
        {
            InstanceMutexName = GenerateMutexName();
        }
    }

    private string? GetPathFromArgs(string[] args)
    {
        // Add proper command line parsing if needed
        return args.FirstOrDefault(File.Exists);
    }

    private bool GetSingleInstanceEnabled()
    {
        var envValue = Environment.GetEnvironmentVariable("TSM_SINGLE_INSTANCE");
        return string.IsNullOrEmpty(envValue) ||
               (envValue.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                envValue.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    private string GenerateMutexName()
    {
        var filePath = Path.GetFullPath(FilePath);
        var outputDir = Path.GetFullPath(OutputDir);
        var combined = $"{filePath}|{outputDir}";

        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(combined));
        var base64 = Convert.ToBase64String(hashBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        return $"TSM_{base64[..8]}";
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

    public static State? Load()
    {
        try
        {
            return File.Exists(StateFile) ? JsonSerializer.Deserialize(File.ReadAllText(StateFile), StateJsonContext.Default.State) : null;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return null;
        }
    }
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
            var writeTime = fileInfo.LastWriteTime;

            await using var stream = new FileStream(
                _config.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);

            var hash = await ComputeHashAsync(stream);
            if (hash == _state?.LastHash) return;

            var outputPath = GenerateOutputPath(writeTime);
            stream.Seek(0, SeekOrigin.Begin);
            await CompressFileAsync(stream, outputPath);

            _state = new State(hash, _config.FilePath, DateTime.UtcNow);
            _state.Save();
            Console.WriteLine($"File processed: {_config.FilePath}#{hash[..8]} -> {outputPath}");
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Processing error: {ex}");
        }
    }

    private async Task<string> ComputeHashAsync(Stream stream)
    {
        using var sha = SHA256.Create();
        var hashBytes = await sha.ComputeHashAsync(stream);
        return Convert.ToHexString(hashBytes);
    }

    private string GenerateOutputPath(DateTime time) =>
        Path.Combine(
            _config.OutputDir,
            $"{Path.GetFileName(_config.FilePath)}_{time:yyyy_MM_dd_HHmmss}.brotli"
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
    private readonly Lazy<FileSystemWatcher?> _watcher;
    private DateTime _lastWrite = DateTime.MinValue;
    private long _lastSize = -1;
    private long _checkIterations;

    public long CheckIterations => _checkIterations;

    public FileMonitor(Config config, FileProcessor processor)
    {
        _config = config;
        _processor = processor;

        _debounceTimer = new Timer(_ => _processor.ProcessAsync().Wait());
        _pollTimer = new Timer(CheckFilePoll);
        _watcher = new Lazy<FileSystemWatcher?>(WatcherValueFactory);
    }

    private FileSystemWatcher? WatcherValueFactory()
    {
        try
        {
            var w = new FileSystemWatcher(Path.GetDirectoryName(_config.FilePath)!, Path.GetFileName(_config.FilePath))
                { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size };
            w.Changed += OnFileChanged;

            return w;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Filesystem watcher initialization failed: {e}");
        }

        return null;
    }

    public void Start()
    {
        _pollTimer.Change(TimeSpan.FromSeconds(_config.PollInterval), TimeSpan.FromSeconds(_config.PollInterval));
        var w = _watcher.Value;
        if (w == null) return;
        w.EnableRaisingEvents = true;
        Console.WriteLine($"Monitoring of {_config.FilePath} every {_config.PollInterval}s started");
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

            IncrementCheckIterations();
        }
        catch
        {
            Console.WriteLine("Polling check failed");
        }
    }

    private void HandleChange()
    {
        IncrementCheckIterations();
        _debounceTimer.Change(
            TimeSpan.FromSeconds(_config.QuietPeriod),
            Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        if (_watcher.IsValueCreated)
        {
            _watcher.Value?.Dispose();
        }

        _pollTimer.Dispose();
        _debounceTimer.Dispose();
    }

    private long IncrementCheckIterations()
    {
        var res = Interlocked.Increment(ref _checkIterations);
        if (res < 0)
        {
            Interlocked.Exchange(ref _checkIterations, 0);
            return 0;
        }
        else
        {
            return res;
        }
    }
}