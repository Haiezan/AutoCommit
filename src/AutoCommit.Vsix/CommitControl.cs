using System;
using System.IO;
using System.Net.Http;
using System.Text;
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
    private readonly TextBox files = Editor(120);
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
    private readonly TextBlock configurationStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly Expander settingsSection = new Expander { Header = "模型设置", IsExpanded = true };
    private readonly TextBlock attempt = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private string projectRoot = "";
    private string loadedModelJson = "";
    private int requestTimeoutSeconds = 240;
    private int inputBudget = 48000;
    private bool? enableThinking;
    private bool generationPending;
    private bool updatingResult;
    private bool initialized;
    private bool loadingSettings;
    private bool settingsSaved;
    private string loadedRulePath = "";
    private string loadedRuleText = "";
    private CancellationTokenSource? cancellation;
    private readonly RepositoryProvider git = new RepositoryProvider();
    private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };

    public CommitControl()
    {
        SetResourceReference(BackgroundProperty, VsBrushes.WindowKey);
        SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
        var shell = new DockPanel { Margin = new Thickness(12) };
        Content = shell;
        var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); shell.Children.Add(footer);
        var actions = new WrapPanel(); actions.Children.Add(generate); actions.Children.Add(copy); actions.Children.Add(commit); actions.Children.Add(cancel);
        footer.Children.Add(modelStatus); footer.Children.Add(configurationStatus); footer.Children.Add(repositoryStatus); footer.Children.Add(actions); footer.Children.Add(progress); footer.Children.Add(status);
        status.Margin = new Thickness(0, 6, 0, 0);
        status.Text = "先选择变更范围，再配置模型并审核提交说明。";
        var panel = new StackPanel();
        shell.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = "AutoCommit", FontSize = 22, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "开发者：Jason Jia", Margin = new Thickness(0, 2, 0, 0) });
        panel.Children.Add(new TextBlock { Text = "读取变更 · 生成说明 · 审核后提交", Margin = new Thickness(0, 4, 0, 12) });

        var settingsPanel = new StackPanel { Margin = new Thickness(8) };
        Add(settingsPanel, "服务地址（Base URL）", endpoint);
        endpoint.ToolTip = "例如 http://localhost:11434/v1；不要包含 /chat/completions";
        Add(settingsPanel, "模型名称", model);
        Add(settingsPanel, "API Key", key);
        key.ToolTip = "Key 按项目与服务地址隔离，在本机加密保存；无认证的本地服务可留空。";
        settingsPanel.Children.Add(allowHttp);
        var openLog = new Button { Content = "查看运行日志" };
        openLog.Click += (sender, args) => {
            try {
                Directory.CreateDirectory(RunLog.DirectoryPath);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RunLog.DirectoryPath) { UseShellExecute = true });
            } catch (Exception ex) { status.Text = "无法打开日志目录：" + ex.Message; }
        };
        settingsPanel.Children.Add(openLog);
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
        files.IsReadOnly = true; files.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        repositoryPanel.Children.Add(new Expander { Header = "变更文件清单", Content = files });
        panel.Children.Add(new GroupBox { Header = "1 · 选择变更", Content = repositoryPanel, Margin = new Thickness(0, 10, 0, 8) });

        diff.IsReadOnly = true; diff.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        diff.TextWrapping = TextWrapping.NoWrap; diff.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        diff.Height = 220; rules.Height = 220;
        panel.Children.Add(new Expander { Header = "变更内容预览", Content = diff });
        var rulePanel = new StackPanel(); rulePanel.Children.Add(ruleSource); rulePanel.Children.Add(rules);
        panel.Children.Add(new Expander { Header = "提交规范", Content = rulePanel });
        panel.Children.Add(new TextBlock { Text = "生成时向配置的模型服务发送所选变更和规范。超出单次预算时会分批汇总。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var output = new StackPanel { Margin = new Thickness(8) };
        var outputHeader = new DockPanel(); outputHeader.Children.Add(draftState);
        undo.HorizontalAlignment = HorizontalAlignment.Right; outputHeader.Children.Add(undo); output.Children.Add(outputHeader);
        result.MinHeight = 180; result.Height = 220; output.Children.Add(result); output.Children.Add(count); output.Children.Add(usage); output.Children.Add(attempt);
        output.Children.Add(new Expander { Header = "更多用量信息", Content = usageDetails });
        output.Children.Add(new TextBlock { Text = "Git 提交到本地；TFVC 签入直接写入服务器。请核对范围。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        panel.Children.Add(new GroupBox { Header = "提交说明", Content = output, Margin = new Thickness(0, 10, 0, 0) });
        SizeChanged += (s, e) => result.Height = Math.Max(180, ActualHeight - 460);
        foreach (var editor in new Control[] { directory, endpoint, model, key, rules, diff, files, result }) {
            editor.SetResourceReference(BackgroundProperty, VsBrushes.WindowKey);
            editor.SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
            editor.Padding = new Thickness(8);
        }
        AutomationProperties.SetName(result, "提交说明，可编辑");
        AutomationProperties.SetName(diff, "变更内容预览，只读");
        AutomationProperties.SetName(files, "变更文件清单，只读");
        AutomationProperties.SetName(rules, "Markdown 提交规范");
        foreach (var button in new[] { refresh, generate, copy, commit, detect, test, save, cancel, undo }) {
            button.Padding = new Thickness(12, 6, 12, 6); button.Margin = new Thickness(0, 3, 8, 3);
        }
        result.TextChanged += (s, e) => { if (!updatingResult) draft.Edit(result.Text); UpdateActions(); };
        directory.TextChanged += (s, e) => { snapshot = null; projectRoot = ""; loadedModelJson = ""; key.Clear(); diff.Clear(); files.Clear(); directory.ToolTip = directory.Text; summary.Text = "目录已变更，请重新读取；已有说明已保留。"; InvalidateModelState(); UpdateActions(); };
        rules.TextChanged += (s, e) => {
            if (rules.Text != loadedRuleText) status.Text = "规范已修改，仅影响后续生成；当前提交说明未改变。";
            if (snapshot != null) UpdateDiffPreview();
        };
        endpoint.TextChanged += (s, e) => { key.Clear(); ValidateSettings(); InvalidateModelState(); };
        model.TextChanged += (s, e) => { ValidateSettings(); InvalidateModelState(); };
        key.PasswordChanged += (s, e) => InvalidateModelState();
        allowHttp.Checked += (s, e) => InvalidateModelState();
        allowHttp.Unchecked += (s, e) => InvalidateModelState();
        undo.Click += (s, e) => {
            if (draft.Edited && !Confirm("撤销替换？", "当前说明包含手动修改。撤销将恢复上一版说明，并替换当前修改。", "撤销替换")) return;
            draft.Undo(); ApplyDraft(); status.Text = "已恢复上一版说明。提交前会重新检查变更。";
        };
        cancel.Click += (s, e) => cancellation?.Cancel();
        test.Click += async (s, e) => await RunAsync("测试连接", async (token, run) => {
            modelStatus.Text = "正在验证模型连接…";
            testUsage.Text = "连接测试：正在等待服务端用量…";
            var response = await new CommitGenerator(Client).TestConnectionAsync(CurrentSettings(), key.Password, token);
            testUsage.Text = "连接测试成功 · " + response.Seconds.ToString("F1") + " 秒 · 输入 " + TokenValue(response.InputTokens) + " / 输出 " + TokenValue(response.OutputTokens) + " Token";
            modelStatus.Text = "连接成功：当前地址、认证及模型可正常调用。可保存配置。";
        }, modelStatus, operationTimeout: false);
        save.Click += async (s, e) => await RunAsync("保存配置", (token, run) => {
            if (!ValidateSettings()) throw new InvalidOperationException(validation.Text);
            if (!string.IsNullOrEmpty(projectRoot) && !Directory.Exists(ProjectConfiguration.Folder(projectRoot)) &&
                !Confirm("创建项目配置？", "将在 " + ProjectConfiguration.Folder(projectRoot) + " 创建 model.json、commit-rules.md、.gitignore 和 README.md。API Key 仅保存在本机。", "保存项目配置")) {
                status.Text = "已取消保存项目配置，未写入文件。"; return Task.CompletedTask;
            }
            SaveConfiguration(CurrentSettings()); settingsSection.IsExpanded = false;
            settingsSaved = true; UpdateConfigurationStatus();
            status.Text = string.IsNullOrEmpty(projectRoot) ? "尚未识别仓库，已保存为全局默认配置。" : "已保存到 .auto/model.json；API Key 已在本机加密保存。";
            return Task.CompletedTask;
        });
        detect.Click += async (s, e) => await RunAsync("识别项目", async (token, run) => {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token); manual.IsChecked = false; directory.Text = RepositoryDiscovery.FindCurrent(); await ReadChangesAsync(token, run);
        }, repositoryStatus);
        refresh.Click += async (s, e) => await RunAsync("读取变更", async (token, run) => {
            if (manual.IsChecked != true) directory.Text = RepositoryDiscovery.FindCurrent();
            await ReadChangesAsync(token, run);
        }, repositoryStatus);
        generate.Click += async (s, e) => await RunAsync("生成提交说明", async (token, run) => {
            var selected = snapshot ?? throw new InvalidOperationException("请先读取变更。");
            var settings = CurrentSettings();
            var currentRules = rules.Text;
            var apiKey = key.Password;
            if (!string.IsNullOrWhiteSpace(result.Text) && !Confirm("重新生成提交说明？",
                (draft.Edited ? "当前说明包含手动修改。" : "当前已有提交说明。") + "新结果生成成功后将替换当前内容；失败或取消会保留原内容。", "重新生成")) {
                status.Text = "已取消重新生成，原内容已保留。"; return;
            }
            var needsBatching = CommitGenerator.NeedsBatching(selected, currentRules, settings.MaxInputCharacters);
            using var capture = needsBatching ? new FileStream(Path.Combine(Path.GetTempPath(), $"AutoCommit-{Guid.NewGuid():N}.diff"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 8192, FileOptions.DeleteOnClose) : null;
            status.Text = needsBatching ? "正在校验并读取完整变更以准备分批…" : "正在校验变更…";
            ChangeSnapshot current;
            var snapshotTimer = System.Diagnostics.Stopwatch.StartNew();
            using (var writer = capture == null ? null : new StreamWriter(capture, new UTF8Encoding(false), 8192, leaveOpen: true))
                current = await git.ReadAsync(directory.Text, token, includeTfvcDiff: false, capture: writer);
            run.Write($"生成前读取快照 {selected.Files.Count} 项 {snapshotTimer.ElapsedMilliseconds} ms");
            EnsureSame(selected, current);
            if (capture != null) capture.Position = 0;
            using var reader = capture == null ? null : new StreamReader(capture, Encoding.UTF8, false, 8192, leaveOpen: true);
            generationPending = true; attempt.Text = "本次生成：正在请求…";
            draftState.Text = "生成中 · 原内容已保留"; cancel.Content = "取消生成";
            status.Text = "正在生成，原内容已保留…";
            GenerationResult response;
            var batchProgress = new Progress<string>(message => { run.Write(message); if (generationPending) status.Text = message; });
            try {
                response = await Task.Run(() => new CommitGenerator(Client).GenerateAsync(selected, currentRules, settings,
                    apiKey, token, reader, batchProgress, run.Write), token);
                modelStatus.Text = response.FallbackReason == null ? "模型：调用成功" : "模型：" + response.FallbackReason;
            }
            catch (Exception ex) { modelStatus.Text = "模型调用失败：" + ex.Message; throw; }

            generationPending = false;
            attempt.Text = $"本次模型请求 {response.Requests} 次 · 输入 {TokenValue(response.InputTokens)} / 输出 {TokenValue(response.OutputTokens)} Token";
            run.Write($"模型结束 请求 {response.Requests} 次");
            snapshotTimer.Restart();
            current = await git.ReadAsync(directory.Text, token, includeTfvcDiff: false);
            run.Write($"生成后读取快照 {snapshotTimer.ElapsedMilliseconds} ms");
            EnsureSame(selected, current);
            if (response.FallbackReason != null && !string.IsNullOrWhiteSpace(result.Text) &&
                !Confirm("使用本地摘要？", response.FallbackReason + " 选择使用会替换现有说明。", "使用本地摘要")) {
                status.Text = "原说明已保留。"; return;
            }
            draft.Accept(response, selected); ApplyDraft();
            status.Text = response.FallbackReason == null ? "已生成，请核对提交规范及事实准确性。" : "已生成本地摘要。" + response.FallbackReason;
            run.Write("草稿已回填");
        }, operationTimeout: false);
        copy.Click += async (s, e) => await RunAsync("复制说明", async (token, run) => {
            var text = result.Text;
            var copyReason = draft.Committed ? "此说明已用于提交。" : draft.Snapshot == null ? "此说明没有对应的已读取变更。" : "";
            if (copyReason.Length == 0) {
                try {
                    var current = await git.ReadAsync(directory.Text, token, includeTfvcDiff: false);
                    if (draft.Snapshot!.Root != current.Root || draft.Snapshot.Fingerprint != current.Fingerprint)
                        copyReason = "当前变更已变化，此说明可能过期。";
                } catch (Exception ex) when (!(ex is OperationCanceledException)) {
                    copyReason = "无法确认当前变更：" + ex.Message;
                }
            }
            if (copyReason.Length != 0) {
                var choice = ConfirmStaleCopy(copyReason);
                if (choice == CopyChoice.Reload) { await ReadChangesAsync(token, run); return; }
                if (choice != CopyChoice.Copy) { status.Text = "已取消复制。"; return; }
            }
            var copied = await ClipboardRetry.TryWriteAsync(() => Clipboard.SetText(text), token);
            if (!copied) {
                result.Focus(); result.SelectAll();
                status.Text = "剪贴板暂时无法打开，未完成复制。说明已保留并选中，请稍后按 Ctrl+C，或关闭占用剪贴板的软件后重试。";
                return;
            }
            status.Text = copyReason.Length == 0 ? "已复制提交说明。" : "已按你的选择复制原说明，请核对后使用。";
        });
        commit.Click += async (s, e) => await RunAsync("提交或签入", async (token, run) => {
            var selected = draft.Snapshot ?? throw new InvalidOperationException("请先生成提交说明。");
            EnsureSame(selected, await git.ReadAsync(directory.Text, token, includeTfvcDiff: false));
            var message = result.Text;
            if (!ConfirmCommit(selected, message)) { status.Text = "已取消提交。"; return; }
            cancel.IsEnabled = false; draftState.Text = "正在提交…"; status.Text = git.IsTfvc ? "正在签入 TFS 服务器…" : "正在提交到本地 Git，请等待 hooks 和签名完成…";
            // Do not cancel a write operation midway: a killed Git may already have committed.
            var outputText = await git.CommitAsync(selected, message, CancellationToken.None);
            draft.MarkCommitted(); snapshot = null; diff.Clear(); summary.Text = "提交完成，请读取下一次变更。";
            repositoryStatus.Text = status.Text = (git.IsTfvc ? "服务器签入结果：\n" : "本地提交成功（未推送）：\n") + outputText.Trim();
        });
        Loaded += async (s, e) => {
            if (initialized) return; initialized = true;
            if (manual.IsChecked != true && cancellation == null)
                await RunAsync("初始化读取", async (token, run) => { directory.Text = RepositoryDiscovery.FindCurrent(); await ReadChangesAsync(token, run); }, repositoryStatus);
        };
        try {
            loadingSettings = true;
            var settings = SettingsStore.Load();
            endpoint.Text = settings.BaseUrl; model.Text = settings.Model; allowHttp.IsChecked = settings.AllowHttp;
            requestTimeoutSeconds = settings.RequestTimeoutSeconds;
            inputBudget = settings.MaxInputCharacters; enableThinking = settings.EnableThinking;
            key.Password = SettingsStore.LoadKey(); settingsSection.IsExpanded = string.IsNullOrWhiteSpace(settings.Model);
            settingsSaved = SettingsStore.HasGlobalSettings; UpdateConfigurationStatus();
        }
        catch { status.Text = "无法加载本地设置，请重新填写。"; }
        finally { loadingSettings = false; }
        Unloaded += (s, e) => cancellation?.Cancel();
    }

    private async Task ReadChangesAsync(CancellationToken token, RunLog readLog)
    {
        snapshot = null; diff.Clear(); files.Clear();
        readLog.Write("开始读取变更");
        var current = await git.ReadAsync(directory.Text, token);
        readLog.Write($"读取完成 {current.Files.Count} 项");
        var modelPath = ProjectConfiguration.ModelPath(current.Root);
        var modelJson = File.Exists(modelPath) ? File.ReadAllText(modelPath) : "";
        if (!string.Equals(projectRoot, current.Root, StringComparison.OrdinalIgnoreCase) || loadedModelJson != modelJson) {
            var projectSettings = ProjectConfiguration.LoadOrDefault(current.Root, SettingsStore.Load());
            loadingSettings = true;
            try {
                endpoint.Text = projectSettings.BaseUrl; model.Text = projectSettings.Model;
                allowHttp.IsChecked = projectSettings.AllowHttp; inputBudget = projectSettings.MaxInputCharacters;
                enableThinking = projectSettings.EnableThinking;
                requestTimeoutSeconds = projectSettings.RequestTimeoutSeconds;
                key.Password = modelJson.Length == 0 ? "" : SettingsStore.LoadProjectKey(current.Root, projectSettings.BaseUrl);
            } finally { loadingSettings = false; }
            projectRoot = current.Root; loadedModelJson = modelJson;
            settingsSaved = modelJson.Length != 0;
            modelStatus.Text = "模型：当前设置尚未测试"; UpdateConfigurationStatus();
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
        snapshot = current;
        files.Text = FormatFiles(current);
        commit.Content = git.IsTfvc ? "签入 TFS 服务器" : "提交到本地 Git";
        repositoryStatus.Text = (git.IsTfvc ? "TFVC：" : "Git：") + "读取成功 · " + current.Root;
        summary.Text = (git.IsTfvc ? "工作区：" + current.Branch.Substring("TFVC / ".Length) : "分支：" + current.Branch) + " · " + current.Files.Count +
            (git.IsTfvc ? " 项包含的更改" : " 个暂存文件") + "\n" + current.Statistics;
        status.Text = !current.HasChanges
            ? git.IsTfvc ? "当前目录内没有包含项，请在 Team Explorer 的“包含的更改”中确认本次签入的文件。" : "暂存区没有变更，请先暂存修改。"
            : !string.IsNullOrWhiteSpace(draft.Text) && !draft.Matches(current) ? "变更已读取，已有说明已保留并标记过期。请重新生成。" : "变更已读取，可以生成提交说明。";
        UpdateDiffPreview();
    }

    private void UpdateDiffPreview()
    {
        if (snapshot == null) { diff.Clear(); return; }
        try { diff.Text = CommitGenerator.PreviewDiff(snapshot, rules.Text, inputBudget); }
        catch (InvalidOperationException ex) { diff.Text = status.Text = ex.Message; }
    }

    private static string TokenValue(long? value) => value?.ToString("N0") ?? "服务未返回";

    private void ApplyDraft()
    {
        updatingResult = true;
        try { result.Text = draft.Text; } finally { updatingResult = false; }
        var response = draft.Response;
        if (response == null) { usage.Text = "Token：无模型响应记录"; usageDetails.Text = ""; }
        else if (response.FallbackReason != null) {
            usage.Text = $"本地摘要 · 模型请求 {response.Requests} 次";
            usageDetails.Text = response.FallbackReason;
        }
        else {
            usage.Text = "模型 " + response.Model + " · " + response.Seconds.ToString("F1") + " 秒 · " + response.Requests + " 次请求\n输入 " + TokenValue(response.InputTokens) +
                " · 输出 " + TokenValue(response.OutputTokens) + " · 总计 " + TokenValue(response.TotalTokens) + " Token";
            usageDetails.Text = "本次生成的模型请求合计，手动编辑不改变用量。\n缓存输入 " + TokenValue(response.CachedTokens) + " / 推理输出 " + TokenValue(response.ReasoningTokens) + "（子项不重复相加）";
        }
        UpdateActions();
    }

    private bool ValidateSettings()
    {
        var valid = Uri.TryCreate(endpoint.Text, UriKind.Absolute, out var address) && (address.Scheme == "http" || address.Scheme == "https");
        validation.Text = !valid ? "请输入有效的 HTTP(S) 服务地址。" : string.IsNullOrWhiteSpace(model.Text) ? "请填写模型名称。" : "";
        return string.IsNullOrEmpty(validation.Text);
    }

    private void InvalidateModelState()
    {
        if (loadingSettings) return;
        modelStatus.Text = "模型：当前设置尚未测试";
        settingsSaved = false;
        UpdateConfigurationStatus();
    }

    private void UpdateConfigurationStatus()
    {
        save.Content = string.IsNullOrEmpty(projectRoot) ? "保存本机默认配置" : "保存项目配置";
        configurationStatus.Text = settingsSaved ?
            (string.IsNullOrEmpty(projectRoot) ? "配置来源：本机默认设置（已保存）" : "配置来源：项目 .auto/model.json（已保存）") :
            (string.IsNullOrEmpty(projectRoot) ? "配置来源：本机默认设置；当前内容尚未保存" :
                loadedModelJson.Length == 0 ? "配置来源：本机默认设置；项目尚无 model.json" : "配置来源：项目 .auto/model.json；当前修改尚未保存");
    }

    private static string FormatFiles(ChangeSnapshot current)
    {
        if (current.Files.Count == 0) return "未发现变更文件。";
        var lines = new string[current.Files.Count];
        for (var i = 0; i < lines.Length; i++) {
            var file = current.Files[i];
            var operation = file.Operation.Replace("Undelete", "恢复").Replace("Add", "新增")
                .Replace("Edit", "编辑").Replace("Delete", "删除").Replace("Rename", "重命名")
                .Replace("Branch", "分支").Replace("Merge", "合并");
            lines[i] = string.IsNullOrEmpty(operation) ? file.Path : operation + "  " + file.Path;
        }
        return string.Join("\n", lines);
    }

    private void UpdateActions()
    {
        var busy = cancellation != null;
        var hasText = !string.IsNullOrWhiteSpace(result.Text);
        count.Text = "标题 " + result.Text.Split('\n')[0].TrimEnd('\r').Length + " / 72 字符（默认建议） · 共 " + result.Text.Length + " 字符";
        generate.Content = hasText ? "重新生成" : "生成提交说明";
        generate.IsEnabled = !busy && snapshot?.HasChanges == true;
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

    private enum CopyChoice { Cancel, Reload, Copy }

    private CopyChoice ConfirmStaleCopy(string reason)
    {
        var choice = CopyChoice.Cancel;
        var dialog = new Window { Title = "复制前核对说明", Width = 500, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this) };
        var panel = new StackPanel { Margin = new Thickness(20) }; dialog.Content = panel;
        panel.Children.Add(new TextBlock { Text = reason + "\n你可以重新读取变更，或明确选择复制当前说明。", TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 18) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancelButton = new Button { Content = "取消", IsCancel = true, IsDefault = true };
        var reloadButton = new Button { Content = "重新读取" };
        var copyButton = new Button { Content = "仍复制此说明" };
        foreach (var button in new[] { cancelButton, reloadButton, copyButton }) {
            button.Padding = new Thickness(12, 6, 12, 6); button.Margin = new Thickness(4);
            buttons.Children.Add(button);
        }
        cancelButton.Click += (s, e) => dialog.DialogResult = false;
        reloadButton.Click += (s, e) => { choice = CopyChoice.Reload; dialog.DialogResult = true; };
        copyButton.Click += (s, e) => { choice = CopyChoice.Copy; dialog.DialogResult = true; };
        panel.Children.Add(buttons); dialog.Loaded += (s, e) => cancelButton.Focus();
        dialog.ShowDialog(); return choice;
    }

    private bool ConfirmCommit(ChangeSnapshot selected, string message)
    {
        var tfvc = selected.Branch.StartsWith("TFVC / ", StringComparison.Ordinal);
        var dialog = new Window { Title = tfvc ? "确认签入 TFS 服务器" : "确认提交到本地 Git",
            Width = 620, Height = 590, MinWidth = 440, MinHeight = 400, ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this) };
        var shell = new DockPanel { Margin = new Thickness(20) }; dialog.Content = shell;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(buttons, Dock.Bottom); shell.Children.Add(buttons);
        var no = new Button { Content = "取消", IsCancel = true, IsDefault = true };
        var yes = new Button { Content = tfvc ? "签入服务器" : "提交到本地" };
        foreach (var button in new[] { no, yes }) {
            button.Padding = new Thickness(14, 6, 14, 6); button.Margin = new Thickness(4); buttons.Children.Add(button);
        }
        no.Click += (s, e) => dialog.DialogResult = false;
        yes.Click += (s, e) => dialog.DialogResult = true;
        var panel = new StackPanel(); shell.Children.Add(panel);
        panel.Children.Add(new TextBlock { Text = (tfvc ? "工作区：" + selected.Branch.Substring("TFVC / ".Length) : "分支：" + selected.Branch) + "\n范围：" + selected.Root +
            "\n" + selected.Files.Count + (tfvc ? " 项 Team Explorer 包含的更改，均位于所选目录范围内。" : " 个暂存文件；提交后不会推送。"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "变更文件", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBox { Text = FormatFiles(selected), IsReadOnly = true, Height = 190,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(new TextBlock { Text = "提交说明", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(new TextBox { Text = message, IsReadOnly = true, Height = 190, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Loaded += (s, e) => no.Focus();
        return dialog.ShowDialog() == true;
    }

    private void SaveConfiguration(ModelSettings settings)
    {
        if (string.IsNullOrEmpty(projectRoot)) SettingsStore.Save(settings, key.Password);
        else {
            ProjectConfiguration.Initialize(projectRoot, SettingsStore.Load());
            SettingsStore.SaveProject(projectRoot, settings, key.Password);
            loadedModelJson = File.ReadAllText(ProjectConfiguration.ModelPath(projectRoot));
        }
    }

    private ModelSettings CurrentSettings() => new ModelSettings {
        BaseUrl = endpoint.Text.Trim(), Model = model.Text.Trim(), AllowHttp = allowHttp.IsChecked == true,
        RequestTimeoutSeconds = requestTimeoutSeconds, MaxInputCharacters = inputBudget, EnableThinking = enableThinking
    };

    private void EnsureSame(ChangeSnapshot expected, ChangeSnapshot actual)
    {
        if (!string.Equals(expected.Root, actual.Root, StringComparison.OrdinalIgnoreCase) || expected.Fingerprint != actual.Fingerprint)
        { snapshot = null; UpdateActions(); throw new InvalidOperationException("仓库或选定的变更已变化。说明已保留，请重新读取并生成。"); }
    }
    private async Task RunAsync(string operation, Func<CancellationToken, RunLog, Task> action, TextBlock? operationStatus = null, bool operationTimeout = true)
    {
        if (cancellation != null) return;
        cancellation = operationTimeout ? new CancellationTokenSource(TimeSpan.FromMinutes(3)) : new CancellationTokenSource();
        refresh.IsEnabled = generate.IsEnabled = copy.IsEnabled = commit.IsEnabled = detect.IsEnabled = manual.IsEnabled = test.IsEnabled = save.IsEnabled = false;
        cancel.IsEnabled = true; progress.Visibility = Visibility.Visible; progress.IsIndeterminate = true;
        directory.IsEnabled = endpoint.IsEnabled = model.IsEnabled = key.IsEnabled = allowHttp.IsEnabled = rules.IsEnabled = false;
        result.IsReadOnly = true; undo.IsEnabled = false;
        if (operationStatus != null) operationStatus.Text = "处理中…";
        status.Text = "处理中…";
        var run = new RunLog();
        run.Write("开始 " + operation);
        try { await action(cancellation.Token, run); }
        catch (OperationCanceledException) {
            run.Write("已取消或到时");
            status.Text = !operationTimeout ? "操作已取消，原说明已保留。" :
                "操作已取消或超时；若已发送请求，服务端仍可能计费用量。";
        }
        catch (HttpRequestException) { run.Write("连接失败"); status.Text = "连接失败，已有说明已保留。请检查服务地址、代理、防火墙及证书。"; }
        catch (Exception ex) { run.Write("操作失败 " + ex.GetType().Name); status.Text = "操作失败，已有说明已保留。\n" + ex.Message; }
        finally {
            run.Write("结束 " + operation);
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
