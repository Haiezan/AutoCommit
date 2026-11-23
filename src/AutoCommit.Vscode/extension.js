'use strict';

const vscode = require('vscode');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { credentialIds, readCredential, saveCredential } = require('./lib/credentials');
const { readGitSnapshot, sameSnapshot, hasChangesToStage, stageAllChanges, readProjectConfiguration,
  needsBatching, generateMessage, testConnection, serviceUrl } = require('./lib/core');

async function chooseRepository(sourceControl, allowNoRepository = false) {
  const extension = vscode.extensions.getExtension('vscode.git');
  if (!extension && allowNoRepository) return null;
  if (!extension) throw new Error('未找到 VS Code 内置 Git 扩展。');
  const git = (await extension.activate()).getAPI(1);
  const root = sourceControl?.rootUri;
  if (root?.scheme === 'file') {
    const selected = git.getRepository(root) || await git.openRepository(root);
    if (!selected) throw new Error('无法打开所选的 Git 仓库。');
    return selected;
  }
  if (!git.repositories.length && vscode.workspace.workspaceFolders?.length === 1)
    await git.openRepository(vscode.workspace.workspaceFolders[0].uri);
  const repos = git.repositories;
  if (!repos.length && allowNoRepository) return null;
  if (!repos.length) throw new Error('未发现 Git 仓库，请先在 VS Code 中打开仓库。');
  if (repos.length === 1) return repos[0];
  const picked = await vscode.window.showQuickPick(repos.map(repo => ({
    label: path.basename(repo.rootUri.fsPath),
    description: repo.rootUri.fsPath,
    repo
  })), { placeHolder: '选择要生成提交说明的 Git 仓库' });
  return picked?.repo;
}

function defaultsFor(repo) {
  const config = vscode.workspace.getConfiguration('autocommit', repo?.rootUri);
  return {
    BaseUrl: config.get('baseUrl'), Model: config.get('model'),
    AllowHttp: config.get('allowHttp'), MaxInputCharacters: config.get('maxInputCharacters')
  };
}

async function configurationFor(repo, context) {
  const defaultRules = await fs.readFile(path.join(context.extensionPath, 'resources', 'default-rules.md'), 'utf8');
  return readProjectConfiguration(repo.rootUri.fsPath, defaultsFor(repo), defaultRules);
}

async function requestKey(context, repo, settings) {
  const ids = credentialIds(repo.rootUri.fsPath, settings.BaseUrl);
  return (await readCredential(context.secrets, ids)).value;
}

async function showError(error) {
  if (error?.name === 'AbortError') {
    vscode.window.showInformationMessage('AutoCommit 已取消，原提交说明已保留。');
    return;
  }
  const message = error?.message || String(error);
  vscode.window.showErrorMessage('AutoCommit：' + message);
}

async function setApiKey(context, _log, sourceControl) {
  const repo = await chooseRepository(sourceControl, true);
  if (repo === undefined) return;
  const settings = repo ? (await configurationFor(repo, context)).settings : defaultsFor(null);
  serviceUrl(settings);
  const ids = credentialIds(repo?.rootUri.fsPath, settings.BaseUrl);
  const labels = { shared: '本机共享', project: '项目专用', anonymous: '此项目不发送 Key' };
  let source;
  try { source = labels[(await readCredential(context.secrets, ids)).scope]; }
  catch { source = '凭据无法读取，请重新设置'; }
  const choices = [{ label: '保存本机共享 Key', action: 'shared' }];
  if (repo) choices.push({ label: '保存项目专用 Key', action: 'project' },
    { label: '恢复使用共享 Key', action: 'inherit' }, { label: labels.anonymous, action: 'anonymous' });
  choices.push({ label: '删除此地址的共享 Key', action: 'removeShared' });
  const choice = await vscode.window.showQuickPick(choices, {
    placeHolder: settings.BaseUrl + ' · 当前凭据方式：' + source
  });
  if (!choice) return;
  if (choice.action === 'removeShared' && await vscode.window.showWarningMessage(
    '删除后，使用此地址共享 Key 的仓库需要重新配置。', { modal: true }, '删除共享 Key') !== '删除共享 Key') return;
  let value;
  if (choice.action === 'shared' || choice.action === 'project') {
    value = await vscode.window.showInputBox({ password: true, ignoreFocusOut: true,
      prompt: choice.label + ' · ' + settings.BaseUrl,
      validateInput: input => input.trim() ? undefined : '请输入 API Key。' });
    if (!value) return;
  }
  await saveCredential(context.secrets, ids, choice.action, value);
  vscode.window.showInformationMessage('API Key 设置已更新：' + choice.label);
}

async function testModel(context, log, sourceControl) {
  const repo = await chooseRepository(sourceControl);
  if (!repo) return;
  const { settings, modelSource } = await configurationFor(repo, context);
  if (!String(settings.Model || '').trim()) throw new Error(`请在 ${modelSource} 中填写模型名称。`);
  const apiKey = await requestKey(context, repo, settings);
  const result = await vscode.window.withProgress({
    location: vscode.ProgressLocation.Notification,
    title: 'AutoCommit 正在测试模型连接（仅发送固定文本）', cancellable: true
  }, async (_progress, token) => {
    const controller = new AbortController();
    const subscription = token.onCancellationRequested(() => controller.abort());
    if (token.isCancellationRequested) controller.abort();
    try {
      const response = await testConnection(settings, apiKey, fetch, controller.signal);
      log('模型连接测试成功');
      return response;
    } catch (error) {
      if (!controller.signal.aborted) log('模型连接测试失败：' + error.message);
      throw error;
    }
    finally { subscription.dispose(); }
  });
  vscode.window.showInformationMessage(`模型连接成功：${result.model}。测试请求可能产生少量费用。`);
}

async function generate(context, log, sourceControl) {
  const repo = await chooseRepository(sourceControl);
  if (!repo) return;
  const started = Date.now();
  const initial = await readGitSnapshot(repo.rootUri.fsPath);
  log(`读取快照 ${initial.files.length} 项 ${Date.now() - started} ms`);
  const needsStaging = !initial.diff.trim();
  if (needsStaging && !(await hasChangesToStage(initial.root))) {
    vscode.window.showInformationMessage('AutoCommit：当前仓库没有可生成提交说明的变更。');
    return;
  }
  const { settings, rules, modelSource } = await configurationFor(repo, context);
  if (!String(settings.Model || '').trim()) throw new Error(`请在 ${modelSource} 中填写模型名称。`);
  serviceUrl(settings);
  const previous = repo.inputBox.value;
  if (needsStaging) {
    const answer = await vscode.window.showWarningMessage('暂存区为空。是否暂存全部变更并生成提交说明？',
      { modal: true, detail: `将暂存 ${initial.root} 下的文件修改、删除和未被 Git 忽略的新文件，并向配置的模型服务发送暂存差异。` },
      '暂存全部并生成');
    if (answer !== '暂存全部并生成') return;
  }
  const apiKey = await requestKey(context, repo, settings);
  const result = await vscode.window.withProgress({
    location: vscode.ProgressLocation.Notification,
    title: 'AutoCommit 正在生成提交说明', cancellable: true
  }, async (progress, token) => {
    const controller = new AbortController();
    const subscription = token.onCancellationRequested(() => controller.abort());
    if (token.isCancellationRequested) controller.abort();
    const { signal } = controller;
    let temporaryDirectory;
    let capturePath;
    try {
      signal.throwIfAborted();
      if (needsStaging) progress.report({ message: '正在暂存全部变更…' });
      const before = needsStaging ? await stageAllChanges(initial, signal) : initial;
      if (!before.diff.trim()) throw new Error('暂存区没有可生成提交说明的变更。');
      const batched = needsBatching(before, rules, settings);
      if (batched) {
        temporaryDirectory = await fs.mkdtemp(path.join(os.tmpdir(), 'autocommit-'));
        capturePath = path.join(temporaryDirectory, 'changes.diff');
      }
      progress.report({ message: '正在校验暂存变更…' });
      const beforeRead = Date.now();
      const current = await readGitSnapshot(repo.rootUri.fsPath, { capturePath, signal });
      log(`生成前校验 ${Date.now() - beforeRead} ms`);
      if (!sameSnapshot(before, current)) throw new Error('暂存内容或分支发生变化。原提交说明已保留，请重新生成。');
      const generated = await generateMessage(before, rules, settings, apiKey, fetch, signal,
        { capturePath, log, progress: message => { log(message); progress.report({ message }); } });
      const afterRead = Date.now();
      const after = await readGitSnapshot(repo.rootUri.fsPath, { signal });
      log(`生成后校验 ${Date.now() - afterRead} ms`);
      if (!sameSnapshot(before, after)) throw new Error('生成期间暂存内容或分支发生变化。原提交说明已保留，请重新生成。');
      signal.throwIfAborted();
      return generated;
    } finally {
      subscription.dispose();
      if (capturePath) {
        await fs.rm(capturePath, { force: true });
        await fs.rmdir(temporaryDirectory);
      }
    }
  });
  if (repo.inputBox.value !== previous || previous.trim()) {
    const replace = await vscode.window.showWarningMessage(
      result.fallbackReason ? '提交框已有内容，是否使用本地摘要替换？' : '提交框已有内容，是否用新生成的说明替换？',
      { modal: true, detail: '取消会保留当前提交框内容。' }, '替换内容'
    );
    if (replace !== '替换内容') return;
  }
  repo.inputBox.value = result.text;
  log(`已回填草稿 请求 ${result.requests} 次 总耗时 ${Date.now() - started} ms`);
  const usage = result.usage;
  const usageText = usage ? ` 输入 ${usage.prompt_tokens ?? '未提供'} / 输出 ${usage.completion_tokens ?? '未提供'} Token。` : ' 服务未返回 Token 用量。';
  if (result.fallbackReason) vscode.window.showWarningMessage('已将本地摘要写入 Git 提交框。' + result.fallbackReason);
  else vscode.window.showInformationMessage(`已写入 Git 提交框，请审核。模型请求 ${result.requests} 次。` + usageText);
}

async function testGeneration(context, log, sourceControl) {
  const repo = await chooseRepository(sourceControl);
  if (!repo) return;
  const { settings } = await configurationFor(repo, context);
  serviceUrl(settings);
  const apiKey = await requestKey(context, repo, settings);
  const sample = { files: ['sample.js'], operations: ['修改'],
    diff: 'diff --git a/sample.js b/sample.js\n--- a/sample.js\n+++ b/sample.js\n@@ -1 +1 @@\n-const retries = 1;\n+const retries = 2;\n' };
  const result = await vscode.window.withProgress({
    location: vscode.ProgressLocation.Notification, title: 'AutoCommit 正在用固定示例测试生成', cancellable: true
  }, async (_progress, token) => {
    const controller = new AbortController();
    const subscription = token.onCancellationRequested(() => controller.abort());
    if (token.isCancellationRequested) controller.abort();
    try {
      return await generateMessage(sample, '用一行中文概括变更。', settings, apiKey, fetch, controller.signal, { log });
    } finally { subscription.dispose(); }
  });
  if (result.fallbackReason) throw new Error(result.fallbackReason);
  log('示例变更生成成功');
  vscode.window.showInformationMessage('AutoCommit 示例提交说明：' + result.text);
  return result.text;
}

function activate(context) {
  const output = vscode.window.createOutputChannel('AutoCommit', { log: true });
  context.subscriptions.push(output);
  const version = context.extension.packageJSON.version;
  const showLog = () => output.show(true);
  context.subscriptions.push(vscode.commands.registerCommand('autocommit.showLog', showLog));
  output.info(`AutoCommit ${version} 已激活`);
  let sequence = 0;
  const register = (name, action) => context.subscriptions.push(vscode.commands.registerCommand(name, async sourceControl => {
    const id = ++sequence;
    const log = message => output.info(`[${id}] ${message}`);
    const start = Date.now();
    if (name !== 'autocommit.setApiKey') showLog();
    log(`AutoCommit ${version} ${name}`);
    try { return await action(context, log, sourceControl); } catch (error) { log(error?.name === "AbortError" ? "已取消" : "操作失败"); await showError(error); }
    finally { log(`命令结束 ${Date.now() - start} ms`); }
  }));
  register('autocommit.generate', generate);
  register('autocommit.testConnection', testModel);
  register('autocommit.testGeneration', testGeneration);
  register('autocommit.setApiKey', setApiKey);
}

function deactivate() {}

module.exports = { activate, deactivate };
