using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace AutoCommit.Vsix;

internal sealed partial class CommitControl
{
    private readonly Dictionary<TextBox, Window> views = new Dictionary<TextBox, Window>();
    private readonly Button openRules = new Button { Content = "打开 Markdown 文件", IsEnabled = false };
    private const string OldViewSuffix = "（上次查看的快照）";

    private void ConfigureChangeViews(StackPanel changes)
    {
        diff.TextWrapping = TextWrapping.NoWrap;
        diff.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        var rulePanel = new StackPanel();
        rulePanel.Children.Add(ruleSource);
        rulePanel.Children.Add(rules);
        AddView("变更文件清单", files, files, false);
        AddView("差异预览", diff, diff, true);
        AddView("提交规范", rules, rulePanel, true);
        openRules.Click += (s, e) => {
            ThreadHelper.ThrowIfNotOnUIThread();
            try {
                if (snapshot == null || !File.Exists(loadedRulePath)) {
                    openRules.IsEnabled = false;
                    status.Text = "项目规范文件尚不存在，请重新读取变更。";
                    return;
                }
                VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, loadedRulePath);
            } catch (Exception ex) { status.Text = "无法打开规范文件：" + ex.Message; }
        };
        ToolTipService.SetShowOnDisabled(openRules, true);
        UpdateRuleSource();

        void AddView(string title, TextBox source, FrameworkElement content, bool collapsible)
        {
            var header = new WrapPanel { Margin = new Thickness(0, 8, 0, 3) };
            var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
            var enlarge = new Button { Content = "放大查看", Margin = new Thickness(6, 0, 4, 2) };
            AutomationProperties.SetName(enlarge, "放大查看" + title);
            enlarge.Click += (s, e) => OpenView(title, source);
            header.Children.Add(label);
            header.Children.Add(enlarge);
            if (source == rules) header.Children.Add(openRules);
            if (collapsible) changes.Children.Add(new Expander { Header = header, Content = content });
            else { changes.Children.Add(header); changes.Children.Add(content); }
        }
    }

    private void UpdateRuleSource()
    {
        var source = openRules.IsEnabled ? loadedRulePath : "内置规范";
        ruleSource.Text = "规范来源：" + source + (rules.Text != loadedRuleText ? " · 使用临时修改" : "");
        openRules.ToolTip = openRules.IsEnabled ? loadedRulePath : "请先读取含 .auto/commit-rules.md 的项目范围。";
    }

    private void OpenView(string title, TextBox source)
    {
        if (views.TryGetValue(source, out var previous)) {
            if (!previous.Title.EndsWith(OldViewSuffix, StringComparison.Ordinal)) {
                if (previous.WindowState == WindowState.Minimized) previous.WindowState = WindowState.Normal;
                previous.Activate();
                return;
            }
            previous.Close();
        }
        var owner = Window.GetWindow(this);
        var area = SystemParameters.WorkArea.Size;
        if (PresentationSource.FromVisual(this) is HwndSource host) {
            var monitor = System.Windows.Forms.Screen.FromHandle(host.Handle).WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(this);
            area = new Size(monitor.Width / dpi.DpiScaleX, monitor.Height / dpi.DpiScaleY);
        }
        var window = new Window {
            Title = title + (snapshot == null ? "" : " — " + snapshot.Root),
            Width = Math.Min(1000, area.Width * 0.94), Height = Math.Min(720, area.Height * 0.90),
            Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, UseLayoutRounding = true
        };
        window.SetResourceReference(BackgroundProperty, VsBrushes.WindowKey);
        window.SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
        var panel = new DockPanel { Margin = new Thickness(12) };
        var caption = new TextBlock { Text = source == rules ? ruleSource.Text : summary.Text,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(caption, Dock.Top);
        panel.Children.Add(caption);
        var text = new TextBox { Text = source.Text, IsReadOnly = true, IsUndoEnabled = false,
            FontFamily = source.FontFamily, TextWrapping = source == rules ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        text.SetResourceReference(BackgroundProperty, VsBrushes.WindowKey);
        text.SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
        AutomationProperties.SetName(text, title);
        panel.Children.Add(text);
        window.Content = panel;
        window.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) { e.Handled = true; window.Close(); } };
        window.Closed += (s, e) => views.Remove(source);
        views.Add(source, window);
        window.Show();
    }

    private void MarkViewOld(TextBox source)
    {
        if (views.TryGetValue(source, out var window) && !window.Title.EndsWith(OldViewSuffix, StringComparison.Ordinal))
            window.Title += OldViewSuffix;
    }

    private void InvalidateViews()
    {
        foreach (var source in views.Keys) MarkViewOld(source);
    }

    private void CloseViews()
    {
        foreach (var window in views.Values.ToArray()) window.Close();
    }
}
