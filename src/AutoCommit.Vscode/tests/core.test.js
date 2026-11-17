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

test('生成仅发送暂存差异和规范，预算超限与未授权 HTTP 不发送', async () => {
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
  await assert.rejects(generateMessage(snapshot, '项目规范', { ...settings, MaxInputCharacters: 1 }, '', fetcher), /超过预算/);
  await assert.rejects(generateMessage(snapshot, '项目规范', { ...settings, AllowHttp: false }, '', fetcher), /允许 HTTP/);
  assert.equal(calls, 1);
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
