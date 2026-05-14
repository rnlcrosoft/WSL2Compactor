using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WSL2Compactor.Models;

internal sealed class DistributionRow : INotifyPropertyChanged
{
    private bool _selected = true;
    private long _beforeDiskUsageBytes;
    private long? _afterDiskUsageBytes;
    private long _beforeVirtualSizeBytes;
    private long? _afterVirtualSizeBytes;
    private string _backend = "";
    private string _status = "Pending";

    public DistributionRow(WslDistribution distribution)
    {
        Distribution = distribution;
        _beforeDiskUsageBytes = distribution.DiskUsageBytes;
        _beforeVirtualSizeBytes = distribution.VirtualSizeBytes;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WslDistribution Distribution { get; }

    public bool Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    public string Name => Distribution.Name;

    public string State => Distribution.State;

    public string VhdPath => Distribution.VhdPath;

    public long BeforeDiskUsageBytes
    {
        get => _beforeDiskUsageBytes;
        set
        {
            if (SetField(ref _beforeDiskUsageBytes, value))
            {
                OnPropertyChanged(nameof(BeforeText));
                OnPropertyChanged(nameof(SavedText));
            }
        }
    }

    public long? AfterDiskUsageBytes
    {
        get => _afterDiskUsageBytes;
        set
        {
            if (SetField(ref _afterDiskUsageBytes, value))
            {
                OnPropertyChanged(nameof(AfterText));
                OnPropertyChanged(nameof(SavedText));
            }
        }
    }

    public long BeforeVirtualSizeBytes
    {
        get => _beforeVirtualSizeBytes;
        set
        {
            if (SetField(ref _beforeVirtualSizeBytes, value))
            {
                OnPropertyChanged(nameof(BeforeVirtualSizeText));
            }
        }
    }

    public long? AfterVirtualSizeBytes
    {
        get => _afterVirtualSizeBytes;
        set
        {
            if (SetField(ref _afterVirtualSizeBytes, value))
            {
                OnPropertyChanged(nameof(AfterVirtualSizeText));
            }
        }
    }

    public string Backend
    {
        get => _backend;
        set => SetField(ref _backend, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string BeforeText => SizeFormatter.Format(BeforeDiskUsageBytes);

    public string AfterText => AfterDiskUsageBytes is null ? "-" : SizeFormatter.Format(AfterDiskUsageBytes.Value);

    public string BeforeVirtualSizeText => SizeFormatter.Format(BeforeVirtualSizeBytes);

    public string AfterVirtualSizeText => AfterVirtualSizeBytes is null ? "-" : SizeFormatter.Format(AfterVirtualSizeBytes.Value);

    public string SavedText
    {
        get
        {
            if (AfterDiskUsageBytes is null)
            {
                return "-";
            }

            var saved = BeforeDiskUsageBytes - AfterDiskUsageBytes.Value;
            return saved <= 0 ? "0 B" : SizeFormatter.Format(saved);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
