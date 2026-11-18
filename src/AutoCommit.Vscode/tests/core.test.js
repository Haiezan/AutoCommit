'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { readGitSnapshot, sameSnapshot, readProjectConfiguration, generateMessage, testConnection } = require('../lib/core');

function git(cwd, ...args) { execFileSync('git', args, { cwd, stdio: 'pipe' }); }

test('读取 Git 暂存内容和文件清单，不创建项目配置；变更后快照失效', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'autocommit-vscode-'));
  try {
    git(root, 'init');
    const file = path.join(root, '中文 文件.txt');
    await fs.writeFile(file, 'staged_marker\n');
    git(root, 'add', '.');
    await fs.writeFile(file, 'unstaged_marker\n');
    const before = await readGitSnapshot(root);
    assert.match(before.diff, /staged_marker/);
    assert.doesNotMatch(before.diff, /unstaged_marker/);
    assert.deepEqual(before.files, ['中文 文件.txt']);
    assert.equal(await fs.stat(path.join(root, '.auto')).then(() => true, () => false), false);
    git(root, 'add', '.');
    const after = await readGitSnapshot(root);
    assert.equal(sameSnapshot(before, after), false);
  } finally { await fs.rm(root, { recursive: true, force: true }); }
});

test('项目配置优先于默认设置且读取不写文件', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'autocommit-vscode-'));
  try {
    const defaults = { BaseUrl: 'https://default.test/v1', Model: 'default', AllowHttp: false, MaxInputCharacters: 48000 };
    const empty = await readProjectConfiguration(root, defaults, '默认规范');
    assert.equal(empty.settings, defaults);
    assert.equal(empty.rules, '默认规范');
    assert.deepEqual(await fs.readdir(root), []);
    await fs.mkdir(path.join(root, '.auto'));
    await fs.writeFile(path.join(root, '.auto', 'model.json'), JSON.stringify({ Model: 'project' }));
    await fs.writeFile(path.join(root, '.auto', 'commit-rules.md'), '项目规范');
    const project = await readProjectConfiguration(root, defaults, '默认规范');
    assert.equal(project.settings.Model, 'project');
    assert.equal(project.settings.MaxInputCharacters, 48000);
    assert.equal(project.rules, '项目规范');
  } finally { await fs.rm(root, { recursive: true, force: true }); }
});

test('小变更只请求一次，预算无法容纳规范时生成本地摘要', async () => {
  const snapshot = { diff: '+staged_marker\n' };
  const settings = { BaseUrl: 'http://localhost:11434/v1', Model: 'local', AllowHttp: true, MaxInputCharacters: 48000 };
  let calls = 0;
  const fetcher = async (url, options) => {
    calls++;
    assert.equal(String(url), 'http://localhost:11434/v1/chat/completions');
    assert.equal(options.redirect, 'manual');
    const body = JSON.parse(options.body);
    assert.match(body.messages[1].content, /staged_marker/);
    assert.match(body.messages[1].content, /项目规范/);
    return { ok: true, json: async () => ({ choices: [{ message: { content: 'fix: 修复错误' } }], usage: { prompt_tokens: 12 } }) };
  };
  const result = await generateMessage(snapshot, '项目规范', settings, '', fetcher);
  assert.equal(result.text, 'fix: 修复错误');
  assert.equal(calls, 1);
  const fallback = await generateMessage(snapshot, '项目规范', { ...settings, MaxInputCharacters: 1 }, '', fetcher);
  assert.match(fallback.fallbackReason, /输入预算/);
  assert.match(fallback.text, /本地摘要/);
  await assert.rejects(generateMessage(snapshot, '项目规范', { ...settings, AllowHttp: false }, '', fetcher), /允许 HTTP/);
  assert.equal(calls, 1);
});

test('完整暂存差异分批发送后逐层合并，所有请求都遵守预算', async () => {
  const temporary = await fs.mkdtemp(path.join(os.tmpdir(), 'autocommit-batch-'));
  const capturePath = path.join(temporary, 'changes.diff');
  const source = 'diff --git a/sample b/sample\n@@ -1,1 +1,1 @@\n+' + '"'.repeat(1500) + '🚀\n+NOT_IN_PREVIEW\n';
  const snapshot = { diff: 'preview only', diffIsPartial: true, files: ['sample'] };
  const settings = { BaseUrl: 'https://example.test/v1', Model: 'model', MaxInputCharacters: 500 };
  const payloads = [];
  const progress = [];
  const fetcher = async (_url, options) => {
    const { messages } = JSON.parse(options.body);
    assert.ok(messages[0].content.length + messages[1].content.length <= settings.MaxInputCharacters);
    payloads.push(JSON.parse(messages[1].content));
    return { ok: true, json: async () => ({ choices: [{ message: { content: '变更事实' } }], usage: { prompt_tokens: 12 } }) };
  };
  try {
    await fs.writeFile(capturePath, source);
    const result = await generateMessage(snapshot, '项目规范', settings, '', fetcher, undefined,
      { capturePath, progress: message => progress.push(message) });
    const leaves = payloads.filter(payload => payload.stagedDiff);
    assert.ok(leaves.length >= 4);
    assert.match(leaves.map(payload => payload.stagedDiff).join(''), /NOT_IN_PREVIEW/);
    assert.match(leaves.map(payload => payload.stagedDiff).join(''), /🚀/);
    assert.equal(leaves.map(payload => payload.stagedDiff).join('').split('"').length - 1, 1500);
    assert.ok(payloads.slice(0, -1).some(payload => payload.changeSummaries));
    assert.equal(payloads.at(-1).commitRules, '项目规范');
    assert.ok(progress.some(message => message.includes('合并')));
    assert.equal(result.text, '变更事实');
    assert.equal(result.requests, payloads.length);
    assert.equal(result.usage.prompt_tokens, payloads.length * 12);
    assert.equal(result.usage.total_tokens, null);
  } finally {
    await fs.rm(capturePath, { force: true });
    await fs.rmdir(temporary);
  }
});

test('分批失败提供本地摘要，用户取消时保留原说明', async () => {
  const snapshot = { diff: '+line\n'.repeat(500), files: ['file.cpp'], operations: ['修改'] };
  const settings = { BaseUrl: 'https://example.test/v1', Model: 'model', MaxInputCharacters: 500 };
  let calls = 0;
  const fetcher = async () => ++calls === 2 ? { ok: false, status: 429 } :
    { ok: true, json: async () => ({ choices: [{ message: { content: '变更事实' } }] }) };
  const fallback = await generateMessage(snapshot, 'rules', settings, '', fetcher);
  assert.equal(fallback.requests, 2);
  assert.match(fallback.fallbackReason, /429/);
  assert.match(fallback.text, /本地摘要/);
  assert.match(fallback.text, /修改 file.cpp/);

  const controller = new AbortController();
  await assert.rejects(generateMessage(snapshot, 'rules', settings, '', async () => {
    controller.abort();
    return { ok: true, json: async () => ({ choices: [{ message: { content: '变更事实' } }] }) };
  }, controller.signal), { name: 'AbortError' });
});

test('超过原 32 MiB 缓冲限制的真实暂存差异可以完整捕获', async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'autocommit-vscode-'));
  const capturePath = path.join(root, 'capture.diff');
  try {
    git(root, 'init');
    const file = path.join(root, 'large.txt');
    await fs.writeFile(file, 'x'.repeat(33 * 1024 * 1024) + '\nLAST_STAGED_LINE\n');
    git(root, 'add', 'large.txt');
    await fs.writeFile(file, 'UNSTAGED_LINE\n');
    const snapshot = await readGitSnapshot(root, { capturePath });
    assert.ok(snapshot.diffIsPartial);
    assert.deepEqual(snapshot.files, ['large.txt']);
    const handle = await fs.open(capturePath);
    try {
      const size = (await handle.stat()).size;
      const tail = Buffer.alloc(100);
      assert.ok(size > 32 * 1024 * 1024);
      await handle.read(tail, 0, tail.length, size - tail.length);
      assert.match(tail.toString(), /LAST_STAGED_LINE/);
      assert.doesNotMatch(tail.toString(), /UNSTAGED_LINE/);
    } finally { await handle.close(); }
    assert.ok(sameSnapshot(snapshot, await readGitSnapshot(root)));
  } finally {
    assert.ok(path.basename(root).startsWith('autocommit-vscode-'));
    assert.equal(path.dirname(root), os.tmpdir());
    await fs.rm(root, { recursive: true, force: true });
  }
});

test('连接测试使用固定文本，不发送仓库内容', async () => {
  const settings = { BaseUrl: 'https://example.test/v1', Model: 'model', AllowHttp: false, MaxInputCharacters: 48000 };
  const fetcher = async (_url, options) => {
    assert.match(options.body, /Reply OK/);
    assert.doesNotMatch(options.body, /stagedDiff|commitRules/);
    return { ok: true, json: async () => ({ choices: [{ message: { content: 'OK' } }] }) };
  };
  assert.equal((await testConnection(settings, '', fetcher)).text, 'OK');
});
