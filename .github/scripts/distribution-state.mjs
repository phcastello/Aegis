// Trusted control plane. Git commits keep reservations durable and pointer changes atomic.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { allocateVersionCode, compareVersions } from '../../apps/aegis-node/scripts/version.mjs';

export const repository = 'phcastello/Aegis';
export const stateBranch = 'aegis-distribution';
export const recommendedFeed = `https://raw.githubusercontent.com/${repository}/${stateBranch}/latest.json`;
export const historicalFloor = 7002001; // v0.7.1 + admitted preview run 40038742314; never lower this floor.
const json = value => JSON.stringify(value, null, 2) + '\n';
const digest = value => createHash('sha256').update(json(value)).digest('hex');

export class GitHubError extends Error {
  constructor(status, path) { super(`GitHub distribution operation failed: ${path.split('?')[0]} (${status}).`); this.status = status; }
}

export class DistributionState {
  constructor({ token = process.env.GH_TOKEN, fetcher = globalThis.fetch } = {}) {
    this.token = token; this.fetcher = fetcher;
  }
  async api(path, method = 'GET', body, absent = false) {
    const response = await this.fetcher(`https://api.github.com/repos/${repository}/${path}`, {
      method, headers: { authorization: `Bearer ${this.token}`, accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28', ...(body ? { 'content-type': 'application/json' } : {}) },
      ...(body ? { body: JSON.stringify(body) } : {})
    });
    if (absent && response.status === 404) return null;
    if (!response.ok) throw new GitHubError(response.status, path);
    return response.status === 204 ? null : response.json();
  }
  async read() {
    const ref = await this.api(`git/ref/heads/${stateBranch}`, 'GET', undefined, true);
    if (!ref) return { head: null, tree: null, ledger: { schema: 1, highWaterMark: historicalFloor, reservations: [] } };
    assert.match(ref.object?.sha ?? '', /^[a-f0-9]{40}$/);
    const commit = await this.api(`git/commits/${ref.object.sha}`);
    const file = await this.api(`contents/ledger.json?ref=${ref.object.sha}`);
    const ledger = JSON.parse(Buffer.from(file.content, 'base64').toString('utf8'));
    assert.equal(ledger.schema, 1, 'Unknown distribution ledger schema.');
    assert.ok(Array.isArray(ledger.reservations), 'Invalid distribution ledger.');
    assert.ok(Number.isSafeInteger(ledger.highWaterMark) && ledger.highWaterMark >= historicalFloor, 'Ledger regressed.');
    const versions = new Set(), codes = new Set();
    let priorCode = historicalFloor;
    for (const item of ledger.reservations) {
      assert.ok(!versions.has(item.version) && !codes.has(item.versionCode), 'Duplicate ledger reservation.');
      assert.ok(Number.isSafeInteger(item.versionCode) && item.versionCode <= ledger.highWaterMark && item.versionCode > priorCode, 'Ledger installation codes must strictly increase.');
      priorCode = item.versionCode;
      versions.add(item.version); codes.add(item.versionCode);
    }
    return { head: ref.object.sha, tree: commit.tree.sha, ledger };
  }
  async commit(snapshot, ledger, manifest) {
    const entries = [{ path: 'ledger.json', mode: '100644', type: 'blob', content: json(ledger) }];
    if (manifest) entries.push({ path: 'latest.json', mode: '100644', type: 'blob', content: json(manifest) });
    const tree = await this.api('git/trees', 'POST', { ...(snapshot.tree ? { base_tree: snapshot.tree } : {}), tree: entries });
    const commit = await this.api('git/commits', 'POST', { message: manifest ? `Recommend Aegis ${manifest.version}` : `Reserve Aegis ${ledger.reservations.at(-1).version}`,
      tree: tree.sha, parents: snapshot.head ? [snapshot.head] : [] });
    // force=false accepts only a fast-forward. A concurrent sibling commit fails;
    // neither its reservation nor the previously valid latest.json is overwritten.
    if (snapshot.head) await this.api(`git/refs/heads/${stateBranch}`, 'PATCH', { sha: commit.sha, force: false });
    else await this.api('git/refs', 'POST', { ref: `refs/heads/${stateBranch}`, sha: commit.sha });
    return commit.sha;
  }
  async reserve(plan, floor = historicalFloor) {
    for (let attempt = 0; attempt < 8; attempt++) {
      const snapshot = await this.read();
      const existing = snapshot.ledger.reservations.find(item => item.version === plan.version || item.runId === plan.runId);
      if (existing) {
        const { versionCode, ...identity } = existing;
        assert.deepEqual(identity, plan, 'Version or run already reserved for different source/metadata. Start a new distribution.');
        return existing;
      }
      for (const item of snapshot.ledger.reservations)
        assert.ok(compareVersions(plan.version, item.version) > 0, `Version must follow reserved ${item.version}.`);
      const versionCode = allocateVersionCode(plan.version, [snapshot.ledger.highWaterMark, floor]);
      const reservation = { ...plan, versionCode };
      const ledger = { ...snapshot.ledger, highWaterMark: versionCode, reservations: [...snapshot.ledger.reservations, reservation] };
      try { await this.commit(snapshot, ledger); return reservation; }
      catch (error) { if (!(error instanceof GitHubError) || ![409, 422].includes(error.status)) throw error; }
    }
    throw Error('Concurrent allocation did not converge; no version/code was reused.');
  }
  async recommend(plan, manifest) {
    assert.equal(manifest.version, plan.version, 'Manifest differs from reserved version.');
    assert.equal(manifest.versionCode, plan.versionCode, 'Manifest differs from reserved installation code.');
    for (let attempt = 0; attempt < 8; attempt++) {
      const snapshot = await this.read();
      const reservation = snapshot.ledger.reservations.find(item => item.version === plan.version);
      assert.deepEqual(reservation, plan, 'Publication must use the exact durable reservation.');
      const current = snapshot.ledger.recommended;
      if (current) {
        const difference = compareVersions(manifest.version, current.version);
        assert.ok(difference >= 0 && manifest.versionCode >= current.versionCode, 'Recommended distribution cannot regress.');
        if (difference === 0) {
          assert.equal(current.sha256, digest(manifest), 'Recommended version cannot change bytes or provenance.');
          return snapshot.head;
        }
        assert.ok(manifest.versionCode > current.versionCode, 'Recommended installation code must increase.');
      }
      // Store only an identity in the ledger; latest.json is the authoritative manifest.
      // A hash/identity is not another mutable copy of the complete distribution contract.
      const ledger = { ...snapshot.ledger, recommended: { version: manifest.version, versionCode: manifest.versionCode, sha256: digest(manifest) } };
      try { return await this.commit(snapshot, ledger, manifest); }
      catch (error) { if (!(error instanceof GitHubError) || ![409, 422].includes(error.status)) throw error; }
    }
    throw Error('Concurrent publication did not converge; previous recommendation remains valid.');
  }
}
