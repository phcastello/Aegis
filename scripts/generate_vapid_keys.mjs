#!/usr/bin/env node
import { generateKeyPairSync } from 'node:crypto';

// Run once. Store the private key in your deployment's secrets, never in git.
const { privateKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
const jwk = privateKey.export({ format: 'jwk' });
const publicKey = Buffer.concat([Buffer.from([4]), Buffer.from(jwk.x, 'base64url'), Buffer.from(jwk.y, 'base64url')]).toString('base64url');
process.stdout.write(`AEGIS_WEB_PUSH_PUBLIC_KEY=${publicKey}\nAEGIS_WEB_PUSH_PRIVATE_KEY=${jwk.d}\n`);
