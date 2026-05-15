using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WSL2Compactor.Models;

internal sealed class DistributionRow : INotifyPropertyChanged
{
    private bool _selected = true;
    private long? _beforeLinuxUsedBytes;
    private long? _beforeExt4OverheadBytes;
    private long _beforeHostAllocatedBytes;
    private long? _afterHostAllocatedBytes;
    private long _beforeVhdxFileSizeBytes;
    private long? _afterVhdxFileSizeBytes;
    private string? _linuxUsageSource;
    private string _backend = "";
    private string _status = "Pending";

    public DistributionRow(WslDistribution distribution)
    {
        Distribution = distribution;
        _beforeLinuxUsedBytes = distribution.LinuxUsedBytes;
        _beforeExt4OverheadBytes = distribution.Ext4OverheadBytes;
        _beforeHostAllocatedBytes = distribution.HostAllocatedBytes;
        _beforeVhdxFileSizeBytes = distribution.VhdxFileSizeBytes;
        _linuxUsageSource = distribution.LinuxUsageSource;
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

    public string? LinuxUsageSource
    {
        get => _linuxUsageSource;
        set => SetField(ref _linuxUsageSource, value);
    }

    public long? BeforeLinuxUsedBytes
    {
        get => _beforeLinuxUsedBytes;
        set
        {
            if (SetField(ref _beforeLinuxUsedBytes, value))
            {
                OnPropertyChanged(nameof(BeforeLinuxUsedText));
                OnPropertyChanged(nameof(BeforeLinuxFootprintText));
            }
        }
    }

    public long? BeforeExt4OverheadBytes
    {
        get => _beforeExt4OverheadBytes;
        set
        {
            if (SetField(ref _beforeExt4OverheadBytes, value))
            {
                OnPropertyChanged(nameof(BeforeExt4OverheadText));
                OnPropertyChanged(nameof(BeforeLinuxFootprintText));
            }
        }
    }

    public long BeforeHostAllocatedBytes
    {
        get => _beforeHostAllocatedBytes;
        set
        {
            if (SetField(ref _beforeHostAllocatedBytes, value))
            {
                OnPropertyChanged(nameof(BeforeHostAllocatedText));
                OnPropertyChanged(nameof(SavedText));
            }
        }
    }

    public long? AfterHostAllocatedBytes
    {
        get => _afterHostAllocatedBytes;
        set
        {
            if (SetField(ref _afterHostAllocatedBytes, value))
            {
                OnPropertyChanged(nameof(AfterHostAllocatedText));
                OnPropertyChanged(nameof(SavedText));
            }
        }
    }

    public long BeforeVhdxFileSizeBytes
    {
        get => _beforeVhdxFileSizeBytes;
        set
        {
            if (SetField(ref _beforeVhdxFileSizeBytes, value))
            {
                OnPropertyChanged(nameof(BeforeVhdxFileSizeText));
            }
        }
    }

    public long? AfterVhdxFileSizeBytes
    {
        get => _afterVhdxFileSizeBytes;
        set
        {
            if (SetField(ref _afterVhdxFileSizeBytes, value))
            {
                OnPropertyChanged(nameof(AfterVhdxFileSizeText));
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

    public string BeforeLinuxUsedText => BeforeLinuxUsedBytes is null ? "-" : SizeFormatter.Format(BeforeLinuxUsedBytes.Value);

    public string BeforeExt4OverheadText => BeforeExt4OverheadBytes is null ? "-" : SizeFormatter.Format(BeforeExt4OverheadBytes.Value);

    public long? BeforeLinuxFootprintBytes
        => BeforeLinuxUsedBytes is null || BeforeExt4OverheadBytes is null
            ? null
            : BeforeLinuxUsedBytes.Value + BeforeExt4OverheadBytes.Value;

    public string BeforeLinuxFootprintText => BeforeLinuxFootprintBytes is null ? "-" : SizeFormatter.Format(BeforeLinuxFootprintBytes.Value);

    public string BeforeHostAllocatedText => SizeFormatter.Format(BeforeHostAllocatedBytes);

    public string AfterHostAllocatedText => AfterHostAllocatedBytes is null ? "-" : SizeFormatter.Format(AfterHostAllocatedBytes.Value);

    public string BeforeVhdxFileSizeText => SizeFormatter.Format(BeforeVhdxFileSizeBytes);

    public string AfterVhdxFileSizeText => AfterVhdxFileSizeBytes is null ? "-" : SizeFormatter.Format(AfterVhdxFileSizeBytes.Value);

    public string SavedText
    {
        get
        {
            if (AfterHostAllocatedBytes is null)
            {
                return "-";
            }

            var saved = BeforeHostAllocatedBytes - AfterHostAllocatedBytes.Value;
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
