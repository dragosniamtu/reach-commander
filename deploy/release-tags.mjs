#!/usr/bin/env node

import { appendFile, readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const approvedImage = 'ghcr.io/dragosniamtu/reach-commander';
const number = '(0|[1-9][0-9]*)';
const prereleaseIdentifier = '(0|[1-9][0-9]*|[A-Za-z-][0-9A-Za-z-]*)';
const versionExpression = new RegExp(
  `^refs/tags/(v(${number})\\.(${number})\\.(${number})(-(${prereleaseIdentifier})(\\.(${prereleaseIdentifier}))*)?)$`,
);
const stableVersionExpression = new RegExp(`^v${number}\\.${number}\\.${number}$`);

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

function stableVersion(value) {
  const match = stableVersionExpression.exec(value);
  if (!match) {
    return null;
  }
  return [BigInt(match[1]), BigInt(match[2]), BigInt(match[3])];
}

function compareVersion(left, right) {
  for (let index = 0; index < left.length; index += 1) {
    if (left[index] < right[index]) return -1;
    if (left[index] > right[index]) return 1;
  }
  return 0;
}

export function assertStablePromotion(candidate, repositoryTags) {
  const candidateVersion = stableVersion(candidate);
  if (!candidateVersion) {
    throw new Error('Stable promotion candidate must be a stable version tag.');
  }
  if (!Array.isArray(repositoryTags)) {
    throw new Error('Repository tags must be an array.');
  }
  const stableTags = repositoryTags
    .map((tag) => ({ tag, version: typeof tag === 'string' ? stableVersion(tag) : null }))
    .filter((item) => item.version !== null);
  if (!stableTags.some((item) => item.tag === candidate)) {
    throw new Error('Stable promotion candidate is not present in repository tags.');
  }
  const newest = stableTags.reduce((current, item) =>
    compareVersion(item.version, current.version) > 0 ? item : current,
  );
  if (newest.tag !== candidate) {
    throw new Error(`Only the newest stable tag may be promoted; newest is ${newest.tag}.`);
  }
}

export function promotionTagsForRef(ref, image, repositoryTags, reusedImmutable) {
  if (typeof reusedImmutable !== 'boolean') {
    throw new Error('Immutable retry state must be a boolean.');
  }
  const tags = tagsForRef(ref, image);
  const release = versionForRef(ref);
  if (!release || release.prerelease) {
    return tags;
  }
  try {
    assertStablePromotion(release.version, repositoryTags);
  } catch (error) {
    if (
      reusedImmutable &&
      error instanceof Error &&
      error.message.startsWith('Only the newest stable tag may be promoted')
    ) {
      return tags.slice(0, 1);
    }
    throw error;
  }
  return tags;
}

async function readRepositoryTags(tagsPath) {
  return (await readFile(tagsPath, 'utf8'))
    .split(/\r?\n/)
    .filter((tag) => tag.length > 0);
}

async function appendTagsOutput(outputPath, tags) {
  const content = [
    'tags<<REACHCOMMANDER_TAGS',
    ...tags,
    'REACHCOMMANDER_TAGS',
    '',
  ].join('\n');
  await appendFile(outputPath, content, { encoding: 'utf8', mode: 0o600 });
}

async function runCli() {
  if (process.argv[2] === 'resolve-promotion-tags') {
    const [, , , ref, image, tagsPath, reusedValue, outputPath] = process.argv;
    if (
      !ref ||
      !image ||
      !tagsPath ||
      !['true', 'false'].includes(reusedValue) ||
      !outputPath ||
      process.argv.length !== 8
    ) {
      throw new Error(
        'Usage: release-tags.mjs resolve-promotion-tags <git-ref> <image> <tags-file> <true|false> <github-output>',
      );
    }
    const repositoryTags = await readRepositoryTags(tagsPath);
    const tags = promotionTagsForRef(
      ref,
      image,
      repositoryTags,
      reusedValue === 'true',
    );
    await appendTagsOutput(outputPath, tags);
    return;
  }
  if (process.argv[2] === 'assert-stable-promotion') {
    const [, , , candidate, tagsPath] = process.argv;
    if (!candidate || !tagsPath || process.argv.length !== 5) {
      throw new Error('Usage: release-tags.mjs assert-stable-promotion <vX.Y.Z> <tags-file>');
    }
    const tags = await readRepositoryTags(tagsPath);
    assertStablePromotion(candidate, tags);
    return;
  }
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
