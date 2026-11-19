import { spawnSync } from 'node:child_process';
import { readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { join, dirname } from 'node:path';
const here = dirname(fileURLToPath(import.meta.url));
const files = readdirSync(here).filter(n => n.endsWith('.test.js')).map(n => join(here, n));
const env = { ...process.env }; delete env.NODE_TEST_CONTEXT;
const r = spawnSync(process.execPath, ['--test', '--test-reporter=tap', ...files], { env, stdio: 'inherit' });
process.exitCode = r.status ?? 1;
