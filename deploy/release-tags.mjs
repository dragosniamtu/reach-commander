#!/usr/bin/env node

import { appendFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const approvedImage = 'ghcr.io/dragosniamtu/reach-commander';
const number = '(0|[1-9][0-9]*)';
const prereleaseIdentifier = '(0|[1-9][0-9]*|[A-Za-z-][0-9A-Za-z-]*)';
const versionExpression = new RegExp(
  `^refs/tags/(v(${number})\\.(${number})\\.(${number})(-(${prereleaseIdentifier})(\\.(${prereleaseIdentifier}))*)?)$`,
);

function validateImage(image) {
  if (image !== approvedImage) {
    throw new Error('Image must be the approved ReachCommander GHCR repository.');
  }
}

function versionForRef(ref) {
  const match = versionExpression.exec(ref);
  if (match) {
    return {
      version: match[1],
      major: match[3],
      minor: match[5],
      prerelease: match[8] !== undefined,
    };
  }
  if (ref.startsWith('refs/tags/')) {
    throw new Error('Version tag must be a strict vX.Y.Z semantic version.');
  }
  return null;
}

export function tagsForRef(ref, image) {
  validateImage(image);
  if (ref === 'refs/heads/master') {
    return [`${image}:edge`];
  }
  const release = versionForRef(ref);
  if (!release) {
    return [];
  }
  if (release.prerelease) {
    return [`${image}:${release.version}`];
  }
  return [
    `${image}:${release.version}`,
    `${image}:v${release.major}.${release.minor}`,
    `${image}:v${release.major}`,
    `${image}:stable`,
  ];
}

async function runCli() {
  const [, , ref, image, outputPath] = process.argv;
  if (!ref || !image || !outputPath || process.argv.length !== 5) {
    throw new Error('Usage: release-tags.mjs <git-ref> <image> <github-output>');
  }
  const tags = tagsForRef(ref, image);
  const release = versionForRef(ref);
  const content = [
    'tags<<REACHCOMMANDER_TAGS',
    ...tags,
    'REACHCOMMANDER_TAGS',
    `stableRelease=${release !== null && !release.prerelease}`,
    `version=${release?.version ?? ''}`,
    '',
  ].join('\n');
  await appendFile(outputPath, content, { encoding: 'utf8', mode: 0o600 });
}

const invokedPath = process.argv[1] ? resolve(process.argv[1]) : '';
if (invokedPath === fileURLToPath(import.meta.url)) {
  runCli().catch((error) => {
    process.stderr.write(`ReachCommander release tags: ${error.message}\n`);
    process.exitCode = 1;
  });
}
