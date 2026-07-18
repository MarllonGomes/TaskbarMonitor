using LibreHardwareMonitor.Hardware;

namespace TaskbarMonitor;

public sealed record Snapshot(
    float? CpuLoad, float? CpuTemp,
    float? GpuLoad, float? GpuTemp, string? GpuName,
    float? RamLoad, float? RamUsedGb, float? RamTotalGb,
    float? DiskLoad, float? DiskTemp,
    float? NetUpBps, float? NetDownBps)
{
    public static readonly Snapshot Empty =
        new(null, null, null, null, null, null, null, null, null, null, null, null);
}

/// <summary>
/// Reads sensors via LibreHardwareMonitor on a background thread (once per
/// second) and publishes an immutable snapshot for the UI.
/// </summary>
public sealed class SensorService : IDisposable
{
    private readonly Computer _computer;
    private readonly System.Threading.Timer _timer;
    private readonly object _pollLock = new();
    private volatile Snapshot _current = Snapshot.Empty;
    private volatile bool _ready;
    private bool _disposed;

    public Snapshot Current => _current;
    public bool Ready => _ready;

    public SensorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsMotherboardEnabled = true,
        };
        _timer = new System.Threading.Timer(_ => Poll(), null, Timeout.Infinite, Timeout.Infinite);

        // Computer.Open() can take a few seconds; don't block the UI.
        Task.Run(() =>
        {
            try { _computer.Open(); } catch { }
            _ready = true;
            if (!_disposed) _timer.Change(0, 1000);
        });
    }

    private void Poll()
    {
        if (!Monitor.TryEnter(_pollLock)) return;
        try
        {
            _computer.Accept(new UpdateVisitor());

            float? cpuLoad = null, cpuTemp = null;
            int cpuTempRank = int.MaxValue;
            float? ramLoad = null, ramUsed = null, ramAvail = null;
            float? diskLoad = null, diskTemp = null;
            float netUp = 0, netDown = 0;
            bool anyNet = false;

            IHardware? gpu = null;
            int gpuRank = int.MaxValue;

            foreach (var hw in _computer.Hardware)
            {
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        foreach (var s in hw.Sensors)
                        {
                            if (s.Value is not float v || float.IsNaN(v)) continue;
                            if (s.SensorType == SensorType.Load && s.Name == "CPU Total")
                                cpuLoad = v;
                            else if (s.SensorType == SensorType.Temperature && v > 0 && v < 120)
                            {
                                int rank = CpuTempRank(s.Name);
                                if (rank < cpuTempRank || (rank == cpuTempRank && v > (cpuTemp ?? 0)))
                                {
                                    cpuTemp = v;
                                    cpuTempRank = rank;
                                }
                            }
                        }
                        break;

                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        // Prefer a discrete GPU (NVIDIA/AMD) over an integrated one (Intel)
                        int rank2 = hw.HardwareType == HardwareType.GpuIntel ? 1 : 0;
                        if (rank2 < gpuRank) { gpu = hw; gpuRank = rank2; }
                        break;

                    case HardwareType.Memory:
                        foreach (var s in hw.Sensors)
                        {
                            if (s.Value is not float v || float.IsNaN(v)) continue;
                            if (s.SensorType == SensorType.Load && s.Name == "Memory") ramLoad = v;
                            else if (s.SensorType == SensorType.Data && s.Name == "Memory Used") ramUsed = v;
                            else if (s.SensorType == SensorType.Data && s.Name == "Memory Available") ramAvail = v;
                        }
                        break;

                    case HardwareType.Storage:
                        foreach (var s in hw.Sensors)
                        {
                            if (s.Value is not float v || float.IsNaN(v)) continue;
                            if (s.SensorType == SensorType.Load && s.Name == "Total Activity")
                                diskLoad = Math.Max(diskLoad ?? 0, v);
                            else if (s.SensorType == SensorType.Temperature && v > 0 && v < 100)
                                diskTemp = Math.Max(diskTemp ?? 0, v);
                        }
                        break;

                    case HardwareType.Network:
                        foreach (var s in hw.Sensors)
                        {
                            if (s.Value is not float v || float.IsNaN(v)) continue;
                            if (s.SensorType != SensorType.Throughput) continue;
                            if (s.Name == "Upload Speed") { netUp += v; anyNet = true; }
                            else if (s.Name == "Download Speed") { netDown += v; anyNet = true; }
                        }
                        break;
                }
            }

            float? gpuLoad = null, gpuTemp = null;
            string? gpuName = null;
            if (gpu != null)
            {
                gpuName = gpu.Name;
                float d3dMax = -1;
                foreach (var s in gpu.Sensors)
                {
                    if (s.Value is not float v || float.IsNaN(v)) continue;
                    if (s.SensorType == SensorType.Load)
                    {
                        if (s.Name == "GPU Core") gpuLoad = v;
                        else if (s.Name.StartsWith("D3D", StringComparison.Ordinal))
                            d3dMax = Math.Max(d3dMax, v);
                    }
                    else if (s.SensorType == SensorType.Temperature && v > 0 && v < 120)
                    {
                        if (s.Name == "GPU Core") gpuTemp = v;
                        else gpuTemp ??= v;
                    }
                }
                // iGPUs often don't expose "GPU Core": fall back to D3D counters
                if (gpuLoad is null && d3dMax >= 0) gpuLoad = d3dMax;
            }

            float? ramTotal = (ramUsed.HasValue && ramAvail.HasValue) ? ramUsed + ramAvail : null;

            _current = new Snapshot(
                cpuLoad, cpuTemp,
                gpuLoad, gpuTemp, gpuName,
                ramLoad, ramUsed, ramTotal,
                diskLoad, diskTemp,
                anyNet ? netUp : null, anyNet ? netDown : null);
        }
        catch { }
        finally { Monitor.Exit(_pollLock); }
    }

    private static int CpuTempRank(string name) => name switch
    {
        "Core (Tctl/Tdie)" => 0,   // AMD Ryzen
        "CPU Package" => 0,        // Intel
        "Core Average" => 1,
        "Core Max" => 2,
        _ => 3,                    // anything else: use the highest value
    };

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
        lock (_pollLock)
        {
            try { _computer.Close(); } catch { }
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
