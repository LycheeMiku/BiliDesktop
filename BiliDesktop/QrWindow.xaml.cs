using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;

namespace BiliDesktop;

public partial class QrWindow : Window
{
    private readonly BiliApi api;
    private readonly CancellationTokenSource cancellation = new();
    public string? Cookie { get; private set; }
    public DateTimeOffset? Expires { get; private set; }
    public event Action<string, DateTimeOffset?>? LoginCompleted;

    public QrWindow(BiliApi api)
    {
        this.api = api; InitializeComponent();
        Loaded += Start;
        Closed += (_, _) => cancellation.Cancel();
    }

    private async void Start(object sender, RoutedEventArgs e)
    {
        try
        {
            var qr = await api.CreateQrLoginAsync();
            if (cancellation.IsCancellationRequested) return;
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(qr.Url, QRCodeGenerator.ECCLevel.M);
            using var renderer = new PngByteQRCode(data);
            using var bytes = new MemoryStream(renderer.GetGraphic(8));
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = bytes; bitmap.EndInit();
            CodeImage.Source = bitmap;
            while (!cancellation.IsCancellationRequested)
            {
                var result = await api.PollQrLoginAsync(qr.Key, cancellation.Token);
                if (result.Code == 0) { Cookie = result.Cookie; Expires = result.Expires; if (Cookie is not null) LoginCompleted?.Invoke(Cookie, Expires); Close(); return; }
                if (result.Code == 86038) { State.Text = "二维码已失效，请关闭后重试。"; return; }
                State.Text = result.Code switch { 86101 => "等待扫码", 86090 => "已扫码，请在手机上确认", _ => "登录状态：" + result.Code };
                await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) State.Text = "登录失败：" + ex.Message; }
    }
}
