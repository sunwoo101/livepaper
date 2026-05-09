using CommunityToolkit.Mvvm.ComponentModel;

namespace livepaper.ViewModels;

public partial class LweMonitorViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private int _index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name;

    public int Fps { get; set; } = 30;
    public bool IsPrimary { get; set; } = false;

    public string DisplayName => $"Monitor {Index + 1}: {Name}";

    public LweMonitorViewModel(string name, int index = 0)
    {
        _name = name;
        _index = index;
    }
}
