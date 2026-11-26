using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;

namespace AutoCommit.Vsix;

internal sealed partial class CommitControl
{
    private RunLog? latestGeneration;
    private readonly Button copyAttempt = new Button { Content = "复制本次摘要", IsEnabled = false };
    private readonly TextBox stages = new TextBox { IsReadOnly = true, MaxHeight = 320, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    private void ConfigureDiagnostics(TabItem tab, Button openLog)
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        tab.Content = Scroll(panel);
        Heading(panel, "本次生成");
        attempt.Text = "尚未生成提交说明。";
        panel.Children.Add(attempt);
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        copyAttempt.Click += (s, e) => CopyInfo(attempt.Text);
        actions.Children.Add(copyAttempt);
        actions.Children.Add(openLog);
        panel.Children.Add(actions);
        var details = new Expander { Header = "阶段明细", Content = stages };
        details.Expanded += (s, e) => stages.Text = latestGeneration?.StageText() ?? "";
        panel.Children.Add(details);
        Heading(panel, "当前草稿的生成用量");
        panel.Children.Add(usage);
        panel.Children.Add(usageDetails);
    }

    private static string FormatAttempt(RunLog run, string outcome)
    {
        var response = run.Response;
        var modelResult = response == null ? (run.Requests == 0 ? "未发起请求" : "未取得完整结果") :
            response.FallbackReason == null ? "模型已返回提交说明" : SummaryKind(response);
        var text = $"{outcome}\n{run.Started:yyyy-MM-dd HH:mm:ss} · 耗时 {run.Duration.TotalSeconds:F1} 秒\n{run.Context}" +
            $"\n请求结果：{modelResult} · {run.Requests} 次请求\n草稿：{(run.Applied ? "已回填" : "未回填")}";
        if (response?.FallbackReason != null && !outcome.Contains(response.FallbackReason))
            text += "\n" + response.FallbackReason;
        if (run.Requests == 0) return text;
        if (response == null) return text + "\n本次总用量未知。已返回的单次用量见阶段明细。";
        var ratio = response.InputTokens > 0 && response.CachedTokens.HasValue
            ? $"（{100.0 * response.CachedTokens.Value / response.InputTokens.Value:F1}%）" : "";
        return text + (string.IsNullOrEmpty(response.Model) ? "" : "\n响应模型：" + response.Model) +
            $"\n本次输入 {TokenValue(response.InputTokens)} · 输出 {TokenValue(response.OutputTokens)} Token" +
            $"\n缓存输入 {TokenValue(response.CachedTokens)}{ratio} · 推理输出 {TokenValue(response.ReasoningTokens)}";
    }

    private static void Heading(StackPanel panel, string text) => panel.Children.Add(new TextBlock {
        Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4)
    });

    private void CopyInfo(string text)
    {
        try { Clipboard.SetText(text); status.Text = "已复制。"; }
        catch (System.Runtime.InteropServices.ExternalException) { status.Text = "剪贴板正被占用，请重试。"; }
    }

    private TabItem CreateAbout()
    {
        var panel = new StackPanel { Margin = new Thickness(10) };
        var version = "未读取";
        var publisher = "Jason Jia";
        try {
            var path = Path.Combine(Path.GetDirectoryName(typeof(CommitControl).Assembly.Location)!, "extension.vsixmanifest");
            var manifest = XDocument.Load(path).Root;
            var ns = manifest?.Name.Namespace ?? XNamespace.None;
            var identity = manifest?.Element(ns + "Metadata")?.Element(ns + "Identity");
            version = (string?)identity?.Attribute("Version") ?? version;
            publisher = (string?)identity?.Attribute("Publisher") ?? publisher;
        } catch (Exception ex) when (ex is IOException || ex is System.Xml.XmlException || ex is UnauthorizedAccessException) { }
        using var host = Process.GetCurrentProcess();
        var hostVersion = host.MainModule.FileVersionInfo.ProductVersion;
        var info = $"AutoCommit {version}\nVisual Studio {hostVersion}\n开发者：{publisher}";
        Heading(panel, "AutoCommit");
        panel.Children.Add(new TextBlock { Text = $"版本 {version} · 开发者 {publisher}\nVisual Studio {hostVersion}\n依据 Git 暂存变更或 TFVC 包含的更改与项目规范生成提交说明。",
            TextWrapping = TextWrapping.Wrap });
        var links = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var homepage = new Button { Content = "项目主页" };
        homepage.Click += (s, e) => {
            try { Process.Start(new ProcessStartInfo("https://github.com/Haiezan/AutoCommit") { UseShellExecute = true }); }
            catch (Exception ex) { status.Text = "无法打开项目主页：" + ex.Message; }
        };
        var copyVersion = new Button { Content = "复制版本信息" };
        copyVersion.Click += (s, e) => CopyInfo(info);
        links.Children.Add(homepage); links.Children.Add(copyVersion);
        panel.Children.Add(links);
        return new TabItem { Header = "关于", Content = Scroll(panel) };
    }
}
