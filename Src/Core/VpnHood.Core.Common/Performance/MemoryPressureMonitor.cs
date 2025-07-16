using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace VpnHood.Core.Common.Performance;

public class MemoryPressureMonitor : IDisposable
{
    private readonly ILogger _logger;
    private readonly Timer _timer;
    private readonly long _thresholdBytes;
    private readonly double _thresholdPercentage;
    private readonly TimeSpan _checkInterval;
    private readonly Process _process;
    private long _lastGcMemory;

    public event EventHandler<MemoryPressureEventArgs>? MemoryPressureDetected;

    public MemoryPressureMonitor(ILogger logger, long thresholdBytes = 1_073_741_824, // 1GB
        double thresholdPercentage = 0.8, TimeSpan? checkInterval = null)
    {
        _logger = logger;
        _thresholdBytes = thresholdBytes;
        _thresholdPercentage = thresholdPercentage;
        _checkInterval = checkInterval ?? TimeSpan.FromSeconds(30);
        _process = Process.GetCurrentProcess();
        _timer = new Timer(CheckMemoryPressure, null, _checkInterval, _checkInterval);
    }

    private void CheckMemoryPressure(object? state)
    {
        try
        {
            _process.Refresh();
            var workingSet = _process.WorkingSet64;
            var gcMemory = GC.GetTotalMemory(false);
            var availableMemory = GetAvailableMemory();
            var totalMemory = GetTotalMemory();

            var memoryInfo = new MemoryInfo
            {
                WorkingSet = workingSet,
                GCMemory = gcMemory,
                AvailableMemory = availableMemory,
                TotalMemory = totalMemory,
                GCMemoryDelta = gcMemory - _lastGcMemory
            };

            _lastGcMemory = gcMemory;

            var isUnderPressure = workingSet > _thresholdBytes ||
                                  (totalMemory > 0 && availableMemory < totalMemory * (1 - _thresholdPercentage));

            if (isUnderPressure)
            {
                _logger.LogWarning("Memory pressure detected: {MemoryInfo}", memoryInfo);
                MemoryPressureDetected?.Invoke(this, new MemoryPressureEventArgs(memoryInfo));
                
                // Trigger garbage collection
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking memory pressure");
        }
    }

    private static long GetAvailableMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var memoryStatus = new MEMORYSTATUSEX();
            memoryStatus.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref memoryStatus))
                return (long)memoryStatus.ullAvailPhys;
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                var lines = File.ReadAllLines("/proc/meminfo");
                var availableLine = lines.FirstOrDefault(l => l.StartsWith("MemAvailable:"));
                if (availableLine != null)
                {
                    var parts = availableLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                        return kb * 1024;
                }
            }
            catch
            {
                // Fallback
            }
        }

        return GC.GetTotalMemory(false);
    }

    private static long GetTotalMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var memoryStatus = new MEMORYSTATUSEX();
            memoryStatus.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            if (GlobalMemoryStatusEx(ref memoryStatus))
                return (long)memoryStatus.ullTotalPhys;
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                var lines = File.ReadAllLines("/proc/meminfo");
                var totalLine = lines.FirstOrDefault(l => l.StartsWith("MemTotal:"));
                if (totalLine != null)
                {
                    var parts = totalLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                        return kb * 1024;
                }
            }
            catch
            {
                // Fallback
            }
        }

        return GC.GetTotalMemory(false);
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}

public class MemoryInfo
{
    public long WorkingSet { get; init; }
    public long GCMemory { get; init; }
    public long AvailableMemory { get; init; }
    public long TotalMemory { get; init; }
    public long GCMemoryDelta { get; init; }

    public override string ToString() =>
        $"WorkingSet: {FormatBytes(WorkingSet)}, " +
        $"GCMemory: {FormatBytes(GCMemory)}, " +
        $"Available: {FormatBytes(AvailableMemory)}, " +
        $"Total: {FormatBytes(TotalMemory)}, " +
        $"Delta: {FormatBytes(GCMemoryDelta)}";

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F2} MB",
            >= 1_024 => $"{bytes / 1_024.0:F2} KB",
            _ => $"{bytes} B"
        };
}

public class MemoryPressureEventArgs : EventArgs
{
    public MemoryInfo MemoryInfo { get; }

    public MemoryPressureEventArgs(MemoryInfo memoryInfo)
    {
        MemoryInfo = memoryInfo;
    }
}
