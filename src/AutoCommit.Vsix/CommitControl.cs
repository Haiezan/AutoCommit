using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using Task = System.Threading.Tasks.Task;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Microsoft.VisualStudio.Shell;
using AutoCommit.Core;

namespace AutoCommit.Vsix;

// WPF click events require void handlers; RunAsync catches and displays operation failures.
#pragma warning disable VSTHRD101

internal sealed class CommitControl : UserControl
{
    private readonly TextBox directory = new TextBox();
    private readonly TextBox endpoint = new TextBox();
    private readonly TextBox model = new TextBox();
    private readonly PasswordBox key = new PasswordBox();
    private readonly CheckBox allowHttp = new CheckBox { Content = "允许本机 / 内网 HTTP（明文传输）" };
    private readonly TextBox rules = Editor(100);
    private readonly TextBox diff = Editor(150);
    private readonly TextBox result = Editor(150);
    private readonly TextBlock modelStatus = new TextBlock { Text = "模型：尚未测试", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock repositoryStatus = new TextBlock { Text = "仓库：尚未读取", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly Button refresh = new Button { Content = "读取变更", Margin = new Thickness(0, 8, 0, 4) };
    private readonly Button generate = new Button { Content = "生成提交说明", IsEnabled = false };
    private readonly Button copy = new Button { Content = "复制提交说明", IsEnabled = false };
    private readonly Button commit = new Button { Content = "提交到本地 Git", IsEnabled = false };
    private readonly Button detect = new Button { Content = "识别当前项目" };
    private readonly CheckBox manual = new CheckBox { Content = "手工指定仓库" };
    private readonly TextBlock usage = new TextBlock { Text = "Token：尚未调用模型", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock ruleSource = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly Button test = new Button { Content = "测试连接" };
    private readonly Button save = new Button { Content = "保存配置" };
    private readonly Button cancel = new Button { Content = "取消", IsEnabled = false };
    private readonly ProgressBar progress = new ProgressBar { Height = 3, Visibility = Visibility.Collapsed };
    private readonly TextBlock summary = new TextBlock { Text = "尚未读取变更", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock count = new TextBlock { Text = "0 字符", HorizontalAlignment = HorizontalAlignment.Right };
    private ChangeSnapshot? snapshot;
    private readonly CommitDraft draft = new CommitDraft();
    private readonly Button undo = new Button { Content = "撤销替换", IsEnabled = false };
    private readonly TextBlock draftState = new TextBlock { Text = "待生成", FontWeight = FontWeights.SemiBold };
    private readonly TextBlock testUsage = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock usageDetails = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly Expander settingsSection = new Expander { Header = "模型设置", IsExpanded = true };
    private readonly TextBlock attempt = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private string projectRoot = "";
    private string loadedModelJson = "";
    private int inputBudget = 48000;
    private bool generationPending;
    private bool updatingResult;
    private bool initialized;
    private string loadedRulePath = "";
    private string loadedRuleText = "";
    private CancellationTokenSource? cancellation;
    private readonly RepositoryProvider git = new RepositoryProvider();
    private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(120) };

    public CommitControl()
    {
        SetResourceReference(BackgroundProperty, VsBrushes.WindowKey);
        SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
        var shell = new DockPanel { Margin = new Thickness(12) };
        Content = shell;
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); shell.Children.Add(footer);
        var actions = new WrapPanel(); actions.Children.Add(generate); actions.Children.Add(copy); actions.Children.Add(commit); actions.Children.Add(cancel);
        footer.Children.Add(modelStatus); footer.Children.Add(repositoryStatus); footer.Children.Add(actions); footer.Children.Add(progress); footer.Children.Add(status);
        status.Margin = new Thickness(0, 6, 0, 0);
        status.Text = "先配置模型并测试连接，再读取 Git 暂存变更。";
        var panel = new StackPanel();
        shell.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = "AutoCommit", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "读取变更 · 生成说明 · 审核后提交", Margin = new Thickness(0, 4, 0, 12) });

        var settingsPanel = new StackPanel { Margin = new Thickness(8) };
        Add(settingsPanel, "服务地址（Base URL）", endpoint);
        endpoint.ToolTip = "例如 http://localhost:11434/v1；不要包含 /chat/completions";
        Add(settingsPanel, "模型名称", model);
        Add(settingsPanel, "API Key", key);
        key.ToolTip = "Key 按项目与服务地址隔离，在本机加密保存；无认证的本地服务可留空。";
        settingsPanel.Children.Add(allowHttp);
        var configActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        configActions.Children.Add(test); configActions.Children.Add(save); settingsPanel.Children.Add(configActions);
        settingsPanel.Children.Add(new TextBlock { Text = "测试连接仅发送固定测试文本，可能产生少量调用费用。", TextWrapping = TextWrapping.Wrap });
        settingsSection.Content = settingsPanel; panel.Children.Add(settingsSection);
        settingsPanel.Children.Add(validation); settingsPanel.Children.Add(testUsage);
        settingsPanel.Children.Add(new TextBlock { Text = "项目配置：.auto/model.json；规范：.auto/commit-rules.md。Key 仅在本机保存。更换地址后需重新填写 Key。", TextWrapping = TextWrapping.Wrap });

        var repositoryPanel = new StackPanel { Margin = new Thickness(8) };
        var repositoryTools = new WrapPanel(); repositoryTools.Children.Add(refresh); repositoryTools.Children.Add(detect); repositoryPanel.Children.Add(repositoryTools);
        directory.IsReadOnly = true;
        Add(repositoryPanel, "当前仓库 / TFVC 范围", directory);
        var manualPanel = new StackPanel(); manualPanel.Children.Add(manual);
        repositoryPanel.Children.Add(new Expander { Header = "切换仓库 / 手工指定", Content = manualPanel });
        manual.Checked += (s, e) => directory.IsReadOnly = false;
        manual.Unchecked += (s, e) => directory.IsReadOnly = true;
        repositoryPanel.Children.Add(summary);
        panel.Children.Add(new GroupBox { Header = "1 · 选择变更", Content = repositoryPanel, Margin = new Thickness(0, 10, 0, 8) });

        diff.IsReadOnly = true; diff.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        diff.TextWrapping = TextWrapping.NoWrap; diff.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        diff.Height = 220; rules.Height = 220;
        panel.Children.Add(new Expander { Header = "差异预览", Content = diff });
        var rulePanel = new StackPanel(); rulePanel.Children.Add(ruleSource); rulePanel.Children.Add(rules);
        panel.Children.Add(new Expander { Header = "提交规范", Content = rulePanel });
        panel.Children.Add(new TextBlock { Text = "生成时向配置的模型服务发送暂存差异和规范。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var output = new StackPanel { Margin = new Thickness(8) };
        var outputHeader = new DockPanel(); outputHeader.Children.Add(draftState);
        undo.HorizontalAlignment = HorizontalAlignment.Right; outputHeader.Children.Add(undo); output.Children.Add(outputHeader);
        result.MinHeight = 180; result.Height = 220; output.Children.Add(result); output.Children.Add(count); output.Children.Add(usage); output.Children.Add(attempt);
        output.Children.Add(new Expander { Header = "更多用量信息", Content = usageDetails });
        output.Children.Add(new TextBlock { Text = "Git 提交到本地；TFVC 签入直接写入服务器。请核对范围。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(new GroupBox { Header = "提交说明", Content = output, Margin = new Thickness(0, 10, 0, 0) });
        SizeChanged += (s, e) => result.Height = Math.Max(180, ActualHeight - 460);
        foreach (var editor in new Control[] { directory, endpoint, model, key, rules, diff, result }) {
            editor.SetResourceReference(BackgroundProperty, VsBrushes.WindowKey);
            editor.SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
            editor.Padding = new Thickness(8);
        }
        AutomationProperties.SetName(result, "提交说明，可编辑");
        AutomationProperties.SetName(diff, "暂存差异，只读");
        AutomationProperties.SetName(rules, "Markdown 提交规范");
        foreach (var button in new[] { refresh, generate, copy, commit, detect, test, save, cancel, undo }) {
            button.Padding = new Thickness(12, 6, 12, 6); button.Margin = new Thickness(0, 3, 8, 3);
        }
        result.TextChanged += (s, e) => { if (!updatingResult) draft.Edit(result.Text); UpdateActions(); };
        directory.TextChanged += (s, e) => { snapshot = null; projectRoot = ""; loadedModelJson = ""; key.Clear(); diff.Clear(); directory.ToolTip = directory.Text; summary.Text = "目录已变更，请重新读取；已有说明已保留。"; UpdateActions(); };
        rules.TextChanged += (s, e) => { if (rules.Text != loadedRuleText) status.Text = "规范已修改，仅影响后续生成；当前提交说明未改变。"; };
        endpoint.TextChanged += (s, e) => { key.Clear(); ValidateSettings(); }; model.TextChanged += (s, e) => ValidateSettings();
        undo.Click += (s, e) => {
            if (draft.Edited && !Confirm("撤销替换？", "当前说明包含手动修改。撤销将恢复上一版说明，并替换当前修改。", "撤销替换")) return;
            draft.Undo(); ApplyDraft(); status.Text = "已恢复上一版说明。提交前会重新检查变更。";
        };
        cancel.Click += (s, e) => cancellation?.Cancel();
        test.Click += async (s, e) => await RunAsync(async token => {
            modelStatus.Text = "正在验证模型连接…";
            testUsage.Text = "连接测试：正在等待服务端用量…";
            var response = await new CommitGenerator(Client).TestConnectionAsync(CurrentSettings(), key.Password, token);
            testUsage.Text = "连接测试成功 · " + response.Seconds.ToString("F1") + " 秒 · 输入 " + TokenValue(response.InputTokens) + " / 输出 " + TokenValue(response.OutputTokens) + " Token";
            modelStatus.Text = "连接成功：当前地址、认证及模型可正常调用。可保存配置。";
        }, modelStatus);
        save.Click += async (s, e) => await RunAsync(token => {
            if (!ValidateSettings()) throw new InvalidOperationException(validation.Text);
            SaveConfiguration(CurrentSettings()); settingsSection.IsExpanded = false;
            status.Text = string.IsNullOrEmpty(projectRoot) ? "尚未识别仓库，已保存为全局默认配置。" : "已保存到 .auto/model.json；API Key 已在本机加密保存。";
            return Task.CompletedTask;
        });
        detect.Click += async (s, e) => await RunAsync(async token => {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token); manual.IsChecked = false; directory.Text = RepositoryDiscovery.FindCurrent(); await ReadChangesAsync(token);
        }, repositoryStatus);
        refresh.Click += async (s, e) => await RunAsync(async token => {
            if (manual.IsChecked != true) directory.Text = RepositoryDiscovery.FindCurrent();
            await ReadChangesAsync(token);
        }, repositoryStatus);
        generate.Click += async (s, e) => await RunAsync(async token => {
            var selected = snapshot ?? throw new InvalidOperationException("请先读取变更。");
            var settings = CurrentSettings();
            var current = await git.ReadAsync(directory.Text, token);
            EnsureSame(selected, current);
            if (!string.IsNullOrWhiteSpace(result.Text) && !Confirm("重新生成提交说明？",
                (draft.Edited ? "当前说明包含手动修改。" : "当前已有提交说明。") + "新结果生成成功后将替换当前内容；失败或取消会保留原内容。", "重新生成")) {
                status.Text = "已取消重新生成，原内容已保留。"; return;
            }
            SaveConfiguration(settings);
            generationPending = true; attempt.Text = "本次生成：正在请求…";
            draftState.Text = "生成中 · 原内容已保留"; cancel.Content = "取消生成";
            status.Text = "正在生成，原内容已保留…";
            GenerationResult response;
            try { response = await new CommitGenerator(Client).GenerateAsync(selected, rules.Text, settings, key.Password, token); modelStatus.Text = "模型：调用成功"; }
            catch (Exception ex) { modelStatus.Text = "模型调用失败：" + ex.Message; throw; }

            generationPending = false; attempt.Text = "最近请求：输入 " + TokenValue(response.InputTokens) + " / 输出 " + TokenValue(response.OutputTokens) + " Token";
            current = await git.ReadAsync(directory.Text, token);
            EnsureSame(selected, current);
            draft.Accept(response, selected); ApplyDraft();
            status.Text = "已生成。当前版本将 Markdown 作为提示词；请人工检查规范及事实准确性。";
        });
        copy.Click += async (s, e) => await RunAsync(async token => {
            var text = result.Text;
            var copied = await ClipboardRetry.TryWriteAsync(() => Clipboard.SetText(text), token);
            if (!copied) {
                result.Focus(); result.SelectAll();
                status.Text = "剪贴板暂时无法打开，未完成复制。说明已保留并选中，请稍后按 Ctrl+C，或关闭占用剪贴板的软件后重试。";
                return;
            }
            status.Text = draft.Matches(snapshot) ? "已复制提交说明。" : "已复制。注意：此说明已过期或已提交，请核对后使用。";
        });
        commit.Click += async (s, e) => await RunAsync(async token => {
            var selected = draft.Snapshot ?? throw new InvalidOperationException("请先生成提交说明。");
            EnsureSame(selected, await git.ReadAsync(directory.Text, token));
            var message = result.Text;
            var answer = MessageBox.Show("仓库：" + selected.Root + "\n分支：" + selected.Branch + "\n\n" + message +
                (git.IsTfvc ? "\n\n将此目录下全部挂起更改签入 TFS 服务器？不使用 Team Explorer 的勾选范围。" : "\n\n提交当前暂存内容到本地 Git？不会推送到远端。"), "确认提交 / 签入", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) { status.Text = "已取消提交。"; return; }
            cancel.IsEnabled = false; draftState.Text = "正在提交…"; status.Text = git.IsTfvc ? "正在签入 TFS 服务器…" : "正在提交到本地 Git，请等待 hooks 和签名完成…";
            // Do not cancel a write operation midway: a killed Git may already have committed.
            var outputText = await git.CommitAsync(selected, message, CancellationToken.None);
            draft.MarkCommitted(); snapshot = null; diff.Clear(); summary.Text = "提交完成，请读取下一次变更。";
            repositoryStatus.Text = status.Text = (git.IsTfvc ? "服务器签入结果：\n" : "本地提交成功（未推送）：\n") + outputText.Trim();
        });
        Loaded += async (s, e) => {
            if (initialized) return; initialized = true;
            if (manual.IsChecked != true && cancellation == null)
                await RunAsync(async token => { directory.Text = RepositoryDiscovery.FindCurrent(); await ReadChangesAsync(token); }, repositoryStatus);
        };
        try { var settings = SettingsStore.Load(); endpoint.Text = settings.BaseUrl; model.Text = settings.Model; allowHttp.IsChecked = settings.AllowHttp; key.Password = SettingsStore.LoadKey(); settingsSection.IsExpanded = string.IsNullOrWhiteSpace(settings.Model); }
        catch { status.Text = "无法加载本地设置，请重新填写。"; }
        Unloaded += (s, e) => cancellation?.Cancel();
    }

    private async Task ReadChangesAsync(CancellationToken token)
    {
        snapshot = null; diff.Clear();
        var current = await git.ReadAsync(directory.Text, token);
        ProjectConfiguration.Initialize(current.Root, SettingsStore.Load());
        var modelJson = File.ReadAllText(ProjectConfiguration.ModelPath(current.Root));
        if (!string.Equals(projectRoot, current.Root, StringComparison.OrdinalIgnoreCase) || loadedModelJson != modelJson) {
            var projectSettings = ProjectConfiguration.Load(current.Root);
            endpoint.Text = projectSettings.BaseUrl; model.Text = projectSettings.Model;
            allowHttp.IsChecked = projectSettings.AllowHttp; inputBudget = projectSettings.MaxInputCharacters;
            key.Password = SettingsStore.LoadProjectKey(current.Root, projectSettings.BaseUrl);
            projectRoot = current.Root; loadedModelJson = modelJson;
            settingsSection.IsExpanded = string.IsNullOrWhiteSpace(projectSettings.Model);
        }
        var path = ProjectConfiguration.RulesPath(current.Root);
        if (File.Exists(path) && new FileInfo(path).Length > 65536) throw new InvalidOperationException("规范文件超过 64KB。");
        var incomingRules = File.Exists(path) ? File.ReadAllText(path) : DefaultRules.Text;
        if ((loadedRulePath != path || loadedRuleText != incomingRules) && rules.Text != loadedRuleText && !string.IsNullOrWhiteSpace(rules.Text)) {
            if (Confirm("加载仓库提交规范？", "当前规范包含临时修改。加载将替换这些修改；取消则继续使用当前规范。", "加载规范")) {
                loadedRuleText = incomingRules; rules.Text = incomingRules;
            }
        } else if (loadedRulePath != path || rules.Text == loadedRuleText) { loadedRuleText = incomingRules; rules.Text = incomingRules; }
        loadedRulePath = path;
        ruleSource.Text = rules.Text != incomingRules ? "规范来源：保留的临时修改（当前仓库规范未覆盖）" : File.Exists(path) ? "规范来源：" + path : "规范来源：内置完整规范（可在此临时编辑）";
        snapshot = current; diff.Text = current.Diff;
        commit.Content = git.IsTfvc ? "签入 TFS 服务器" : "提交到本地 Git";
        repositoryStatus.Text = (git.IsTfvc ? "TFVC：" : "Git：") + "读取成功 · " + current.Root;
        summary.Text = "分支：" + current.Branch + " · 暂存差异 " + current.Diff.Length + " 字符\n" + current.Statistics;
        status.Text = string.IsNullOrWhiteSpace(current.Diff) ? "未发现变更：Git 请暂存修改；TFVC 请先在 Team Explorer 确认挂起更改。" : !string.IsNullOrWhiteSpace(draft.Text) && !draft.Matches(current) ? "变更已读取，已有说明已保留并标记过期。请重新生成。" : "变更已读取，可以生成提交说明。";
    }

    private static string TokenValue(long? value) => value?.ToString("N0") ?? "服务未返回";

    private void ApplyDraft()
    {
        updatingResult = true;
        try { result.Text = draft.Text; } finally { updatingResult = false; }
        var response = draft.Response;
        if (response == null) { usage.Text = "Token：无模型响应记录"; usageDetails.Text = ""; }
        else {
            usage.Text = "模型 " + response.Model + " · " + response.Seconds.ToString("F1") + " 秒\n输入 " + TokenValue(response.InputTokens) +
                " · 输出 " + TokenValue(response.OutputTokens) + " · 总计 " + TokenValue(response.TotalTokens) + " Token";
            usageDetails.Text = "对应原始模型响应，手动编辑不改变用量。\n缓存输入 " + TokenValue(response.CachedTokens) + " / 推理输出 " + TokenValue(response.ReasoningTokens) + "（子项不重复相加）";
        }
        UpdateActions();
    }

    private bool ValidateSettings()
    {
        var valid = Uri.TryCreate(endpoint.Text, UriKind.Absolute, out var address) && (address.Scheme == "http" || address.Scheme == "https");
        validation.Text = !valid ? "请输入有效的 HTTP(S) 服务地址。" : string.IsNullOrWhiteSpace(model.Text) ? "请填写模型名称。" : "";
        return string.IsNullOrEmpty(validation.Text);
    }

    private void UpdateActions()
    {
        var busy = cancellation != null;
        var hasText = !string.IsNullOrWhiteSpace(result.Text);
        count.Text = "标题 " + result.Text.Split('\n')[0].TrimEnd('\r').Length + " / 72 字符（默认建议） · 共 " + result.Text.Length + " 字符";
        generate.Content = hasText ? "重新生成" : "生成提交说明";
        generate.IsEnabled = !busy && !string.IsNullOrWhiteSpace(snapshot?.Diff);
        copy.IsEnabled = !busy && hasText;
        commit.IsEnabled = !busy && hasText && draft.Matches(snapshot);
        undo.IsEnabled = !busy && draft.CanUndo;
        draftState.Text = !hasText ? "待生成" : draft.Committed ? "已提交" : !draft.Matches(snapshot) ? "已过期 · 内容已保留" : draft.Edited ? "已手动编辑" : "待审核";
    }

    private bool Confirm(string title, string message, string accept)
    {
        var dialog = new Window { Title = title, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var owner = Window.GetWindow(this); if (owner != null) dialog.Owner = owner;
        dialog.SetResourceReference(BackgroundProperty, VsBrushes.WindowKey); dialog.SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
        var panel = new StackPanel { Margin = new Thickness(20) }; dialog.Content = panel;
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var no = new Button { Content = "取消", IsCancel = true, IsDefault = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
        var yes = new Button { Content = accept, Padding = new Thickness(14, 6, 14, 6) }; yes.Click += (s, e) => dialog.DialogResult = true;
        buttons.Children.Add(no); buttons.Children.Add(yes); panel.Children.Add(buttons); dialog.Loaded += (s, e) => no.Focus();
        return dialog.ShowDialog() == true;
    }

    private void SaveConfiguration(ModelSettings settings)
    {
        if (string.IsNullOrEmpty(projectRoot)) SettingsStore.Save(settings, key.Password);
        else {
            SettingsStore.SaveProject(projectRoot, settings, key.Password);
            loadedModelJson = File.ReadAllText(ProjectConfiguration.ModelPath(projectRoot));
        }
    }

    private ModelSettings CurrentSettings() => new ModelSettings { BaseUrl = endpoint.Text.Trim(), Model = model.Text.Trim(), AllowHttp = allowHttp.IsChecked == true, MaxInputCharacters = inputBudget };

    private void EnsureSame(ChangeSnapshot expected, ChangeSnapshot actual)
    {
        if (!string.Equals(expected.Root, actual.Root, StringComparison.OrdinalIgnoreCase) || expected.Fingerprint != actual.Fingerprint)
        { snapshot = null; UpdateActions(); throw new InvalidOperationException("仓库、分支或暂存内容已变化。说明已保留，请重新读取并生成。"); }
    }
    private async Task RunAsync(Func<CancellationToken, Task> action, TextBlock? operationStatus = null)
    {
        if (cancellation != null) return;
        cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        refresh.IsEnabled = generate.IsEnabled = copy.IsEnabled = commit.IsEnabled = detect.IsEnabled = manual.IsEnabled = test.IsEnabled = save.IsEnabled = false;
        cancel.IsEnabled = true; progress.Visibility = Visibility.Visible; progress.IsIndeterminate = true;
        directory.IsEnabled = endpoint.IsEnabled = model.IsEnabled = key.IsEnabled = allowHttp.IsEnabled = rules.IsEnabled = false;
        result.IsReadOnly = true; undo.IsEnabled = false;
        if (operationStatus != null) operationStatus.Text = "处理中…";
        status.Text = "处理中…";
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { status.Text = "操作已取消或超时；若已发送请求，服务端仍可能计费用量。"; }
        catch (HttpRequestException) { status.Text = "连接失败，已有说明已保留。请检查服务地址、代理、防火墙及证书。"; }
        catch (Exception ex) { status.Text = "操作失败，已有说明已保留。\n" + ex.Message; }
        finally {
            if (operationStatus != null && (status.Text.StartsWith("操作失败") || status.Text.StartsWith("连接失败") || status.Text.StartsWith("操作已取消"))) operationStatus.Text = status.Text;
            if (generationPending) { attempt.Text = "本次生成未获得用量，可能仍产生费用；原说明及其用量已保留。"; generationPending = false; }
            cancel.Content = "取消";
            if (testUsage.Text.Contains("正在等待")) testUsage.Text = "连接测试未获得用量；请求仍可能产生费用。";
            cancellation.Dispose(); cancellation = null;
            directory.IsEnabled = endpoint.IsEnabled = model.IsEnabled = key.IsEnabled = allowHttp.IsEnabled = rules.IsEnabled = true;
            cancel.IsEnabled = false; progress.Visibility = Visibility.Collapsed; progress.IsIndeterminate = false;
            refresh.IsEnabled = detect.IsEnabled = manual.IsEnabled = test.IsEnabled = save.IsEnabled = true; result.IsReadOnly = false; UpdateActions();
        }
    }
    private static TextBox Editor(double height) => new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static void Add(Panel panel, string title, Control control) { panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 8, 0, 3), TextWrapping = TextWrapping.Wrap }); control.Padding = new Thickness(6); AutomationProperties.SetName(control, title); panel.Children.Add(control); }
}
