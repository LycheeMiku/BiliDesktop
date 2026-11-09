using System.Windows;
using System.Windows.Controls;

namespace BiliDesktop;

internal enum BackupRemovalChoice { KeepFiles, DeleteFiles }

internal sealed class BackupRemovalDialog : Window
{
    internal BackupRemovalChoice? Choice { get; private set; }

    internal BackupRemovalDialog(Window owner, int count)
    {
        Owner = owner; Title = Localizer.Translate("删除备份规则"); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 520 };
        panel.Children.Add(new TextBlock { Text = string.Format(Localizer.Translate("将停止所选的 {0} 条规则，不再添加任务，并取消其等待中和正在进行的下载。请选择对已下载文件的处理方式。"), count), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(new TextBlock { Text = Localizer.Translate("删除文件会永久删除这些规则记录的备份文件，包括曾经匹配的手动下载文件；其他规则可能也在使用这些文件。"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Button MakeButton(string text, BackupRemovalChoice choice)
        {
            var button = new Button { Content = Localizer.Translate(text), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 0, 0, 0) };
            button.Click += (_, _) => { Choice = choice; DialogResult = true; };
            return button;
        }
        buttons.Children.Add(MakeButton("取消下载并保留文件", BackupRemovalChoice.KeepFiles));
        buttons.Children.Add(MakeButton("取消下载并删除文件", BackupRemovalChoice.DeleteFiles));
        buttons.Children.Add(new Button { Content = Localizer.Translate("取消"), IsCancel = true, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(6, 0, 0, 0) });
        panel.Children.Add(buttons); Content = panel;
    }
}
