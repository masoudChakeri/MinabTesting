using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Windows;

namespace MinabClassManager;

public partial class StudentWindow : Window
{
    private readonly string _signedUrl;
    private readonly bool _useCamera;
    private readonly bool _useMic;
    private readonly string _tempFolder;

    public StudentWindow(string signedUrl, bool useCamera, bool useMic, string studentName, string classTitle)
    {
        InitializeComponent();
        _signedUrl = signedUrl;
        _useCamera = useCamera;
        _useMic = useMic;
        _tempFolder = Path.Combine(Path.GetTempPath(), $"MinabStudent_{Guid.NewGuid():N}");
        Title = $"{studentName} — {classTitle}";

        Loaded += async (_, _) => await InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var args = new List<string>();
            if (_useCamera || _useMic)
            {
                args.Add("--use-fake-ui-for-media-stream");
                args.Add("--use-fake-device-for-media-stream");
            }

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = string.Join(" ", args)
            };

            var env = await CoreWebView2Environment.CreateAsync(null, _tempFolder, options);
            await webView.EnsureCoreWebView2Async(env);

            loadingOverlay.Visibility = Visibility.Collapsed;
            webView.Source = new Uri(_signedUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not start WebView2.\n\n" +
                "Make sure Microsoft Edge (Chromium) is installed on this PC.\n" +
                "Download at: https://www.microsoft.com/edge\n\n" +
                $"Detail: {ex.Message}",
                "WebView2 Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try { webView.Dispose(); } catch { }
        // WebView2 locks its user-data folder briefly after disposal; retry in background
        var folder = _tempFolder;
        Task.Run(async () =>
        {
            await Task.Delay(1500);
            try { Directory.Delete(folder, recursive: true); } catch { }
        });
    }
}
