using System.Diagnostics;
using System.Text;
using FileExplorer.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace FileExplorer.Views;

public sealed partial class TerminalPane : UserControl
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;
    private readonly List<string> _history = new();
    private int _historyIndex;
    private Process? _process;

    public event EventHandler? GoToActiveFolderRequested;

    public TerminalPane()
    {
        InitializeComponent();
        _dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Loaded += (_, _) => StartShell();
        Unloaded += (_, _) => StopShell();
    }

    public void RunCommand(string command)
    {
        if (_process is null || _process.HasExited || string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        AppendLine($"PS> {command}");
        _history.Add(command);
        _historyIndex = _history.Count;

        try
        {
            _process.StandardInput.WriteLine(command);
        }
        catch (IOException)
        {
            AppendLine("[terminal] Shell is no longer available.");
        }
    }

    private void StartShell()
    {
        if (_process is not null)
        {
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoLogo -NoProfile",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => AppendLine(e.Data);
            _process.ErrorDataReceived += (_, e) => AppendLine(e.Data);
            _process.Exited += (_, _) => AppendLine("[terminal] PowerShell session ended.");
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            AppendLine($"[terminal] Failed to start PowerShell: {ex.Message}");
        }
    }

    private void StopShell()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LoggingService.LogWarning("TerminalPane.StopShell", ex);
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    private void AppendLine(string? text)
    {
        if (text is null)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            OutputText.Text += text + "\n";
            OutputScroller.UpdateLayout();
            OutputScroller.ChangeView(null, OutputScroller.ScrollableHeight, null, disableAnimation: true);
        });
    }

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                var command = InputBox.Text;
                InputBox.Text = string.Empty;
                RunCommand(command);
                e.Handled = true;
                break;

            case VirtualKey.Up:
                if (_history.Count > 0)
                {
                    _historyIndex = Math.Max(0, _historyIndex - 1);
                    InputBox.Text = _history[_historyIndex];
                    InputBox.SelectionStart = InputBox.Text.Length;
                }
                e.Handled = true;
                break;

            case VirtualKey.Down:
                if (_history.Count > 0)
                {
                    _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
                    InputBox.Text = _historyIndex < _history.Count ? _history[_historyIndex] : string.Empty;
                    InputBox.SelectionStart = InputBox.Text.Length;
                }
                e.Handled = true;
                break;
        }
    }

    private void ClearButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        OutputText.Text = string.Empty;
    }

    private void GoToActiveFolderButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        GoToActiveFolderRequested?.Invoke(this, EventArgs.Empty);
    }
}
