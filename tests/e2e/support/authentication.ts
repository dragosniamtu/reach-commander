import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const supportDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(supportDirectory, '..', '..', '..');

export const e2eUsername = 'reachcommander-e2e';
export const e2ePassword = 'ReachCommander-E2E-Password-2026!';
export const e2eChangedPassword = 'ReachCommander-E2E-Changed-2026!';
export const e2eWrongPassword = 'ReachCommander-E2E-Wrong-2026!';
export const e2eSetupCodePath = resolve(repositoryRoot, 'artifacts', 'e2e-setup-code.txt');
export const e2eAuthStatePath = resolve(repositoryRoot, 'artifacts', 'playwright-auth-state.json');
