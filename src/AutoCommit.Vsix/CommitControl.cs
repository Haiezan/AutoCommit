using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
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

internal sealed partial class CommitControl : UserControl
{
    private readonly TextBox directory = new TextBox();
    private readonly TextBox endpoint = new TextBox();
    private readonly TextBox model = new TextBox();
    private readonly PasswordBox key = new PasswordBox();
    private readonly TextBlock credentialStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel credentialActions = new WrapPanel();
    private bool editingKey, loadingKey, keyUnavailable;
    private readonly Button saveProjectKey = new Button { Content = "保存项目专用 Key" };
    private readonly Button useShared = new Button { Content = "恢复使用共享 Key" };
    private readonly Button anonymous = new Button { Content = "此项目不发送 Key" };
    private readonly CheckBox allowHttp = new CheckBox { Content = "允许本机 / 内网 HTTP（明文传输）" };
    private readonly TextBox rules = Editor(240);
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
    private readonly TabItem compose = new TabItem { Header = "提交说明" };
    private readonly TabItem settingsSection = new TabItem { Header = "设置" };
    private readonly TextBlock sendHint = new TextBlock { TextWrapping = TextWrapping.Wrap };
    private readonly Button configure = new Button { Content = "配置模型" };
    private readonly ComboBox credentialChoice = new ComboBox();
    private readonly Button saveShared = new Button { Content = "保存本机共享 Key" };
    private readonly TextBlock sharedHint = new TextBlock { Text = "更新共享 Key 会影响使用此服务地址共享凭据的项目。", TextWrapping = TextWrapping.Wrap };
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
        footer.Children.Add(progress);
        footer.Children.Add(new ScrollViewer { Content = status, MaxHeight = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        footer.Children.Add(cancel);
        var tabs = new TabControl();
        tabs.SetResourceReference(BackgroundProperty, VsBrushes.WindowKey);
        tabs.SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
        shell.Children.Add(tabs);
        var details = new TabItem { Header = "查看变更" };
        var diagnostics = new TabItem { Header = "运行详情" };
        foreach (var tab in new[] { compose, details, settingsSection, diagnostics }) tabs.Items.Add(tab);
        tabs.Items.Add(CreateAbout());
        compose.IsSelected = true;

        var settingsPanel = new StackPanel { Margin = new Thickness(8) };
        Add(settingsPanel, "服务地址（Base URL）", endpoint);
        endpoint.ToolTip = "例如 http://localhost:11434/v1；不要包含 /chat/completions";
        settingsPanel.Children.Add(allowHttp);
        Add(settingsPanel, "模型名称", model);
        settingsPanel.Children.Add(credentialStatus);
        var authentication = new StackPanel();
        settingsPanel.Children.Add(new Expander { Header = "更改认证方式", Content = authentication });
        foreach (var label in new[] { "本机共享 Key", "项目专用 Key", "恢复使用共享 Key", "此项目不发送 Key" })
            credentialChoice.Items.Add(new ComboBoxItem { Content = label });
        Add(authentication, "认证方式", credentialChoice);
        AutomationProperties.SetName(key, "API Key");
        authentication.Children.Add(key);
        authentication.Children.Add(sharedHint);
        foreach (var button in new[] { saveShared, saveProjectKey, useShared, anonymous }) credentialActions.Children.Add(button);
        authentication.Children.Add(credentialActions);
        credentialChoice.SelectionChanged += (s, e) => UpdateCredentialActions();
        credentialChoice.SelectedIndex = 0;
        saveShared.Click += (s, e) => SaveCredential(KeyScope.Shared);
        saveProjectKey.Click += (s, e) => SaveCredential(KeyScope.Project);
        anonymous.Click += (s, e) => SaveCredential(KeyScope.Anonymous);
        useShared.Click += (s, e) => SaveCredential(null);
        var openLog = new Button { Content = "打开日志目录" };
        openLog.Click += (sender, args) => {
            try {
                Directory.CreateDirectory(RunLog.DirectoryPath);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(RunLog.DirectoryPath) { UseShellExecute = true });
            } catch (Exception ex) { status.Text = "无法打开日志目录：" + ex.Message; }
        };
        settingsPanel.Children.Add(configurationStatus);
        settingsPanel.Children.Add(modelStatus);
        var configActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        configActions.Children.Add(test); configActions.Children.Add(save); settingsPanel.Children.Add(configActions);
        settingsPanel.Children.Add(new TextBlock { Text = "测试连接仅发送固定测试文本，可能产生少量调用费用。", TextWrapping = TextWrapping.Wrap });
        settingsSection.Content = Scroll(settingsPanel);
        settingsPanel.Children.Add(validation); settingsPanel.Children.Add(testUsage);
        settingsPanel.Children.Add(new TextBlock { Text = "项目配置：.auto/model.json；规范：.auto/commit-rules.md。Key 仅在本机保存。Key 按服务地址选择。", TextWrapping = TextWrapping.Wrap });

        var editorPanel = new Grid { Margin = new Thickness(6) };
        editorPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        editorPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        editorPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        compose.Content = editorPanel;
        var scope = new StackPanel();
        editorPanel.Children.Add(scope);
        scope.Children.Add(summary);
        var scopeActions = new StackPanel();
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        scopeActions.Children.Add(refresh);
        var switchPanel = new StackPanel();
        switchPanel.Children.Add(detect);
        switchPanel.Children.Add(manual);
        directory.IsReadOnly = true;
        Add(switchPanel, "仓库或 TFVC 范围", directory);
        var switchScope = new Expander { Header = "切换范围", Content = switchPanel };
        scopeActions.Children.Add(switchScope);
        scope.Children.Add(scopeActions);
        manual.Checked += (s, e) => { directory.IsReadOnly = false; directory.Focus(); };
        manual.Unchecked += (s, e) => directory.IsReadOnly = true;
        var header = new WrapPanel();
        header.Children.Add(draftState);
        header.Children.Add(undo);
        scope.Children.Add(header);

        result.Height = double.NaN;
        Grid.SetRow(result, 1);
        editorPanel.Children.Add(result);
        var tools = new StackPanel();
        Grid.SetRow(tools, 2);
        editorPanel.Children.Add(tools);
        tools.Children.Add(count);
        tools.Children.Add(sendHint);
        var actions = new WrapPanel();
        foreach (var button in new[] { configure, generate, copy, commit }) actions.Children.Add(button);
        tools.Children.Add(actions);
        configure.Click += (s, e) => {
            settingsSection.IsSelected = true;
            settingsSection.UpdateLayout();
            FocusMissingSetting();
        };

        var changes = new StackPanel { Margin = new Thickness(6) };
        details.Content = Scroll(changes);
        changes.Children.Add(repositoryStatus);
        files.IsReadOnly = diff.IsReadOnly = true;
        files.FontFamily = diff.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        ConfigureChangeViews(changes);
        ConfigureDiagnostics(diagnostics, openLog);
        PreviewKeyDown += (s, e) => {
            if (e.Key == System.Windows.Input.Key.Escape && cancel.IsEnabled && cancellation != null) {
                cancellation.Cancel(); e.Handled = true;
            }
        };
        foreach (var editor in new Control[] { directory, endpoint, model, key, rules, diff, files, result }) {
            editor.SetResourceReference(BackgroundProperty, VsBrushes.WindowKey);
            editor.SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
            editor.Padding = new Thickness(8);
        }
        AutomationProperties.SetName(result, "提交说明，可编辑");
        AutomationProperties.SetName(diff, "变更内容预览，只读");
        AutomationProperties.SetName(files, "变更文件清单，只读");
        AutomationProperties.SetName(rules, "Markdown 提交规范");
        foreach (var button in new[] { refresh, generate, configure, copy, commit, detect, test, save, cancel, undo }) {
            button.Padding = new Thickness(12, 6, 12, 6); button.Margin = new Thickness(0, 3, 8, 3);
        }
        result.TextChanged += (s, e) => { if (!updatingResult) draft.Edit(result.Text); UpdateActions(); };
        directory.TextChanged += (s, e) => { InvalidateViews(); openRules.IsEnabled = false; snapshot = null; projectRoot = ""; loadedModelJson = ""; LoadCredential(); diff.Clear(); files.Clear(); directory.ToolTip = directory.Text; summary.Text = "目录已变更，请重新读取；已有说明已保留。"; InvalidateModelState(); UpdateActions(); };
        rules.TextChanged += (s, e) => {
            MarkViewOld(rules);
            UpdateRuleSource();
            if (rules.Text != loadedRuleText) status.Text = "规范已修改，仅影响后续生成；当前提交说明未改变。";
            if (snapshot != null) { MarkViewOld(diff); UpdateDiffPreview(); }
            UpdateActions();
        };
        endpoint.TextChanged += (s, e) => { if (!loadingSettings) LoadCredential(); InvalidateModelState(); };
        model.TextChanged += (s, e) => InvalidateModelState();
        key.PasswordChanged += (s, e) => {
            if (loadingKey) return;
            editingKey = true;
            credentialStatus.Text = "正在使用本次输入的 Key，选择保存范围后可在下次使用。";
            modelStatus.Text = "模型：当前设置尚未测试";
        };
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
            var response = await new CommitGenerator(Client).TestConnectionAsync(CurrentSettings(), RequestKey(), token);
            testUsage.Text = "连接测试成功 · " + response.Seconds.ToString("F1") + " 秒 · 输入 " + TokenValue(response.InputTokens) + " / 输出 " + TokenValue(response.OutputTokens) + " Token";
            modelStatus.Text = "连接成功：当前地址、认证及模型可正常调用。可保存配置。";
        }, modelStatus, operationTimeout: false);
        save.Click += async (s, e) => await RunAsync("保存配置", (token, run) => {
            if (!ValidateSettings(forRequest: false)) throw new InvalidOperationException(validation.Text);
            if (!string.IsNullOrEmpty(projectRoot) && !Directory.Exists(ProjectConfiguration.Folder(projectRoot)) &&
                !Confirm("创建项目配置？", "将在 " + ProjectConfiguration.Folder(projectRoot) + " 创建 model.json、commit-rules.md、.gitignore 和 README.md。API Key 仅保存在本机。", "保存项目配置")) {
                status.Text = "已取消保存项目配置，未写入文件。"; return Task.CompletedTask;
            }
            SaveConfiguration(CurrentSettings()); compose.IsSelected = true;
            if (snapshot != null) { openRules.IsEnabled = File.Exists(loadedRulePath); UpdateRuleSource(); }
            settingsSaved = true; UpdateConfigurationStatus();
            status.Text = string.IsNullOrEmpty(projectRoot) ? "尚未识别仓库，已保存为全局默认配置。" : "模型设置已保存到 .auto/model.json。";
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
            run.Context = $"{(git.IsTfvc ? "TFVC" : "Git")} · {selected.Root} · {settings.Model}\n{ruleSource.Text}";

            var apiKey = RequestKey();
            if (!string.IsNullOrWhiteSpace(result.Text) && !Confirm("重新生成提交说明？",
                (draft.Edited ? "当前说明包含手动修改。" : "当前已有提交说明。") + "新结果生成成功后将替换当前内容；失败或取消会保留原内容。", "重新生成")) {
                status.Text = "已取消重新生成，原内容已保留。"; return;
            }
            if (selected.CanStageAll) {
                if (!Confirm("暂存全部 Git 更改？", selected.Root + "\n将暂存此仓库内的修改、删除及未被忽略的新增文件，然后生成提交说明。", "暂存全部并生成")) {
                    status.Text = "已取消暂存。"; return;
                }
                selected = await git.StageAllAsync(selected, token);
                ShowSnapshot(selected);
                if (!selected.HasChanges) { status.Text = "暂存后没有可用于生成说明的差异。"; return; }
            }
            var needsBatching = CommitGenerator.NeedsBatching(selected, currentRules, settings.MaxInputCharacters);
            run.Context += $"\n{selected.Files.Count} 个文件 · {(needsBatching ? "分批生成" : "直接生成")}";
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
            generationPending = true; attempt.Text = run.Context + "\n正在请求模型…";
            draftState.Text = "生成中 · 原内容已保留"; cancel.Content = "取消生成";
            status.Text = "正在生成，原内容已保留…";
            GenerationResult response;
            var batchProgress = new Progress<string>(message => { run.Write(message); if (generationPending && latestGeneration == run) { status.Text = message; if (stages.IsVisible) stages.Text = run.StageText(); } });
            try {
                response = await Task.Run(() => new CommitGenerator(Client, count => run.Requests = count).GenerateAsync(selected, currentRules, settings,
                    apiKey, token, reader, batchProgress, run.Write), token);
                modelStatus.Text = response.FallbackReason == null ? "模型：调用成功" : "模型：" + response.FallbackReason;
            }
            catch (Exception ex) { modelStatus.Text = "模型调用失败：" + ex.Message; throw; }

            generationPending = false;
            run.Response = response;
            run.Write($"模型结束 请求 {response.Requests} 次");
            snapshotTimer.Restart();
            current = await git.ReadAsync(directory.Text, token, includeTfvcDiff: false);
            run.Write($"生成后读取快照 {snapshotTimer.ElapsedMilliseconds} ms");
            token.ThrowIfCancellationRequested();
            EnsureSame(selected, current);
            if (response.FallbackReason != null && !string.IsNullOrWhiteSpace(result.Text) &&
                !Confirm("使用" + SummaryKind(response) + "？", response.FallbackReason + " 选择使用会替换现有说明。", "使用该摘要", "保留原稿")) {
                status.Text = "已保留原稿。"; return;
            }
            token.ThrowIfCancellationRequested();
            draft.Accept(response, selected); run.Applied = true; ApplyDraft(); result.Focus();
            status.Text = response.FallbackReason == null ? "已生成，请核对提交规范及事实准确性。" : "已生成" + SummaryKind(response) + "。" + response.FallbackReason;
            run.Write("草稿已回填");
        }, operationTimeout: false, generating: true);
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
            cancel.IsEnabled = false; cancel.Visibility = Visibility.Collapsed; draftState.Text = "正在提交…"; status.Text = git.IsTfvc ? "正在签入 TFS 服务器…" : "正在提交到本地 Git，请等待 hooks 和签名完成…";
            // Do not cancel a write operation midway: a killed Git may already have committed.
            var outputText = await git.CommitAsync(selected, message, CancellationToken.None);
            draft.MarkCommitted(); InvalidateViews(); openRules.IsEnabled = false; snapshot = null; diff.Clear(); files.Clear(); summary.Text = "提交完成，请读取下一次变更。";
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
            var legacyUnbound = SettingsStore.MigrateLegacyKey();
            LoadCredential();
            if (legacyUnbound) status.Text = "旧本机 Key 缺少原服务地址，已备份。请为当前地址重新保存共享 Key。";
            if (legacyUnbound || keyUnavailable) settingsSection.IsSelected = true;
            settingsSaved = SettingsStore.HasGlobalSettings; UpdateConfigurationStatus();
        }
        catch { status.Text = "无法加载本地设置，请重新填写。"; }
        finally { loadingSettings = false; }
        cancel.Visibility = Visibility.Collapsed;
        UpdateActions();
        Unloaded += (s, e) => { cancellation?.Cancel(); CloseViews(); };
    }

    private async Task ReadChangesAsync(CancellationToken token, RunLog readLog)
    {
        InvalidateViews(); openRules.IsEnabled = false; snapshot = null; diff.Clear(); files.Clear();
        summary.Text = "当前范围尚未读取成功。";
        summary.ToolTip = directory.Text;
        readLog.Write("开始读取变更");
        var current = await git.ReadAsync(directory.Text, token, detectTfvcScope: manual.IsChecked != true);
        if (git.IsTfvc) directory.Text = current.Root;
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
                projectRoot = current.Root; loadedModelJson = modelJson;
                LoadCredential(readLog);
            } finally { loadingSettings = false; }
            settingsSaved = modelJson.Length != 0;
            modelStatus.Text = "模型：当前设置尚未测试";
            UpdateConfigurationStatus();
            if (keyUnavailable) settingsSection.IsSelected = true;
        }
        var path = ProjectConfiguration.RulesPath(current.Root);
        if (File.Exists(path) && new FileInfo(path).Length > 65536) throw new InvalidOperationException("规范文件超过 64KB。");
        var incomingRules = File.Exists(path) ? File.ReadAllText(path) : DefaultRules.Text;
        var changedSource = loadedRulePath != path || loadedRuleText != incomingRules;
        var temporary = rules.Text != loadedRuleText;
        if (!temporary || (changedSource && Confirm("加载仓库提交规范？", "当前规范包含临时修改。加载将替换这些修改；取消则继续使用当前规范。", "加载规范")))
            rules.Text = incomingRules;
        loadedRuleText = incomingRules;
        loadedRulePath = path;
        openRules.IsEnabled = File.Exists(path);
        UpdateRuleSource();
        ShowSnapshot(current);
    }

    private void ShowSnapshot(ChangeSnapshot current)
    {
        InvalidateViews();
        snapshot = current;
        files.Text = FormatFiles(current);
        commit.Content = git.IsTfvc ? "签入 TFS 服务器" : "提交到本地 Git";
        repositoryStatus.Text = (git.IsTfvc ? "TFVC：" : "Git：") + "读取成功 · " + current.Root;
        summary.Text = (git.IsTfvc ? current.Root : new DirectoryInfo(current.Root).Name) + " · " + (git.IsTfvc ? "工作区：" + current.Branch.Substring("TFVC / ".Length) : "分支：" + current.Branch) + " · " + current.Files.Count +
            (git.IsTfvc ? " 项包含的更改" : " 个暂存文件");
        summary.ToolTip = current.Root + "\n" + current.Statistics;
        if (current.HasChanges)
            status.Text = !string.IsNullOrWhiteSpace(draft.Text) && !draft.Matches(current) ? "变更已读取，已有说明已保留并标记过期。请重新生成。" : "";
        else if (git.IsTfvc)
            status.Text = "当前目录内没有包含项，请在 Team Explorer 的“包含的更改”中确认本次签入的文件。";
        else
            status.Text = current.CanStageAll ? "暂存区为空。可暂存全部并生成说明，或先在 Git 更改窗口暂存所需文件。" : "当前 Git 仓库没有可暂存的更改。";
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
            usage.Text = $"{SummaryKind(response)} · 模型请求 {response.Requests} 次";
            usageDetails.Text = response.FallbackReason;
        }
        else {
            usage.Text = "模型 " + response.Model + " · " + response.Seconds.ToString("F1") + " 秒 · " + response.Requests + " 次请求\n输入 " + TokenValue(response.InputTokens) +
                " · 输出 " + TokenValue(response.OutputTokens) + " · 总计 " + TokenValue(response.TotalTokens) + " Token";
            var cacheShare = response.InputTokens > 0 && response.CachedTokens.HasValue
                ? $"（占输入 {100.0 * response.CachedTokens.Value / response.InputTokens.Value:F1}%）" : "";
            usageDetails.Text = "本次生成的模型请求合计，手动编辑不改变用量。\n缓存输入 " + TokenValue(response.CachedTokens) + cacheShare + " / 推理输出 " + TokenValue(response.ReasoningTokens) + "（子项不重复相加）";
        }
        UpdateActions();
    }

    private bool ValidateSettings(bool forRequest = true)
    {
        var valid = Uri.TryCreate(endpoint.Text, UriKind.Absolute, out var address) && (address.Scheme == "http" || address.Scheme == "https");
        validation.Text = !valid ? "请输入有效的 HTTP(S) 服务地址。" : string.IsNullOrWhiteSpace(model.Text) ? "请填写模型名称。" : "";
        if (forRequest && valid && validation.Text.Length == 0 && address.Scheme == "http" && allowHttp.IsChecked != true) validation.Text = "请在设置中明确允许 HTTP 或使用 HTTPS 地址。";
        allowHttp.Visibility = Visible(valid && address.Scheme == "http");
        return string.IsNullOrEmpty(validation.Text);
    }

    private void InvalidateModelState()
    {
        UpdateActions();
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
        generate.Content = snapshot?.CanStageAll == true ? "暂存全部并生成说明…" : hasText ? "重新生成" : "生成提交说明";
        var configured = ValidateSettings();
        var ready = snapshot?.HasChanges == true || snapshot?.CanStageAll == true;
        configure.Visibility = Visible(!configured);
        configure.IsEnabled = !busy;
        refresh.Content = snapshot == null ? draft.Committed ? "读取下一组变更" : "读取变更" : "刷新";
        copy.Visibility = Visible(hasText);
        undo.Visibility = Visible(draft.CanUndo);
        generate.Visibility = Visible(ready && configured);
        generate.IsEnabled = !busy && ready && configured;
        sendHint.Text = !configured ? validation.Text : ready
            ? "将向 " + new Uri(endpoint.Text.Trim()).Authority + " 发送所选变更和" + (rules.Text != loadedRuleText ? "临时规范。" : "提交规范。") : "";
        count.Visibility = Visible(hasText);
        var canSubmit = hasText && draft.Matches(snapshot);
        commit.Visibility = Visible(canSubmit);
        commit.FontWeight = FontWeights.SemiBold;
        generate.FontWeight = canSubmit ? FontWeights.Normal : FontWeights.SemiBold;
        copy.IsEnabled = !busy && hasText;
        commit.IsEnabled = !busy && canSubmit;
        undo.IsEnabled = !busy && draft.CanUndo;
        draftState.Text = !hasText ? "待生成" : draft.Committed ? "已提交" : draft.Snapshot == null ? "手写草稿" :
            !draft.Matches(snapshot) ? "变更待重新核对 · 已保留草稿" : draft.Edited ? "已手动编辑" : "待审核";
        if (hasText && draft.Response?.FallbackReason != null) draftState.Text += " · " + SummaryKind(draft.Response);
    }

    private bool Confirm(string title, string message, string accept, string decline = "取消")
    {
        var dialog = new Window { Title = title, Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var owner = Window.GetWindow(this); if (owner != null) dialog.Owner = owner;
        dialog.SetResourceReference(BackgroundProperty, VsBrushes.WindowKey); dialog.SetResourceReference(ForegroundProperty, VsBrushes.WindowTextKey);
        var panel = new StackPanel { Margin = new Thickness(20) }; dialog.Content = panel;
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var no = new Button { Content = decline, IsCancel = true, IsDefault = true, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0) };
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

    private void LoadCredential(RunLog? log = null)
    {
        var scope = KeyScope.Shared;
        loadingKey = true;
        try {
            editingKey = false;
            keyUnavailable = false;
            key.Clear();
            for (var i = 1; i < credentialChoice.Items.Count; i++)
                ((ComboBoxItem)credentialChoice.Items[i]).IsEnabled = projectRoot.Length != 0;
            key.Password = SettingsStore.Credentials.Load(projectRoot, endpoint.Text, out scope);
            credentialStatus.Text = scope == KeyScope.Anonymous ? "此项目不发送 Key" :
                scope == KeyScope.Project ? "正在使用项目专用 Key" :
                key.Password.Length == 0 ? "此服务地址尚未配置共享 Key" : "正在使用本机共享 Key";
        } catch (Exception ex) when (ex is CryptographicException || ex is IOException || ex is UnauthorizedAccessException) {
            keyUnavailable = true;
            credentialStatus.Text = (scope == KeyScope.Project ? "项目专用" : "本机共享") +
                " Key 无法读取，请重新填写并保存，或选择其他凭据方式。";
            log?.Write("凭据读取失败 " + scope + " " + ex.GetType().Name);
        } finally {
            credentialChoice.SelectedIndex = scope == KeyScope.Project ? 1 : scope == KeyScope.Anonymous ? 3 : 0;
            loadingKey = false;
        }
    }

    private static Visibility Visible(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static ScrollViewer Scroll(UIElement content) => new ScrollViewer {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    private static string SummaryKind(GenerationResult response) => response.PartialSummary ? "部分摘要" : "本地摘要";

    private void FocusMissingSetting()
    {
        if (!Uri.TryCreate(endpoint.Text, UriKind.Absolute, out var address) || (address.Scheme != "https" && address.Scheme != "http")) endpoint.Focus();
        else if (string.IsNullOrWhiteSpace(model.Text)) model.Focus();
        else allowHttp.Focus();
    }

    private void UpdateCredentialActions()
    {
        var choice = credentialChoice.SelectedIndex;
        key.Visibility = Visible(choice == 0 || choice == 1);
        sharedHint.Visibility = Visible(choice == 0);
        saveShared.Visibility = Visible(choice == 0);
        saveProjectKey.Visibility = Visible(choice == 1);
        useShared.Visibility = Visible(choice == 2);
        anonymous.Visibility = Visible(choice == 3);
    }

    private string RequestKey()
    {
        if (!editingKey) LoadCredential();
        if (!editingKey && keyUnavailable) throw new InvalidOperationException(credentialStatus.Text);
        return key.Password;
    }

    private void SaveCredential(KeyScope? scope)
    {
        if (!Uri.TryCreate(endpoint.Text.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http")) { credentialStatus.Text = "请先填写有效的服务地址。"; return; }
        try {
            if ((scope == KeyScope.Project || scope == KeyScope.Shared) && string.IsNullOrEmpty(key.Password)) {
                credentialStatus.Text = "请先填写要保存的 Key。"; return;
            }
            if (scope.HasValue) SettingsStore.Credentials.Save(projectRoot, endpoint.Text, scope.Value, key.Password);
            if (scope == null || scope == KeyScope.Shared) SettingsStore.Credentials.UseShared(projectRoot, endpoint.Text);
            LoadCredential();
            modelStatus.Text = "模型：当前设置尚未测试";
        } catch (Exception ex) when (ex is CryptographicException || ex is IOException || ex is UnauthorizedAccessException) {
            credentialStatus.Text = "保存凭据失败，请重试。" + ex.GetType().Name;
        }
    }

    private void SaveConfiguration(ModelSettings settings)
    {
        if (string.IsNullOrEmpty(projectRoot)) SettingsStore.Save(settings);
        else {
            ProjectConfiguration.Initialize(projectRoot, SettingsStore.Load());
            ProjectConfiguration.Save(projectRoot, settings);
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
        {
            InvalidateViews(); openRules.IsEnabled = false; snapshot = null; files.Clear(); diff.Clear();
            summary.Text = repositoryStatus.Text = "所选变更已变化，请重新读取。";
            UpdateActions();
            throw new InvalidOperationException("仓库或选定的变更已变化。说明已保留，请重新读取并生成。");
        }
    }
    private async Task RunAsync(string operation, Func<CancellationToken, RunLog, Task> action, TextBlock? operationStatus = null, bool operationTimeout = true, bool generating = false)
    {
        if (cancellation != null) return;
        cancellation = operationTimeout ? new CancellationTokenSource(TimeSpan.FromMinutes(3)) : new CancellationTokenSource();
        refresh.IsEnabled = generate.IsEnabled = copy.IsEnabled = commit.IsEnabled = detect.IsEnabled = manual.IsEnabled = test.IsEnabled = save.IsEnabled = false;
        cancel.Visibility = Visibility.Visible; cancel.IsEnabled = true; progress.Visibility = Visibility.Visible; progress.IsIndeterminate = true;
        credentialChoice.IsEnabled = credentialActions.IsEnabled = directory.IsEnabled = endpoint.IsEnabled = model.IsEnabled = key.IsEnabled = allowHttp.IsEnabled = rules.IsEnabled = false;
        result.IsReadOnly = true; UpdateActions();
        if (operationStatus != null) operationStatus.Text = "处理中…";
        status.Text = "处理中…";
        var run = new RunLog(generating);
        if (generating) {
            latestGeneration = run;
            attempt.Text = "正在核对变更…";
            stages.Text = "";
            copyAttempt.IsEnabled = false;
        }
        run.Write("开始 " + operation);
        try { await action(cancellation.Token, run); }
        catch (OperationCanceledException) {
            run.Write("已取消或到时");
            status.Text = !operationTimeout ? "操作已取消，原说明已保留。" :
                "操作已取消或超时；若已发送请求，服务端仍可能计费用量。";
        }
        catch (HttpRequestException) { run.Write("连接失败"); status.Text = "连接失败，已有说明已保留。请检查服务地址、代理、防火墙及证书。"; }
        catch (TfvcSelectionException ex) { run.Write(ex.Message); status.Text = "操作失败，已有说明已保留。\n" + ex.Message; }
        catch (Exception ex) { run.Write("操作失败 " + ex.GetType().Name); status.Text = "操作失败，已有说明已保留。\n" + ex.Message; }
        finally {
            run.Write("结束 " + operation);
            if (operationStatus != null && (status.Text.StartsWith("操作失败") || status.Text.StartsWith("连接失败") || status.Text.StartsWith("操作已取消"))) operationStatus.Text = status.Text;
            if (generating) {
                generationPending = false;
                run.Finish();
                attempt.Text = FormatAttempt(run, status.Text);
                stages.Text = run.StageText();
                copyAttempt.IsEnabled = true;
            }
            cancel.Content = "取消";
            if (testUsage.Text.Contains("正在等待")) testUsage.Text = "连接测试未获得用量；请求仍可能产生费用。";
            cancellation.Dispose(); cancellation = null;
            credentialChoice.IsEnabled = credentialActions.IsEnabled = directory.IsEnabled = endpoint.IsEnabled = model.IsEnabled = key.IsEnabled = allowHttp.IsEnabled = rules.IsEnabled = true;
            cancel.Visibility = Visibility.Collapsed; cancel.IsEnabled = false; progress.Visibility = Visibility.Collapsed; progress.IsIndeterminate = false;
            refresh.IsEnabled = detect.IsEnabled = manual.IsEnabled = test.IsEnabled = save.IsEnabled = true; result.IsReadOnly = false; UpdateActions();
        }
    }
    private static TextBox Editor(double height) => new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = height, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static void Add(Panel panel, string title, Control control) { panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 8, 0, 3), TextWrapping = TextWrapping.Wrap }); control.Padding = new Thickness(6); AutomationProperties.SetName(control, title); panel.Children.Add(control); }
}
