using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

internal static class Program
{
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopAgenda");
    private static readonly string SettingsPath = Path.Combine(DirectoryPath, "subscription.txt");
    internal static readonly string LanguagePath = Path.Combine(DirectoryPath, "language.txt");
    internal static readonly string CachePath = Path.Combine(DirectoryPath, "agenda.tsv");
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private const int MaxFeedBytes = 16 * 1024 * 1024;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "fixture")
        {
            File.WriteAllLines(args[2], BuildRows(File.ReadAllText(args[1]), DateTime.Now), Encoding.UTF8);
            return 0;
        }

        Directory.CreateDirectory(DirectoryPath);
        if (args is ["settings"])
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            ShowSettings();
            return 0;
        }

        if (args is ["sync"]) return Sync().GetAwaiter().GetResult();
        if (args is ["preview", var fixture])
            return new System.Windows.Application().Run(new AgendaWindow(fixture));
        if (args is ["preview", var themeFixture, "light" or "dark"])
            return new System.Windows.Application().Run(new AgendaWindow(themeFixture, args[2] == "dark"));
        if (args.Length != 0) return 1;
        using var instance = new Mutex(true, @"Local\DesktopAgendaNative", out bool firstInstance);
        return firstInstance ? new System.Windows.Application().Run(new AgendaWindow()) : 0;
    }

    private static bool ValidUrl(string text, out Uri? uri)
    {
        return Uri.TryCreate(text, UriKind.Absolute, out uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.IdnHost == "calendar.google.com"
            && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && uri.AbsolutePath.StartsWith("/calendar/ical/", StringComparison.Ordinal)
            && uri.AbsolutePath.EndsWith("/basic.ics", StringComparison.Ordinal)
            && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    private sealed class SettingsOwner(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    internal static void ShowSettings(IntPtr owner = default)
    {
        using var dialog = new Form
        {
            StartPosition = owner == IntPtr.Zero ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ShowInTaskbar = false,
            TopMost = owner != IntPtr.Zero,
            MaximizeBox = false,
            MinimizeBox = false,
            Font = new Font("Microsoft YaHei UI", 10),
            AutoScaleDimensions = new SizeF(96, 96),
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(540, 352),
            BackColor = Color.White
        };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var displayPage = new TabPage { BackColor = Color.White };
        var appearancePage = new TabPage { BackColor = Color.White };
        var languagePage = new TabPage { BackColor = Color.White };
        var accountPage = new TabPage { BackColor = Color.White };
        tabs.TabPages.AddRange([displayPage, appearancePage, languagePage, accountPage]);
        var languageLabel = new Label { Bounds = new Rectangle(20, 20, 135, 28) };
        var language = new ComboBox
        {
            Bounds = new Rectangle(156, 17, 174, 28),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        language.Items.AddRange(["简体中文", "English"]);
        language.SelectedIndex = File.Exists(LanguagePath) && File.ReadAllText(LanguagePath).Trim() == "en" ? 1 : 0;
        string T(string chinese, string english) => language.SelectedIndex == 1 ? english : chinese;

        var help = new Label
        {
            Bounds = new Rectangle(20, 22, 490, 60)
        };
        var field = new TextBox
        {
            Bounds = new Rectangle(20, 88, 490, 28),
            UseSystemPasswordChar = true
        };
        if (File.Exists(SettingsPath))
            field.Text = File.ReadAllText(SettingsPath).Trim();

        var status = new Label { Bounds = new Rectangle(20, 127, 490, 44) };
        var languageStatus = new Label { Bounds = new Rectangle(20, 70, 490, 40) };
        var save = new Button { Bounds = new Rectangle(360, 186, 150, 34) };
        var options = DisplayOptions.Load();
        var view = new ComboBox { Bounds = new Rectangle(175, 24, 300, 29), DropDownStyle = ComboBoxStyle.DropDownList };
        var week = new ComboBox { Bounds = new Rectangle(175, 68, 300, 29), DropDownStyle = ComboBoxStyle.DropDownList };
        var density = new ComboBox { Bounds = new Rectangle(175, 152, 300, 29), DropDownStyle = ComboBoxStyle.DropDownList };
        var start = new NumericUpDown { Bounds = new Rectangle(175, 110, 83, 28), Minimum = 0, Maximum = 23, Value = options.StartHour };
        var finish = new NumericUpDown { Bounds = new Rectangle(282, 110, 83, 28), Minimum = 1, Maximum = 24, Value = options.EndHour };
        var showNow = new CheckBox { Bounds = new Rectangle(175, 197, 300, 28), Checked = options.ShowNow };
        var fitDay = new CheckBox { Bounds = new Rectangle(175, 228, 330, 28), Checked = options.FitDay };
        var displaySave = new Button { Bounds = new Rectangle(360, 270, 150, 35) };
        var displayStatus = new Label { Bounds = new Rectangle(20, 272, 328, 32) };
        var timedLabel = new Label { Bounds = new Rectangle(20, 29, 151, 28) };
        var allDayLabel = new Label { Bounds = new Rectangle(20, 73, 151, 28) };
        var opacityLabel = new Label { Bounds = new Rectangle(20, 117, 151, 28) };
        var textLabel = new Label { Bounds = new Rectangle(20, 161, 151, 28) };
        var opacity = new NumericUpDown { Bounds = new Rectangle(175, 114, 90, 28), Minimum = 50, Maximum = 100, Value = options.OpacityPercent };
        var percent = new Label { Text = "%", Bounds = new Rectangle(271, 117, 30, 28) };
        var textSize = new NumericUpDown { Bounds = new Rectangle(175, 158, 90, 28), Minimum = 11, Maximum = 20, Value = options.TextSize };
        var appearanceStatus = new Label { Bounds = new Rectangle(20, 246, 328, 36) };
        var appearanceSave = new Button { Bounds = new Rectangle(360, 243, 150, 35) };
        var appearanceReset = new Button { Bounds = new Rectangle(20, 196, 150, 34) };
        Button ColorPicker(string value, int y)
        {
            var button = new Button { Bounds = new Rectangle(175, y, 170, 31), UseVisualStyleBackColor = false };
            SetSwatch(button, ColorTranslator.FromHtml(value));
            button.Click += (_, _) =>
            {
                using var picker = new ColorDialog { Color = button.BackColor, FullOpen = true };
                if (picker.ShowDialog(dialog) == DialogResult.OK) SetSwatch(button, picker.Color);
            };
            return button;
        }
        static void SetSwatch(Button button, Color color)
        {
            button.BackColor = color;
            button.ForeColor = color.GetBrightness() < .46 ? Color.White : Color.Black;
            button.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        var timedColor = ColorPicker(options.TimedColor, 23);
        var allDayColor = ColorPicker(options.AllDayColor, 67);
        var viewLabel = new Label { Bounds = new Rectangle(20, 27, 151, 29) };
        var weekLabel = new Label { Bounds = new Rectangle(20, 71, 151, 29) };
        var hourLabel = new Label { Bounds = new Rectangle(20, 112, 151, 29) };
        var densityLabel = new Label { Bounds = new Rectangle(20, 154, 151, 29) };
        var rangeDash = new Label { Text = "–", Bounds = new Rectangle(263, 110, 19, 28) };
        string[] views = ["", "Day", "Week", "Month", "List"];
        void LocalizeChoices(ComboBox box, int index, params string[] items)
        {
            box.Items.Clear();
            box.Items.AddRange(items);
            box.SelectedIndex = Math.Max(0, index);
        }
        void Translate()
        {
            dialog.Text = T("日历设置", "Calendar settings");
            displayPage.Text = T("显示", "Display");
            appearancePage.Text = T("外观", "Appearance");
            languagePage.Text = T("语言", "Language");
            accountPage.Text = T("订阅", "Subscription");
            languageLabel.Text = T("界面语言", "Language");
            viewLabel.Text = T("启动视图", "Startup view");
            weekLabel.Text = T("每周起始日", "First day of week");
            hourLabel.Text = T("时间轴时段", "Timeline hours");
            densityLabel.Text = T("时间轴密度", "Timeline density");
            showNow.Text = T("显示当前时间红线", "Show current-time line");
            fitDay.Text = T("整天一屏显示", "Fit the full day on screen");
            timedLabel.Text = T("定时卡片颜色", "Timed event color");
            allDayLabel.Text = T("全天卡片颜色", "All-day event color");
            opacityLabel.Text = T("挂件不透明度", "Widget opacity");
            textLabel.Text = T("界面字体大小", "Interface font size");
            appearanceSave.Text = T("应用外观", "Apply appearance");
            appearanceReset.Text = T("恢复默认外观", "Reset appearance");
            displaySave.Text = T("应用显示设置", "Apply display settings");
            LocalizeChoices(view, view.SelectedIndex < 0 ? Array.IndexOf(views, options.StartView) : view.SelectedIndex,
                T("记住上次视图", "Last used view"), T("日", "Day"), T("周", "Week"), T("月", "Month"), T("列表", "List"));
            LocalizeChoices(week, week.SelectedIndex < 0 ? (options.SundayFirst ? 1 : 0) : week.SelectedIndex,
                T("星期一", "Monday"), T("星期日", "Sunday"));
            LocalizeChoices(density, density.SelectedIndex < 0 ? (options.HourHeight < 56 ? 0 : options.HourHeight > 56 ? 2 : 1) : density.SelectedIndex,
                T("紧凑", "Compact"), T("标准", "Standard"), T("宽松", "Roomy"));
            help.Text = T(
                "在谷歌日历打开「设置和共享 → 集成日历」，复制 iCal 私密地址。",
                "In Google Calendar, open Settings and sharing → Integrate calendar, then copy the secret iCal address.");
            field.PlaceholderText = T("粘贴 iCal 私密地址（留空则断开）", "Paste the secret iCal address (leave blank to disconnect)");
            save.Text = T("保存并同步", "Save and sync");
        }
        Translate();
        density.Enabled = !fitDay.Checked;
        fitDay.CheckedChanged += (_, _) => density.Enabled = !fitDay.Checked;
        appearanceReset.Click += (_, _) =>
        {
            var defaults = new DisplayOptions();
            SetSwatch(timedColor, ColorTranslator.FromHtml(defaults.TimedColor));
            SetSwatch(allDayColor, ColorTranslator.FromHtml(defaults.AllDayColor));
            opacity.Value = defaults.OpacityPercent;
            textSize.Value = defaults.TextSize;
        };
        appearanceSave.Click += (_, _) =>
        {
            try
            {
                (DisplayOptions.Load() with
                {
                    TimedColor = timedColor.Text, AllDayColor = allDayColor.Text,
                    OpacityPercent = (int)opacity.Value, TextSize = (int)textSize.Value
                }).Save();
                appearanceStatus.Text = T("外观已应用。", "Appearance applied.");
            }
            catch (IOException) { appearanceStatus.Text = T("无法保存设置。", "Could not save settings."); }
        };
        language.SelectedIndexChanged += (_, _) =>
        {
            Translate();
            try
            {
                AtomicWrite(LanguagePath, [T("zh-CN", "en")]);
                languageStatus.Text = T("界面语言已更新。", "Display language updated.");
            }
            catch (IOException)
            {
                languageStatus.Text = T("无法保存界面语言。", "Could not save the display language.");
            }
        };
        displaySave.Click += (_, _) =>
        {
            if (start.Value >= finish.Value)
            {
                displayStatus.Text = T("结束小时须大于开始小时。", "End hour must be later than start hour.");
                return;
            }
            try
            {
                (DisplayOptions.Load() with
                {
                    StartView = views[view.SelectedIndex], SundayFirst = week.SelectedIndex == 1,
                    StartHour = (int)start.Value, EndHour = (int)finish.Value,
                    HourHeight = new[] { 44, 56, 72 }[density.SelectedIndex], ShowNow = showNow.Checked, FitDay = fitDay.Checked
                }).Save();
                displayStatus.Text = T("显示设置已应用。", "Display settings applied.");
            }
            catch (IOException)
            {
                displayStatus.Text = T("无法保存设置。", "Could not save display settings.");
            }
        };
        save.Click += async (_, _) =>
        {
            string value = field.Text.Trim();
            if (value.Length > 0 && !ValidUrl(value, out _))
            {
                status.Text = T("请输入谷歌日历的 HTTPS iCal 地址。", "Enter an HTTPS iCal address from Google Calendar.");
                return;
            }

            save.Enabled = false;
            field.Enabled = false;
            status.Text = T("正在同步…", "Syncing…");
            try
            {
                WithPublishLock(() =>
                {
                    if (value.Length == 0)
                        File.Delete(SettingsPath);
                    else
                        AtomicWrite(SettingsPath, [value]);
                    AtomicWrite(CachePath, [value.Length == 0
                        ? "setup\t点击右上角「设置」连接 Google 日历"
                        : "loading\t正在同步…"]);
                });
                int result = await Sync();
                status.Text = result == 0 ? T("设置已保存。", "Settings saved.")
                    : T("设置已保存，同步失败。请检查链接和网络。", "Settings saved. Sync failed. Check the link and connection.");
            }
            catch (IOException)
            {
                status.Text = T("无法保存本机设置。", "Could not save local settings.");
            }
            finally
            {
                field.Enabled = true;
                save.Enabled = true;
            }
        };
        displayPage.Controls.AddRange([viewLabel, view, weekLabel, week, hourLabel, start, rangeDash, finish,
            densityLabel, density, showNow, fitDay, displayStatus, displaySave]);
        appearancePage.Controls.AddRange([timedLabel, timedColor, allDayLabel, allDayColor, opacityLabel, opacity, percent,
            textLabel, textSize, appearanceStatus, appearanceSave, appearanceReset]);
        languagePage.Controls.AddRange([languageLabel, language, languageStatus]);
        accountPage.Controls.AddRange([help, field, status, save]);
        dialog.Controls.Add(tabs);
        if (owner == IntPtr.Zero) dialog.ShowDialog();
        else dialog.ShowDialog(new SettingsOwner(owner));
    }

    internal static async Task<int> Sync()
    {
        string url = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath).Trim() : "";
        if (!ValidUrl(url, out Uri? uri))
        {
            WithPublishLock(() =>
            {
                if (!File.Exists(SettingsPath))
                    AtomicWrite(CachePath, ["setup\t点击右上角「设置」连接 Google 日历"]);
            });
            return 0;
        }

        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        string[] rows;
        int result;
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(20),
                MaxResponseContentBufferSize = MaxFeedBytes
            };
            using var response = await client.GetAsync(uri!);
            response.EnsureSuccessStatusCode();
            string ics = await response.Content.ReadAsStringAsync();
            if (!ics.TrimStart('\uFEFF', ' ', '\r', '\n').StartsWith("BEGIN:VCALENDAR", StringComparison.Ordinal))
                throw new FormatException();

            DateTime now = DateTime.Now;
            rows = [$"ok\t更新于 {now:HH:mm}\t{key}", .. BuildRows(ics, now)];
            result = 0;
        }
        catch (Exception)
        {
            string[] previous = File.Exists(CachePath) ? File.ReadAllLines(CachePath) : [];
            string[] header = previous.FirstOrDefault()?.Split('\t') ?? [];
            rows = header.Length == 3 && header[2] == key && previous.Length > 1
                ? [$"error\t同步失败 · 显示上次日程\t{key}", .. previous.Skip(1)]
                : [$"error\t同步失败 · 检查链接或网络\t{key}"];
            result = 1;
        }

        // Discard in-flight results after changing the selected subscription.
        WithPublishLock(() =>
        {
            if (File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Trim() == url)
                AtomicWrite(CachePath, rows);
        });
        return result;
    }

    private static void WithPublishLock(Action write)
    {
        using var writeLock = new Mutex(false, @"Local\DesktopAgendaPublish");
        try { writeLock.WaitOne(); } catch (AbandonedMutexException) { }
        try { write(); }
        finally { writeLock.ReleaseMutex(); }
    }

    private static string[] BuildRows(string ics, DateTime now)
    {
        Ical.Net.Calendar calendar = Ical.Net.Calendar.Load(ics) ?? throw new FormatException();
        DateTime first = new DateTime(now.Year, now.Month, 1).AddMonths(-3).AddDays(-7);
        DateTime end = new DateTime(now.Year, now.Month, 1).AddMonths(7).AddDays(7);
        return calendar.GetOccurrences(new CalDateTime(first.AddDays(-1)))
            .TakeWhileBefore(new CalDateTime(DateTime.SpecifyKind(end.AddDays(1), DateTimeKind.Unspecified)))
            .Where(item => item.Source is CalendarEvent ev && ev.Status != "CANCELLED")
            .Select(item =>
            {
                var ev = (CalendarEvent)item.Source;
                var start = item.Period.StartTime;
                DateTime localStart = Local(start);
                DateTime localEnd = item.Period.EffectiveEndTime is { } periodEnd ? Local(periodEnd) : localStart;
                return new { ev.Summary, ev.Location, ev.Description, Start = localStart, End = localEnd, AllDay = !start.HasTime };
            })
            .Where(item => item.Start < end && (item.Start >= first || item.End > first))
            .SelectMany(item => DayRows(item.Start, item.End, item.AllDay, item.Summary, item.Location, item.Description, first, end))
            .OrderBy(item => item.Date)
            .ThenByDescending(item => item.AllDay)
            .ThenBy(item => item.StartMinute)
            .Select(item => $"{item.Date:yyyy-MM-dd}\t{item.Date.ToString("M/d ddd", Chinese)}\t{item.Time}\t{item.Title}\t{item.StartMinute}\t{item.EndMinute}\t{item.Location}\t{item.Notes}")
            .ToArray();
    }

    private sealed record DayRow(DateTime Date, bool AllDay, int StartMinute, int EndMinute, string Time, string Title, string Location, string Notes);

    private static IEnumerable<DayRow> DayRows(DateTime start, DateTime end, bool allDay, string? summary, string? location, string? description,
        DateTime firstDay, DateTime limit)
    {
        string title = string.IsNullOrWhiteSpace(summary) ? "无标题日程" : SafeText(summary);
        string notes = description is null ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(description));
        DateTime effectiveEnd = end > start ? end : allDay ? start.Date.AddDays(1) : start.AddMinutes(1);
        DateTime day = start.Date < firstDay ? firstDay : start.Date;
        for (; day < effectiveEnd && day < limit; day = day.AddDays(1))
        {
            int startMinute = Math.Max(0, (int)(start - day).TotalMinutes);
            int endMinute = Math.Min(1440, (int)Math.Ceiling((effectiveEnd - day).TotalMinutes));
            string time = allDay ? "全天" : $"{MinuteLabel(startMinute)}–{MinuteLabel(endMinute)}";
            yield return new DayRow(day, allDay, startMinute, endMinute, time, title, SafeText(location ?? ""), notes);
        }
    }

    private static string MinuteLabel(int minute) => $"{minute / 60:D2}:{minute % 60:D2}";

    private static DateTime Local(CalDateTime value) =>
        value.HasTime && value.TzId is not null ? value.AsUtc.ToLocalTime() : value.Value;

    private static string SafeText(string text) => string.Concat(text.Select(c => c switch
    {
        '[' => '［', ']' => '］', '#' => '＃', _ => char.IsControl(c) ? ' ' : c
    })).Trim();

    internal static void AtomicWrite(string path, string[] lines)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
