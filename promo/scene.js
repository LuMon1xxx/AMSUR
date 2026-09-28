/* AMSUR promo scene renderer.
 *
 * Exposes window.renderFrame(timeSeconds, config) -> { sceneId, progress }.
 * - Selects the scene with start <= t < end (clamps out-of-range t).
 * - progress is the local scene progress in [0, 1].
 * - Updates the DOM (headline/subhead/footnote/badge/asset/cards) and applies
 *   only smooth opacity/transform motion. No strobe, no sharp zoom.
 * - Real PNG screenshots are shown undistorted (object-fit: contain, handled
 *   in CSS); screenshot pixels are never redrawn or restyled here.
 */
(function () {
  'use strict';

  function $(id) {
    return document.getElementById(id);
  }

  function clamp01(v) {
    if (typeof v !== 'number' || !isFinite(v)) return 0;
    if (v < 0) return 0;
    if (v > 1) return 1;
    return v;
  }

  // Smoothstep easing for gentle fade edges.
  function smooth(v) {
    v = clamp01(v);
    return v * v * (3 - 2 * v);
  }

  function findScene(t, scenes, duration) {
    for (var i = 0; i < scenes.length; i++) {
      if (t >= scenes[i].start && t < scenes[i].end) return scenes[i];
    }
    if (t >= duration) return scenes[scenes.length - 1];
    return scenes[0];
  }

  function setText(el, value) {
    if (!el) return;
    if (value === null || value === undefined || value === '') {
      el.hidden = true;
      if (el.tagName === 'H1' || el.tagName === 'P') el.textContent = '';
    } else {
      el.hidden = false;
      if (el.textContent !== String(value)) el.textContent = String(value);
    }
  }

  // Abstract decorative chips for the hook scene (pure typography, no controls).
  function renderHookChips(cards) {
    cards.innerHTML = '';
    var row = document.createElement('div');
    row.className = 'chip-row';
    var labels = ['Уроки', 'Классы', 'Учителя', 'Кабинеты', 'Смены', 'СанПиН', 'Нагрузка', 'Окна'];
    for (var i = 0; i < labels.length; i++) {
      var chip = document.createElement('span');
      chip.className = 'chip';
      chip.textContent = labels[i];
      row.appendChild(chip);
    }
    cards.appendChild(row);
    cards.hidden = false;
  }

  // Two equal role cards for the roles scene (copy from promo-design.md §4).
  function renderRoleCards(cards) {
    cards.innerHTML = '';
    var roles = [
      { title: 'Человек', items: ['Контекст школы', 'Приоритеты', 'Окончательное решение'], cls: '' },
      { title: 'АМСУР', items: ['Расчёт вариантов', 'Проверки ограничений', 'Рутинные вычисления'], cls: 'green' }
    ];
    for (var i = 0; i < roles.length; i++) {
      var card = document.createElement('div');
      card.className = 'role-card' + (roles[i].cls ? ' ' + roles[i].cls : '');
      var h = document.createElement('h2');
      h.textContent = roles[i].title;
      card.appendChild(h);
      var ul = document.createElement('ul');
      for (var j = 0; j < roles[i].items.length; j++) {
        var li = document.createElement('li');
        li.textContent = roles[i].items[j];
        ul.appendChild(li);
      }
      card.appendChild(ul);
      cards.appendChild(card);
    }
    cards.hidden = false;
  }

  function renderFrame(timeSeconds, config) {
    var scenes = (config && config.scenes) || [];
    if (!scenes.length) return { sceneId: '', progress: 0 };
    var duration = (config && typeof config.duration === 'number') ? config.duration : scenes[scenes.length - 1].end;

    var t = Number(timeSeconds);
    if (!isFinite(t)) t = 0;

    var scene = findScene(t, scenes, duration);
    var span = scene.end - scene.start;
    var progress = span > 0 ? (t - scene.start) / span : 0;
    progress = clamp01(progress);

    var stage = $('stage');
    var bgLayer = $('bg-layer');
    var bgImg = $('asset-img');
    var scrim = $('scrim');
    var badge = $('badge');
    var headline = $('headline');
    var subhead = $('subhead');
    var footnote = $('footnote');
    var cards = $('cards');
    var assetCard = $('asset-card');
    var assetCardImg = $('asset-card-img');
    var progressFill = $('progress-fill');

    if (stage) stage.setAttribute('data-scene', scene.id);

    setText(badge, scene.badge || null);
    setText(headline, scene.headline || '');
    if (headline) headline.hidden = false;
    setText(subhead, scene.subhead || null);
    setText(footnote, scene.footnote || null);

    var hasAsset = !!(scene.asset && assetCard && assetCardImg);
    if (assetCard && assetCardImg) {
      if (hasAsset) {
        if (assetCardImg.getAttribute('src') !== scene.asset) assetCardImg.setAttribute('src', scene.asset);
        assetCard.hidden = false;
      } else {
        assetCard.hidden = true;
        assetCardImg.removeAttribute('src');
      }
    }
    if (bgLayer && bgImg) {
      if (hasAsset) {
        if (bgImg.getAttribute('src') !== scene.asset) bgImg.setAttribute('src', scene.asset);
        bgLayer.classList.add('visible');
      } else {
        bgLayer.classList.remove('visible');
        bgImg.removeAttribute('src');
      }
    }
    if (scrim) {
      if (hasAsset) scrim.classList.add('visible');
      else scrim.classList.remove('visible');
    }

    if (cards) {
      if (scene.id === 'hook') renderHookChips(cards);
      else if (scene.id === 'roles') renderRoleCards(cards);
      else {
        cards.hidden = true;
        cards.innerHTML = '';
      }
    }

    // Gentle kinetic motion: content is fully visible from progress=0
    // (first frame is never empty), holds, then fades over the last 15%.
    // No incoming fade — it would blank frame 0 and flash on frame 1.
    // Asset card drifts at most ~2% scale across the scene.
    var fadeIn = 1;
    var fadeOut = 1 - smooth((progress - 0.85) / 0.15);
    var visibility = Math.min(fadeIn, fadeOut);
    var rise = 0;
    var sink = Math.round((1 - fadeOut) * 16);

    var animated = [badge, headline, subhead, footnote];
    for (var k = 0; k < animated.length; k++) {
      var el = animated[k];
      if (!el || el.hidden) continue;
      el.style.opacity = visibility.toFixed(3);
      el.style.transform = 'translateY(' + (rise + sink) + 'px)';
    }
    if (cards && !cards.hidden) {
      cards.style.opacity = visibility.toFixed(3);
      cards.style.transform = 'translateY(' + (rise + sink) + 'px)';
    }
    if (assetCard && !assetCard.hidden) {
      assetCard.style.opacity = visibility.toFixed(3);
      var drift = (1 + progress * 0.02).toFixed(4);
      assetCard.style.transform = 'translateY(' + (rise + sink) + 'px) scale(' + drift + ')';
    }
    if (progressFill && duration > 0) {
      progressFill.style.width = (clamp01(t / duration) * 100).toFixed(2) + '%';
    }

    return { sceneId: scene.id, progress: progress };
  }

  window.renderFrame = renderFrame;
})();
