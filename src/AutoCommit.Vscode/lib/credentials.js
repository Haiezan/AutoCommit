'use strict';

const path = require('node:path');
const { createHash } = require('node:crypto');

function credentialIds(root, endpoint) {
  const address = endpoint.trim().replace(/\/+$/, '');
  const digest = value => createHash('sha256').update(value).digest('hex');
  const shared = 'shared-api-key:' + digest(address);
  if (!root) return { shared };
  const directory = path.resolve(root);
  const identity = (process.platform === 'win32' ? directory.toUpperCase() : directory) + '\n' + address;
  const hash = digest(identity);
  return { shared, project: 'project-credential:' + hash, legacy: 'api-key:' + hash };
}

async function readCredential(secrets, ids) {
  if (ids.project) {
    const record = await secrets.get(ids.project);
    if (record) {
      let credential;
      try { credential = JSON.parse(record); }
      catch { throw new Error('项目凭据无法读取，请重新设置 API Key。'); }
      const { scope, value } = credential || {};
      if (scope === 'anonymous') return { scope, value: '' };
      if (scope === 'project' && typeof value === 'string' && value.length) return { scope, value };
      if (scope !== 'shared') throw new Error('项目凭据无法读取，请重新设置 API Key。');
    } else {
      const value = await secrets.get(ids.legacy);
      if (value) return { scope: 'project', value };
    }
  }
  return { scope: 'shared', value: await secrets.get(ids.shared) || '' };
}

async function saveCredential(secrets, ids, action, value) {
  if (action === 'removeShared') { await secrets.delete(ids.shared); return; }
  if (action === 'shared') await secrets.store(ids.shared, value);
  if (!ids.project) return;
  const scope = action === 'inherit' ? 'shared' : action;
  const record = scope === 'project' ? { scope, value } : { scope };
  await secrets.store(ids.project, JSON.stringify(record));
  await secrets.delete(ids.legacy);
}

module.exports = { credentialIds, readCredential, saveCredential };
