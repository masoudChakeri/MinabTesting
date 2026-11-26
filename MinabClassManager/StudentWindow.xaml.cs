using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Windows;

namespace MinabClassManager;

/// <summary>
/// Hosts one simulated student. By default the window runs in the background:
/// it is parked off-screen and hidden from the taskbar, but stays "visible" to
/// Chromium so media and timers are not throttled. Call ToggleView to look at it.
/// </summary>
public partial class StudentWindow : Window
{
    private const double OffScreen = -20000;

    private readonly string _signedUrl;
    private readonly bool _useCamera;
    private readonly bool _useMic;
    private readonly string _tempFolder;
    private readonly string _studentName;
    private readonly string? _logFile;
    private readonly object _logLock = new();

    public bool IsShownOnScreen { get; private set; }

    public StudentWindow(string signedUrl, bool useCamera, bool useMic, string studentName, string classTitle)
    {
        InitializeComponent();
        _signedUrl = signedUrl;
        _useCamera = useCamera;
        _useMic = useMic;
        _studentName = studentName;
        _logFile = CreateLogFile(studentName);
        _tempFolder = Path.Combine(Path.GetTempPath(), $"MinabStudent_{Guid.NewGuid():N}");
        Title = $"{studentName} - {classTitle}";

        // Run in the background
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Left = OffScreen;
        Top = OffScreen;

        Loaded += async (_, _) => await InitWebViewAsync();
    }

    public void ToggleView()
    {
        if (IsShownOnScreen)
        {
            Left = OffScreen;
            Top = OffScreen;
            ShowInTaskbar = false;
            IsShownOnScreen = false;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
            Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
            ShowInTaskbar = true;
            IsShownOnScreen = true;
            Activate();
        }
    }

    // Hide back to the background instead of closing when the user closes a viewed window
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (IsShownOnScreen && !_forceClose)
        {
            e.Cancel = true;
            ToggleView();
            return;
        }
        base.OnClosing(e);
    }

    private bool _forceClose;

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var args = new List<string>
            {
                // Keep the background session running at full speed
                "--disable-background-timer-throttling",
                "--disable-backgrounding-occluded-windows",
                "--disable-renderer-backgrounding",
                "--autoplay-policy=no-user-gesture-required",
            };
            if (_useCamera || _useMic)
                args.Add("--use-fake-device-for-media-stream");

            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = string.Join(" ", args)
            };

            var env = await CoreWebView2Environment.CreateAsync(null, _tempFolder, options);
            await webView.EnsureCoreWebView2Async(env);

            var core = webView.CoreWebView2;
            Log($"Started. camera={_useCamera} mic={_useMic} url={_signedUrl}");

            // WebView2 ignores --use-fake-ui-for-media-stream, so answer permission prompts here.
            // The media shim strips disabled kinds before calling getUserMedia, so a camera-only
            // student never has a combined camera+mic request fail because of the mic.
            core.PermissionRequested += (_, e) =>
            {
                e.State = e.PermissionKind switch
                {
                    CoreWebView2PermissionKind.Camera => _useCamera ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny,
                    CoreWebView2PermissionKind.Microphone => _useMic ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny,
                    CoreWebView2PermissionKind.Autoplay => CoreWebView2PermissionState.Allow,
                    _ => CoreWebView2PermissionState.Default
                };
                e.Handled = true;
                Log($"Permission {e.PermissionKind} from {e.Uri} -> {e.State}");
            };

            core.WebMessageReceived += (_, e) =>
            {
                try { Log("page: " + e.TryGetWebMessageAsString()); } catch { }
            };

            // Capture console errors/warnings from the class page
            try
            {
                core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived += (_, e) =>
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(e.ParameterObjectAsJson);
                        var root = doc.RootElement;
                        var type = root.GetProperty("type").GetString();
                        var parts = new List<string>();
                        foreach (var arg in root.GetProperty("args").EnumerateArray())
                        {
                            if (arg.TryGetProperty("value", out var v)) parts.Add(v.ToString());
                            else if (arg.TryGetProperty("preview", out var p) && p.TryGetProperty("properties", out var props))
                                parts.Add("{" + string.Join(", ", props.EnumerateArray().Select(x =>
                                    $"{x.GetProperty("name").GetString()}={(x.TryGetProperty("value", out var xv) ? xv.GetString() : "?")}")) + "}");
                            else if (arg.TryGetProperty("description", out var d)) parts.Add(d.GetString() ?? "");
                        }
                        var text = string.Join(" ", parts);
                        Log($"console.{type}: " + (text.Length > 500 ? text[..500] : text));
                    }
                    catch { }
                };
                core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived += (_, e) =>
                    Log("exception: " + (e.ParameterObjectAsJson.Length > 600 ? e.ParameterObjectAsJson[..600] : e.ParameterObjectAsJson));
                await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            }
            catch (Exception ex) { Log("DevTools logging unavailable: " + ex.Message); }

            // Pre-grant camera/mic for the class site before it loads, so the first join
            // behaves like a rejoin (where the permission already exists and the webcam works)
            try
            {
                var origin = new Uri(_signedUrl).GetLeftPart(UriPartial.Authority);
                await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Camera, origin,
                    _useCamera ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny);
                await core.Profile.SetPermissionStateAsync(CoreWebView2PermissionKind.Microphone, origin,
                    _useMic ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny);
                Log($"Pre-set permissions for {origin}: camera={_useCamera} mic={_useMic}");
            }
            catch (Exception ex) { Log("Could not pre-set permissions: " + ex.Message); }

            await core.AddScriptToExecuteOnDocumentCreatedAsync(MediaShim.Build(_useCamera, _useMic, _studentName));

            // Never pop up extra browser windows; open them in this session instead
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                core.Navigate(e.Uri);
            };

            loadingOverlay.Visibility = Visibility.Collapsed;
            webView.Source = new Uri(_signedUrl);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not start WebView2.\n\n" +
                "Make sure the Microsoft Edge WebView2 Runtime is installed on this PC.\n\n" +
                $"Detail: {ex.Message}",
                "WebView2 Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ForceClose();
        }
    }

    // Logs go to a "logs" folder next to the exe (falls back to %TEMP% if that is read-only)
    private static string? CreateLogFile(string studentName)
    {
        var safe = string.Concat(studentName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var file = $"{DateTime.Now:yyyyMMdd-HHmmss}_{safe}.log";
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "logs"), Path.Combine(Path.GetTempPath(), "MinabClassManager", "logs") })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, file);
                File.AppendAllText(path, "");
                return path;
            }
            catch { }
        }
        return null;
    }

    private void Log(string message)
    {
        if (_logFile is null) return;
        try
        {
            lock (_logLock)
                File.AppendAllText(_logFile, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { }
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
