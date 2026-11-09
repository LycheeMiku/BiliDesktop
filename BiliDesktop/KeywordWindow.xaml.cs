using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Input;

namespace BiliDesktop;

public partial class KeywordWindow : Window
{
    private readonly BackupStore store;
    private readonly Action trigger;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ObservableCollection<string> keywords = [];
    private sealed record Choice(string Label, int Value);
    public KeywordWindow(BackupStore store, Action trigger)
    {
        this.store = store; this.trigger = trigger; InitializeComponent();
        SinceDate.SelectedDate = DateTime.Today;
        DirectoryBox.Text = AppSettings.IndependentDownloadDirectory();
        Terms.ItemsSource = keywords;
        KeywordHint.SetResourceReference(TextBlock.TextProperty, Localizer.Key("逐个输入并添加关键词；可选择满足任意或全部。"));
        TermsLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("已添加关键词"));
        ModeLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("匹配条件"));
        RateLabel.SetResourceReference(TextBlock.TextProperty, Localizer.Key("查询速率"));
        AddTermButton.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("添加关键词"));
        RemoveTermButton.SetResourceReference(ContentControl.ContentProperty, Localizer.Key("移除关键词"));
        Localizer.LanguageChanged += RefreshOptions; RefreshOptions();
        refresh.Tick += (_, _) => RefreshRules(); refresh.Start(); RefreshRules(); Closed += (_, _) => { refresh.Stop(); Localizer.LanguageChanged -= RefreshOptions; };
    }
    private void RefreshOptions()
    {
        var interval = (Interval.SelectedItem as Choice)?.Value ?? 900; var mode = (MatchMode.SelectedItem as Choice)?.Value ?? 0; var rate = (Rate.SelectedItem as Choice)?.Value ?? 250;
        Interval.ItemsSource = new[] { new Choice(Localizer.Translate("30 秒"), 30), new Choice(Localizer.Translate("1 分钟"), 60), new Choice(Localizer.Translate("2 分钟"), 120), new Choice(Localizer.Translate("3 分钟"), 180), new Choice(Localizer.Translate("5 分钟"), 300), new Choice(Localizer.Translate("15 分钟"), 900), new Choice(Localizer.Translate("1 小时"), 3600), new Choice(Localizer.Translate("6 小时"), 21600), new Choice(Localizer.Translate("12 小时"), 43200), new Choice(Localizer.Translate("24 小时"), 86400) };
        MatchMode.ItemsSource = new[] { new Choice(Localizer.Translate("满足任意"), 0), new Choice(Localizer.Translate("必须全部"), 1) };
        Rate.ItemsSource = new[] { new Choice(Localizer.Translate("软件默认"), 250), new Choice(Localizer.Translate("更低"), 1500) };
        Interval.SelectedItem = Interval.Items.Cast<Choice>().FirstOrDefault(x => x.Value == interval); MatchMode.SelectedItem = MatchMode.Items.Cast<Choice>().FirstOrDefault(x => x.Value == mode); Rate.SelectedItem = Rate.Items.Cast<Choice>().FirstOrDefault(x => x.Value == rate);
        RuleModeColumn.Header = Localizer.Translate("匹配条件"); RuleIntervalColumn.Header = Localizer.Translate("间隔"); RuleRateColumn.Header = Localizer.Translate("查询速率");
    }
    private void AddPendingTerm()
    {
        var term = Keyword.Text.Trim();
        if (term.Length is < 1 or > 200 || term.Any(char.IsControl) || keywords.Count >= 20) throw new ArgumentException("请输入 1 至 200 个字符的关键词，最多 20 个。");
        if (keywords.Contains(term, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("关键词已添加。");
        keywords.Add(term); Keyword.Clear(); Keyword.Focus();
    }
    private void AddTerm_Click(object sender, RoutedEventArgs e) { try { AddPendingTerm(); Status.Text = ""; } catch (Exception ex) { Status.Text = ex.Message; } }
    private void Keyword_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; AddTerm_Click(sender, e); } }
    private void RemoveTerm_Click(object sender, RoutedEventArgs e) { if (Terms.SelectedItem is string term) keywords.Remove(term); else Status.Text = "请选择要移除的关键词。"; }
    private void RefreshRules()
    {
        try { var id = (Rules.SelectedItem as KeywordRule)?.Id; var list = store.ListKeywords(); Rules.ItemsSource = list; if (id.HasValue) Rules.SelectedItem = list.FirstOrDefault(r => r.Id == id); }
        catch (Exception ex) { Status.Text = "读取规则失败：" + ex.Message; }
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择下载目录", Multiselect = false };
        if (dialog.ShowDialog() == true) DirectoryBox.Text = dialog.FolderName;
    }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (SinceDate.SelectedDate is not DateTime date || !TimeSpan.TryParseExact(SinceTime.Text, @"hh\:mm", CultureInfo.InvariantCulture, out var time) || time >= TimeSpan.FromDays(1)) throw new ArgumentException("请选择日期并输入 00:00 至 23:59 的时间。");
            var local = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(local) || TimeZoneInfo.Local.IsAmbiguousTime(local)) throw new ArgumentException("该本机时间因夏令时转换不存在或有歧义，请选择其他时间。");
            var since = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeSeconds();
            if (!string.IsNullOrWhiteSpace(Keyword.Text)) AddPendingTerm();
            if (Interval.SelectedItem is not Choice interval || MatchMode.SelectedItem is not Choice match || Rate.SelectedItem is not Choice rate) throw new InvalidOperationException("请选择匹配条件、检查间隔和查询速率。");
            store.AddKeyword(keywords.ToArray(), since, DirectoryBox.Text, interval.Value, (KeywordMatchMode)match.Value, rate.Value); keywords.Clear(); RefreshRules(); Status.Text = "规则已添加"; trigger();
        }
        catch (Exception ex) { Status.Text = "添加失败：" + ex.Message; }
    }
    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (Rules.SelectedItem is not KeywordRule rule) { Status.Text = "请选择规则。"; return; }
        try { if (!rule.Enabled) { Status.Text = "请先启用规则。"; return; } if (rule.Status.StartsWith("正在", StringComparison.Ordinal)) { Status.Text = "规则正在检查。"; return; } store.RunKeywordNow(rule.Id); RefreshRules(); trigger(); Status.Text = "已安排检查"; }
        catch (Exception ex) { Status.Text = "操作失败：" + ex.Message; }
    }
    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (Rules.SelectedItem is not KeywordRule rule) { Status.Text = "请选择规则。"; return; }
        try { store.SetKeywordEnabled(rule.Id, !rule.Enabled); RefreshRules(); if (!rule.Enabled) trigger(); Status.Text = rule.Enabled ? "已暂停；正在下载的文件会完成。" : "规则已启用"; }
        catch (Exception ex) { Status.Text = "操作失败：" + ex.Message; }
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Rules.SelectedItem is not KeywordRule rule) { Status.Text = "请选择规则。"; return; }
        try { store.RemoveKeyword(rule.Id); RefreshRules(); Status.Text = "规则已删除；正在下载的文件会完成。"; }
        catch (Exception ex) { Status.Text = "操作失败：" + ex.Message; }
    }
}
