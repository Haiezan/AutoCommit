'use strict';

const vscode = require('vscode');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { readGitSnapshot, sameSnapshot, readProjectConfiguration, needsBatching, generateMessage, testConnection, serviceUrl } = require('./lib/core');

async function chooseRepository() {
  const extension = vscode.extensions.getExtension('vscode.git');
  if (!extension) throw new Error('未找到 VS Code 内置 Git 扩展。');
  const git = (await extension.activate()).getAPI(1);
  const repos = git.repositories;
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
  const config = vscode.workspace.getConfiguration('autocommit', repo.rootUri);
  return {
    BaseUrl: config.get('baseUrl'), Model: config.get('model'),
    AllowHttp: config.get('allowHttp'), MaxInputCharacters: config.get('maxInputCharacters')
  };
}

async function configurationFor(repo, context) {
  const defaultRules = await fs.readFile(path.join(context.extensionPath, 'resources', 'default-rules.md'), 'utf8');
  return readProjectConfiguration(repo.rootUri.fsPath, defaultsFor(repo), defaultRules);
}

function secretKey(repo, settings) {
  const root = path.resolve(repo.rootUri.fsPath);
  const identity = (process.platform === 'win32' ? root.toUpperCase() : root) + '\n' +
    String(settings.BaseUrl || '').trim().replace(/\/+$/, '');
  return 'api-key:' + createHash('sha256').update(identity).digest('hex');
}

async function showError(error) {
  if (error?.name === 'AbortError') {
    vscode.window.showInformationMessage('AutoCommit 请求已取消或超时，原提交说明已保留。');
    return;
  }
  const message = error?.message || String(error);
  vscode.window.showErrorMessage('AutoCommit：' + message);
}

async function setApiKey(context) {
  const repo = await chooseRepository();
  if (!repo) return;
  const { settings } = await configurationFor(repo, context);
  serviceUrl(settings);
  const value = await vscode.window.showInputBox({
    prompt: '当前仓库和服务地址的 API Key；留空并确认可删除已保存的 Key',
    password: true, ignoreFocusOut: true
  });
  if (value === undefined) return;
  const key = secretKey(repo, settings);
  if (value) await context.secrets.store(key, value);
  else await context.secrets.delete(key);
  vscode.window.showInformationMessage(value ? 'API Key 已保存在 VS Code 凭据存储中。' : '已删除当前仓库的 API Key。');
}

async function testModel(context) {
  const repo = await chooseRepository();
  if (!repo) return;
  const { settings, modelSource } = await configurationFor(repo, context);
  if (!String(settings.Model || '').trim()) throw new Error(`请在 ${modelSource} 中填写模型名称。`);
  const apiKey = await context.secrets.get(secretKey(repo, settings)) || '';
  const result = await vscode.window.withProgress({
    location: vscode.ProgressLocation.Notification,
    title: 'AutoCommit 正在测试模型连接（仅发送固定文本）', cancellable: true
  }, async (_progress, token) => {
    const controller = new AbortController();
    const subscription = token.onCancellationRequested(() => controller.abort());
    if (token.isCancellationRequested) controller.abort();
    try { return await testConnection(settings, apiKey, fetch, controller.signal); }
    finally { subscription.dispose(); }
  });
  vscode.window.showInformationMessage(`模型连接成功：${result.model}。测试请求可能产生少量费用。`);
}

async function generate(context) {
  const repo = await chooseRepository();
  if (!repo) return;
  const before = await readGitSnapshot(repo.rootUri.fsPath);
  if (!before.diff.trim()) throw new Error('暂存区没有变更，请先在 VS Code 中暂存文件。');
  const { settings, rules, modelSource, rulesSource } = await configurationFor(repo, context);
  if (!String(settings.Model || '').trim()) throw new Error(`请在 ${modelSource} 中填写模型名称。`);
  const url = serviceUrl(settings);
  const batched = needsBatching(before, rules, settings);
  const previous = repo.inputBox.value;
  const visibleFiles = before.files.slice(0, 30).join('\n');
  const remaining = before.files.length > 30 ? `\n另有 ${before.files.length - 30} 个文件，请在源代码管理视图中核对。` : '';
  const answer = await vscode.window.showWarningMessage(
    '向模型服务发送 Git 暂存变更？',
    { modal: true, detail: `仓库：${before.root}\n分支：${before.branch}\n文件：${before.files.length} 个\n${visibleFiles}${remaining}\n服务：${url.href}\n模型配置：${modelSource}\n规范：${rulesSource}` +
      (batched ? '\n\n变更将分批发送再合并，可能产生多次模型调用。' : '') +
      (previous.trim() ? '\n\n提交框已有内容；生成成功后会再次确认是否替换。' : '') },
    '发送并生成'
  );
  if (answer !== '发送并生成') return;
  const apiKey = await context.secrets.get(secretKey(repo, settings)) || '';
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
      if (batched) {
        temporaryDirectory = await fs.mkdtemp(path.join(os.tmpdir(), 'autocommit-'));
        capturePath = path.join(temporaryDirectory, 'changes.diff');
      }
      progress.report({ message: '正在校验暂存变更…' });
      const current = await readGitSnapshot(repo.rootUri.fsPath, { capturePath, signal });
      if (!sameSnapshot(before, current)) throw new Error('暂存内容或分支发生变化。原提交说明已保留，请重新生成。');
      const generated = await generateMessage(before, rules, settings, apiKey, fetch, signal,
        { capturePath, progress: message => progress.report({ message }) });
      const after = await readGitSnapshot(repo.rootUri.fsPath, { signal });
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
  const usage = result.usage;
  const usageText = usage ? ` 输入 ${usage.prompt_tokens ?? '未提供'} / 输出 ${usage.completion_tokens ?? '未提供'} Token。` : ' 服务未返回 Token 用量。';
  if (result.fallbackReason) vscode.window.showWarningMessage('已将本地摘要写入 Git 提交框。' + result.fallbackReason);
  else vscode.window.showInformationMessage(`已写入 Git 提交框，请审核。模型请求 ${result.requests} 次。` + usageText);
}

function activate(context) {
  const register = (name, action) => context.subscriptions.push(vscode.commands.registerCommand(name, async () => {
    try { await action(context); } catch (error) { await showError(error); }
  }));
  register('autocommit.generate', generate);
  register('autocommit.testConnection', testModel);
  register('autocommit.setApiKey', setApiKey);
}

function deactivate() {}

module.exports = { activate, deactivate };
