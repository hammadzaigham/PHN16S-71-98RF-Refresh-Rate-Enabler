using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

public sealed class MainWindow : Window
{
    private static readonly SolidColorBrush Ink = Freeze(Color.FromRgb(242, 240, 236));
    private static readonly SolidColorBrush Muted = Freeze(Color.FromRgb(196, 192, 184));
    private static readonly SolidColorBrush Paper = Freeze(Color.FromRgb(22, 24, 28));
    private static readonly SolidColorBrush LogPaper = Freeze(Color.FromRgb(16, 18, 22));
    private static readonly SolidColorBrush ButtonFace = Freeze(Color.FromRgb(46, 52, 64));
    private static readonly SolidColorBrush PrimaryFace = Freeze(Color.FromRgb(232, 228, 220));
    private static readonly SolidColorBrush PrimaryInk = Freeze(Color.FromRgb(22, 24, 28));

    private readonly TextBlock _gpuLine = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Foreground = Ink, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock _step3 = new() { TextWrapping = TextWrapping.Wrap, Foreground = Muted, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _nextStep = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Foreground = Ink, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), Foreground = Ink };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 13,
        BorderThickness = new Thickness(0),
        Background = LogPaper,
        Foreground = Ink,
        CaretBrush = Ink,
        Padding = new Thickness(12)
    };

    private MonitorInfo? _monitor;
    private PanelEdid? _panel;
    private byte[]? _edid;

    public MainWindow()
    {
        Title = "PHN16S-71-98RF Refresh Rate Enabler";
        Width = 760;
        Height = 860;
        MinWidth = 640;
        MinHeight = 640;
        Background = Paper;
        Foreground = Ink;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 14;

        var root = new DockPanel { Margin = new Thickness(20) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(MakeButton("Add refresh rates", StageClick, primary: true));
        buttons.Children.Add(MakeButton("Restart driver", RestartClick, primary: false));
        buttons.Children.Add(MakeButton("Remove override", RevertClick, primary: false));
        buttons.Children.Add(MakeButton("Refresh", (_, _) => Load(clearLog: true), primary: false));
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_nextStep, Dock.Bottom);

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = "PHN16S-71-98RF Refresh Rate Enabler",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink
        });
        header.Children.Add(new TextBlock
        {
            Text = "Adds 60, 75, 100, 120, 144, and 165 Hz to Acer's PHN16S-71-98RF.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted,
            Margin = new Thickness(0, 4, 0, 12)
        });
        header.Children.Add(_gpuLine);
        header.Children.Add(new TextBlock
        {
            Text = "How to use",
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            Margin = new Thickness(0, 0, 0, 8)
        });
        header.Children.Add(StepTitle("1 — Select your GPU"));
        header.Children.Add(StepBody("Switch to either the Intel or NVIDIA GPU, whichever you want to set up first."));
        header.Children.Add(StepTitle("2 — Add refresh rates"));
        header.Children.Add(StepBody("Click Add refresh rates to add 60, 75, 100, 120, 144, and 165 Hz."));
        header.Children.Add(StepTitle("3 — Apply the changes"));
        header.Children.Add(_step3);
        header.Children.Add(StepTitle("4 — Repeat for the other GPU"));
        header.Children.Add(StepBody("Switch to the other GPU, then click Refresh. Click Add refresh rates again. On Intel, click Restart driver again."));
        header.Children.Add(StepTitle("5 — Choose a refresh rate"));
        header.Children.Add(StepBody("Open Windows Settings → System → Display → Advanced display. Choose a refresh rate, and confirm if Windows asks."));
        header.Children.Add(MoreInfo());
        header.Children.Add(_summary);
        DockPanel.SetDock(header, Dock.Top);

        root.Children.Add(buttons);
        root.Children.Add(_nextStep);
        root.Children.Add(header);
        root.Children.Add(_log);
        Content = root;

        ProbeLog.Sink = line =>
        {
            if (Dispatcher.CheckAccess())
                AppendLog(line);
            else
                Dispatcher.Invoke(() => AppendLog(line));
        };
        Loaded += (_, _) => Load(clearLog: true);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int enabled = 1;
        DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int));
        DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        int caption = 0x000000;
        int captionText = 0x00FFFFFF;
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36, ref captionText, sizeof(int));
    }

    private void AppendLog(string line)
    {
        _log.AppendText(line + Environment.NewLine);
        _log.ScrollToEnd();
    }

    private void Load(bool clearLog)
    {
        if (clearLog)
        {
            _log.Clear();
            _nextStep.Visibility = Visibility.Collapsed;
        }
        try
        {
            _monitor = MonitorLocator.FindInternal();
            if (_monitor == null)
            {
                _gpuLine.Text = "No internal display was found.";
                _step3.Text = "Connect the internal panel, then click Refresh.";
                _summary.Text = "";
                return;
            }

            _edid = _monitor.ReadEdid();
            _panel = PanelEdid.Parse(_edid);
            CcdSignal? signal = CcdSignal.ReadActive();
            bool over = Program.OverrideExists(_monitor);
            string gpu = Program.ActiveGpu(_monitor);
            ShowGpu(gpu);
            string modeList = _monitor.Modes.Count == 0 ? "none listed yet" : string.Join(", ", _monitor.Modes.Select(hz => hz + " Hz"));
            _summary.Text =
                _monitor.CurrentWidth + "×" + _monitor.CurrentHeight + " at " + _monitor.CurrentHz + " Hz"
                + Environment.NewLine
                + "Windows is offering: " + modeList
                + Environment.NewLine
                + (over ? "Intel refresh rates are saved." : "Intel refresh rates are not saved.");

            if (_panel.Error != null)
            {
                ProbeLog.Line(_panel.Error);
                return;
            }

            if (clearLog)
            {
                foreach (string line in _panel.Report)
                    ProbeLog.Line(line);
                if (signal != null)
                    ProbeLog.Line(_panel.Compare(signal));
            }

            if (clearLog)
            {
                foreach (int hz in Program.StagedRates)
                {
                    _panel.TryBuild(hz, out _, out string detail);
                    ProbeLog.Line(hz + " Hz  " + detail);
                }
            }
        }
        catch (Exception ex)
        {
            _summary.Text = ex.Message;
        }
    }

    private void StageClick(object sender, RoutedEventArgs e)
    {
        if (_monitor == null || _panel == null || _edid == null || _panel.Error != null)
            return;

        var rates = new List<int>();
        var descriptors = new List<byte[]>();
        foreach (int hz in Program.StagedRates)
        {
            if (!_panel.TryBuild(hz, out byte[] descriptor, out string detail))
            {
                ProbeLog.Line(hz + " Hz  " + detail);
                return;
            }
            rates.Add(hz);
            descriptors.Add(descriptor);
        }

        string gpu = Program.ActiveGpu(_monitor);
        string action = gpu == "NVIDIA"
            ? "Add 60, 75, 100, 120, 144, and 165 Hz for the NVIDIA GPU? 240 Hz stays available."
            : "Add 60, 75, 100, 120, 144, and 165 Hz for the Intel GPU? Click Restart driver afterward. The screen may briefly turn black. 240 Hz stays available.";
        if (MessageBox.Show(this, action, "Add refresh rates", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        MonitorInfo monitor = _monitor;
        byte[] edid = _edid;
        RunWork(() =>
        {
            int result = Program.Stage(monitor, edid, rates, descriptors, force: true);
            Dispatcher.Invoke(() =>
            {
                if (result != 0)
                {
                    _nextStep.Visibility = Visibility.Collapsed;
                    return;
                }
                _nextStep.Text = gpu == "NVIDIA"
                    ? "Refresh rates added. Open Windows Settings → System → Display → Advanced display and choose a rate. A driver restart is normally unnecessary."
                    : "Refresh rates saved. Click Restart driver to apply them. The screen may briefly turn black.";
                _nextStep.Visibility = Visibility.Visible;
            });
        });
    }

    private void RevertClick(object sender, RoutedEventArgs e)
    {
        if (_monitor == null)
            return;
        string gpu = Program.ActiveGpu(_monitor);
        string action = gpu == "NVIDIA"
            ? "Remove override only removes Intel refresh rates. NVIDIA rates stay until you clean install the driver from the NVIDIA app."
            : "Remove the Intel refresh rates? 240 Hz stays available. Click Restart driver afterward.";
        if (MessageBox.Show(this, action, "Remove override", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        RunWork(() => Program.Revert(_monitor));
    }

    private void RestartClick(object sender, RoutedEventArgs e)
    {
        if (_monitor == null)
            return;
        if (MessageBox.Show(this,
                "Restart the driver now? The screen may briefly turn black. If it stays black, press F8 within 15 seconds.",
                "Restart driver",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        RunWork(() => Program.Restart(_monitor));
    }

    private void RunWork(Action work)
    {
        IsEnabled = false;
        Task.Run(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                ProbeLog.Line(ex.Message);
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    IsEnabled = true;
                    Load(clearLog: false);
                });
            }
        });
    }

    private void ShowGpu(string gpu)
    {
        bool nvidia = gpu == "NVIDIA";
        _gpuLine.Text = nvidia
            ? "NVIDIA GPU is driving this panel."
            : "Intel GPU is driving this panel.";
        _step3.Text = nvidia
            ? "The rates are added through the NVIDIA driver. A driver restart is normally unnecessary."
            : "Click Restart driver to apply the new rates. The screen may briefly turn black.";
    }

    private static TextBlock StepTitle(string text)
        => new()
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ink,
            Margin = new Thickness(0, 0, 0, 2)
        };

    private static TextBlock StepBody(string text)
        => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Muted,
            Margin = new Thickness(0, 0, 0, 10)
        };

    private static Expander MoreInfo()
    {
        var expander = new Expander
        {
            Header = "Troubleshooting & Advanced Info",
            Foreground = Ink,
            Background = Paper,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 2, 0, 4),
            IsExpanded = false,
            FontWeight = FontWeights.SemiBold,
            Template = DarkExpanderTemplate()
        };
        expander.Content = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.Normal,
            Foreground = Muted,
            LineHeight = 20,
            Margin = new Thickness(0, 4, 0, 0),
            Text =
                "Screen stays black? Press F8 within 15 seconds of restarting the driver. That restores 240 Hz for this session. The saved Intel rates remain, so you can restart the driver again later.\n\n" +
                "Want to undo the changes?\n" +
                "Intel: switch to the Intel GPU, click Remove override, then Restart driver.\n" +
                "NVIDIA: clean install the driver from the NVIDIA app.\n\n" +
                "At 60 Hz, brightness can flicker a little because of the panel's PWM dimming.\n" +
                "Windows may show 165.02 Hz instead of 165 Hz. That is expected.\n" +
                "The original 240 Hz mode is kept."
        };
        return expander;
    }

    private static Button MakeButton(string label, RoutedEventHandler click, bool primary)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(14, 8, 14, 8),
            Foreground = primary ? PrimaryInk : Ink,
            Background = primary ? PrimaryFace : ButtonFace,
            BorderThickness = new Thickness(0),
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Template = DarkButtonTemplate()
        };
        button.Click += click;
        return button;
    }

    private static ControlTemplate DarkExpanderTemplate()
    {
        var headerText = new FrameworkElementFactory(typeof(ContentPresenter));
        headerText.SetValue(ContentPresenter.ContentProperty, new TemplateBindingExtension(ContentControl.ContentProperty));
        headerText.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        headerText.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        var headerChrome = new FrameworkElementFactory(typeof(Border));
        headerChrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        headerChrome.SetValue(Border.PaddingProperty, new Thickness(0, 4, 0, 4));
        headerChrome.AppendChild(headerText);
        var headerTemplate = new ControlTemplate(typeof(ToggleButton)) { VisualTree = headerChrome };

        var header = new FrameworkElementFactory(typeof(ToggleButton), "HeaderSite");
        header.SetValue(ContentControl.ContentProperty, new TemplateBindingExtension(HeaderedContentControl.HeaderProperty));
        header.SetValue(Control.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        header.SetValue(Control.FontWeightProperty, new TemplateBindingExtension(Control.FontWeightProperty));
        header.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        header.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        header.SetValue(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left);
        header.SetValue(Control.PaddingProperty, new Thickness(0));
        header.SetValue(Control.TemplateProperty, headerTemplate);
        header.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Expander.IsExpanded))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
            Mode = BindingMode.TwoWay
        });
        header.SetValue(DockPanel.DockProperty, Dock.Top);

        var body = new FrameworkElementFactory(typeof(ContentPresenter), "ExpandSite");
        body.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
        body.SetValue(DockPanel.DockProperty, Dock.Bottom);

        var dock = new FrameworkElementFactory(typeof(DockPanel));
        dock.AppendChild(header);
        dock.AppendChild(body);

        var expanded = new Trigger { Property = Expander.IsExpandedProperty, Value = true };
        expanded.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "ExpandSite"));

        var template = new ControlTemplate(typeof(Expander)) { VisualTree = dock };
        template.Triggers.Add(expanded);
        return template;
    }

    private static ControlTemplate DarkButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Button.ForegroundProperty));
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
