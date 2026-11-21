using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SpdPool.Core;

namespace SpdPool.UI;

public sealed class MainWindow : Window
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#1E1F22"));
    private static readonly IBrush Card = new SolidColorBrush(Color.Parse("#2B2D30"));
    private static readonly IBrush TextMain = new SolidColorBrush(Color.Parse("#E6E6E6"));
    private static readonly IBrush TextDim = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#017D7B"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF9800"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#F44336"));

    private readonly StackPanel _membersPanel = new() { Spacing = 10 };
    private readonly TextBlock _poolSummary = new();
    private readonly TextBlock _nextTarget = new();
    private readonly TextBlock _footer = new();
    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        Title = "SpdPool 存储池";
        Width = 820;
        Height = 600;
        MinWidth = 680;
        MinHeight = 420;
        Background = Bg;
        Content = BuildLayout();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private Control BuildLayout()
    {
        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(new TextBlock
        {
            Text = "SpdPool",
            FontSize = 28,
            FontWeight = FontWeight.Bold,
            Foreground = TextMain,
        });
        header.Children.Add(new TextBlock
        {
            Text = "文件级存储池 · 单显 S: · 无冗余",
            FontSize = 13,
            Foreground = TextDim,
        });

        _poolSummary.FontSize = 15;
        _poolSummary.Foreground = TextMain;
        var summaryCard = new Border
        {
            Background = Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 14),
            Child = _poolSummary,
        };

        var memberHeader = new TextBlock
        {
            Text = "成员盘",
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = TextMain,
            Margin = new Thickness(0, 8, 0, 0),
        };

        _nextTarget.FontSize = 13;
        _nextTarget.Foreground = TextDim;

        _footer.FontSize = 12;
        _footer.Foreground = TextDim;

        var panel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(28, 24),
            Children =
            {
                header,
                summaryCard,
                memberHeader,
                _membersPanel,
                _nextTarget,
                _footer,
            },
        };

        return new ScrollViewer { Content = panel };
    }

    private void Refresh()
    {
        try
        {
            var reg = MemberRegistry.ProbeDefault();
            _poolSummary.Text =
                $"总容量 {GiB(reg.TotalBytes)}  ·  可用 {GiB(reg.FreeBytes)}  ·  " +
                $"已用 {UsedPercent(reg):F2}%  ·  成员 {reg.OnlineMembers.Count}/{reg.Members.Count} 在线";

            _membersPanel.Children.Clear();
            foreach (var m in reg.Members)
                _membersPanel.Children.Add(BuildMemberCard(m));

            var pick = reg.ChooseForNewFile();
            _nextTarget.Text = pick == null
                ? "没有在线成员"
                : $"新文件将写入：{pick.Id}（{pick.MountPath}）";
            _footer.Text = $"每 2 秒自动刷新 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _footer.Text = "刷新失败：" + ex.Message;
        }
    }

    private Control BuildMemberCard(MemberInfo m)
    {
        var badge = new Border
        {
            Background = m.IsSsd ? Accent : new SolidColorBrush(Color.Parse("#3A3D41")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            Child = new TextBlock
            {
                Text = m.IsSsd ? "SSD 加速层" : "HDD 容量层",
                FontSize = 11,
                Foreground = Brushes.White,
            },
        };

        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"{m.Id}  {m.DiskName ?? "未知磁盘"}",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = TextMain,
                },
                badge,
            },
        };

        var info = new TextBlock
        {
            Text = m.Online
                ? $"{m.MountPath}  ·  {GiB(m.TotalBytes)}  ·  可用 {GiB(m.FreeBytes)}"
                : $"{m.MountPath}  ·  离线" + (m.Error != null ? $"（{m.Error}）" : ""),
            FontSize = 12,
            Foreground = TextDim,
        };

        var healthText = m.DiskHealth ?? "未知";
        var tempText = m.TemperatureCelsius.HasValue ? $"{m.TemperatureCelsius} °C" : "N/A";
        var stats = new TextBlock
        {
            Text = $"健康度：{healthText}   ·   温度：{tempText}   ·   使用率：{(m.Online ? $"{m.UsedPercent * 100:F2}%" : "—")}",
            FontSize = 12,
            Foreground = m.DiskHealth == "Unhealthy" ? Red : TextDim,
        };

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = m.Online ? m.UsedPercent * 100 : 100,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Foreground = m.Online ? UsageBrush(m.UsedPercent) : Red,
            Background = new SolidColorBrush(Color.Parse("#3A3D41")),
        };

        return new Border
        {
            Background = Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { title, info, stats, bar },
            },
        };
    }

    private static IBrush UsageBrush(double used) =>
        used >= 0.85 ? Red : used >= 0.70 ? Orange : Green;

    private static string GiB(long bytes) => $"{bytes / 1073741824.0:F1} GB";

    private static double UsedPercent(MemberRegistry reg) =>
        reg.TotalBytes > 0 ? (reg.TotalBytes - reg.FreeBytes) * 100.0 / reg.TotalBytes : 0;
}
