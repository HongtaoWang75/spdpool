using System.Text.Json;
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
    private readonly TextBlock _ioStatus = new();
    private readonly TextBlock _ioRate = new();
    private readonly DispatcherTimer _timer;

    public MainWindow()
    {
        FontFamily = new FontFamily("avares://SpdPool.UI/Assets/fonts#HarmonyOS Sans SC");
        Title = "SpdPool 存储池";
        Width = 820;
        Height = 600;
        MinWidth = 680;
        MinHeight = 420;
        Background = Bg;
        Content = BuildLayout();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
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

        _ioRate.FontSize = 22;
        _ioRate.FontWeight = FontWeight.Bold;
        _ioRate.Foreground = Accent;
        _ioRate.VerticalAlignment = VerticalAlignment.Center;

        var topBar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        topBar.Children.Add(header);
        Grid.SetColumn(_ioRate, 1);
        topBar.Children.Add(_ioRate);

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

        _ioStatus.FontSize = 13;
        _ioStatus.FontWeight = FontWeight.SemiBold;
        _ioStatus.Foreground = Accent;
        _ioStatus.TextWrapping = TextWrapping.Wrap;

        var ioCard = new Border
        {
            Background = Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10),
            Child = _ioStatus,
        };

        _nextTarget.FontSize = 13;
        _nextTarget.Foreground = TextDim;

        var panel = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(28, 24),
            Children =
            {
                topBar,
                summaryCard,
                ioCard,
                memberHeader,
                _membersPanel,
                _nextTarget,
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

            try
            {
                var json = File.ReadAllText(@"C:\ProgramData\SpdPool\stats.json");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var r = root.GetProperty("readBytesPerSec").GetInt64();
                var w = root.GetProperty("writeBytesPerSec").GetInt64();
                var q = root.GetProperty("queueDepth").GetInt32();
                var active = root.GetProperty("taskActive").GetBoolean();
                var task = active ? root.GetProperty("task").GetString() : "无迁移任务";
                _ioRate.Text = $"↑ {FmtRate(r)}   ↓ {FmtRate(w)}";
                _ioStatus.Text = $"队列 {q}  ·  {task}";
            }
            catch
            {
                _ioStatus.Text = "统计不可用（服务未运行或 stats.json 未生成）";
            }
        }
        catch (Exception ex)
        {
            _ioStatus.Text = "刷新失败：" + ex.Message;
        }
    }

    private static Control Tag(string text, IBrush background = null)
    {
        return new Border
        {
            Background = background ?? new SolidColorBrush(Color.Parse("#3A3D41")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = TextMain,
            },
        };
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
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = TextMain,
                },
                badge,
            },
        };

        var healthText = m.DiskHealth ?? "未知";
        var tags = new WrapPanel();
        tags.Children.Add(Tag(m.Online
            ? $"使用率 {m.UsedPercent * 100:F2}%"
            : "离线",
            m.Online ? null : Red));
        tags.Children.Add(Tag($"容量 {GiB(m.TotalBytes)}"));
        tags.Children.Add(Tag($"可用 {GiB(m.FreeBytes)}"));
        tags.Children.Add(Tag($"健康 {healthText}",
            m.DiskHealth == "Unhealthy" ? Red : null));
        tags.Children.Add(Tag(m.MountPath));

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
                Spacing = 8,
                Children = { title, tags, bar },
            },
        };
    }

    private static IBrush UsageBrush(double used) =>
        used >= 0.85 ? Red : used >= 0.70 ? Orange : Green;

    private static string GiB(long bytes) => $"{bytes / 1073741824.0:F1} GB";

    private static string FmtRate(long bytesPerSec) =>
        bytesPerSec >= 1048576 ? $"{bytesPerSec / 1048576.0:F1} MB/s"
        : bytesPerSec >= 1024 ? $"{bytesPerSec / 1024.0:F1} KB/s"
        : $"{bytesPerSec} B/s";

    private static double UsedPercent(MemberRegistry reg) =>
        reg.TotalBytes > 0 ? (reg.TotalBytes - reg.FreeBytes) * 100.0 / reg.TotalBytes : 0;
}
