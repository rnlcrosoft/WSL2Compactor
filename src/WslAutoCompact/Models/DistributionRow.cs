using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WslAutoCompact.Models;

internal sealed class DistributionRow : INotifyPropertyChanged
{
    private bool _selected = true;
    private long _beforeBytes;
    private long? _afterBytes;
    private string _backend = "";
    private string _status = "待機中";

    public DistributionRow(WslDistribution distribution)
    {
        Distribution = distribution;
        _beforeBytes = distribution.SizeBytes;
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

    public long BeforeBytes
    {
        get => _beforeBytes;
        set
        {
            if (SetField(ref _beforeBytes, value))
            {
                OnPropertyChanged(nameof(BeforeText));
                OnPropertyChanged(nameof(SavedText));
            }
        }
    }

    public long? AfterBytes
    {
        get => _afterBytes;
        set
        {
            if (SetField(ref _afterBytes, value))
            {
                OnPropertyChanged(nameof(AfterText));
                OnPropertyChanged(nameof(SavedText));
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

    public string BeforeText => SizeFormatter.Format(BeforeBytes);

    public string AfterText => AfterBytes is null ? "-" : SizeFormatter.Format(AfterBytes.Value);

    public string SavedText
    {
        get
        {
            if (AfterBytes is null)
            {
                return "-";
            }

            var saved = BeforeBytes - AfterBytes.Value;
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
