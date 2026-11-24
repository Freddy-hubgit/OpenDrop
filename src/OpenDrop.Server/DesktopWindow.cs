using Microsoft.Web.WebView2.WinForms;
using System.Windows.Forms;

internal sealed class DesktopWindow : Form
{
    private readonly CancellationTokenSource closed = new();

    public DesktopWindow(string url, string? startupError, string? transferUrl)
    {
        Text = "OpenDrop Windows";
        BackColor = Color.FromArgb(16, 25, 35);
        ForeColor = Color.FromArgb(237, 243, 249);
        WindowState = FormWindowState.Maximized;
        KeyPreview = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new(760, 760);
        MinimumSize = new(440, 480);
        AutoScaleMode = AutoScaleMode.Dpi;
        var status = new Label { AutoSize = true, Text = startupError ?? "Empfangsseite wird geladen …", Padding = new(10) };
        if (startupError is not null)
        {
            status.MaximumSize = new(680, 0);
            Controls.Add(status);
            return;
        }

        var owner = new Uri(url);
        var transfer = new Uri(transferUrl ?? url);
        string? connectedOrigin = null;
        string? certificatePin = null;
        bool IsAppUrl(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.GetLeftPart(UriPartial.Authority) == owner.GetLeftPart(UriPartial.Authority) ||
             uri.GetLeftPart(UriPartial.Authority) == transfer.GetLeftPart(UriPartial.Authority) ||
             uri.GetLeftPart(UriPartial.Authority) == connectedOrigin);

        var webView = new WebView2
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            DefaultBackgroundColor = BackColor,
            CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OpenDropWindows", "WebView2")
            }
        };
        var home = new Button { Text = "Empfangsseite", AutoSize = true, Enabled = false };
        home.Click += (_, _) =>
        {
            connectedOrigin = null;
            certificatePin = null;
            webView.Source = owner;
        };
        var full = new Button { Text = "Vollbild", AutoSize = true };
        var fullScreen = false;
        void ToggleFullScreen()
        {
            fullScreen = !fullScreen;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = fullScreen ? FormBorderStyle.None : FormBorderStyle.Sizable;
            if (fullScreen) Bounds = Screen.FromControl(this).Bounds;
            else WindowState = FormWindowState.Maximized;
            full.Text = fullScreen ? "Vollbild beenden" : "Vollbild";
        }
        full.Click += (_, _) => ToggleFullScreen();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11 || (fullScreen && e.KeyCode == Keys.Escape))
            {
                ToggleFullScreen();
                e.SuppressKeyPress = true;
            }
        };
        foreach (var button in new[] { home, full })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(65, 83, 107);
            button.BackColor = Color.FromArgb(24, 39, 55);
            button.ForeColor = ForeColor;
        }
        var update = new Label { AutoEllipsis = true, Dock = DockStyle.Fill, Text = "Suche nach Updates …", Padding = new(10) };
        var checkUpdate = new Button { Text = "Updates prüfen", AutoSize = true, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(24, 39, 55), ForeColor = ForeColor };
        async Task CheckUpdatesAsync()
        {
            checkUpdate.Enabled = false;
            update.Text = "Suche nach Updates …";
            var result = await ReleaseUpdater.DownloadAsync(closed.Token);
            if (!IsDisposed && !closed.IsCancellationRequested)
            {
                update.Text = result;
                checkUpdate.Enabled = true;
            }
        }
        checkUpdate.Click += async (_, _) => await CheckUpdatesAsync();
        var toolbar = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Padding = new(4) };
        toolbar.Controls.Add(home);
        toolbar.Controls.Add(full);
        toolbar.Controls.Add(status);
        toolbar.Controls.Add(checkUpdate);
        var version = typeof(DesktopWindow).Assembly.GetName().Version!.ToString(3);
        var versionInfo = new Label
        {
            Text = $"Version {version}", AutoSize = true, Padding = new(10, 7, 6, 0)
        };
        toolbar.Controls.Add(versionInfo);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(webView, 0, 1);
        layout.Controls.Add(update, 0, 2);
        Controls.Add(layout);

        Shown += async (_, _) =>
        {
            var updateTask = CheckUpdatesAsync();
            try
            {
                await webView.EnsureCoreWebView2Async();
                if (IsDisposed || closed.IsCancellationRequested) return;
                webView.CoreWebView2.WebMessageReceived += async (_, e) =>
                {
                    if (e.Source != owner.AbsoluteUri) return;
                    string value;
                    try { value = e.TryGetWebMessageAsString(); }
                    catch (ArgumentException) { return; }
                    if (!Uri.TryCreate(value, UriKind.Absolute, out var invitation) ||
                        invitation.Scheme != Uri.UriSchemeHttps || invitation.UserInfo.Length != 0 ||
                        invitation.AbsolutePath != "/" || invitation.Query.Length != 0 ||
                        !System.Text.RegularExpressions.Regex.IsMatch(invitation.Fragment, @"\A#pair=[0-9A-F]{64}(?:&pin=[0-9A-F]{64})?\z"))
                        return;
                    try { await webView.CoreWebView2.ClearServerCertificateErrorActionsAsync(); }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        status.Text = "Die Zertifikatprüfung konnte nicht vorbereitet werden.";
                        return;
                    }
                    if (IsDisposed) return;
                    var fragment = invitation.Fragment.Split("&pin=", 2, StringSplitOptions.None);
                    certificatePin = fragment.Length == 2 ? fragment[1] : null;
                    connectedOrigin = invitation.GetLeftPart(UriPartial.Authority);
                    webView.CoreWebView2.Navigate(invitation.AbsoluteUri);
                };
                webView.CoreWebView2.ServerCertificateErrorDetected += (_, e) =>
                {
                    if (certificatePin is null || !Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var request) ||
                        request.GetLeftPart(UriPartial.Authority) != connectedOrigin)
                        return;

                    try
                    {
                        using var candidate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
                            e.ServerCertificate.ToPemEncoding());
                        if (candidate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256) == certificatePin)
                            e.Action = Microsoft.Web.WebView2.Core.CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                    }
                    catch (System.Security.Cryptography.CryptographicException) { }
                };
                webView.CoreWebView2.NavigationStarting += (_, e) => e.Cancel = !IsAppUrl(e.Uri);
                webView.CoreWebView2.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    if (IsAppUrl(e.Uri)) webView.CoreWebView2.Navigate(e.Uri);
                };
                webView.NavigationCompleted += (_, e) => status.Text =
                    e.IsSuccess ? "" : $"Die Seite konnte nicht geladen werden ({e.WebErrorStatus}).";
                home.Enabled = true;
                webView.Source = owner;
            }
            catch (Exception error)
            {
                if (!IsDisposed && !closed.IsCancellationRequested)
                    status.Text = $"WebView2 konnte nicht gestartet werden ({error.GetType().Name}).";
            }

            await updateTask;
        };
        FormClosed += (_, _) => closed.Cancel();
    }

    public static Task ShowAsync(string url, string? startupError = null, string? transferUrl = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                using var window = new DesktopWindow(url, startupError, transferUrl);
                Application.Run(window);
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}