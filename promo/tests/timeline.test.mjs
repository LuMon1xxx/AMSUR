import test from 'node:test';
import assert from 'node:assert/strict';
import config from '../promo.config.json' with { type: 'json' };

test('timeline is contiguous and exactly 30 seconds', () => {
  assert.equal(config.width, 1920);
  assert.equal(config.height, 1080);
  assert.equal(config.fps, 30);
  assert.equal(config.duration, 30);
  assert.equal(config.scenes.length, 7);
  assert.equal(config.scenes[0].start, 0);
  assert.equal(config.scenes.at(-1).end, 30);
  config.scenes.forEach((scene, index) => {
    assert.ok(scene.end > scene.start);
    if (index > 0) assert.equal(scene.start, config.scenes[index - 1].end);
  });
});

test('claims keep their required scope', () => {
  const text = JSON.stringify(config).toLowerCase();
  assert.match(text, /0 окон у учеников/);
  assert.match(text, /1196/);
  assert.match(text, /12 с/);
  assert.doesNotMatch(text, /оптимальное расписание|идеальное расписание|победа над человеком|140.{0,10}139/);
});
