import assert from 'node:assert/strict';
import { DistributionState, stateBranch } from './distribution-state.mjs';
export function githubFixture() {
  let next = 1, head = null, failure;
  const trees = new Map(), commits = new Map(), calls = [];
  const id = () => (next++).toString(16).padStart(40, '0');
  const fetcher = async (url, options = {}) => {
    const path = new URL(url).pathname.split('/phcastello/Aegis/')[1], method = options.method ?? 'GET';
    const body = options.body ? JSON.parse(options.body) : undefined;
    calls.push({ path, method, body });
    let status = 200, value;
    if (failure?.(path, method, body)) status = 503;
    else if (path === `git/ref/heads/${stateBranch}`) { if (!head) status = 404; else value = { object: { sha: head } }; }
    else if (path.startsWith('git/commits/') && method === 'GET') value = commits.get(path.split('/').at(-1));
    else if (path === 'contents/ledger.json') {
      const requested = new URL(url).searchParams.get('ref');
      value = { content: Buffer.from(trees.get(commits.get(requested).tree.sha)['ledger.json']).toString('base64') };
    } else if (path === 'git/trees' && method === 'POST') {
      const sha = id(), files = { ...(body.base_tree ? trees.get(body.base_tree) : {}) };
      for (const entry of body.tree) files[entry.path] = entry.content;
      trees.set(sha, files); value = { sha };
    } else if (path === 'git/commits' && method === 'POST') {
      const sha = id(); commits.set(sha, { tree: { sha: body.tree }, parents: body.parents }); value = { sha };
    } else if (path === 'git/refs' && method === 'POST') {
      if (head) status = 422; else { head = body.sha; value = { object: { sha: head } }; }
    } else if (path === `git/refs/heads/${stateBranch}` && method === 'PATCH') {
      assert.equal(body.force, false);
      if (commits.get(body.sha).parents[0] !== head) status = 422;
      else { head = body.sha; value = { object: { sha: head } }; }
    } else throw Error(`Unexpected GitHub ${method} ${path}`);
    return { status, ok: status >= 200 && status < 300, json: async () => value };
  };
  const state = new DistributionState({ fetcher, token: 'fixture' });
  return { state, fetcher, calls, setFailure: fn => { failure = fn; }, files: () => head ? trees.get(commits.get(head).tree.sha) : {}, head: () => head };
}
