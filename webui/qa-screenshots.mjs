import { chromium } from 'playwright-core';

const browser = await chromium.launch({ channel: 'msedge', headless: true });
for (const [name, width, height, isMobile] of [['mobile', 390, 844, true], ['desktop', 1440, 1000, false]]) {
  const page = await browser.newPage({ viewport: { width, height }, deviceScaleFactor: 1, isMobile, hasTouch: isMobile });
  await page.goto('http://127.0.0.1:6769', { waitUntil: 'networkidle' });
  await page.screenshot({ path: `${name}-review.png`, fullPage: true });
  const metrics = await page.evaluate(() => ({ viewport: document.documentElement.clientWidth, document: document.documentElement.scrollWidth, tiles: document.querySelectorAll('.sound-card').length, columns: getComputedStyle(document.querySelector('.sound-grid')).gridTemplateColumns }));
  console.log(name, JSON.stringify(metrics));
  if (isMobile) {
    await page.getByRole('button', { name: 'Select' }).click();
    await page.getByRole('button', { name: /Select correct/ }).click();
    await page.screenshot({ path: 'selection-review.png', fullPage: true });
    await page.getByRole('button', { name: 'Done' }).click();
    await page.getByRole('button', { name: 'Edit correct' }).click();
    await page.screenshot({ path: 'editor-review.png', fullPage: true });
    await page.getByRole('button', { name: 'Close editor' }).click();
  }
  await page.close();
}
await browser.close();
