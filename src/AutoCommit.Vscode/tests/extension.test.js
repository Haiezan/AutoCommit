'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { createRequire } = require('node:module');
const { runInNewContext } = require('node:vm');

const extensionPath = path.resolve(__dirname, '..');
const extensionFile = path.join(extensionPath, 'extension.js');
const loadFromExtension = createRequire(extensionFile);
const stageAction = '暂存全部并生成';

function git(root, ...args) {
  return execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

async function createRepository(t) {
  const temp = await fs.realpath(os.tmpdir());
  const root = await fs.mkdtemp(path.join(temp, 'autocommit-command-'));
  t.after(async () => {
    assert.equal(path.dirname(await fs.realpath(root)), temp);
    assert.ok(path.basename(root).startsWith('autocommit-command-'));
    await fs.rm(root, { recursive: true, force: true });
  });
  git(root, 'init');
  await fs.writeFile(path.join(root, 'changed.txt'), 'original\n');
  await fs.writeFile(path.join(root, 'deleted.txt'), 'delete me\n');
  await fs.writeFile(path.join(root, '.gitignore'), 'ignored.txt\n');
  git(root, 'add', '--all');
  git(root, '-c', 'user.name=AutoCommit Test', '-c', 'user.email=autocommit@example.test',
    '-c', 'commit.gpgsign=false', '-c', 'core.hooksPath=.git/disabled-hooks', 'commit', '-m', 'baseline');
  return root;
}

async function runGenerate(root, { answer, initialText = '', secrets = { get: async () => undefined }, pick, input, command = 'autocommit.generate', multiRoot = false } = {}) {
  const repo = { rootUri: { fsPath: root, scheme: 'file' }, inputBox: { value: initialText } };
  const gitApi = { repositories: multiRoot ? [{}, repo] : [repo], getRepository: uri => uri === repo.rootUri ? repo : null };
  const commands = new Map();
  const warnings = [];
  const errors = [];
  const information = [];
  const requests = [];
  const authorization = [];
  const logs = [];
  const settings = { baseUrl: 'https://example.test/v1', model: 'test-model', allowHttp: false, maxInputCharacters: 48000 };
  const vscode = {
    ProgressLocation: { Notification: 1 },
    extensions: { getExtension: () => ({ activate: async () => ({ getAPI: () => gitApi }) }) },
    workspace: { getConfiguration: () => ({ get: key => settings[key] }) },
    commands: { registerCommand: (name, run) => commands.set(name, run) },
    window: {
      createOutputChannel: () => ({ info(message) { logs.push(message); }, show() { logs.push("已打开日志"); }, dispose() {} }),
      showQuickPick: async choices => choices.find(choice => choice.action === pick),
      showInputBox: async () => input,
      showWarningMessage: async (message, options, action) => {
        warnings.push({ message, modal: options?.modal, action });
        return answer?.(action);
      },
      showErrorMessage: message => errors.push(message),
      showInformationMessage: message => information.push(message),
      withProgress: (_options, run) => run({ report() {} }, {
        isCancellationRequested: false, onCancellationRequested: () => ({ dispose() {} })
      })
    }
  };
  const module = { exports: {} };
  runInNewContext(await fs.readFile(extensionFile, 'utf8'), {
    module, process, AbortController,
    require: id => id === 'vscode' ? vscode : loadFromExtension(id),
    fetch: async (_url, options) => {
      authorization.push(options.headers.Authorization);
      requests.push(JSON.parse(JSON.parse(options.body).messages[1].content));
      return { ok: true, json: async () => ({ choices: [{ message: { content: 'fix: 测试说明' } }] }) };
    }
  }, { filename: extensionFile });
  module.exports.activate({ extension: { packageJSON: { version: "0.6.5" } }, extensionPath, subscriptions: [], secrets });
  await commands.get(command)(multiRoot ? { rootUri: repo.rootUri } : undefined);
  return { warnings, errors, information, requests, authorization, logs, text: repo.inputBox.value };
}

test('多仓库示例测试只发送固定差异，日志可独立打开', async t => {
  const root = await createRepository(t);
  await fs.writeFile(path.join(root, 'changed.txt'), 'private_marker');
  git(root, 'add', 'changed.txt');
  const result = await runGenerate(root, { command: 'autocommit.testGeneration', multiRoot: true, initialText: '已有说明' });
  assert.deepEqual(result.errors, []);
  assert.equal(result.text, '已有说明');
  assert.equal(result.requests.length, 1);
  assert.match(result.requests[0].stagedDiff, /const retries = 2/);
  assert.doesNotMatch(result.requests[0].stagedDiff, /private_marker/);
  const opened = await runGenerate(root, { command: 'autocommit.showLog' });
  assert.ok(opened.logs.includes('已打开日志'));
  assert.equal(opened.requests.length, 0);
});

test('确认后暂存全部变更并生成说明，忽略文件和 HEAD 保持原样', async t => {
  const root = await createRepository(t);
  const head = git(root, 'rev-parse', 'HEAD');
  await fs.writeFile(path.join(root, 'changed.txt'), 'changed_marker\n');
  await fs.unlink(path.join(root, 'deleted.txt'));
  await fs.writeFile(path.join(root, 'added.txt'), 'added_marker\n');
  await fs.writeFile(path.join(root, 'ignored.txt'), 'ignored_marker\n');
  const result = await runGenerate(root, { answer: action => action === stageAction ? action : undefined });

  assert.deepEqual(result.errors, []);
  assert.match(result.logs.join('\n'), /生成后校验/);
  assert.doesNotMatch(result.logs.join('\n'), /added_marker|changed_marker|测试说明/);
  assert.deepEqual(result.warnings.map(warning => warning.action), [stageAction]);
  assert.ok(result.warnings[0].modal);
  assert.equal(result.text, 'fix: 测试说明');
  assert.equal(result.requests.length, 1);
  assert.match(result.requests[0].stagedDiff, /changed_marker/);
  assert.match(result.requests[0].stagedDiff, /added_marker/);
  assert.doesNotMatch(result.requests[0].stagedDiff, /ignored_marker/);
  assert.deepEqual(git(root, 'diff', '--cached', '--name-status').split(/\r?\n/), [
    'A\tadded.txt', 'M\tchanged.txt', 'D\tdeleted.txt'
  ]);
  assert.equal(git(root, 'rev-parse', 'HEAD'), head);
});

test('取消全部暂存时保留索引和原说明，有暂存内容时直接生成', async t => {
  const root = await createRepository(t);
  await fs.writeFile(path.join(root, 'changed.txt'), 'staged_marker\n');
  await fs.writeFile(path.join(root, 'unstaged.txt'), 'untracked_marker\n');
  const cancelled = await runGenerate(root, { initialText: '原说明' });
  assert.deepEqual(cancelled.warnings.map(warning => warning.action), [stageAction]);
  assert.equal(cancelled.text, '原说明');
  assert.equal(cancelled.requests.length, 0);
  assert.equal(git(root, 'diff', '--cached', '--name-only'), '');

  git(root, 'add', 'changed.txt');
  await fs.writeFile(path.join(root, 'changed.txt'), 'unstaged_marker\n');
  const result = await runGenerate(root);
  assert.deepEqual(result.errors, []);
  assert.deepEqual(result.warnings, []);
  assert.equal(result.text, 'fix: 测试说明');
  assert.equal(result.requests.length, 1);
  assert.match(result.requests[0].stagedDiff, /staged_marker/);
  assert.doesNotMatch(result.requests[0].stagedDiff, /\b(?:unstaged|untracked)_marker/);
  assert.equal(git(root, 'diff', '--cached', '--name-only'), 'changed.txt');

  const replacement = await runGenerate(root, { initialText: '原说明' });
  assert.deepEqual(replacement.warnings.map(warning => warning.action), ['替换内容']);
  assert.equal(replacement.text, '原说明');
});

test('干净仓库没有暂存确认，也不请求模型', async t => {
  const root = await createRepository(t);
  await fs.writeFile(path.join(root, 'ignored.txt'), 'ignored_marker\n');
  const result = await runGenerate(root);
  assert.deepEqual(result.errors, []);
  assert.deepEqual(result.warnings, []);
  assert.equal(result.requests.length, 0);
  assert.match(result.information[0], /没有可生成/);
});

test('确认期间出现暂存项时保留用户的暂存范围', async t => {
  const root = await createRepository(t);
  await fs.writeFile(path.join(root, 'changed.txt'), 'manual_stage\n');
  await fs.writeFile(path.join(root, 'unstaged.txt'), 'keep_unstaged\n');
  const result = await runGenerate(root, { answer: action => {
    git(root, 'add', 'changed.txt');
    return action;
  } });
  assert.match(result.errors[0], /Git 状态发生变化/);
  assert.equal(result.requests.length, 0);
  assert.equal(git(root, 'diff', '--cached', '--name-only'), 'changed.txt');
});

test('通过命令保存共享凭据、覆盖、停用认证和恢复共享', async t => {
  const first = await createRepository(t);
  const second = await createRepository(t);
  const data = new Map();
  const secrets = { get: id => data.get(id), store: (id, value) => data.set(id, value), delete: id => data.delete(id) };
  const configure = async (root, pick, input) => {
    const result = await runGenerate(root, { secrets, command: 'autocommit.setApiKey', pick, input });
    assert.deepEqual(result.errors, []);
  };
  const header = async root => {
    const result = await runGenerate(root, { secrets, command: 'autocommit.testGeneration' });
    assert.deepEqual(result.errors, []);
    assert.equal(result.requests.length, 1);
    return result.authorization[0];
  };
  await configure(first, 'shared', 'test-shared');
  assert.equal(await header(second), 'Bearer test-shared');
  await configure(second, 'project', 'test-project');
  assert.equal(await header(first), 'Bearer test-shared');
  assert.equal(await header(second), 'Bearer test-project');
  await configure(second, 'anonymous');
  assert.equal(await header(second), undefined);
  await configure(second, 'inherit');
  assert.equal(await header(second), 'Bearer test-shared');
  await fs.mkdir(path.join(second, '.auto'));
  await fs.writeFile(path.join(second, '.auto', 'model.json'), JSON.stringify({ BaseUrl: 'https://example.test/other', Model: 'test' }));
  assert.equal(await header(second), undefined);
});

test('旧版仓库 Key 优先，损坏的项目记录不会回退到共享账号', async () => {
  const { credentialIds, readCredential, saveCredential } = require('../lib/credentials');
  const { createHash } = require('node:crypto');
  const root = path.resolve('legacy-project');
  const address = 'https://example.test/v1';
  const ids = credentialIds(root, address + '/');
  const directory = process.platform === 'win32' ? root.toUpperCase() : root;
  const legacy = 'api-key:' + createHash('sha256').update(directory + '\n' + address).digest('hex');
  const data = new Map([[legacy, 'old-project'], [ids.shared, 'shared-test']]);
  const secrets = { get: id => data.get(id), store: (id, value) => data.set(id, value), delete: id => data.delete(id) };
  assert.deepEqual(await readCredential(secrets, ids), { scope: 'project', value: 'old-project' });
  data.set(ids.project, 'invalid-record');
  await assert.rejects(readCredential(secrets, ids), /项目凭据无法读取/);
  await saveCredential(secrets, ids, 'inherit');
  assert.deepEqual(await readCredential(secrets, ids), { scope: 'shared', value: 'shared-test' });
  assert.equal(data.has(legacy), false);
});
