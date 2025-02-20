# TSM AppData Monitor

Robust file monitor for TradeSkillMaster's AppData.lua with compression and single-instance safety.

## Features
- 🔍 Dual monitoring (filesystem watcher + polling fallback)
- ⏳ Debounced processing (30s quiet period default)
- 🔐 Single-instance enforcement per config
- 📦 Brotli compression with timestamped outputs
- 💾 State persistence across restarts

## Usage

### Requirements
- .NET 8.0 Runtime

### Basic Command
```bash
TSMMonitor.exe "C:\WoW\_retail_\Interface\AddOns\TradeSkillMaster_AppHelper\AppData.lua"
```

### Configuration

| Option               | Env Variable           | Default | Description                          |
|----------------------|------------------------|---------|--------------------------------------|
| Monitor Path         | TSM_MONITOR_PATH       | -       | Path to AppData.lua                  |
| Output Directory     | TSM_OUTPUT_DIR         | ./      | Where to save compressed files       |
| Quiet Period         | TSM_QUIET_PERIOD       | 30      | Seconds to wait after changes (int)  |
| Single Instance Mode | TSM_SINGLE_INSTANCE    | true    | Prevent duplicate monitors (true/false) |

### Examples

1. Classic WoW with custom output:
```bash
set TSM_OUTPUT_DIR=D:\tsm_backups
TSMMonitor.exe "C:\WoW\_classic_\Interface\AddOns\TradeSkillMaster_AppHelper\AppData.lua"
```

2. Disable single-instance mode:
```bash
set TSM_SINGLE_INSTANCE=false
TSMMonitor.exe "C:\Path\To\AppData.lua"
```

## Troubleshooting

1. **File Not Found**  
   Verify path matches your WoW version (_retail_/_classic_)

2. **Permission Issues**  
   Run as Administrator if seeing file access errors

3. **Multiple Instances**  
   Check single-instance mode or reboot to clear mutex locks

Output files will be created in format:  
`AppData.lua_YYYY_MM_DD_HHmmss.brotli`