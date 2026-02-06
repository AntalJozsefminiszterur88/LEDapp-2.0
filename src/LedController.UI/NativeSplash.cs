using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace LedController.UI;

internal static class NativeSplash
{
    private static readonly object Sync = new();
    private static Thread? _thread;
    private static ManualResetEventSlim? _ready;
    private static Form? _form;

    internal static void Show()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (Sync)
        {
            if (_thread is not null)
            {
                return;
            }

            _ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                try
                {
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);

                    _form = CreateForm();
                    _ready?.Set();
                    Application.Run(_form);
                }
                catch
                {
                    _ready?.Set();
                }
                finally
                {
                    _form = null;
                }
            })
            {
                IsBackground = true
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        _ready?.Wait(TimeSpan.FromSeconds(2));
    }

    internal static void Close()
    {
        Form? form;
        lock (Sync)
        {
            form = _form;
        }

        if (form is null)
        {
            return;
        }

        try
        {
            if (form.IsHandleCreated)
            {
                form.BeginInvoke(new Action(() => form.Close()));
            }
            else
            {
                form.Close();
            }
        }
        catch
        {
        }
    }

    private static Form CreateForm()
    {
        var form = new Form
        {
            Text = "LedController",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(230, 230, 230),
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
            Width = 420,
            Height = 160
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            RowCount = 3,
            ColumnCount = 1
        };

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));

        var title = new Label
        {
            Text = "LedController",
            Font = new Font("Segoe UI", 13, FontStyle.Bold),
            AutoSize = true
        };

        var status = new Label
        {
            Text = "Betöltés...",
            Font = new Font("Segoe UI", 10, FontStyle.Regular),
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 6)
        };

        var progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Height = 6,
            Dock = DockStyle.Top
        };

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(progress, 0, 2);

        form.Controls.Add(layout);
        return form;
    }
}
