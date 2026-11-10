# Desktop Agenda

[简体中文](https://github.com/chenbinghan666/desktop-agenda/blob/main/README.md) · **English** · [日本語](https://github.com/chenbinghan666/desktop-agenda/blob/main/docs/locales/README.ja.md) · [Deutsch](https://github.com/chenbinghan666/desktop-agenda/blob/main/docs/locales/README.de.md) · [Français](https://github.com/chenbinghan666/desktop-agenda/blob/main/docs/locales/README.fr.md) · [Español](https://github.com/chenbinghan666/desktop-agenda/blob/main/docs/locales/README.es.md) · [Português](https://github.com/chenbinghan666/desktop-agenda/blob/main/docs/locales/README.pt.md)

Keep Google Calendar visible on your Windows desktop. Switch between day, week, month, and agenda views to check classes, meetings, and deadlines. Events sync read-only via iCal.

C# · WPF · Google Calendar · [Download desktop calendar](https://github.com/chenbinghan666/desktop-agenda/releases/latest)

![dark week view. Today's date column is slightly wider, the red line marks the current time, and scheduled and all-day events are displayed separately.](../../docs/images/week-dark.png)

<table>
  <tr>
    <td width="50%"><img src="../../docs/images/week-light.png" alt="Light week view"><br>Light week view</td>
    <td width="50%"><img src="../../docs/images/month-light.png" alt="Light month view"><br>Light month view</td>
  </tr>
</table>

The screenshots all use a fictional schedule.

## Features

| Function | Operation and effect |
| --- | --- |
| **day, week, month, list** | Switch view from top. The day and week views are displayed on one screen by default, and more height is allocated to periods with longer titles. The month view can be expanded by clicking on the date, and the list displays the schedule for the next 30 days by day. |
| **Desktop fixed** | Drag the top date to move it and drag the edges to resize it. Click the lock icon to fix the position and size, and click again to unlock. Position, size and lock status are saved. |
| **schedule details** | The card displays the title and location. Hover for approximately 250 milliseconds to see full title, time and location, click to view notes. |
| **Personalization** | Follow Windows application color mode, support Simplified Chinese and English. Card color, font size, opacity, time range and start of week can be adjusted. |
| **automatically synchronizes with** | The widget will try to refresh every 5 minutes when it is running, or it can be refreshed manually. When synchronization fails, the last successfully read schedule is retained. |

## Installation and connection

Requires **Windows 11 x64** and [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).

1. Download the Windows compressed package from [Releases](https://github.com/chenbinghan666/desktop-agenda/releases/latest), unzip it and double-click `DesktopAgenda.exe`.
2. In [Google Calendar web version settings](https://calendar.google.com/calendar/u/0/r/settings), enter **my calendar settings → select a calendar → integrated calendar**.
3. Copy the **Secret address in iCal format**. In the widget, open **Settings → Subscription**, paste the address, and save.

One calendar can be connected at a time. The subscription address, schedule cache and interface settings are saved in `%LOCALAPPDATA%\DesktopAgenda` of the local machine. If your work or school account doesn't show a non-public URL, your administrator may have restricted iCal subscriptions. [Google Calendar iCal Help](https://support.google.com/calendar/answer/37648?hl=zh-Hans)

When it needs to be started with login, enter `shell:startup` in the Windows Run box and put the **shortcut** of the program into the open folder.

## Build from source

Using **.NET 10 SDK** on Windows. The interface uses WPF, the settings window uses WinForms, and iCal parsing uses Ical.Net.

```powershell
dotnet publish .\src\DesktopAgenda\DesktopAgenda.csproj -c Release -r win-x64 -p:SelfContained=false -p:PublishSelfContained=false -p:PublishSingleFile=true -o .\dist
```

`dist\DesktopAgenda.exe` can be run directly. For dependent licenses, see [Third-Party License](../../third-party/README.md).
