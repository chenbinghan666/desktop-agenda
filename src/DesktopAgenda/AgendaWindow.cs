using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

internal sealed partial class AgendaWindow : Window
{
    private enum Mode { Day, Week, Month, List }
    private sealed record Entry(DateTime Date, string Time, string Title, string Location, string Notes, int Start, int End)
    {
        public bool AllDay => Time == "全天";
    }

    private sealed record Theme(Brush Paper, Brush Surface, Brush Ink, Brush Muted, Brush Rule, Brush Sage,
        Brush Slate, Brush SelectedDay, Brush TodayWash, Brush TodayRule, Brush TodayInk, Brush NowRed);

    private static readonly Theme Light = new(Paint("#FCFCFD"), Brushes.White, Paint("#202631"), Paint("#626C7D"),
        Paint("#E4E9F0"), Paint("#335D57"), Paint("#345784"), Paint("#F1D9B7"), Paint("#FAEFE0"),
        Paint("#E4D2BB"), Paint("#70491F"), Paint("#D64C55"));
    private static readonly Theme Dark = new(Paint("#202329"), Paint("#292D34"), Paint("#EEF0F4"), Paint("#ADB6C5"),
        Paint("#393F49"), Paint("#A1CBBF"), Paint("#A8C1EC"), Paint("#493E59"), Paint("#302A3A"),
        Paint("#51475E"), Paint("#E2D5F0"), Paint("#F1747D"));
    private static readonly Brush LightDayDivider = Paint("#D6DDE7");
    private static readonly Brush DarkDayDivider = Paint("#49515E");
    private static readonly FontFamily LatinFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily ChineseFont = new("Microsoft YaHei UI");
    private const double LocationFontSize = 12;
    private Theme theme = Light;
    private Brush Paper => theme.Paper;
    private Brush Ink => theme.Ink;
    private Brush Muted => theme.Muted;
    private Brush Rule => theme.Rule;
    private Brush Sage => theme.Sage;
    private Brush Slate => theme.Slate;
    private Brush TodayWash => theme.TodayWash;

    private readonly string? fixture;
    private readonly bool? previewDark;
    private readonly string settings = Path.Combine(Program.DirectoryPath, "window.txt");
    private readonly Grid root = new();
    private readonly Border frame;
    private TextBlock statusLabel = new();
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer saveLayoutDelay = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Popup preview = new()
    {
        AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, Placement = PlacementMode.Bottom,
        StaysOpen = true, IsHitTestVisible = false
    };
    private Forms.NotifyIcon? tray;
    private DesktopLayer? desktopLayer;
    private DisplayOptions display = DisplayOptions.Load();
    private Brush timedColor = Brushes.LightSteelBlue;
    private Brush allDayColor = Brushes.LightSeaGreen;
    private Brush timedStripe = Brushes.SlateBlue;
    private Brush allDayStripe = Brushes.SeaGreen;
    private ILookup<DateTime, Entry> entries = Array.Empty<Entry>().ToLookup(item => item.Date);
    private DateTime selected = DateTime.Today;
    private DateTime renderedDay;
    private DateTime nextSync;
    private Mode mode = Mode.Week;
    private Mode? lastRenderedMode;
    private string? lastFile;
    private string? lastRows;
    private string status = "loading";
    private string syncAt = "";
    private bool english;
    private bool syncing;
    private bool settingsOpen;
    private (Button Card, Func<FrameworkElement> Content, bool Compact)? hovered;
    private Action? updateNow;
    private int lastMinute = -1;

    public AgendaWindow(string? fixturePath = null, bool? darkFixture = null)
    {
        fixture = fixturePath;
        previewDark = darkFixture;
        Width = 1060; Height = 640; MinWidth = 640; MinHeight = 390;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = fixture != null;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Foreground = Ink;
        FontFamily = ChineseFont;
        FontSize = 14;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(8),
            GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0)
        });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frame = new Border
        {
            Background = Paper, BorderBrush = Rule, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(2), Child = root
        };
        Content = frame;
        if (fixture == null)
        {
            LoadWindow();
            SourceInitialized += (_, _) => desktopLayer = new DesktopLayer(this);
        }
        ApplyStartupView();

        hoverDelay.Tick += (_, _) =>
        {
            hoverDelay.Stop();
            if (hovered is not { } target || !target.Card.IsMouseOver) return;
            preview.PlacementTarget = target.Card;
            var card = new Border
            {
                Child = target.Content(), Width = target.Compact ? double.NaN : 338,
                Padding = target.Compact ? new Thickness(11, 7, 11, 7) : new Thickness(17, 14, 17, 14),
                Background = theme.Surface, BorderBrush = Rule, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Margin = new Thickness(10, 5, 10, 15),
                Effect = new DropShadowEffect { BlurRadius = 17, ShadowDepth = 4, Opacity = .17 },
                IsHitTestVisible = false
            };
            preview.Child = card;
            preview.IsOpen = true;
        };
        Deactivated += (_, _) => DismissPreview();
        LocationChanged += (_, _) => { DismissPreview(); QueueLayoutSave(); };
        SizeChanged += (_, _) => { DismissPreview(); QueueLayoutSave(); };
        PreviewMouseWheel += (_, _) => DismissPreview();
        Loaded += async (_, _) =>
        {
            ReadLocal();
            poll.Start();
            if (fixture == null)
            {
                AddTray();
                SendToDesktop();
                await Refresh();
            }
        };
        poll.Tick += async (_, _) =>
        {
            ReadLocal();
            if (fixture == null && DateTime.Now >= nextSync) await Refresh();
        };
        saveLayoutDelay.Tick += (_, _) =>
        {
            saveLayoutDelay.Stop();
            SaveWindow();
        };
        Closing += (_, _) =>
        {
            saveLayoutDelay.Stop();
            SaveWindow();
        };
        Closed += (_, _) => { poll.Stop(); DismissPreview(); desktopLayer?.Dispose(); tray?.Dispose(); };
    }

    private void AddTray()
    {
        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application, Visible = true,
            Text = "桌面日历 · Desktop Calendar", ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        tray.ContextMenuStrip.Items.Add("显示日历 / Show calendar", null, (_, _) => Raise());
        tray.ContextMenuStrip.Items.Add("退出 / Exit", null, (_, _) => Close());
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) Raise(); };
        Deactivated += (_, _) => SendToDesktop();
    }

    private void Raise()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void SendToDesktop()
    {
        if (!settingsOpen) desktopLayer?.Lower();
    }

    private void OpenSettings()
    {
        if (settingsOpen) return;
        DismissPreview();
        settingsOpen = true;
        if (desktopLayer is not null) desktopLayer.DialogOpen = true;
        try
        {
            Program.ShowSettings(new WindowInteropHelper(this).Handle);
        }
        finally
        {
            settingsOpen = false;
            if (desktopLayer is not null) desktopLayer.DialogOpen = false;
            if (!IsActive) SendToDesktop();
            ReadLocal();
        }
    }

    private void ApplyStartupView()
    {
        if (Enum.TryParse(display.StartView, out Mode startup) && Enum.IsDefined(startup)) mode = startup;
    }

    private void LoadWindow()
    {
        var parts = Read(settings).Trim().Split('|');
        if (parts.Length != 5) return;
        if (Enum.TryParse(parts[4], out Mode previous) && Enum.IsDefined(previous)) mode = previous;
        var dimensions = parts.Take(4).Select(value =>
            double.TryParse(value, CultureInfo.InvariantCulture, out double result) ? result : double.NaN).ToArray();
        var area = SystemParameters.WorkArea;
        if (dimensions.Any(value => !double.IsFinite(value)) ||
            !area.Contains(new Point(dimensions[0] + 20, dimensions[1] + 20))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = dimensions[0]; Top = dimensions[1];
        Width = Math.Clamp(dimensions[2], MinWidth, Math.Max(MinWidth, area.Right - Left));
        Height = Math.Clamp(dimensions[3], MinHeight, Math.Max(MinHeight, area.Bottom - Top));
    }

    private void SaveWindow()
    {
        if (fixture != null) return;
        var rect = RestoreBounds;
        if (rect.IsEmpty || !double.IsFinite(rect.Left) || !double.IsFinite(rect.Top) ||
            !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height)) return;
        try { Program.AtomicWrite(settings, [FormattableString.Invariant(
            $"{rect.Left}|{rect.Top}|{rect.Width}|{rect.Height}|{mode}")]); }
        catch (IOException) { }
    }

    private void QueueLayoutSave()
    {
        if (fixture != null || !IsLoaded) return;
        saveLayoutDelay.Stop();
        saveLayoutDelay.Start();
    }

    private static string Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (IOException) { return ""; }
    }

    private void ReadLocal()
    {
        var file = Read(fixture ?? Program.CachePath);
        bool useEnglish = Read(Program.LanguagePath).Trim() == "en";
        bool redraw = useEnglish != english || renderedDay != DateTime.Today;
        english = useEnglish;
        var systemTheme = (previewDark ?? (Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is 0)) ? Dark : Light;
        if (theme != systemTheme)
        {
            theme = systemTheme;
            redraw = true;
        }
        var options = DisplayOptions.Load();
        if (options != display)
        {
            bool changedView = options.StartView != display.StartView;
            display = options;
            if (changedView) ApplyStartupView();
            redraw = true;
        }
        if (file != lastFile)
        {
            lastFile = file;
            int newline = file.IndexOf('\n');
            var header = (newline < 0 ? file : file[..newline]).Trim().Split('\t');
            status = header[0].Length == 0 ? "loading" : header[0];
            syncAt = header.Length > 1 ? System.Text.RegularExpressions.Regex.Match(header[1], @"\d\d:\d\d").Value : "";
            var rows = newline < 0 ? "" : file[(newline + 1)..];
            if (rows != lastRows)
            {
                lastRows = rows;
                entries = rows.Split('\n').Select(Parse).OfType<Entry>().ToLookup(item => item.Date);
                redraw = true;
            }
        }
        if (redraw) Render();
        else UpdateStatus();
        int minute = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
        if (lastMinute != minute)
        {
            lastMinute = minute;
            updateNow?.Invoke();
        }
    }

    private static Entry? Parse(string row)
    {
        var columns = row.TrimEnd('\r').Split('\t');
        if (columns.Length < 4 || !DateTime.TryParseExact(columns[0], "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)) return null;
        int start = 0, end = 0;
        if (columns.Length >= 6 && int.TryParse(columns[4], out start) && int.TryParse(columns[5], out end) && end > start)
        {
            start = Math.Clamp(start, 0, 1439);
            end = Math.Clamp(end, start + 1, 1440);
        }
        else
        {
            var time = System.Text.RegularExpressions.Regex.Matches(columns[2], @"(\d\d):(\d\d)");
            int ReadMinute(int index) => int.Parse(time[index].Groups[1].Value) * 60 + int.Parse(time[index].Groups[2].Value);
            start = time.Count > 0 ? Math.Clamp(ReadMinute(0), 0, 1439) : 0;
            end = time.Count > 1 ? Math.Clamp(ReadMinute(1), start + 1, 1440) : Math.Min(1440, start + 30);
        }
        return new Entry(date, columns[2], columns[3], columns.Length > 6 ? columns[6] : "",
            columns.Length > 7 ? DecodeNotes(columns[7]) : "", start, end);
    }

    private static string DecodeNotes(string encoded)
    {
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded)); }
        catch (FormatException) { return ""; }
    }

    private async Task Refresh()
    {
        if (fixture != null || syncing) return;
        syncing = true;
        nextSync = DateTime.Now.AddMinutes(5);
        UpdateStatus();
        try { await Task.Run(Program.Sync); }
        catch (IOException) { status = "error"; }
        finally { syncing = false; }
        ReadLocal();
    }

    private string T(string zh, string en) => english ? en : zh;
    private string DateText(DateTime date, string zh, string en) =>
        date.ToString(T(zh, en), CultureInfo.GetCultureInfo(T("zh-CN", "en-US")));
    private string TimeText(Entry item) => item.AllDay ? T("全天", "All-day") : item.Time;
    private DateTime WeekStart(DateTime date) =>
        date.AddDays(-(((int)date.DayOfWeek + (display.SundayFirst ? 0 : 6)) % 7));

    private void Render()
    {
        DismissPreview();
        if (lastRenderedMode != mode)
        {
            lastRenderedMode = mode;
            QueueLayoutSave();
        }
        Opacity = display.OpacityPercent / 100.0;
        FontSize = display.TextSize;
        frame.Background = Paper;
        frame.BorderBrush = Rule;
        Foreground = Ink;
        var defaults = new DisplayOptions();
        timedColor = Paint(theme == Dark && display.TimedColor == defaults.TimedColor ? "#365477" : display.TimedColor);
        allDayColor = Paint(theme == Dark && display.AllDayColor == defaults.AllDayColor ? "#365F54" : display.AllDayColor);
        timedStripe = AccentStripe(timedColor);
        allDayStripe = AccentStripe(allDayColor);
        ResizeMode = display.PositionLocked ? ResizeMode.NoResize : ResizeMode.CanResize;
        WindowChrome.GetWindowChrome(this).ResizeBorderThickness = new Thickness(display.PositionLocked ? 0 : 8);
        updateNow = null;
        RememberTimelineOffset();
        renderedDay = DateTime.Today;
        Title = T("桌面日历", "Desktop Calendar");
        root.Children.Clear();
        root.Children.Add(Header());
        var view = mode switch
        {
            Mode.Week => Timeline(7),
            Mode.Month => Month(),
            Mode.Day => Timeline(1),
            _ => Agenda(30)
        };
        FrameworkElement body = mode is Mode.Day or Mode.Week ? view : new ScrollViewer
        {
            Content = view, Padding = new Thickness(16, 0, 16, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        statusLabel = Text("", 12, Muted);
        var footerContent = new DockPanel();
        if (!display.PositionLocked)
        {
            var resizeHint = Text("⌟", 14, Muted);
            DockPanel.SetDock(resizeHint, Dock.Right);
            footerContent.Children.Add(resizeHint);
        }
        footerContent.Children.Add(statusLabel);
        var footer = new Border { Child = footerContent, Padding = new Thickness(20, 9, 11, 10) };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        UpdateStatus();
    }

    private void TogglePositionLock()
    {
        try
        {
            var updated = DisplayOptions.Load() with { PositionLocked = !display.PositionLocked };
            updated.Save();
            display = updated;
            SaveWindow();
            Render();
        }
        catch (IOException)
        {
            statusLabel.Text = T("无法保存固定设置", "Could not save the position lock");
        }
    }

    private FrameworkElement Header()
    {
        var header = new DockPanel { Margin = new Thickness(16, 12, 16, 8) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(IconButton("\uE72C", T("立即同步", "Sync now"), async () => await Refresh()));
        tools.Children.Add(IconButton("\uE8A7", T("打开谷歌日历", "Open Google Calendar"), () =>
            Process.Start(new ProcessStartInfo("https://calendar.google.com/") { UseShellExecute = true })));
        tools.Children.Add(IconButton(display.PositionLocked ? "\uE72E" : "\uE785",
            display.PositionLocked ? T("解除固定", "Unlock position") : T("固定位置", "Lock position"),
            TogglePositionLock, display.PositionLocked));
        tools.Children.Add(IconButton("\uE713", T("设置与语言", "Settings and language"), OpenSettings));
        tools.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(tools, Dock.Right);
        header.Children.Add(tools);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        AddTab(Mode.Day, T("日", "Day")); AddTab(Mode.Week, T("周", "Week"));
        AddTab(Mode.Month, T("月", "Month")); AddTab(Mode.List, T("列表", "List"));
        void AddTab(Mode choice, string label) => tabs.Children.Add(ActionButton(label, label, () =>
        {
            mode = choice; Render();
        }, choice == mode));
        var segmented = new Border { Background = Rule, CornerRadius = new CornerRadius(9), Padding = new Thickness(3),
            Child = tabs, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(segmented, Dock.Right);
        header.Children.Add(segmented);
        var nav = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        nav.Children.Add(IconButton("\uE76B", T("上一段", "Previous"), () => Move(-1)));
        nav.Children.Add(ActionButton(T("今天", "Today"), T("回到今天", "Go to today"), () => { selected = DateTime.Today; Render(); }));
        nav.Children.Add(IconButton("\uE76C", T("下一段", "Next"), () => Move(1)));
        DockPanel.SetDock(nav, Dock.Left);
        header.Children.Add(nav);
        var heading = Text(Caption(), 21, Ink, true);
        heading.VerticalAlignment = VerticalAlignment.Center;
        heading.Margin = new Thickness(12, 0, 8, 0);
        var dragArea = new Border { Child = heading, Background = Brushes.Transparent };
        dragArea.MouseLeftButtonDown += (_, args) =>
        {
            if (display.PositionLocked) return;
            if (args.ClickCount == 2 && fixture != null)
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else if (WindowState == WindowState.Normal && args.ButtonState == MouseButtonState.Pressed) DragMove();
        };
        header.Children.Add(dragArea);
        return header;
    }

    private Button IconButton(string glyph, string hint, Action action, bool active = false)
    {
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = ActionButton(icon, hint, action, active);
        button.Width = 32;
        button.Height = 32;
        button.Padding = new Thickness(0);
        if (active) button.Background = theme.SelectedDay;
        if (active) button.Foreground = theme.TodayInk;
        return button;
    }

    private string Caption() => mode switch
    {
        Mode.Day => DateText(selected, "M月d日 ddd", "MMM d, ddd"),
        Mode.Month => DateText(selected, "yyyy年M月", "MMMM yyyy"),
        Mode.Week => DateText(WeekStart(selected), "M月d日", "MMM d") + " – " +
            DateText(WeekStart(selected).AddDays(6), "M月d日", "MMM d"),
        _ => DateText(selected, "M月d日", "MMM d") + " – " + DateText(selected.AddDays(29), "M月d日", "MMM d")
    };

    private void Move(int direction)
    {
        DateTime next = mode switch
        {
            Mode.Day => selected.AddDays(direction),
            Mode.Week => selected.AddDays(direction * 7),
            Mode.Month => selected.AddMonths(direction),
            _ => selected.AddDays(direction * 30)
        };
        var month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        if (next < month.AddMonths(-3) || next >= month.AddMonths(6)) return;
        selected = next;
        Render();
    }


    private FrameworkElement Month()
    {
        var first = new DateTime(selected.Year, selected.Month, 1);
        var monday = WeekStart(first);
        int weeks = ((first - monday).Days + DateTime.DaysInMonth(first.Year, first.Month) + 6) / 7;
        var grid = Columns();
        for (int row = 0; row <= weeks; row++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int column = 0; column < 7; column++)
        {
            var label = Text(DateText(monday.AddDays(column), "ddd", "ddd"), 13, Muted, true);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.Margin = new Thickness(0, 5, 0, 8);
            Grid.SetColumn(label, column);
            grid.Children.Add(label);
            for (int week = 0; week < weeks; week++)
            {
                var date = monday.AddDays(week * 7 + column);
                var panel = new StackPanel();
                var day = ActionButton(date.Day.ToString(), T("查看这一天", "Open day"), () => OpenDay(date), date == DateTime.Today);
                day.HorizontalAlignment = HorizontalAlignment.Left;
                panel.Children.Add(day);
                var items = entries[date].ToArray();
                foreach (var item in items.Take(2)) panel.Children.Add(EventCard(item, true));
                if (items.Length > 2)
                    panel.Children.Add(ActionButton(T($"另有 {items.Length - 2} 项", $"+{items.Length - 2} more"),
                        T("显示全部日程", "Show every event"), () => OpenDay(date)));
                AddCell(grid, panel, date, column, week + 1, 83, date.Month != selected.Month);
            }
        }
        return grid;
    }

    private static Grid Columns()
    {
        var grid = new Grid();
        for (int index = 0; index < 7; index++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        return grid;
    }

    private void AddCell(Grid grid, FrameworkElement child, DateTime date, int column, int row, double height, bool outside = false)
    {
        var cell = new Border
        {
            Child = child, Background = date == DateTime.Today ? TodayWash : outside ? Paper : theme.Surface,
            BorderBrush = Rule, BorderThickness = new Thickness(column == 0 ? 1 : 0, row <= 1 ? 1 : 0, 1, 1),
            MinHeight = height, Padding = new Thickness(6)
        };
        Grid.SetColumn(cell, column);
        Grid.SetRow(cell, row);
        grid.Children.Add(cell);
    }

    private FrameworkElement Agenda(int days)
    {
        var list = new StackPanel { Margin = new Thickness(4, 0, 4, 12) };
        for (int index = 0; index < days; index++)
        {
            var date = selected.AddDays(index);
            if (!entries[date].Any()) continue;
            var group = Text(DateText(date, "M月d日 dddd", "dddd, MMMM d"), 16, Sage, true);
            group.Margin = new Thickness(1, 12, 0, 7);
            list.Children.Add(group);
            foreach (var item in entries[date])
            {
                var row = new DockPanel();
                var time = Text(TimeText(item), 14, item.AllDay ? Sage : Slate, true);
                time.Width = 116;
                time.Margin = new Thickness(1, 10, 8, 0);
                DockPanel.SetDock(time, Dock.Left);
                row.Children.Add(time);
                row.Children.Add(EventCard(item, false, true));
                list.Children.Add(row);
            }
        }
        if (list.Children.Count == 0)
        {
            var empty = Text(T("这个时段没有日程", "No events in this period"), 15, Muted);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 42, 0, 22);
            list.Children.Add(empty);
        }
        return list;
    }

    private void OpenDay(DateTime date) { selected = date; mode = Mode.Day; Render(); }

    private Button EventCard(Entry item, bool compact, bool detailed = false)
    {
        var background = item.AllDay ? allDayColor : timedColor;
        var foreground = Contrast(background);
        var stack = new StackPanel();
        if (!compact && !detailed) stack.Children.Add(Text(TimeText(item), 12, foreground, true));
        var title = CardTitle(item.Title, compact ? 13 : 14, foreground);
        if (compact) { title.MaxHeight = 40; title.TextTrimming = TextTrimming.CharacterEllipsis; }
        stack.Children.Add(title);
        if (item.Location.Length > 0) stack.Children.Add(LocationText(item.Location, background));
        var card = ActionButton(stack, "", () => ShowDetails(item));
        card.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        card.Background = background;
        card.BorderBrush = item.AllDay ? allDayStripe : timedStripe;
        card.BorderThickness = new Thickness(2, 0, 0, 0);
        card.Padding = new Thickness(7, compact ? 4 : 7, 7, compact ? 4 : 7);
        card.Margin = new Thickness(0, 0, 0, 5);
        System.Windows.Automation.AutomationProperties.SetName(card, item.Title);
        AttachPreview(card, () => EventDetails(item), false);
        return card;
    }

    private void AttachPreview(Button target, Func<FrameworkElement> content, bool compact)
    {
        target.MouseEnter += (_, _) =>
        {
            DismissPreview();
            hovered = (target, content, compact);
            hoverDelay.Start();
        };
        target.MouseLeave += (_, _) => { if (hovered?.Card == target) DismissPreview(); };
        target.Unloaded += (_, _) => { if (hovered?.Card == target) DismissPreview(); };
    }

    private void DismissPreview()
    {
        hoverDelay.Stop();
        hovered = null;
        preview.IsOpen = false;
    }

    private StackPanel EventDetails(Entry item)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text(T("日程详情", "EVENT DETAILS"), 11, item.AllDay ? Sage : Slate, true));
        var title = Text(item.Title, 18, Ink, true);
        title.Margin = new Thickness(0, 6, 0, 8);
        panel.Children.Add(title);
        panel.Children.Add(Text(DateText(item.Date, "M月d日 ddd", "ddd, MMM d") + "  ·  " + TimeText(item), 13, Muted));
        if (item.Location.Length > 0) panel.Children.Add(Text(item.Location, 13, Muted));
        return panel;
    }

    private void ShowDetails(Entry item)
    {
        DismissPreview();
        var details = EventDetails(item);
        details.Margin = new Thickness(23);
        if (item.Notes.Length > 0)
        {
            var notes = Text(item.Notes, 14, Ink);
            notes.Margin = new Thickness(0, 15, 0, 0);
            details.Children.Add(notes);
        }
        new Window
        {
            Title = T("日程详情", "Event details"), Owner = this, Width = 450, MaxHeight = 500,
            Background = theme.Surface, SizeToContent = SizeToContent.Height, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        }.ShowDialog();
    }

    private void UpdateStatus() => statusLabel.Text = syncing ? T("正在同步…", "Syncing…") : status switch
    {
        "ok" => T("已同步", "Synced") + (syncAt.Length == 0 ? "" : "  " + syncAt),
        "setup" => T("尚未连接日历 · 点击齿轮设置", "Calendar disconnected · open Settings"),
        "error" => entries.Any() ? T("同步失败 · 显示上次日程", "Sync failed · showing cached events")
            : T("同步失败 · 检查链接或网络", "Sync failed · check the link or connection"),
        _ => T("正在读取日程…", "Reading calendar…")
    };

    private Button ActionButton(object label, string hint, Action action, bool active = false)
    {
        var button = new Button
        {
            Content = label, Background = active ? theme.Surface : Brushes.Transparent,
            Foreground = active ? Sage : Ink, BorderThickness = new Thickness(0),
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Padding = new Thickness(10, 5, 10, 5), MinHeight = 29,
            Template = ButtonStyle
        };
        if (hint.Length > 0)
        {
            System.Windows.Automation.AutomationProperties.SetName(button, hint);
            if (hint != label as string) AttachPreview(button, () => Text(hint, 12, Ink), true);
        }
        button.Click += (_, _) => action();
        return button;
    }

    private static readonly ControlTemplate ButtonStyle = BuildButtonStyle();

    private static ControlTemplate BuildButtonStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(OpacityProperty, .75));
        template.Triggers.Add(hover);
        return template;
    }

    private static bool HasChinese(string value) => value.Any(c => c is >= '\u3400' and <= '\u9FFF');

    private double DisplayFontSize(string value, double size) =>
        Math.Max(9, size + display.TextSize - 14 - (HasChinese(value) ? 1 : 0));

    private TextBlock Text(string value, double size, Brush brush, bool bold = false)
    {
        var label = new TextBlock
        {
            Text = value, FontSize = DisplayFontSize(value, size), Foreground = brush, TextWrapping = TextWrapping.Wrap,
            FontFamily = HasChinese(value) ? ChineseFont : LatinFont,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal
        };
        Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        return label;
    }

    private TextBlock CardTitle(string value, double size, Brush brush)
    {
        var title = Text(value, size, brush, !HasChinese(value));
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        title.LineHeight = Math.Ceiling(title.FontSize * 1.3);
        return title;
    }

    private static Brush Paint(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }

    private static Brush Contrast(Brush background)
    {
        var color = ((SolidColorBrush)background).Color;
        static double Linear(byte channel) => channel <= 10
            ? channel / 3294.6 : Math.Pow((channel / 255.0 + .055) / 1.055, 2.4);
        double luminance = .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        return luminance < .18 ? Brushes.White : Brushes.Black;
    }

    private static Brush AccentStripe(Brush background)
    {
        var color = ((SolidColorBrush)background).Color;
        byte target = Contrast(background) == Brushes.White ? (byte)255 : (byte)0;
        byte Blend(byte channel) => (byte)Math.Round(channel * .70 + target * .30);
        var stripe = new SolidColorBrush(Color.FromRgb(Blend(color.R), Blend(color.G), Blend(color.B)));
        stripe.Freeze();
        return stripe;
    }

    private TextBlock LocationText(string location, Brush background)
    {
        var label = Text(location, LocationFontSize, Contrast(background) == Brushes.White ? Paint("#E0E4EB") : Paint("#424B55"));
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        return label;
    }
}
