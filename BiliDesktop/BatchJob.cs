using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BiliDesktop;

public sealed class BatchJob : INotifyPropertyChanged
{
    public required int Number { get; init; }
    public required string Bvid { get; init; }
    public required string Label { get; init; }
    public VideoPage? Page { get; init; }
    public string? CollectionTitle { get; init; }
    public int? CollectionNumber { get; init; }
    public string? CollectionItemTitle { get; init; }
    public bool Selected { get; set; } = true;
    private string status = "等待";
    public string Status { get => status; set { status = value; OnPropertyChanged(); } }
    public bool Finished { get; set; }
    public bool HasError { get; set; }
    public string? OutputPath { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
