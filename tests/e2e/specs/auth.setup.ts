import { readFileSync } from 'node:fs';
import { expect, test } from '@playwright/test';
import {
  e2eAuthStatePath,
  e2eChangedPassword,
  e2ePassword,
  e2eSetupCodePath,
  e2eUsername,
  e2eWrongPassword,
} from '../support/authentication';

test('creates and verifies the real first-run administrator', async ({ page }) => {
  test.setTimeout(60_000);
  const setupCode = readFileSync(e2eSetupCodePath, 'utf8').trim();

  await page.goto('/');
  await expect(page.getByTestId('setup-form')).toBeVisible();
  await page.getByTestId('setup-code').fill(setupCode);
  await page.getByTestId('setup-username').fill(e2eUsername);
  await page.getByTestId('setup-password').fill(e2ePassword);
  await page.getByTestId('setup-password-confirmation').fill(e2ePassword);
  await page.getByRole('button', { name: 'Create administrator' }).click();
  await expect(page.getByTestId('left-panel')).toBeVisible();

  await logout(page);
  await login(page, e2eUsername, e2eWrongPassword);
  await expect(
    page.getByText('The supplied credentials are not valid.', { exact: true }),
  ).toBeVisible();
  await login(page, e2eUsername, e2ePassword);
  await expect(page.getByTestId('left-panel')).toBeVisible();

  await changePassword(page, e2ePassword, e2eChangedPassword);
  await expect(
    page.getByText('Password changed. Other sessions were signed out.', { exact: true }),
  ).toBeAttached();
  await changePassword(page, e2eChangedPassword, e2ePassword);
  await expect(
    page.getByText('Password changed. Other sessions were signed out.', { exact: true }),
  ).toBeAttached();

  await page.context().storageState({ path: e2eAuthStatePath });
});

async function login(page: import('@playwright/test').Page, username: string, password: string) {
  await page.getByTestId('login-username').fill(username);
  await page.getByTestId('login-password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
}

async function logout(page: import('@playwright/test').Page) {
  await page.getByTestId('account-menu-trigger').click();
  await page.getByTestId('logout').click();
  await expect(page.getByTestId('login-form')).toBeVisible();
}

async function changePassword(
  page: import('@playwright/test').Page,
  currentPassword: string,
  newPassword: string,
) {
  await page.getByTestId('account-menu-trigger').click();
  await page.getByTestId('change-password').click();
  const dialog = page.getByTestId('change-password-dialog');
  await dialog.locator('#current-password').fill(currentPassword);
  await dialog.locator('#new-password').fill(newPassword);
  await dialog.locator('#confirm-password').fill(newPassword);
  await dialog.getByRole('button', { name: 'Change password', exact: true }).click();
  await expect(dialog).toBeHidden();
}
