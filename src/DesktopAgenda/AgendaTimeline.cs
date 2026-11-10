using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

internal sealed partial class AgendaWindow
{
    private const double TimeGutter = 56;
    private const double TodayColumnWeight = 1.35;
    private ScrollViewer? activeTimeline;
    private (Mode View, DateTime Date, DisplayOptions Display)? activeTimelineKey;
    private (Mode View, DateTime Date, DisplayOptions Display, double Offset)? savedOffset;

    private sealed class TimelineItem(double top, double bottom, Button card)
    {
        internal double Top { get; } = top;
        internal double Bottom { get; } = bottom;
        internal Button Card { get; } = card;
        internal int Lane { get; set; }
        internal int LaneCount { get; set; }
    }

    private void RememberTimelineOffset()
    {
        if (activeTimeline is not null && activeTimelineKey is { } key)
            savedOffset = (key.View, key.Date, key.Display, activeTimeline.VerticalOffset);
        activeTimeline = null;
        activeTimelineKey = null;
    }

    private FrameworkElement Timeline(int days)
    {
        DateTime first = days == 1 ? selected : WeekStart(selected);
        var dates = Enumerable.Range(0, days).Select(index => first.AddDays(index)).ToArray();
        var timed = dates.SelectMany(date => entries[date]).Where(item => !item.AllDay).ToArray();
        int hourStart = timed.Length == 0 ? display.StartHour : Math.Min(display.StartHour, timed.Min(item => item.Start / 60));
        int hourEnd = timed.Length == 0 ? display.EndHour : Math.Max(display.EndHour, timed.Max(item => (item.End + 59) / 60));
        var now = DateTime.Now;
        int todayIndex = Array.IndexOf(dates, DateTime.Today);
        if (todayIndex >= 0 && display.ShowNow)
        {
            hourStart = Math.Min(hourStart, now.Hour);
            hourEnd = Math.Max(hourEnd, now.Hour + 1);
        }
        double hourHeight = display.HourHeight;
        double bodyHeight = (hourEnd - hourStart) * hourHeight;
        int hours = hourEnd - hourStart;
        double[] hourOffsets = Enumerable.Range(0, hours + 1).Select(index => index * hourHeight).ToArray();
        double Position(int minute)
        {
            double hour = Math.Clamp(minute / 60.0 - hourStart, 0, hours);
            int index = Math.Min((int)hour, hours - 1);
            return hourOffsets[index] + (hour - index) * (hourOffsets[index + 1] - hourOffsets[index]);
        }

        var layout = new Grid { Margin = new Thickness(15, 0, 15, 0) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        var headings = TimelineColumns(days, todayIndex);
        headings.Margin = new Thickness(0, 0, display.FitDay ? 0 : SystemParameters.VerticalScrollBarWidth, 0);
        headings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        headings.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bool hasAllDay = dates.Any(date => entries[date].Any(item => item.AllDay));
        if (hasAllDay)
        {
            var allDay = Text(T("全天", "All-day"), 11, Muted);
            allDay.Margin = new Thickness(0, 7, 7, 0);
            allDay.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(allDay, 1);
            headings.Children.Add(allDay);
        }
        for (int index = 0; index < days; index++)
        {
            DateTime date = dates[index];
            bool today = date == DateTime.Today;
            var dateLabel = new StackPanel { Orientation = Orientation.Horizontal };
            var weekday = Text(DateText(date, "ddd", "ddd"), 11, today ? theme.TodayInk : Muted);
            weekday.VerticalAlignment = VerticalAlignment.Center;
            weekday.Margin = new Thickness(0, 1, 7, 0);
            dateLabel.Children.Add(weekday);
            dateLabel.Children.Add(Text(date.Day.ToString(), 19, today ? theme.TodayInk : Ink, true));
            var day = ActionButton(dateLabel,
                T($"查看{DateText(date, "M月d日", "MMM d")}", $"Open {DateText(date, "M月d日", "MMM d")}"),
                () => OpenDay(date), today);
            day.Margin = new Thickness(4, 0, 4, 6);
            day.Padding = new Thickness(4, 3, 4, 3);
            day.HorizontalContentAlignment = HorizontalAlignment.Center;
            if (today) day.Background = theme.SelectedDay;
            Grid.SetColumn(day, index + 1);
            headings.Children.Add(day);
            if (!hasAllDay) continue;

            var allItems = new StackPanel { Margin = new Thickness(4, 1, 4, 0) };
            var dayItems = entries[date].Where(item => item.AllDay).ToArray();
            foreach (var item in display.FitDay ? dayItems.Take(2) : dayItems)
                allItems.Children.Add(EventCard(item, true));
            if (display.FitDay && dayItems.Length > 2)
                allItems.Children.Add(ActionButton(T($"另有 {dayItems.Length - 2} 项", $"+{dayItems.Length - 2} more"),
                    T("在列表中查看全部全天事项", "See every all-day event in the list"), () =>
                    { selected = date; mode = Mode.List; Render(); }));
            if (display.FitDay)
            {
                Grid.SetRow(allItems, 1);
                Grid.SetColumn(allItems, index + 1);
                headings.Children.Add(allItems);
                continue;
            }
            var band = new ScrollViewer { Content = allItems, MaxHeight = 96, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(band, 1);
            Grid.SetColumn(band, index + 1);
            headings.Children.Add(band);
        }
        layout.Children.Add(headings);

        Grid BuildBody()
        {
            var body = TimelineColumns(days, todayIndex);
            body.Height = bodyHeight;
            for (int hour = hourStart; hour < hourEnd; hour++)
            {
                double top = hourOffsets[hour - hourStart];
                var rule = new Border
                {
                    Height = 1, Background = Rule, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, top, 0, 0), IsHitTestVisible = false
                };
                Grid.SetColumn(rule, 1);
                Grid.SetColumnSpan(rule, days);
                body.Children.Add(rule);
                if (todayIndex >= 0)
                {
                    var todayRule = new Border
                    {
                        Height = 1, Background = theme.TodayRule, VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, top, 0, 0), IsHitTestVisible = false
                    };
                    Grid.SetColumn(todayRule, todayIndex + 1);
                    body.Children.Add(todayRule);
                }
                var label = Text($"{hour:00}:00", 11, Muted);
                label.HorizontalAlignment = HorizontalAlignment.Right;
                label.VerticalAlignment = VerticalAlignment.Top;
                label.Margin = new Thickness(0, top + 2, 9, 0);
                body.Children.Add(label);
            }
            for (int index = 0; index < days; index++)
            {
                DateTime date = dates[index];
                var canvas = new Canvas { ClipToBounds = true };
                bool isToday = index == todayIndex;
                var column = new Border
                {
                    Child = canvas, BorderBrush = theme == Dark ? DarkDayDivider : LightDayDivider,
                    BorderThickness = new Thickness(1, 0, index == days - 1 ? 1 : 0, 0)
                };
                if (isToday)
                {
                    var background = new Border { Background = TodayWash, IsHitTestVisible = false };
                    Grid.SetColumn(background, index + 1);
                    body.Children.Insert(0, background);
                }
                Grid.SetColumn(column, index + 1);
                body.Children.Add(column);
                AddTimedCards(canvas, entries[date], Position, bodyHeight);
            }
            if (todayIndex >= 0 && display.ShowNow) AddCurrentTime(body, todayIndex, Position, bodyHeight);
            return body;
        }

        if (display.FitDay)
        {
            var fit = new Grid { ClipToBounds = true };
            fit.SizeChanged += (_, args) =>
            {
                if (args.NewSize.Height < 1 || args.NewSize.Width <= TimeGutter) return;
                bodyHeight = Math.Floor(args.NewSize.Height);
                double columnWidth = (args.NewSize.Width - TimeGutter) / (days + (todayIndex >= 0 ? TodayColumnWeight - 1 : 0));
                hourOffsets = FitHourOffsets(timed, hourStart, hours, bodyHeight, columnWidth,
                    todayIndex >= 0 ? dates[todayIndex] : null);
                DismissPreview();
                fit.Children.Clear();
                fit.Children.Add(BuildBody());
            };
            Grid.SetRow(fit, 1);
            layout.Children.Add(fit);
            return layout;
        }

        var scroll = new ScrollViewer { Content = BuildBody(), VerticalScrollBarVisibility = ScrollBarVisibility.Visible };
        activeTimeline = scroll;
        activeTimelineKey = (mode, selected, display);
        double startOffset = Math.Max(0, Position(display.StartHour * 60));
        if (todayIndex >= 0 && display.ShowNow && now.Hour >= display.StartHour)
            startOffset = Math.Max(startOffset, Position(now.Hour * 60 + now.Minute) - hourHeight * 3);
        if (savedOffset is { } previous && previous.View == mode && previous.Date == selected && previous.Display == display)
            startOffset = previous.Offset;
        scroll.Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => scroll.ScrollToVerticalOffset(startOffset)));
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        return layout;
    }

    private double[] FitHourOffsets(Entry[] timed, int startHour, int hours, double height, double columnWidth, DateTime? today)
    {
        double floor = Math.Min(18, height / hours * .55);
        var weights = new double[hours];
        foreach (var day in timed.GroupBy(item => item.Date))
        {
            var items = day.ToArray();
            foreach (var item in items)
            {
                int lanes = items.Count(other => item.Start < other.End && other.Start < item.End);
                var title = CardTitle(item.Title, 14, Ink);
                double width = columnWidth * (item.Date == today ? TodayColumnWeight : 1);
                title.Measure(new Size(Math.Max(20, (width - 7) / lanes - 17), double.PositiveInfinity));
                double locationHeight = item.Location.Length == 0 ? 0 : DisplayFontSize(item.Location, LocationFontSize) * 1.4;
                double desired = title.DesiredSize.Height + locationHeight + 12;
                // Cap the rate for brief events so one long title cannot flatten the rest of the day.
                double perHour = Math.Min(150, desired * 60 / Math.Max(20, item.End - item.Start));
                for (int index = item.Start / 60 - startHour; index <= (item.End - 1) / 60 - startHour; index++)
                    weights[index] = Math.Max(weights[index], perHour - floor);
            }
        }
        double totalWeight = weights.Sum();
        double remaining = height - floor * hours;
        var offsets = new double[hours + 1];
        for (int index = 0; index < hours; index++)
            offsets[index + 1] = offsets[index] + floor +
                remaining * (totalWeight > 0 ? weights[index] / totalWeight : 1.0 / hours);
        offsets[hours] = height;
        return offsets;
    }

    private static Grid TimelineColumns(int days, int todayIndex)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimeGutter) });
        for (int i = 0; i < days; i++) grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(i == todayIndex ? TodayColumnWeight : 1, GridUnitType.Star)
        });
        return grid;
    }

    private void AddTimedCards(Canvas canvas, IEnumerable<Entry> events, Func<int, double> position, double height)
    {
        var group = new List<TimelineItem>();
        var lanes = new List<double>();
        double groupEnd = 0;
        void FinishGroup()
        {
            foreach (var item in group) item.LaneCount = lanes.Count;
            group.Clear();
            lanes.Clear();
        }
        var arranged = new List<TimelineItem>();
        foreach (var entry in events.Where(item => !item.AllDay).OrderBy(item => item.Start).ThenByDescending(item => item.End))
        {
            double minCardHeight = Math.Min(height, Math.Max(24, display.TextSize + 10) + (entry.Location.Length > 0 ? 14 : 0));
            double top = Math.Clamp(position(entry.Start) + 2, 0, height - minCardHeight);
            double bottom = Math.Min(height, Math.Max(top + minCardHeight, position(entry.End) - 2));
            if (top >= groupEnd)
            {
                FinishGroup();
                groupEnd = 0;
            }
            var card = TimedCard(entry, bottom - top);
            var item = new TimelineItem(top, bottom, card);
            item.Lane = lanes.FindIndex(end => end <= top);
            if (item.Lane < 0) { item.Lane = lanes.Count; lanes.Add(bottom); }
            else lanes[item.Lane] = bottom;
            groupEnd = Math.Max(groupEnd, bottom);
            group.Add(item);
            arranged.Add(item);
            canvas.Children.Add(card);
        }
        FinishGroup();
        canvas.SizeChanged += (_, _) =>
        {
            foreach (var item in arranged)
            {
                double width = Math.Max(1, (canvas.ActualWidth - 6) / item.LaneCount);
                Canvas.SetLeft(item.Card, 3 + item.Lane * width);
                Canvas.SetTop(item.Card, item.Top);
                item.Card.Width = Math.Max(1, width - 3);
                item.Card.Height = item.Bottom - item.Top;
            }
        };
    }

    private Button TimedCard(Entry item, double height)
    {
        var text = new StackPanel { ClipToBounds = true };
        var foreground = Contrast(timedColor);
        var title = CardTitle(item.Title, 14, foreground);
        double locationHeight = DisplayFontSize(item.Location, LocationFontSize) * 1.4;
        bool showLocation = item.Location.Length > 0 && height >= title.LineHeight + locationHeight + 6;
        bool showTime = !display.FitDay && height >= (showLocation ? 65 : 51);
        title.MaxHeight = Math.Max(0, height - 6 - (showLocation ? locationHeight : 0) - (showTime ? 14 : 0));
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(title);
        if (showTime) text.Children.Add(Text(TimeText(item), 11, foreground));
        if (showLocation) text.Children.Add(LocationText(item.Location, timedColor));
        var card = ActionButton(text, "", () => ShowDetails(item));
        card.Background = timedColor;
        card.BorderBrush = timedStripe;
        card.BorderThickness = new Thickness(2, 0, 0, 0);
        card.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        card.Padding = new Thickness(7, 3, 5, 3);
        card.MinHeight = 0;
        card.ClipToBounds = true;
        System.Windows.Automation.AutomationProperties.SetName(card, item.Title);
        AttachPreview(card, () => EventDetails(item), false);
        return card;
    }

    private void AddCurrentTime(Grid body, int todayIndex, Func<int, double> position, double height)
    {
        var line = new Border { Height = 1.5, Background = theme.NowRed, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
        var dot = new Ellipse { Width = 6, Height = 6, Fill = theme.NowRed, IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        Grid.SetColumn(line, todayIndex + 1);
        Grid.SetColumn(dot, todayIndex + 1);
        body.Children.Add(line);
        body.Children.Add(dot);
        void RefreshLine()
        {
            var now = DateTime.Now;
            var y = position(now.Hour * 60 + now.Minute);
            line.Visibility = dot.Visibility = y >= 0 && y < height ? Visibility.Visible : Visibility.Collapsed;
            line.Margin = new Thickness(0, Math.Max(0, y), 0, 0);
            dot.Margin = new Thickness(0, Math.Max(0, y - 2), 0, 0);
        }
        updateNow = RefreshLine;
        RefreshLine();
    }
}
