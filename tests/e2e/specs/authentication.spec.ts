import { expect, test } from '@playwright/test';
import { e2ePassword, e2eUsername } from '../support/authentication';

test('logout clears protected rows and persisted panel state before login restores access', async ({
  page,
}) => {
  await page.goto('/');
  const left = page.getByTestId('left-panel');
  await expect(left).toBeVisible();
  await expect(left.getByText('existing.txt', { exact: true })).toBeVisible();
  await left.click();
  await page.keyboard.type('existing');
  await expect(page.getByRole('searchbox', { name: 'Search active panel' })).toHaveValue('existing');

  await page.getByTestId('account-menu-trigger').click();
  await page.getByTestId('logout').click();

  await expect(page.getByTestId('login-form')).toBeVisible();
  await expect(page.getByTestId('left-panel')).toHaveCount(0);
  await expect(page.getByText('existing.txt', { exact: true })).toHaveCount(0);
  expect(
    await page.evaluate(() => localStorage.getItem('reachcommander.panel-state.v1')),
  ).toBeNull();

  await page.getByTestId('login-username').fill(e2eUsername);
  await page.getByTestId('login-password').fill(e2ePassword);
  await page.getByRole('button', { name: 'Sign in' }).click();

  await expect(page.getByTestId('left-panel')).toBeVisible();
  await expect(page.getByText('existing.txt', { exact: true })).toBeVisible();
});
