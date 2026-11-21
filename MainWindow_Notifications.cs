using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;

namespace FrostClient;

// Uma notificação da lista (hoje: vídeos do YouTube; dá para juntar releases do GitHub, notícias, etc.)
public sealed class NotificationItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string ThumbUrl { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset Published { get; set; }
    public bool IsNew { get; set; }
    public Visibility NewVisibility => IsNew ? Visibility.Visible : Visibility.Collapsed;
}

public partial class MainWindow
{
    private const string YoutubeChannelId = "UC7LDaydZ72gY_85WQQ2__Nw";   // @FrostClient-q4i
    private const string YoutubeFeedUrl   = "https://www.youtube.com/feeds/videos.xml?channel_id=" + YoutubeChannelId;
    private static readonly Regex VideoIdRx = new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);

    private DispatcherTimer? _notifTimer;
    private List<NotificationItem> _notifications = new();
    private DateTime _notifPopupClosedAt;
    private bool _notifBusy;

    // Chamar uma vez no arranque (Window_Loaded). Depois atualiza sozinho de 30 em 30 minutos.
    private void StartNotifications()
    {
        _ = RefreshNotificationsAsync();
        _notifTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _notifTimer.Tick += (_, _) => _ = RefreshNotificationsAsync();
        _notifTimer.Start();
    }

    private async Task RefreshNotificationsAsync()
    {
        if (_notifBusy) return;
        _notifBusy = true;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, YoutubeFeedUrl);
            req.Headers.UserAgent.ParseAdd("FrostClient/1.0");
            using var resp = await ProfileHttp.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            string xml = await resp.Content.ReadAsStringAsync();

            var items = ParseYoutubeFeed(xml);
            DateTime lastSeen = _cfg.NotificationsLastSeenUtc;
            foreach (var n in items) n.IsNew = n.Published.UtcDateTime > lastSeen;
            _notifications = items;
        }
        catch (Exception ex)
        {
            // Sem net / YouTube em baixo: mantém a lista anterior em silêncio.
            Debug.WriteLine("Notifications: " + ex.Message);
        }
        finally { _notifBusy = false; }

        RenderNotifications();
    }

    private static List<NotificationItem> ParseYoutubeFeed(string xml)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace yt   = "http://www.youtube.com/xml/schemas/2015";

        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var sr = new StringReader(xml);
        using var xr = XmlReader.Create(sr, settings);
        var doc = XDocument.Load(xr);

        var list = new List<NotificationItem>();
        foreach (var en in doc.Root!.Elements(atom + "entry").Take(10))
        {
            string? id    = en.Element(yt + "videoId")?.Value;
            string? title = en.Element(atom + "title")?.Value;
            string? pub   = en.Element(atom + "published")?.Value;
            if (id == null || !VideoIdRx.IsMatch(id) || string.IsNullOrWhiteSpace(title)) continue;
            if (!DateTimeOffset.TryParse(pub, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)) continue;

            list.Add(new NotificationItem
            {
                Id        = id,
                Title     = title.Trim(),
                Published = when,
                Subtitle  = "New video · " + FormatAgo(when),
                ThumbUrl  = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg",
                Url       = $"https://www.youtube.com/watch?v={id}",
            });
        }
        return list.OrderByDescending(n => n.Published).ToList();
    }

    private static string FormatAgo(DateTimeOffset when)
    {
        var d = DateTimeOffset.UtcNow - when;
        if (d.TotalMinutes < 1)  return "just now";
        if (d.TotalHours   < 1)  return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays    < 1)  return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays    < 2)  return "yesterday";
        if (d.TotalDays    < 14) return $"{(int)d.TotalDays} days ago";
        return when.LocalDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    private void RenderNotifications()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(RenderNotifications); return; }

        if (NotifList != null) { NotifList.ItemsSource = null; NotifList.ItemsSource = _notifications; }
        if (NotifEmptyText != null)
            NotifEmptyText.Visibility = _notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int unread = _notifications.Count(n => n.IsNew);
        if (NotifBadge != null) NotifBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (NotifBadgeText != null) NotifBadgeText.Text = unread > 9 ? "9+" : unread.ToString();
        if (NotifButton != null)
            NotifButton.ToolTip = unread > 0 ? $"{unread} new notification(s)" : "Notifications";
    }

    private void Notifications_Click(object s, RoutedEventArgs e)
    {
        // Com StaysOpen=False, o clique no botão fecha e reabre o popup; isto faz de toggle.
        if ((DateTime.UtcNow - _notifPopupClosedAt).TotalMilliseconds < 250) return;
        NotifPopup.IsOpen = true;

        // Ao abrir, marca tudo como visto (o "NEW" das linhas só desaparece na próxima atualização).
        if (_notifications.Count > 0)
        {
            var newest = _notifications.Max(n => n.Published.UtcDateTime);
            if (newest > _cfg.NotificationsLastSeenUtc)
            {
                _cfg.NotificationsLastSeenUtc = newest;
                SaveSilently();
            }
        }
        if (NotifBadge != null) NotifBadge.Visibility = Visibility.Collapsed;
    }

    private void NotifPopup_Closed(object? s, EventArgs e) => _notifPopupClosedAt = DateTime.UtcNow;

    private void NotifItem_Click(object s, RoutedEventArgs e)
    {
        if (s is FrameworkElement { Tag: string url } && url.StartsWith("https://www.youtube.com/", StringComparison.Ordinal))
        {
            NotifPopup.IsOpen = false;
            OpenUrl(url);
        }
    }

    private void NotifChannel_Click(object s, RoutedEventArgs e)
    {
        NotifPopup.IsOpen = false;
        Youtube_Click(s, e);
    }
}
