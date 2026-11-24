using System.Diagnostics;
using System.Windows.Forms;

internal sealed class DesktopWindow : Form
{
    private readonly CancellationTokenSource closed = new();

    public DesktopWindow(string url, string? startupError = null)
    {
        Text = "OpenDrop Windows";
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        var panel = new FlowLayoutPanel
        {
            AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new(20), WrapContents = false
        };
        var status = new Label { AutoSize = true, MaximumSize = new(480, 0) };
        status.Text = startupError ?? $"Der Empfangsserver läuft unter {url}.";
        panel.Controls.Add(status);

        if (startupError is null)
        {
            var open = new Button { Text = "Empfangsseite öffnen", AutoSize = true };
            var update = new Label { AutoSize = true, MaximumSize = new(480, 0), Text = "Suche nach Updates …" };
            void OpenBrowser()
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception) { status.Text = $"Öffne im Browser: {url}"; }
            }
            open.Click += (_, _) => OpenBrowser();
            panel.Controls.Add(open);
            panel.Controls.Add(update);
            Shown += async (_, _) =>
            {
                if (Environment.GetEnvironmentVariable("OPENDROP_NO_BROWSER") != "1") OpenBrowser();
                var result = await ReleaseUpdater.DownloadAsync(closed.Token);
                if (!IsDisposed && !closed.IsCancellationRequested) update.Text = result;
            };
        }
        Controls.Add(panel);
        FormClosed += (_, _) => closed.Cancel();
    }

    public static Task ShowAsync(string url, string? startupError = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                using var window = new DesktopWindow(url, startupError);
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
