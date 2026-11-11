using System.Windows;
using System.IO;
using Microsoft.Win32;

namespace BiliDesktop;

public partial class LiveEventsWindow : Window
{
    public LiveEventsWindow(IReadOnlyList<LiveEvent> events)
    {
        InitializeComponent(); Events.ItemsSource = events.Reverse().ToArray();
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        var selected = Events.SelectedItems.Cast<LiveEvent>().ToArray();
        if (selected.Length < 2 || selected.Any(x => x.Kind != "file" || string.IsNullOrWhiteSpace(x.Path)) || selected.Select(x => x.RoomId).Distinct().Count() != 1)
        {
            Status.Text = "请选择同一直播间至少两条 file 记录。"; return;
        }
        var dialog = new SaveFileDialog { Filter = "Matroska 视频|*.mkv", FileName = "直播合并.mkv", OverwritePrompt = false };
        if (dialog.ShowDialog() != true) return;
        if (File.Exists(dialog.FileName)) { Status.Text = Localizer.Translate("目标文件已存在，请选择其他文件名。"); return; }
        Merge.IsEnabled = false; Status.Text = "正在检查并合并片段…";
        try
        {
            await LiveMerge.MergeAsync(selected.OrderBy(x => x.Time).Select(x => x.Path!).ToArray(), dialog.FileName);
            Status.Text = "合并完成，原始片段已保留。";
        }
        catch (Exception ex) { Status.Text = "合并失败，原始片段已保留：" + ex.Message; }
        finally { Merge.IsEnabled = true; }
    }
}
