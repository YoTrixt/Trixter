using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Interop;

namespace WpfApp1;

public partial class MainWindow : Window
{
    const int WmGetMinMaxInfo = 0x0024;
    const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeMinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    static readonly object browserEnvironmentLock = new();
    static Task<CoreWebView2Environment>? browserEnvironmentTask;
    static readonly string browserUserDataFolder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trixter", "WebView2");
    static readonly SemaphoreSlim uBlockImportGate = new(1, 1);
    static bool uBlockImportAttempted;
    const string UBlockExtensionId = "cjpalhdlnbpafiamejdnhcphjbkeiagm";

    sealed class BrowserTab
    {
        public string Title = "New tab";
        public string Url = "";
        public WebView2 View = new();
        public Button TitleButton = null!;
        public Border Surface = null!;
    }

    sealed class Shortcut
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
    }

    sealed class Bookmark
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
    }

    sealed class RecentPage
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
    }

    sealed class AddressSuggestion
    {
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Value { get; set; } = "";
        public bool IsSearch { get; set; }
        public bool IsRecentSearch { get; set; }
    }

    sealed class Preferences
    {
        public string Accent { get; set; } = "#B6F36A";
        public string Theme { get; set; } = "Dark";
        public string Density { get; set; } = "Balanced";
        public string HomePage { get; set; } = "";
        public string StartPageBackground { get; set; } = "";
        public string SearchUrl { get; set; } = "https://www.google.com/search?q={query}";
        public string SearchEngine { get; set; } = "Google";
        public bool ShowBookmarks { get; set; } = true;
        public bool ShowShortcuts { get; set; } = true;
        public bool ShowSuggestions { get; set; } = true;
        public List<string> RecentSearches { get; set; } = new();
        public List<RecentPage> RecentPages { get; set; } = new();
        public List<Bookmark> Bookmarks { get; set; } = new()
        {
            new() { Title = "YouTube", Url = "https://youtube.com" },
            new() { Title = "GitHub", Url = "https://github.com" },
            new() { Title = "Wikipedia", Url = "https://wikipedia.org" }
        };
        public List<Shortcut> Shortcuts { get; set; } = new()
        {
            new() { Title = "YouTube", Url = "https://youtube.com" },
            new() { Title = "GitHub", Url = "https://github.com" },
            new() { Title = "Wikipedia", Url = "https://wikipedia.org" }
        };
    }

    readonly List<BrowserTab> tabs = new();
    readonly string settingsPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trixter", "settings.json");
    Preferences preferences = new();
    readonly bool privateMode;
    string? privateProfilePath;
    CoreWebView2Environment? browserEnvironment;
    CoreWebView2Environment? privateEnvironment;
    BrowserTab? activeTab;
    Path maximizeIcon = null!;
    Grid rootGrid = null!;
    List<AddressSuggestion> currentSuggestions = new();
    bool settingAddressText;
    readonly TaskCompletionSource<bool> initialTabReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    BrowserTab? tabDragSource;
    Point tabDragStart;
    bool tabDragStarted;

    public MainWindow() : this(false, null) { }

    MainWindow(bool privateMode) : this(privateMode, null) { }

    MainWindow(bool privateMode, string? initialAddress)
    {
        InitializeComponent();
        maximizeIcon = (Path)FindName("MaximizeIcon");
        this.privateMode = privateMode;
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessageHook);
        StateChanged += (_, _) =>
        {
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
                chrome.ResizeBorderThickness = WindowState == WindowState.Maximized
                    ? new Thickness(6, 0, 6, 6)
                    : new Thickness(6);
            if (maximizeIcon != null)
                maximizeIcon.Data = WindowState == WindowState.Maximized
                    ? Geometry.Parse("M 5,5 L 16,5 L 16,16 L 5,16 Z M 2,12 L 2,2 L 12,2")
                    : Geometry.Parse("M 3,3 L 15,3 L 15,15 L 3,15 Z");
        };
        if (privateMode)
        {
            Title = "Trixter — Incognito";
            BrandLabel.Text = "TRIXTER  ·  INCOGNITO";
            BrandLabel.Foreground = new SolidColorBrush(Color.FromRgb(190, 164, 255));
            privateProfilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Trixter-InPrivate-" + Guid.NewGuid().ToString("N"));
            Closed += (_, _) => _ = DeletePrivateProfileWhenReleased(privateProfilePath);
        }
        rootGrid = (Grid)Content;
        PreviewMouseMove += TabDrag_MouseMove;
        PreviewMouseLeftButtonUp += TabDrag_MouseUp;
        BuildSearchEngineIcons();
        FitToWorkArea();
        LoadPreferences();
        SearchEngineBox.SelectedItem = SearchEngineBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == preferences.SearchEngine)
            ?? SearchEngineBox.Items.OfType<ComboBoxItem>().First();
        ApplyAppearance();
        Loaded += async (_, _) =>
        {
            RenderShortcuts();
            RenderBookmarks();
            if (privateMode)
            {
                try
                {
                    var options = new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true };
                    privateEnvironment = await CoreWebView2Environment.CreateAsync(null, privateProfilePath, options);
                }
                catch (Exception ex)
                {
                    ShowWebView2InitializationError(ex);
                    initialTabReady.TrySetResult(false);
                    return;
                }
            }
            else
            {
                try { browserEnvironment = await GetBrowserEnvironmentAsync(); }
                catch (Exception ex)
                {
                    ShowWebView2InitializationError(ex);
                    initialTabReady.TrySetResult(false);
                    return;
                }
            }
            initialTabReady.TrySetResult(await AddTab(initialAddress));
        };
        PreviewKeyDown += HandleShortcuts;
    }

    static Task<CoreWebView2Environment> GetBrowserEnvironmentAsync()
    {
        lock (browserEnvironmentLock)
            return browserEnvironmentTask ??= CreateBrowserEnvironmentAsync();
    }

    static async Task<CoreWebView2Environment> CreateBrowserEnvironmentAsync()
    {
        System.IO.Directory.CreateDirectory(browserUserDataFolder);
        return await CoreWebView2Environment.CreateAsync(
            null,
            browserUserDataFolder,
            new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = true });
    }

    static bool IsWebView2RuntimeMissing(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
            if (current is WebView2RuntimeNotFoundException) return true;
        return false;
    }

    void ShowWebView2InitializationError(Exception exception)
    {
        if (!IsWebView2RuntimeMissing(exception))
        {
            MessageBox.Show(
                "Trixter couldn't start WebView2.\n\n" + exception.Message,
                "Trixter Browser Startup",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        const string downloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";
        var message = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(20),
            FontSize = 14
        };
        message.Inlines.Add(new System.Windows.Documents.Run(
            "Trixter needs the Microsoft Edge WebView2 Evergreen Runtime to display web pages. It isn't installed on this computer.\n\n"));
        var downloadLink = new System.Windows.Documents.Hyperlink(
            new System.Windows.Documents.Run("Download the WebView2 Runtime from Microsoft"))
        {
            NavigateUri = new Uri(downloadUrl)
        };
        downloadLink.RequestNavigate += (_, args) =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
            args.Handled = true;
        };
        message.Inlines.Add(downloadLink);

        var dialog = new Window
        {
            Title = "WebView2 Runtime Required",
            Owner = this,
            Width = 470,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Content = message
        };
        dialog.ShowDialog();
    }

    void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1200, area.Width * .92);
        Height = Math.Min(760, area.Height * .90);
        MinWidth = Math.Min(700, area.Width);
        MinHeight = Math.Min(500, area.Height);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    void LoadPreferences()
    {
        try
        {
            if (System.IO.File.Exists(settingsPath))
                preferences = JsonSerializer.Deserialize<Preferences>(System.IO.File.ReadAllText(settingsPath)) ?? new();
        }
        catch { preferences = new(); }
        preferences.Bookmarks ??= new();
        preferences.Shortcuts ??= new();
        preferences.RecentSearches ??= new();
        preferences.RecentPages ??= new();
    }

    void SavePreferences()
    {
        if (privateMode) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath)!);
            System.IO.File.WriteAllText(settingsPath, JsonSerializer.Serialize(preferences));
        }
        catch (Exception ex) { MessageBox.Show("Could not save settings: " + ex.Message, "Trixter"); }
    }

    void ApplyAppearance()
    {
        bool light = preferences.Theme == "Light";
        Color background = light ? Color.FromRgb(244, 246, 249) : Color.FromRgb(16, 19, 25);
        Color surface = light ? Colors.White : Color.FromRgb(23, 26, 33);
        Color inset = light ? Color.FromRgb(255, 255, 255) : Color.FromRgb(30, 36, 46);
        Color outline = light ? Color.FromRgb(202, 210, 221) : Color.FromRgb(55, 64, 77);
        Brush text = new SolidColorBrush(light ? Color.FromRgb(35, 39, 47) : Color.FromRgb(238, 240, 244));
        Brush muted = new SolidColorBrush(light ? Color.FromRgb(105, 112, 123) : Color.FromRgb(146, 152, 165));
        Brush accent = ParseBrush(preferences.Accent, Brushes.LightGreen);

        Background = new SolidColorBrush(background);
        Resources["HoverSurface"] = new SolidColorBrush(light ? Color.FromRgb(225, 230, 237) : Color.FromRgb(48, 53, 64));
        TitleBar.Background = Toolbar.Background = BookmarkBar.Background = new SolidColorBrush(surface);
        if (!string.IsNullOrWhiteSpace(preferences.StartPageBackground) && System.IO.File.Exists(preferences.StartPageBackground))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(preferences.StartPageBackground); image.EndInit(); image.Freeze();
                NewTabPanel.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill, Opacity = .72 };
            }
            catch { NewTabPanel.Background = new SolidColorBrush(background); }
        }
        else NewTabPanel.Background = new SolidColorBrush(background);
        AddressSurface.Background = HomeSearchSurface.Background = new SolidColorBrush(inset);
        AddressSurface.BorderBrush = HomeSearchSurface.BorderBrush = new SolidColorBrush(outline);
        HomeSearchSurface.BorderThickness = AddressSurface.BorderThickness = new Thickness(1);
        BrandLabel.Foreground = HomeSearchMark.Foreground = accent;
        SearchEngineBox.Foreground = text;
        SearchEngineBox.Background = new SolidColorBrush(inset);
        SearchEngineBox.BorderBrush = new SolidColorBrush(outline);
        var engineItems = new Style(typeof(ComboBoxItem));
        engineItems.Setters.Add(new Setter(Control.ForegroundProperty, text));
        engineItems.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(surface)));
        SearchEngineBox.ItemContainerStyle = engineItems;
        HomeHeading.Foreground = AddressBox.Foreground = HomeSearch.Foreground = text;
        StarButton.Foreground = accent;
        HomeSubtitle.Foreground = QuickLinksLabel.Foreground = muted;
        BookmarkBar.Visibility = preferences.ShowBookmarks ? Visibility.Visible : Visibility.Collapsed;
        QuickLinks.Visibility = preferences.ShowShortcuts ? Visibility.Visible : Visibility.Collapsed;
        if (QuickLinks.Parent is StackPanel startPanel && startPanel.Children.OfType<DockPanel>().FirstOrDefault() is DockPanel shortcutHeader)
            shortcutHeader.Visibility = preferences.ShowShortcuts ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in FindButtons(TitleBar)) button.Foreground = text;

        rootGrid.RowDefinitions[0].Height = new GridLength(preferences.Density == "Compact" ? 36 : preferences.Density == "Roomy" ? 50 : 42);
        rootGrid.RowDefinitions[1].Height = new GridLength(preferences.Density == "Compact" ? 42 : preferences.Density == "Roomy" ? 56 : 48);
        StarButton.Padding = preferences.Density == "Compact" ? new Thickness(3, 0, 3, 0) : new Thickness(8, 4, 8, 4);
        foreach (var tab in tabs)
        {
            tab.TitleButton.Foreground = text;
            tab.Surface.Background = tab == activeTab ? new SolidColorBrush(Blend(ParseColor(preferences.Accent), backgroundColor: background, .20)) : new SolidColorBrush(light ? Color.FromRgb(230, 233, 238) : Color.FromRgb(32, 35, 43));
        }
        foreach (var button in Toolbar.Children.OfType<Panel>().SelectMany(p => p.Children.OfType<Button>())) button.Foreground = text;
        UpdateBookmarkButton();
    }

    static Brush ParseBrush(string value, Brush fallback)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(value)!; }
        catch { return fallback; }
    }

    static Color ParseColor(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value)!; }
        catch { return Color.FromRgb(182, 243, 106); }
    }

    static Canvas CreateColorWheel()
    {
        const int size = 220;
        const double radius = 106;
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            double dx = x - (size - 1) / 2.0;
            double dy = y - (size - 1) / 2.0;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            int offset = (y * size + x) * 4;
            if (distance > radius) continue;
            double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            Color color = HsvToColor(hue, Math.Min(1, distance / radius), 1);
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
            pixels[offset + 3] = 255;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        var canvas = new Canvas { Width = size, Height = size, Background = Brushes.Transparent, IsHitTestVisible = true, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 5) };
        canvas.Children.Add(new Image { Source = bitmap, Width = size, Height = size, IsHitTestVisible = false });
        return canvas;
    }

    static Color ColorAtWheelPoint(Point point, double width, double height)
    {
        double centerX = width / 2, centerY = height / 2, radius = Math.Min(width, height) / 2 - 4;
        double dx = point.X - centerX, dy = point.Y - centerY;
        double distance = Math.Min(radius, Math.Sqrt(dx * dx + dy * dy));
        double hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        return HsvToColor(hue, distance / radius, 1);
    }

    static Point ColorToWheelPoint(Color color, double width, double height)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double hue = delta == 0 ? 0 : max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        if (hue < 0) hue += 360;
        double saturation = max == 0 ? 0 : delta / max;
        double radius = (Math.Min(width, height) / 2 - 4) * saturation;
        double angle = hue * Math.PI / 180;
        return new Point(width / 2 + Math.Cos(angle) * radius, height / 2 + Math.Sin(angle) * radius);
    }

    static Color HsvToColor(double hue, double saturation, double value)
    {
        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs((hue / 60 % 2) - 1));
        double m = value - chroma;
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    static Color Blend(Color a, Color backgroundColor, double amount) => Color.FromRgb(
        (byte)(a.R * amount + backgroundColor.R * (1 - amount)),
        (byte)(a.G * amount + backgroundColor.G * (1 - amount)),
        (byte)(a.B * amount + backgroundColor.B * (1 - amount)));

    static IEnumerable<Button> FindButtons(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button) yield return button;
            foreach (var nested in FindButtons(child)) yield return nested;
        }
    }

    async Task<bool> AddTab(string? address = null)
    {
        var tab = new BrowserTab();
        tab.Surface = new Border
        {
            CornerRadius = new CornerRadius(8, 8, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(32, 35, 43)),
            Margin = new Thickness(2, 5, 2, 0),
            BorderThickness = new Thickness(0, 0, 0, 2)
        };
        var header = new DockPanel { LastChildFill = true };
        var close = new Button { Content = "x", ToolTip = "Close tab", Style = (Style)FindResource("ChromeButton"), Padding = new Thickness(8, 6, 9, 6), FontSize = 12 };
        close.Click += (_, e) => { e.Handled = true; CloseTab(tab); };
        DockPanel.SetDock(close, Dock.Right);
        tab.TitleButton = new Button { Content = "New tab", ToolTip = "Switch to tab", Style = (Style)FindResource("TabTitleButton"), Background = Brushes.Transparent, Foreground = ParseBrush(preferences.Theme == "Light" ? "#263142" : "#E7EAF0", Brushes.White), MinWidth = 105, Padding = new Thickness(12, 7, 5, 7) };
        tab.TitleButton.Click += (_, e) => { e.Handled = true; ActivateTab(tab); };
        tab.TitleButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            tabDragSource = tab;
            tabDragStart = tab.TitleButton.PointToScreen(e.GetPosition(tab.TitleButton));
            tabDragStarted = false;
        };
        header.Children.Add(close);
        header.Children.Add(tab.TitleButton);
        tab.Surface.Child = header;
        tabs.Add(tab);
        TabStrip.Children.Add(tab.Surface);

        tab.View.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 16, 19, 25);
        BrowserHost.Children.Add(tab.View);
        tab.View.NavigationStarting += (_, e) =>
        {
            tab.Url = e.Uri;
            if (tab == activeTab) SetAddressText(e.Uri);
        };
        tab.View.NavigationCompleted += (_, _) =>
        {
            tab.Title = tab.View.CoreWebView2?.DocumentTitle ?? Host(tab.Url);
            tab.TitleButton.Content = tab.Title;
            tab.TitleButton.ToolTip = tab.Title;
            if (!string.IsNullOrWhiteSpace(tab.Url))
            {
                if (!privateMode)
                {
                    preferences.RecentPages.RemoveAll(x => x.Url == tab.Url);
                    preferences.RecentPages.Insert(0, new RecentPage { Title = tab.Title, Url = tab.Url });
                    if (preferences.RecentPages.Count > 40) preferences.RecentPages.RemoveRange(40, preferences.RecentPages.Count - 40);
                    SavePreferences();
                }
            }
            if (tab == activeTab) UpdateBookmarkButton();
        };
        tab.View.CoreWebView2InitializationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
            {
                CoreWebView2 core = tab.View.CoreWebView2;
                core.WebMessageReceived += (_, message) =>
                {
                    if (message.WebMessageAsJson == "\"trixter:new-window\"") OpenBrowserWindow(false);
                    else if (message.WebMessageAsJson == "\"trixter:incognito-window\"") OpenBrowserWindow(true);
                    else if (message.WebMessageAsJson == "\"trixter:history\"") History_Click(this, new RoutedEventArgs());
                };
                _ = core.AddScriptToExecuteOnDocumentCreatedAsync("document.addEventListener('keydown',e=>{if(e.ctrlKey&&e.key.toLowerCase()==='n'){e.preventDefault();e.stopImmediatePropagation();window.chrome.webview.postMessage(e.shiftKey?'trixter:incognito-window':'trixter:new-window')}else if(e.ctrlKey&&e.key.toLowerCase()==='h'){e.preventDefault();e.stopImmediatePropagation();window.chrome.webview.postMessage('trixter:history')}},true);");
                core.NewWindowRequested += (_, request) =>
                {
                    request.Handled = true;
                    _ = AddTab(request.Uri);
                };
            }
        };

        try { await tab.View.EnsureCoreWebView2Async(privateMode ? privateEnvironment : browserEnvironment); }
        catch (Exception ex)
        {
            ShowWebView2InitializationError(ex);
            return false;
        }
        if (!privateMode && tab.View.CoreWebView2 != null)
        {
            try { await ImportOperaUBlockAsync(tab.View.CoreWebView2.Profile); }
            catch (Exception ex) { MessageBox.Show("Trixter found uBlock Origin in Opera GX but could not load it.\n\n" + ex.Message, "Trixter Extensions", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        ActivateTab(tab);
        var target = string.IsNullOrWhiteSpace(address) ? preferences.HomePage : address;
        if (!string.IsNullOrWhiteSpace(target)) Navigate(target, tab);
        return tab.View.CoreWebView2 != null;
    }

    void TabDrag_MouseMove(object sender, MouseEventArgs e)
    {
        BrowserTab? draggedTab = tabDragSource;
        if (draggedTab == null || e.LeftButton != MouseButtonState.Pressed || !tabs.Contains(draggedTab)) return;

        Point screenPoint = PointToScreen(e.GetPosition(this));
        if (!tabDragStarted)
        {
            if (Math.Abs(screenPoint.X - tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(screenPoint.Y - tabDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            tabDragStarted = true;
        }

        double tabBarBottom = TitleBar.PointToScreen(new Point(0, TitleBar.ActualHeight)).Y;
        if (screenPoint.Y > tabBarBottom + 18)
        {
            tabDragSource = null;
            _ = TearOffTabAsync(draggedTab);
            e.Handled = true;
            return;
        }

        int targetIndex = -1;
        for (int i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            Point topLeft = tab.Surface.PointToScreen(new Point(0, 0));
            if (new Rect(topLeft, tab.Surface.RenderSize).Contains(screenPoint))
            {
                targetIndex = i;
                break;
            }
        }

        int oldIndex = tabs.IndexOf(draggedTab);
        if (targetIndex >= 0 && targetIndex != oldIndex)
        {
            tabs.RemoveAt(oldIndex);
            tabs.Insert(Math.Min(targetIndex, tabs.Count), draggedTab);
            TabStrip.Children.Remove(draggedTab.Surface);
            TabStrip.Children.Insert(tabs.IndexOf(draggedTab), draggedTab.Surface);
            e.Handled = true;
        }
    }

    void TabDrag_MouseUp(object sender, MouseButtonEventArgs e)
    {
        tabDragSource = null;
        tabDragStarted = false;
    }

    async Task TearOffTabAsync(BrowserTab tab)
    {
        if (!tabs.Contains(tab)) return;

        var detachedWindow = new MainWindow(privateMode, tab.Url);
        detachedWindow.Show();
        bool detachedTabReady = await detachedWindow.initialTabReady.Task;
        if (!detachedTabReady)
        {
            detachedWindow.Close();
            return;
        }

        CloseTab(tab);
        detachedWindow.Activate();
    }

    static async Task ImportOperaUBlockAsync(CoreWebView2Profile profile)
    {
        await uBlockImportGate.WaitAsync();
        try
        {
            if (uBlockImportAttempted) return;
            uBlockImportAttempted = true;
            if ((await profile.GetBrowserExtensionsAsync()).Any(extension => extension.Id == UBlockExtensionId)) return;

            string operaExtensions = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Opera Software", "Opera GX Stable", "Default", "Extensions", UBlockExtensionId);
            if (!System.IO.Directory.Exists(operaExtensions)) return;

            string? source = System.IO.Directory.GetDirectories(operaExtensions)
                .OrderByDescending(System.IO.Directory.GetLastWriteTimeUtc)
                .FirstOrDefault(path => System.IO.File.Exists(System.IO.Path.Combine(path, "manifest.json")));
            if (source == null) return;

            using (var manifest = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(source, "manifest.json"))))
            {
                if (!manifest.RootElement.TryGetProperty("name", out var name) || name.GetString() != "uBlock Origin") return;
            }

            string version = System.IO.Path.GetFileName(source);
            string managedFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Trixter", "Extensions", "uBlockOrigin-" + version);
            if (!System.IO.Directory.Exists(managedFolder)) CopyExtensionDirectory(source, managedFolder);
            await profile.AddBrowserExtensionAsync(managedFolder);
        }
        finally { uBlockImportGate.Release(); }
    }

    static void CopyExtensionDirectory(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (string file in System.IO.Directory.GetFiles(source))
            System.IO.File.Copy(file, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(file)), true);
        foreach (string directory in System.IO.Directory.GetDirectories(source))
        {
            if ((System.IO.File.GetAttributes(directory) & System.IO.FileAttributes.ReparsePoint) != 0) continue;
            CopyExtensionDirectory(directory, System.IO.Path.Combine(destination, System.IO.Path.GetFileName(directory)));
        }
    }

    void CloseTab(BrowserTab tab)
    {
        int oldIndex = tabs.IndexOf(tab);
        if (oldIndex < 0) return;
        bool wasActive = tab == activeTab;
        tabs.RemoveAt(oldIndex);
        TabStrip.Children.Remove(tab.Surface);
        BrowserHost.Children.Remove(tab.View);
        if (tabs.Count == 0) { activeTab = null; _ = AddTab(); return; }
        if (wasActive) ActivateTab(tabs[Math.Min(oldIndex, tabs.Count - 1)]);
    }

    void ActivateTab(BrowserTab tab)
    {
        activeTab = tab;
        foreach (var item in tabs)
        {
            item.View.Visibility = item == tab ? Visibility.Visible : Visibility.Collapsed;
            item.TitleButton.Foreground = ParseBrush(preferences.Theme == "Light" ? "#263142" : "#E7EAF0", Brushes.White);
            item.Surface.Background = item == tab
                ? new SolidColorBrush(Blend(ParseColor(preferences.Accent), preferences.Theme == "Light" ? Colors.White : Color.FromRgb(16, 19, 25), .19))
                : new SolidColorBrush(preferences.Theme == "Light" ? Color.FromRgb(230, 233, 238) : Color.FromRgb(32, 35, 43));
            item.Surface.BorderBrush = item == tab ? ParseBrush(preferences.Accent, Brushes.LightGreen) : Brushes.Transparent;
            item.Surface.BorderThickness = new Thickness(0, 0, 0, item == tab ? 2 : 0);
        }
        SetAddressText(tab.Url);
        UpdateBookmarkButton();
        NewTabPanel.Visibility = string.IsNullOrEmpty(tab.Url) ? Visibility.Visible : Visibility.Collapsed;
        BrowserHost.Visibility = string.IsNullOrEmpty(tab.Url) ? Visibility.Collapsed : Visibility.Visible;
    }

    static string Host(string url)
    {
        try { return new Uri(url).Host.Replace("www.", ""); }
        catch { return "New tab"; }
    }

    string Resolve(string input)
    {
        string value = input.Trim();
        if (value.Length == 0) return "https://www.google.com";
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https")) return uri.ToString();
        if (!value.Contains(' ') && value.Contains('.')) return "https://" + value;
        string search = SearchTemplate();
        return search.Replace("{query}", Uri.EscapeDataString(value));
    }

    string SearchTemplate() => preferences.SearchEngine switch
    {
        "Bing" => "https://www.bing.com/search?q={query}",
        "DuckDuckGo" => "https://duckduckgo.com/?q={query}",
        "Brave" => "https://search.brave.com/search?q={query}",
        "Custom" => string.IsNullOrWhiteSpace(preferences.SearchUrl) ? "https://www.google.com/search?q={query}" : preferences.SearchUrl,
        _ => "https://www.google.com/search?q={query}"
    };

    void SearchEngine_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchEngineBox.SelectedItem is not ComboBoxItem selected) return;
        preferences.SearchEngine = selected.Tag?.ToString() ?? "Google";
        SavePreferences();
    }

    void BuildSearchEngineIcons()
    {
        SearchEngineBox.Style = (Style)FindResource("SearchEnginePicker");
        SearchEngineBox.Width = 48;
        SearchEngineBox.Height = 26;
        SearchEngineBox.Padding = new Thickness(0);
        SearchEngineBox.HorizontalContentAlignment = HorizontalAlignment.Center;
        SearchEngineBox.VerticalContentAlignment = VerticalAlignment.Center;
        SearchEngineBox.ToolTip = "Choose search engine";
        SearchEngineBox.Items.Clear();
        foreach (string engine in new[] { "Google", "Bing", "DuckDuckGo", "Brave", "Custom" })
            SearchEngineBox.Items.Add(new ComboBoxItem { Content = SearchEngineIcon(engine), Tag = engine, ToolTip = engine, Width = 44, Height = 24, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center });
    }

    static FrameworkElement SearchEngineIcon(string engine)
    {
        if (engine == "Custom")
        {
            var customIcon = new Grid { Width = 20, Height = 20, ToolTip = engine };
            customIcon.Children.Add(new Path { Data = Geometry.Parse("M 3,5 L 17,5 M 3,10 L 17,10 M 3,15 L 17,15"), Stroke = new SolidColorBrush(Color.FromRgb(151, 126, 235)), StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            customIcon.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(151, 126, 235)), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 0, 0, 10) });
            customIcon.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(151, 126, 235)), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0) });
            return customIcon;
        }

        // Use each provider's real favicon so the menu shows the actual brand mark.
        // The favicon endpoint returns a cached 64px PNG, suitable for the compact picker.
        string domain = engine switch
        {
            "Google" => "google.com",
            "Bing" => "bing.com",
            "DuckDuckGo" => "duckduckgo.com",
            "Brave" => "search.brave.com",
            _ => "google.com"
        };
        return new Image
        {
            Width = 18,
            Height = 18,
            ToolTip = engine,
            Stretch = Stretch.Uniform,
            Source = new BitmapImage(new Uri($"https://www.google.com/s2/favicons?domain={domain}&sz=64", UriKind.Absolute))
        };
    }

    void Navigate(string value, BrowserTab? tab = null)
    {
        SuggestionPopup.IsOpen = false;
        tab ??= activeTab;
        if (tab == null) return;
        string trimmed = value.Trim();
        if (!privateMode && trimmed.Length > 1 && !trimmed.Contains('.') && !trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            preferences.RecentSearches.RemoveAll(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase));
            preferences.RecentSearches.Insert(0, trimmed);
            if (preferences.RecentSearches.Count > 30) preferences.RecentSearches.RemoveRange(30, preferences.RecentSearches.Count - 30);
            SavePreferences();
        }
        var url = Resolve(value);
        tab.Url = url;
        SetAddressText(url);
        NewTabPanel.Visibility = Visibility.Collapsed;
        BrowserHost.Visibility = Visibility.Visible;
        tab.View.Source = new Uri(url);
    }

    void SetAddressText(string value)
    {
        settingAddressText = true;
        AddressBox.Text = value;
        settingAddressText = false;
    }

    void RenderBookmarks()
    {
        BookmarkBar.Children.Clear();
        BookmarkBar.Visibility = preferences.ShowBookmarks ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in preferences.Bookmarks)
        {
            var button = new Button { Content = item.Title, Style = (Style)FindResource("ChromeButton"), FontSize = 11, Foreground = ParseBrush(preferences.Theme == "Light" ? "#343943" : "#C9CCD4", Brushes.White), ToolTip = item.Url };
            button.Click += (_, _) => Navigate(item.Url);
            button.ContextMenu = new ContextMenu();
            var remove = new MenuItem { Header = "Remove bookmark" };
            remove.Click += (_, _) => { preferences.Bookmarks.Remove(item); SavePreferences(); RenderBookmarks(); };
            button.ContextMenu.Items.Add(remove);
            BookmarkBar.Children.Add(button);
        }
    }

    void RenderShortcuts()
    {
        QuickLinks.Children.Clear();
        foreach (var item in preferences.Shortcuts)
        {
            var button = new Button { Content = item.Title, Style = (Style)FindResource("ChromeButton"), MinWidth = 84, MinHeight = 54, Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(preferences.Theme == "Light" ? Color.FromRgb(230, 233, 238) : Color.FromRgb(27, 31, 39)), Foreground = ParseBrush(preferences.Theme == "Light" ? "#343943" : "#E5E8ED", Brushes.White), ToolTip = item.Url };
            button.Click += (_, _) => Navigate(item.Url);
            var remove = new MenuItem { Header = "Remove shortcut" };
            remove.Click += (_, _) => { preferences.Shortcuts.Remove(item); SavePreferences(); RenderShortcuts(); };
            button.ContextMenu = new ContextMenu();
            button.ContextMenu.Items.Add(remove);
            QuickLinks.Children.Add(button);
        }
    }

    void AddShortcut_Click(object sender, RoutedEventArgs e)
    {
        string? title = Ask("Shortcut name", "Add a shortcut");
        if (string.IsNullOrWhiteSpace(title)) return;
        string? url = Ask("Website address", "Add a shortcut");
        if (string.IsNullOrWhiteSpace(url)) return;
        preferences.Shortcuts.Add(new Shortcut { Title = title.Trim(), Url = Resolve(url) });
        SavePreferences();
        RenderShortcuts();
    }

    string? Ask(string prompt, string title)
    {
        var dialog = new Window { Title = title, Owner = this, Width = 390, Height = 170, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(25, 28, 36)), Foreground = Brushes.White, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox { Padding = new Thickness(8) };
        panel.Children.Add(input);
        var submit = new Button { Content = "Save", IsDefault = true, Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        submit.Click += (_, _) => dialog.DialogResult = true;
        panel.Children.Add(submit);
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? input.Text : null;
    }

    void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (activeTab == null || string.IsNullOrEmpty(activeTab.Url)) return;
        var existing = preferences.Bookmarks.FirstOrDefault(x => x.Url == activeTab.Url);
        if (existing != null) preferences.Bookmarks.Remove(existing);
        else preferences.Bookmarks.Add(new Bookmark { Title = activeTab.Title, Url = activeTab.Url });
        SavePreferences();
        RenderBookmarks();
        UpdateBookmarkButton();
    }

    void UpdateBookmarkButton()
    {
        bool saved = activeTab != null && preferences.Bookmarks.Any(x => x.Url == activeTab.Url);
        StarButton.Foreground = ParseBrush(saved ? preferences.Accent : (preferences.Theme == "Light" ? "#586475" : "#C9CCD4"), Brushes.White);
        StarButton.ToolTip = saved ? "Remove bookmark" : "Bookmark current page";
    }

    void History_Click(object sender, RoutedEventArgs e)
    {
        if (privateMode)
        {
            MessageBox.Show("History is not available in this incognito window.", "Trixter Incognito", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new Window { Title = "Browsing history", Owner = this, Width = 600, Height = 680, MinWidth = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(17, 20, 27)), Foreground = new SolidColorBrush(Color.FromRgb(239, 242, 247)) };
        var layout = new DockPanel { Margin = new Thickness(22) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = "History", FontSize = 23, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Recent pages and searches are stored on this PC.", Foreground = new SolidColorBrush(Color.FromRgb(161, 170, 183)), FontSize = 11, Margin = new Thickness(0, 3, 0, 10) });
        var filter = new TextBox { Padding = new Thickness(9), Background = new SolidColorBrush(Color.FromRgb(27, 32, 41)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(52, 60, 72)) };
        header.Children.Add(filter);
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var entries = new StackPanel();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var clear = new Button { Content = "Clear pages and searches", Padding = new Thickness(12, 8, 12, 8) };
        clear.Click += (_, _) => { preferences.RecentPages.Clear(); preferences.RecentSearches.Clear(); SavePreferences(); Render(); };
        var close = new Button { Content = "Done", Padding = new Thickness(15, 8, 15, 8), Margin = new Thickness(8, 0, 0, 0) };
        close.Click += (_, _) => dialog.Close();
        footer.Children.Add(clear); footer.Children.Add(close); DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var scroll = new ScrollViewer { Content = entries, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        layout.Children.Add(scroll);

        void AddRow(string title, string detail, Action open, Action remove)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), Background = new SolidColorBrush(Color.FromRgb(27, 32, 41)) };
            var removeButton = new Button { Content = "x", ToolTip = "Remove", Padding = new Thickness(10, 6, 10, 6), Background = Brushes.Transparent, Foreground = new SolidColorBrush(Color.FromRgb(190, 199, 211)), BorderThickness = new Thickness(0) };
            removeButton.Click += (_, _) => { remove(); SavePreferences(); Render(); };
            DockPanel.SetDock(removeButton, Dock.Right); row.Children.Add(removeButton);
            var openButton = new Button { Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 7, 10, 7), Content = new StackPanel { Children = { new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 12 }, new TextBlock { Text = detail, Foreground = new SolidColorBrush(Color.FromRgb(161, 170, 183)), FontSize = 10, Margin = new Thickness(0, 2, 0, 0) } } } };
            openButton.Click += (_, _) => { open(); dialog.Close(); };
            row.Children.Add(openButton); entries.Children.Add(new Border { Child = row, CornerRadius = new CornerRadius(8) });
        }

        void Render()
        {
            entries.Children.Clear();
            string query = filter.Text.Trim();
            entries.Children.Add(new TextBlock { Text = "RECENT PAGES", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(161, 170, 183)), Margin = new Thickness(2, 5, 0, 5) });
            foreach (var page in preferences.RecentPages.ToList().Where(x => query.Length == 0 || Matches(x.Title, x.Url, query)))
                AddRow(page.Title, page.Url, () => { if (activeTab != null) Navigate(page.Url); }, () => preferences.RecentPages.Remove(page));
            entries.Children.Add(new TextBlock { Text = "RECENT SEARCHES", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(161, 170, 183)), Margin = new Thickness(2, 14, 0, 5) });
            foreach (var term in preferences.RecentSearches.ToList().Where(x => query.Length == 0 || x.Contains(query, StringComparison.OrdinalIgnoreCase)))
                AddRow(term, "Search phrase", () => { if (activeTab != null) Navigate(term); }, () => preferences.RecentSearches.RemoveAll(x => x == term));
        }
        filter.TextChanged += (_, _) => Render();
        Render();
        dialog.Content = layout;
        dialog.ShowDialog();
    }

    void NewTab_Click(object sender, RoutedEventArgs e) => _ = AddTab();

    async void Extensions_Click(object sender, RoutedEventArgs e)
    {
        var profile = activeTab?.View.CoreWebView2?.Profile;
        if (profile == null)
        {
            MessageBox.Show("Wait for a page or new tab to finish opening, then open Extensions again.", "Trixter Extensions");
            return;
        }

        bool light = preferences.Theme == "Light";
        var ink = ParseBrush(light ? "#263142" : "#E9EDF3", Brushes.White);
        var muted = ParseBrush(light ? "#687487" : "#9CA7B7", Brushes.Gray);
        var surface = ParseBrush(light ? "#F5F7FA" : "#171B23", Brushes.Black);
        var dialog = new Window { Title = "Extensions", Owner = this, Width = 480, Height = 430, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = surface, Foreground = ink, ResizeMode = ResizeMode.CanResizeWithGrip };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var install = new Button { Content = "Load unpacked…", Padding = new Thickness(13, 8, 13, 8), Margin = new Thickness(0, 0, 8, 0), Background = ParseBrush(preferences.Accent, Brushes.LightGreen), Foreground = light ? Brushes.White : Brushes.Black, BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold };
        var close = new Button { Content = "Done", Padding = new Thickness(13, 8, 13, 8), Background = ParseBrush(light ? "#E5E9EF" : "#2A303B", Brushes.Gray), Foreground = ink, BorderThickness = new Thickness(0) };
        footer.Children.Add(install); footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var list = new StackPanel();
        layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        close.Click += (_, _) => dialog.Close();

        async Task RefreshExtensions()
        {
            list.Children.Clear();
            IReadOnlyList<CoreWebView2BrowserExtension> extensions;
            try { extensions = await profile.GetBrowserExtensionsAsync(); }
            catch (Exception ex) { list.Children.Add(new TextBlock { Text = "Could not read installed extensions: " + ex.Message, Foreground = muted, TextWrapping = TextWrapping.Wrap }); return; }
            if (extensions.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "No extensions installed yet. Load an unpacked extension folder to get started.", Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 2, 12) });
                return;
            }
            foreach (var extension in extensions)
            {
                var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8), Background = ParseBrush(light ? "#FFFFFF" : "#222833", Brushes.Gray) };
                var remove = new Button { Content = "Remove", Tag = extension, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(4), Background = Brushes.Transparent, Foreground = ink, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
                remove.Click += async (_, _) =>
                {
                    if (remove.Tag is not CoreWebView2BrowserExtension current) return;
                    try { await current.RemoveAsync(); await RefreshExtensions(); }
                    catch (Exception ex) { MessageBox.Show("Could not remove the extension: " + ex.Message, "Trixter Extensions"); }
                };
                DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
                var enabled = new CheckBox { Content = extension.Name, IsChecked = extension.IsEnabled, Tag = extension, Foreground = ink, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 8, 5, 8), FontWeight = FontWeights.SemiBold };
                enabled.Checked += async (_, _) => await SetExtensionEnabled(extension, true);
                enabled.Unchecked += async (_, _) => await SetExtensionEnabled(extension, false);
                row.Children.Add(enabled);
                row.ToolTip = "Extension ID: " + extension.Id;
                list.Children.Add(row);
            }
        }

        async Task SetExtensionEnabled(CoreWebView2BrowserExtension extension, bool enabled)
        {
            try { await extension.EnableAsync(enabled); }
            catch (Exception ex) { MessageBox.Show("Could not update the extension: " + ex.Message, "Trixter Extensions"); }
        }

        install.Click += async (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose the extension's manifest.json", Filter = "Extension manifest|manifest.json|JSON files|*.json", FileName = "manifest.json", CheckFileExists = true };
            if (picker.ShowDialog(dialog) != true) return;
            string? folder = System.IO.Path.GetDirectoryName(picker.FileName);
            if (string.IsNullOrWhiteSpace(folder)) return;
            try
            {
                var added = await profile.AddBrowserExtensionAsync(folder);
                await RefreshExtensions();
                MessageBox.Show($"{added.Name} was added to this Trixter profile.", "Trixter Extensions", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show("Could not install this extension. Choose its unpacked folder containing manifest.json.\n\n" + ex.Message, "Trixter Extensions", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };

        dialog.Content = layout;
        await RefreshExtensions();
        dialog.ShowDialog();
    }

    void OpenBrowserWindow(bool incognito)
    {
        var window = new MainWindow(incognito);
        window.Show();
        window.Activate();
    }

    static async Task DeletePrivateProfileWhenReleased(string? profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath)) return;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try { if (System.IO.Directory.Exists(profilePath)) System.IO.Directory.Delete(profilePath, true); return; }
            catch { await Task.Delay(500); }
        }
    }
    void Back_Click(object sender, RoutedEventArgs e) { if (activeTab?.View.CoreWebView2?.CanGoBack == true) activeTab.View.CoreWebView2.GoBack(); }
    void Forward_Click(object sender, RoutedEventArgs e) { if (activeTab?.View.CoreWebView2?.CanGoForward == true) activeTab.View.CoreWebView2.GoForward(); }
    void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (activeTab == null) return;
        if (string.IsNullOrWhiteSpace(activeTab.Url))
        {
            SuggestionPopup.IsOpen = false;
            SetAddressText("");
            HomeSearch.Clear();
            RenderShortcuts();
            RenderBookmarks();
            NewTabPanel.Visibility = Visibility.Visible;
            BrowserHost.Visibility = Visibility.Collapsed;
            return;
        }
        activeTab.View.CoreWebView2?.Reload();
    }
    void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!settingAddressText) UpdateSuggestions();
    }

    void UpdateSuggestions()
    {
        if (!preferences.ShowSuggestions || !AddressBox.IsKeyboardFocused)
        {
            SuggestionPopup.IsOpen = false;
            return;
        }
        string query = AddressBox.Text.Trim();
        if (query.Length < 2) { SuggestionPopup.IsOpen = false; return; }
        var results = new List<AddressSuggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string title, string detail, string value, bool isSearch = false, bool isRecentSearch = false)
        {
            if (results.Count >= 8 || !seen.Add((isSearch ? "search:" : "link:") + value)) return;
            results.Add(new AddressSuggestion { Title = title, Detail = detail, Value = value, IsSearch = isSearch, IsRecentSearch = isRecentSearch });
        }
        if (!privateMode)
            foreach (string recent in preferences.RecentSearches)
                if (recent.Contains(query, StringComparison.OrdinalIgnoreCase)) Add(recent, "Recent search", recent, true, true);
        foreach (var bookmark in preferences.Bookmarks)
            if (Matches(bookmark.Title, bookmark.Url, query)) Add(bookmark.Title, "Bookmark  ·  " + bookmark.Url, bookmark.Url);
        foreach (var shortcut in preferences.Shortcuts)
            if (Matches(shortcut.Title, shortcut.Url, query)) Add(shortcut.Title, "Shortcut  ·  " + shortcut.Url, shortcut.Url);
        if (!privateMode)
            foreach (var page in preferences.RecentPages)
                if (Matches(page.Title, page.Url, query)) Add(page.Title, "Recently visited  ·  " + page.Url, page.Url);
        Add("Search for “" + query + "”", "Search the web", query, true);
        currentSuggestions = results;
        SuggestionList.ItemsSource = currentSuggestions;
        SuggestionList.SelectedIndex = -1;
        SuggestionPopup.IsOpen = results.Count > 0;
    }

    static bool Matches(string title, string url, string query) =>
        title.Contains(query, StringComparison.OrdinalIgnoreCase) || url.Contains(query, StringComparison.OrdinalIgnoreCase);

    void SelectSuggestion(AddressSuggestion suggestion)
    {
        SuggestionPopup.IsOpen = false;
        Navigate(suggestion.Value);
        Keyboard.ClearFocus();
    }

    void SuggestionList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source != null)
        {
            if (source is Button) return;
            source = VisualTreeHelper.GetParent(source);
        }
        if (SuggestionList.SelectedItem is AddressSuggestion suggestion) SelectSuggestion(suggestion);
    }

    void RemoveRecentSearch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AddressSuggestion suggestion })
        {
            preferences.RecentSearches.RemoveAll(x => string.Equals(x, suggestion.Value, StringComparison.OrdinalIgnoreCase));
            SavePreferences();
            UpdateSuggestions();
        }
        e.Handled = true;
    }

    void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && SuggestionPopup.IsOpen)
        {
            e.Handled = true;
            SuggestionList.SelectedIndex = (SuggestionList.SelectedIndex + 1) % currentSuggestions.Count;
            SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
        }
        else if (e.Key == Key.Up && SuggestionPopup.IsOpen)
        {
            e.Handled = true;
            SuggestionList.SelectedIndex = SuggestionList.SelectedIndex <= 0 ? currentSuggestions.Count - 1 : SuggestionList.SelectedIndex - 1;
            SuggestionList.ScrollIntoView(SuggestionList.SelectedItem);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (SuggestionPopup.IsOpen && SuggestionList.SelectedItem is AddressSuggestion suggestion) SelectSuggestion(suggestion);
            else { Navigate(AddressBox.Text); Keyboard.ClearFocus(); }
        }
        else if (e.Key == Key.Escape) SuggestionPopup.IsOpen = false;
    }
    void HomeSearch_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Navigate(HomeSearch.Text); }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        bool light = preferences.Theme == "Light";
        var bg = new SolidColorBrush(light ? Color.FromRgb(244, 246, 249) : Color.FromRgb(17, 20, 27));
        var card = new SolidColorBrush(light ? Colors.White : Color.FromRgb(27, 32, 41));
        var ink = new SolidColorBrush(light ? Color.FromRgb(32, 39, 49) : Color.FromRgb(239, 242, 247));
        var muted = new SolidColorBrush(light ? Color.FromRgb(83, 94, 108) : Color.FromRgb(161, 170, 183));
        var field = new SolidColorBrush(light ? Color.FromRgb(248, 249, 251) : Color.FromRgb(19, 23, 30));
        var outline = new SolidColorBrush(light ? Color.FromRgb(210, 216, 225) : Color.FromRgb(52, 60, 72));
        var accent = ParseBrush(preferences.Accent, Brushes.LightGreen);
        var dialog = new Window { Title = "Settings", Owner = this, Width = 520, Height = 740, MinHeight = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = bg, Foreground = ink, ResizeMode = ResizeMode.CanResizeWithGrip };
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(26, 22, 26, 18) };
        heading.Children.Add(new TextBlock { Text = "Settings", FontSize = 25, FontWeight = FontWeights.SemiBold, Foreground = ink });
        heading.Children.Add(new TextBlock { Text = "Shape Trixter around the way you browse.", FontSize = 12, Foreground = muted, Margin = new Thickness(0, 4, 0, 0) });
        layout.Children.Add(heading);

        var content = new StackPanel { Margin = new Thickness(22, 0, 22, 10) };
        string selectedTheme = preferences.Theme;
        string selectedDensity = preferences.Density;
        var hex = new TextBox { Text = preferences.Accent, Padding = new Thickness(9), Background = field, Foreground = ink, BorderBrush = outline };
        var themeChoices = new Grid { Margin = new Thickness(0, 4, 0, 10) };
        themeChoices.ColumnDefinitions.Add(new ColumnDefinition()); themeChoices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); themeChoices.ColumnDefinitions.Add(new ColumnDefinition());
        var darkChoice = MakeThemeChoice("Dark", false, selectedTheme == "Dark");
        var lightChoice = MakeThemeChoice("Light", true, selectedTheme == "Light");
        Grid.SetColumn(lightChoice, 2); themeChoices.Children.Add(darkChoice); themeChoices.Children.Add(lightChoice);
        void PickTheme(string value)
        {
            selectedTheme = value;
            var currentAccent = ParseBrush(hex.Text, accent);
            SetThemeChoiceState(darkChoice, selectedTheme == "Dark", currentAccent);
            SetThemeChoiceState(lightChoice, selectedTheme == "Light", currentAccent);
        }
        darkChoice.Click += (_, _) => PickTheme("Dark");
        lightChoice.Click += (_, _) => PickTheme("Light");
        var densityChoices = new Grid { Margin = new Thickness(0, 4, 0, 10) };
        densityChoices.ColumnDefinitions.Add(new ColumnDefinition());
        densityChoices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        densityChoices.ColumnDefinitions.Add(new ColumnDefinition());
        densityChoices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        densityChoices.ColumnDefinitions.Add(new ColumnDefinition());
        var compactChoice = MakeDensityChoice("Compact", selectedDensity == "Compact", ink, muted, outline);
        var balancedChoice = MakeDensityChoice("Balanced", selectedDensity == "Balanced", ink, muted, outline);
        var roomyChoice = MakeDensityChoice("Roomy", selectedDensity == "Roomy", ink, muted, outline);
        Grid.SetColumn(balancedChoice, 2); Grid.SetColumn(roomyChoice, 4);
        densityChoices.Children.Add(compactChoice); densityChoices.Children.Add(balancedChoice); densityChoices.Children.Add(roomyChoice);
        void PickDensity(string value)
        {
            selectedDensity = value;
            var currentAccent = ParseBrush(hex.Text, accent);
            SetDensityChoiceState(compactChoice, selectedDensity == "Compact", currentAccent, outline);
            SetDensityChoiceState(balancedChoice, selectedDensity == "Balanced", currentAccent, outline);
            SetDensityChoiceState(roomyChoice, selectedDensity == "Roomy", currentAccent, outline);
        }
        compactChoice.Click += (_, _) => PickDensity("Compact");
        balancedChoice.Click += (_, _) => PickDensity("Balanced");
        roomyChoice.Click += (_, _) => PickDensity("Roomy");
        var wheel = CreateColorWheel();
        var wheelMarker = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent, IsHitTestVisible = false };
        wheel.Children.Add(wheelMarker);
        void PlaceWheelMarker(Color color)
        {
            Point position = ColorToWheelPoint(color, wheel.Width, wheel.Height);
            Canvas.SetLeft(wheelMarker, position.X - wheelMarker.Width / 2);
            Canvas.SetTop(wheelMarker, position.Y - wheelMarker.Height / 2);
        }
        PlaceWheelMarker(ParseColor(preferences.Accent));
        void PickWheelColor(object? _, MouseEventArgs e)
        {
            Point point = e.GetPosition(wheel);
            Color picked = ColorAtWheelPoint(point, wheel.Width, wheel.Height);
            hex.Text = picked.ToString();
            PlaceWheelMarker(picked);
        }
        wheel.MouseLeftButtonDown += (s, e) => { wheel.CaptureMouse(); PickWheelColor(s, e); e.Handled = true; };
        wheel.MouseMove += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) PickWheelColor(s, e); };
        wheel.MouseLeftButtonUp += (_, _) => wheel.ReleaseMouseCapture();
        hex.TextChanged += (_, _) =>
        {
            if (!IsColor(hex.Text)) return;
            Color color = ParseColor(hex.Text);
            PlaceWheelMarker(color);
            var selectedAccent = new SolidColorBrush(color);
            SetThemeChoiceState(darkChoice, selectedTheme == "Dark", selectedAccent);
            SetThemeChoiceState(lightChoice, selectedTheme == "Light", selectedAccent);
            SetDensityChoiceState(compactChoice, selectedDensity == "Compact", selectedAccent, outline);
            SetDensityChoiceState(balancedChoice, selectedDensity == "Balanced", selectedAccent, outline);
            SetDensityChoiceState(roomyChoice, selectedDensity == "Roomy", selectedAccent, outline);
        };
        var swatches = new WrapPanel { Margin = new Thickness(-4, 3, 0, 8) };
        foreach (string color in new[] { "#B6F36A", "#83C9FF", "#C2A0FF", "#FF9E80", "#FFCF70", "#FF80AA", "#66DBC7" })
        {
            var swatch = new Button { Content = " ", Width = 27, Height = 27, Margin = new Thickness(4), Background = ParseBrush(color, Brushes.White), BorderThickness = new Thickness(0), ToolTip = color };
            swatch.Click += (_, _) => { hex.Text = color; PlaceWheelMarker(ParseColor(color)); };
            swatches.Children.Add(swatch);
        }
        var bookmarkCheck = new CheckBox { Content = "Show bookmarks bar", IsChecked = preferences.ShowBookmarks, Foreground = ink, Margin = new Thickness(0, 0, 0, 3) };
        var shortcutCheck = new CheckBox { Content = "Show shortcuts on start page", IsChecked = preferences.ShowShortcuts, Foreground = ink, Margin = new Thickness(0, 0, 0, 3) };
        var suggestionCheck = new CheckBox { Content = "Show address suggestions while typing", IsChecked = preferences.ShowSuggestions, Foreground = ink, Margin = new Thickness(0, 0, 0, 3) };
        var home = new TextBox { Text = preferences.HomePage, Padding = new Thickness(9), Background = field, Foreground = ink, BorderBrush = outline };
        var search = new TextBox { Text = preferences.SearchUrl, Padding = new Thickness(9), Background = field, Foreground = ink, BorderBrush = outline };
        string selectedBackgroundPath = preferences.StartPageBackground;
        var backgroundStatus = new TextBlock { Text = string.IsNullOrWhiteSpace(selectedBackgroundPath) ? "No background image" : System.IO.Path.GetFileName(selectedBackgroundPath), Foreground = muted, FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var chooseBackground = new Button { Content = "Choose image…", Padding = new Thickness(11, 7, 11, 7), Foreground = ink, Background = field, BorderBrush = outline, BorderThickness = new Thickness(1), Cursor = Cursors.Hand };
        chooseBackground.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose a start page background", Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*", CheckFileExists = true };
            if (picker.ShowDialog(dialog) == true)
            {
                selectedBackgroundPath = picker.FileName;
                backgroundStatus.Text = System.IO.Path.GetFileName(selectedBackgroundPath);
            }
        };
        var clearBackground = new Button { Content = "Remove", Padding = new Thickness(10, 7, 10, 7), Foreground = ink, Background = field, BorderBrush = outline, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Margin = new Thickness(7, 0, 0, 0) };
        clearBackground.Click += (_, _) => { selectedBackgroundPath = ""; backgroundStatus.Text = "No background image"; };
        var backgroundControls = new StackPanel { Orientation = Orientation.Horizontal };
        backgroundControls.Children.Add(chooseBackground); backgroundControls.Children.Add(clearBackground); backgroundControls.Children.Add(backgroundStatus);

        content.Children.Add(SettingsCard("Appearance", "Theme and color choices", new UIElement[] { SettingLabel("COLOR MODE", muted), themeChoices, SettingLabel("ACCENT COLOR", muted), swatches, hex, new TextBlock { Text = "Drag on the wheel to pick a color", FontSize = 10, Foreground = muted, Margin = new Thickness(0, 6, 0, 3) }, wheel, SettingLabel("INTERFACE LAYOUT", muted), densityChoices }, card, outline, ink, muted));
        content.Children.Add(SettingsCard("Start page", "Choose what appears when you open a new tab", new UIElement[] { shortcutCheck, bookmarkCheck, suggestionCheck, SettingLabel("BACKGROUND IMAGE", muted), backgroundControls, SettingLabel("Homepage (blank uses Trixter start page)", muted), home, SettingLabel("Search URL template (include {query})", muted), search }, card, outline, ink, muted));
        content.Children.Add(SettingsCard("Shortcuts and bookmarks", "Add shortcuts from the start page. Right-click an item to remove it.", Array.Empty<UIElement>(), card, outline, ink, muted));
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);

        var footer = new DockPanel { Margin = new Thickness(22, 12, 22, 18), LastChildFill = false };
        var savedStatus = new TextBlock { Text = "", Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 9, 14, 9), Foreground = ink, Background = field, BorderBrush = outline, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => dialog.Close();
        var save = new Button { Content = "Save changes", Padding = new Thickness(17, 9, 17, 9), Foreground = light ? Brushes.White : new SolidColorBrush(Color.FromRgb(23, 29, 18)), Background = accent, BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold };
        save.Click += (_, _) =>
        {
            if (!IsColor(hex.Text)) { MessageBox.Show("Enter a valid color such as #83C9FF.", "Settings"); return; }
            preferences.Theme = selectedTheme;
            preferences.Density = selectedDensity;
            preferences.Accent = hex.Text.Trim();
            preferences.ShowBookmarks = bookmarkCheck.IsChecked == true;
            preferences.ShowShortcuts = shortcutCheck.IsChecked == true;
            preferences.ShowSuggestions = suggestionCheck.IsChecked == true;
            preferences.HomePage = home.Text.Trim();
            if (!string.IsNullOrWhiteSpace(selectedBackgroundPath))
            {
                if (!System.IO.File.Exists(selectedBackgroundPath)) { MessageBox.Show("The selected background image could not be found.", "Settings"); return; }
                try
                {
                    string extension = System.IO.Path.GetExtension(selectedBackgroundPath);
                    string destination = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(settingsPath)!, "start-page-background" + extension);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsPath)!);
                    if (!string.Equals(System.IO.Path.GetFullPath(selectedBackgroundPath), System.IO.Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                        System.IO.File.Copy(selectedBackgroundPath, destination, true);
                    preferences.StartPageBackground = destination;
                }
                catch (Exception ex) { MessageBox.Show("Could not save the background image: " + ex.Message, "Settings"); return; }
            }
            else preferences.StartPageBackground = "";
            string newSearchUrl = string.IsNullOrWhiteSpace(search.Text) ? "https://www.google.com/search?q={query}" : search.Text.Trim();
            if (!string.Equals(newSearchUrl, preferences.SearchUrl, StringComparison.Ordinal))
            {
                preferences.SearchUrl = newSearchUrl;
                preferences.SearchEngine = "Custom";
                SearchEngineBox.SelectedItem = SearchEngineBox.Items.OfType<ComboBoxItem>().First(x => x.Tag?.ToString() == "Custom");
            }
            SavePreferences(); ApplyAppearance(); RenderBookmarks(); RenderShortcuts(); savedStatus.Text = "Changes saved";
        };
        footer.Children.Add(savedStatus); footer.Children.Add(cancel); footer.Children.Add(save); Grid.SetRow(footer, 2); layout.Children.Add(footer);
        dialog.Content = layout;
        dialog.ShowDialog();
    }

    static TextBlock SettingLabel(string text, Brush muted) => new() { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = muted, Margin = new Thickness(0, 9, 0, 5) };

    Button MakeDensityChoice(string title, bool selected, Brush ink, Brush muted, Brush outline)
    {
        var preview = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        preview.Children.Add(new Border { Height = title == "Compact" ? 5 : title == "Balanced" ? 8 : 11, Background = ParseBrush(preferences.Accent, Brushes.LightGreen), CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 0, 3, 3) });
        preview.Children.Add(new Border { Height = title == "Compact" ? 7 : title == "Balanced" ? 11 : 15, Background = new SolidColorBrush(preferences.Theme == "Light" ? Color.FromRgb(222, 228, 236) : Color.FromRgb(58, 67, 81)), CornerRadius = new CornerRadius(3), Margin = new Thickness(3, 0, 3, 0) });
        var sample = new Border { Height = 36, Background = new SolidColorBrush(preferences.Theme == "Light" ? Color.FromRgb(246, 248, 251) : Color.FromRgb(19, 23, 30)), CornerRadius = new CornerRadius(6), Padding = new Thickness(5), Child = preview };
        var stack = new StackPanel();
        stack.Children.Add(sample);
        stack.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ink, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 7, 0, 1) });
        stack.Children.Add(new TextBlock { Text = title == "Compact" ? "More space" : title == "Balanced" ? "Everyday" : "Room to breathe", FontSize = 9, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Center });
        var frame = new Border { Child = stack, Background = new SolidColorBrush(preferences.Theme == "Light" ? Colors.White : Color.FromRgb(27, 32, 41)), BorderBrush = selected ? ParseBrush(preferences.Accent, Brushes.LightGreen) : outline, BorderThickness = new Thickness(selected ? 2 : 1), CornerRadius = new CornerRadius(10), Padding = new Thickness(7) };
        return new Button { Content = frame, Tag = frame, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Cursor = Cursors.Hand, ToolTip = title + " interface layout" };
    }

    static void SetDensityChoiceState(Button button, bool selected, Brush accent, Brush outline)
    {
        if (button.Tag is Border frame)
        {
            frame.BorderBrush = selected ? accent : outline;
            frame.BorderThickness = new Thickness(selected ? 2 : 1);
        }
    }

    Button MakeThemeChoice(string title, bool light, bool selected)
    {
        Color previewBg = light ? Color.FromRgb(245, 247, 250) : Color.FromRgb(17, 20, 27);
        Color previewBar = light ? Colors.White : Color.FromRgb(33, 39, 49);
        Brush previewInk = new SolidColorBrush(light ? Color.FromRgb(38, 48, 63) : Color.FromRgb(235, 239, 245));
        var sample = new StackPanel();
        sample.Children.Add(new Border { Height = 14, CornerRadius = new CornerRadius(5, 5, 0, 0), Background = new SolidColorBrush(previewBar), Child = new TextBlock { Text = "TRIXTER     tab     tab", FontSize = 7, Foreground = previewInk, Margin = new Thickness(7, 1, 0, 0) } });
        sample.Children.Add(new Border { Height = 37, CornerRadius = new CornerRadius(0, 0, 5, 5), Background = new SolidColorBrush(previewBg), Padding = new Thickness(7), Child = new Border { Background = light ? Brushes.White : new SolidColorBrush(Color.FromRgb(37, 44, 55)), CornerRadius = new CornerRadius(4), BorderBrush = light ? new SolidColorBrush(Color.FromRgb(205, 213, 223)) : new SolidColorBrush(Color.FromRgb(61, 72, 87)), BorderThickness = new Thickness(1) } });
        sample.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = previewInk, Margin = new Thickness(1, 8, 0, 0) });
        sample.Children.Add(new TextBlock { Text = light ? "Bright and crisp" : "Soft and focused", FontSize = 10, Foreground = previewInk, Opacity = .75 });
        var frame = new Border { Child = sample, Background = light ? Brushes.White : new SolidColorBrush(Color.FromRgb(27, 32, 41)), BorderBrush = selected ? ParseBrush(preferences.Accent, Brushes.LightGreen) : new SolidColorBrush(Color.FromRgb(72, 82, 96)), BorderThickness = new Thickness(selected ? 2 : 1), CornerRadius = new CornerRadius(10), Padding = new Thickness(9) };
        return new Button { Content = frame, Tag = frame, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(0), Cursor = Cursors.Hand, ToolTip = title + " theme" };
    }

    static void SetThemeChoiceState(Button button, bool selected, Brush accent)
    {
        if (button.Tag is Border frame)
        {
            frame.BorderBrush = selected ? accent : new SolidColorBrush(Color.FromRgb(72, 82, 96));
            frame.BorderThickness = new Thickness(selected ? 2 : 1);
        }
    }

    static Border SettingsCard(string title, string description, IEnumerable<UIElement> controls, Brush background, Brush outline, Brush text, Brush muted)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = text, Margin = new Thickness(0, 0, 0, 3) });
        stack.Children.Add(new TextBlock { Text = description, FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 11) });
        foreach (var item in controls) { if (item is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 5); stack.Children.Add(item); }
        return new Border { Background = background, BorderBrush = outline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 12), Child = stack };
    }

    static bool IsColor(string value) { try { _ = ColorConverter.ConvertFromString(value); return true; } catch { return false; } }

    IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var monitorInfo = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;

        var limits = Marshal.PtrToStructure<NativeMinMaxInfo>(lParam);
        limits.MaxPosition.X = monitorInfo.Work.Left - monitorInfo.Monitor.Left;
        limits.MaxPosition.Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top;
        limits.MaxSize.X = monitorInfo.Work.Right - monitorInfo.Work.Left;
        limits.MaxSize.Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
        Marshal.StructureToPtr(limits, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current != null)
        {
            if (current is Button) return;
            current = VisualTreeHelper.GetParent(current);
        }
        if (e.ClickCount == 2) { Maximize_Click(sender, new RoutedEventArgs()); e.Handled = true; }
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    void HandleShortcuts(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key == Key.N) { e.Handled = true; OpenBrowserWindow(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.H) { e.Handled = true; History_Click(sender, new RoutedEventArgs()); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.T) { e.Handled = true; _ = AddTab(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W && activeTab != null) { e.Handled = true; CloseTab(activeTab); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L) { e.Handled = true; AddressBox.Focus(); AddressBox.SelectAll(); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R) { e.Handled = true; Reload_Click(sender, new RoutedEventArgs()); }
        else if (e.Key == Key.Escape) Keyboard.ClearFocus();
    }
}
