// What the pages of Atelier's apps share: elements placed where the theme puts them,
// in fractions of the screen width and height as ES-DE does, in the colors and sizes of
// the theme as ES-DE has it set up; shelves of box covers and lists of names; the
// options menu; help prompts with the theme's icons; the theme's navigation sounds; and
// input from a controller through the Gamepad API, or from the keyboard. The page loads
// this first and defines act(action), which receives every button press.

let S; // the session: colors, sizes, layouts, language and settings from ES-DE
let T; // the words in the language of ES-DE
let L; // the layouts for this aspect ratio

// In fractions of the screen height: the parts of a shelf
const SHELF = { label: 0.062, cover: 0.17, bar: 0.02, gap: 0.045 };
// The platform name sits at the top of its label space, leaving room above the covers
const SHELF_NAME = 0.03;
// Room beside the shelves for the light around a selected cover, in screen widths
const SHELF_PAD = 0.03;

const vw = f => `${f * 100}vw`;
const vh = f => `${f * 100}vh`;
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
const themeUrl = p => '/theme/' + p.replace(/^(\.\/)?(\.\.\/)?/, '');
const percent = f => `${Math.floor(f * 100)}%`;
const cycle = (list, value, step) => list[(list.indexOf(value) + step + list.length) % list.length];

function el(tag, cls, parent, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  if (parent) parent.appendChild(e);
  return e;
}

// Positions an element like ES-DE: pos and origin, then size, cropSize or maxSize
function place(e, p, { line = false, origin } = {}) {
  if (!p || !p.pos) {
    e.hidden = true;
    return e;
  }
  const size = p.size || p.cropSize || p.maxSize;
  const [ox, oy] = origin || p.origin || (p.cropSize || p.maxSize ? [0.5, 0.5] : [0, 0]);
  e.classList.add('el');
  e.style.left = vw(p.pos[0]);
  e.style.top = vh(p.pos[1]);
  if (size) {
    if (size[0]) e.style.width = vw(size[0]);
    if (size[1]) e.style.height = vh(size[1]);
    if (line && size[1]) e.style.lineHeight = vh(size[1]);
  }
  e.style.transform = `translate(${-ox * 100}%, ${-oy * 100}%)`;
  if (p.cornerRadius) e.style.borderRadius = vw(p.cornerRadius);
  return e;
}

function setVars(vars) {
  for (const [name, value] of Object.entries(vars)) document.documentElement.style.setProperty(`--${name}`, value);
}

const systemInfo = system => S.systems[system] || S.systems._default || { color: 'C9AB79', cover: 0.75 };

// The color scheme of ES-DE; the atmosphere schemes take the system's color
function applyColors(system) {
  const c = S.colors;
  const systemColor = systemInfo(system).color || 'C9AB79';
  const color = v => '#' + v.replace('${systemColor}', systemColor);
  setVars({
    bg: color(c.bgColor), surface: color(c.surfaceColor), title: color(c.titleColor), text: color(c.textColor),
    muted: color(c.mutedColor), list: color(c.listColor), faint: color(c.faintColor), accent: color(c.accentColor),
    placeholder: color(c.placeholderColor), ambient: color(c.ambientColor), glow: color(c.selectionGlowColor),
    'ambient-image': `url(${themeUrl(c.ambientImage)})`, 'glow-image': `url(${themeUrl(c.ambientTileImage)})`,
    dim: c.gridDimOpacity, 'atmosphere-opacity': c.atmosphereOpacity,
  });
  document.getElementById('atmosphere').style.filter = `saturate(${c.atmosphereSaturation})`;
}

function applySizes() {
  const f = S.fontSizes;
  // The footer of a 4:3 screen has no room for the larger prompts of some font sizes
  const help = Number(aspectRatio() === '4:3' ? f.helpSizeNarrow : f.helpSize) * 0.62;
  setVars({
    'caps-size': vh(f.capsSize), 'label-size': vh(f.labelSize), 'tag-size': vh(f.tagSize), 'value-size': vh(f.valueSize),
    'desc-size': vh(f.descSize), 'game-title-size': vh(f.gameTitleSize), 'tile-text': vh(f.gridTextSize),
    'help-size': vh(help), 'help-icon': vh(help * 1.25), 'help-spacing': vw(0.017), 'help-icon-spacing': vw(0.0055),
    'tile-radius': vw(0.0025), 'badge-gap': vw(0.0055), 'tab-gap': vw(0.018), 'list-size': vh(f.listSize),
    'list-margin': vw(L.games.gamelistTextlist.horizontalMargin), 'icon-gap': vw(0.008),
    'selector-width': vw(L.games.gamelistTextlist.selectorWidth), 'selector-height': vh(L.games.gamelistTextlist.selectorHeight),
  });
}

function aspectRatio() {
  const set = { '21:9': '21:9', '16:9': '16:9', '16:10': '16:9', '4:3': '4:3' }[S.aspectRatio];
  if (set) return set;
  const r = innerWidth / innerHeight;
  return r >= 2 ? '21:9' : r >= 1.5 ? '16:9' : '4:3';
}

// The content area left of the details column: from the page margin to the end of
// the theme's grid, between the header and the footer
function contentBox() {
  const grid = L.achievements.gamelistGrid;
  const list = L.games.gamelistTextlist;
  const left = L.games.help.pos[0];
  return { left, right: grid.pos[0] + grid.size[0] - (left - grid.pos[0]), top: list.pos[1], bottom: list.pos[1] + list.size[1] };
}

// System drawings, with the theme's fallback for systems that have none
const artChecked = new Map();
function setArt(e, system) {
  const own = themeUrl(`systems/controllers/${system}.svg`);
  const apply = ok => e.style.setProperty('--src', `url(${ok ? own : themeUrl('systems/controllers/custom-collections.svg')})`);
  if (artChecked.has(system)) return apply(artChecked.get(system));
  const img = new Image();
  img.onload = () => { artChecked.set(system, true); apply(true); };
  img.onerror = () => { artChecked.set(system, false); apply(false); };
  img.src = own;
}

// Labels above values, as the facts of the theme
function fact(parent, layout, labelName, valueName) {
  const label = place(el('div', 'line label', parent), layout[labelName], { line: true });
  const value = place(el('div', 'line value', parent), layout[valueName], { line: true });
  return (name, text) => {
    label.textContent = name;
    value.textContent = text ?? '—';
  };
}

// A hairline track filled with the accent up to a fraction
function bar(parent, p) {
  const track = el('div', 'bar', parent);
  if (p) place(track, p);
  const fill = el('div', 'fill', track);
  const set = fraction => { fill.style.width = `${clamp(fraction, 0, 1) * 100}%`; };
  set.track = track;
  return set;
}

// A label with a percentage at the right end, a bar beneath and a line of figures; the
// bar can be filled without a percentage. The label element is the function's label,
// for an icon before the text, and the bar's track its track.
function progressBlock(parent, x, y, width, { scroll = false } = {}) {
  const label = place(el('div', 'line label', parent), { pos: [x, y], size: [width * 0.7, 0.03] }, { line: true, origin: [0, 0.5] });
  const value = place(el('div', 'line percent', parent), { pos: [x + width, y], size: [width * 0.3, 0.04] }, { line: true, origin: [1, 0.5] });
  const fill = bar(parent, { pos: [x, y + 0.026], size: [width, 0.004] });
  const sub = place(el('div', 'line sub', parent), { pos: [x, y + 0.06], size: [width, 0.035] }, { line: true, origin: [0, 0.5] });
  const set = (name, fraction, figures, warning, barFraction) => {
    label.textContent = name;
    value.textContent = fraction == null ? '' : percent(fraction);
    fill(barFraction ?? (fraction || 0));
    if (scroll) scrollText(sub, figures || '');
    else sub.textContent = figures || '';
    sub.classList.toggle('warning', Boolean(warning));
  };
  set.label = label;
  set.track = fill.track;
  return set;
}

// What is wider than its box scrolls sideways, as the theme's lines do in ES-DE: it rests
// at its start for a moment, then slides until its end has gone by, a copy of it
// following after a gap, and rests again where it began. What fits stays still, and
// nothing ends in an ellipsis. The box keeps its content; call it once the content is in.
const MARQUEE_DELAY = 2500;
// In font sizes a second
const MARQUEE_SPEED = 2.5;
function marquee(box) {
  if (box.firstElementChild && box.firstElementChild.classList.contains('marquee-run')) return;
  if (box.scrollWidth <= box.clientWidth) return;
  const run = el('span', 'marquee-run');
  const first = el('span', '', run);
  first.append(...box.childNodes);
  const gap = el('span', 'marquee-gap', run);
  run.append(first.cloneNode(true));
  box.replaceChildren(run);
  box.classList.add('scrolling');
  const shift = first.offsetWidth + gap.offsetWidth;
  const travel = shift / (MARQUEE_SPEED * parseFloat(getComputedStyle(box).fontSize)) * 1000;
  const hold = MARQUEE_DELAY / (MARQUEE_DELAY + travel);
  run.animate([
    { transform: 'translateX(0)', offset: 0 },
    { transform: 'translateX(0)', offset: hold },
    { transform: `translateX(${-shift}px)`, offset: 1 },
  ], { duration: MARQUEE_DELAY + travel, iterations: Infinity });
}

// A line of text that scrolls when it doesn't fit (marquee). Setting the same text
// again leaves it scrolling where it is, as the pages set their lines over and over.
function scrollText(box, text) {
  const width = box.clientWidth;
  if (box.dataset.text === text && Number(box.dataset.width) === width) return;
  box.dataset.text = text;
  box.dataset.width = width;
  box.classList.remove('scrolling');
  box.textContent = text;
  marquee(box);
}

// An image in a rounded card with the ambient light behind and the system drawing as
// placeholder. Given a video too, the video takes the image's place as in ES-DE's game
// lists: once the selection has rested for the session's video delay, fading in, over
// and over; pause() holds it while something covers the page, resume() lets it go on.
function mediaCard(parent, layout, imageName) {
  place(el('div', 'ambient', parent), layout.mediaAmbient);
  place(el('div', 'matte', parent), layout.mediaMatte);
  const placeholder = place(el('div', 'art', parent), layout.mediaPlaceholder);
  placeholder.style.setProperty('--tint', 'var(--placeholder)');
  const box = place(el('div', 'matte', parent), layout[imageName]);
  box.style.background = 'none';
  const img = el('img', 'shot', box);
  img.onload = () => img.classList.add('shown');
  let video = null;
  let current = null;
  let timer = null;
  let paused = false;
  const stop = () => {
    clearTimeout(timer);
    if (!video) return;
    video.classList.remove('shown');
    video.pause();
    video.removeAttribute('src');
    video.load();
  };
  const schedule = () => {
    clearTimeout(timer);
    if (!current || paused) return;
    timer = setTimeout(() => {
      if (!video) {
        video = el('video', 'shot clip', box);
        video.loop = true;
        video.addEventListener('playing', () => video.classList.add('shown'));
      }
      const settings = S.video || {};
      video.style.transitionDuration = `${settings.fadeIn || 1.1}s`;
      video.muted = !settings.audio;
      video.volume = clamp(settings.volume ?? 1, 0, 1);
      video.src = current;
      video.play().catch(() => {});
    }, ((S.video && S.video.delay) || 1.6) * 1000);
  };
  const set = (url, system, videoUrl = null) => {
    setArt(placeholder, system);
    if (img.getAttribute('src') !== url) {
      img.classList.remove('shown');
      if (url) img.src = url;
      else img.removeAttribute('src');
    }
    // The same video goes on as it was
    if (videoUrl === current) return;
    stop();
    current = videoUrl;
    schedule();
  };
  set.pause = () => {
    if (paused) return;
    paused = true;
    clearTimeout(timer);
    if (video && video.getAttribute('src')) video.pause();
  };
  set.resume = () => {
    if (!paused) return;
    paused = false;
    if (video && video.getAttribute('src')) video.play().catch(() => {});
    else schedule();
  };
  // For good, before the card is built again
  set.stop = () => {
    current = null;
    stop();
  };
  return set;
}

// Text that scrolls up slowly when it doesn't fit, as the theme's descriptions do
function scroller(box) {
  const inner = el('div', 'scroll', box);
  let timer = null;
  return content => {
    clearTimeout(timer);
    inner.style.transition = 'none';
    inner.style.transform = 'none';
    inner.replaceChildren(document.createTextNode(content || ''));
    const overflow = inner.scrollHeight - box.clientHeight;
    if (overflow <= 0) return;
    const lineHeight = parseFloat(getComputedStyle(box).fontSize) * 1.55;
    const run = () => {
      const seconds = overflow / lineHeight * 2.2;
      inner.style.transition = `transform ${seconds}s linear`;
      inner.style.transform = `translateY(${-overflow}px)`;
      timer = setTimeout(() => {
        inner.style.transition = 'none';
        inner.style.transform = 'none';
        timer = setTimeout(run, 5000);
      }, seconds * 1000 + 8000);
    };
    timer = setTimeout(run, 5000);
  };
}

// At the right margin of the header line, where ES-DE puts the clock, which the page does
// not show
function corner(parent, layout) {
  return place(el('div', 'line corner', parent),
    { pos: layout.clock.pos, size: [0.3, S.fontSizes.capsSize * 2] },
    { line: true, origin: [1, 0.5] });
}

// The tags beneath the caption, where the theme shows the file tags; the tag font draws
// each bracketed word as an outlined tag
function tag(parent, layout) {
  const e = place(el('div', 'line tags', parent), layout.fileTags, { line: true });
  return words => { e.textContent = words.filter(Boolean).map(w => `(${w})`).join(' '); };
}

function header(root, layout, text) {
  const art = place(el('div', 'art', root), layout.headerArt);
  const title = place(el('div', 'line caps header', root, text), layout.header, { line: true });
  return { art, title };
}

function message(target, title, text) {
  target.hidden = !title;
  if (!title) return;
  target.replaceChildren(el('span', 'caps', null, title), document.createTextNode(text || ''));
}

// The theme's icon for a button of the controller chosen in ES-DE: a, b, x, y, start and
// back (Select) stand for the buttons of that controller, other names are icons of their
// own. Start and Back differ between the models of a family, as on PS4 and PS5.
function helpIcon(name) {
  const type = S.controller.toLowerCase();
  const family = type.startsWith('ps') ? 'PS' : type.startsWith('switch') ? 'switch' : type === 'snes' ? 'SNES' : 'XBOX';
  const model = { ps123: 'PS123', ps4: 'PS4', ps5: 'PS5', xbox360: 'XBOX360' }[type] || (family === 'PS' ? 'PS4' : family);
  const file = ['a', 'b', 'x', 'y'].includes(name) ? `button_${name}_${family}`
    : ['start', 'back'].includes(name) ? `button_${name}_${model}` : name;
  return themeUrl(`core/help/${file}.svg`);
}

// Help prompts with the theme's icons, as [[button, label]]
function showHelp(entries) {
  const help = document.getElementById('help');
  place(help, { pos: L.games.help.pos }, { origin: [0, 0.5] });
  help.replaceChildren(...entries.map(([name, label]) => {
    const entry = el('div', 'entry');
    el('div', 'art', entry).style.setProperty('--src', `url(${helpIcon(name)})`);
    el('span', '', entry, label);
    return entry;
  }));
}

const locale = () => S.language.replace('_', '-');
const number = n => new Intl.NumberFormat(locale()).format(n);
const parseDate = s => s ? new Date(/Z|[+-]\d\d:?\d\d$/.test(s) ? s : s.replace(' ', 'T') + 'Z') : null;
function date(s, withTime) {
  const d = parseDate(s);
  if (!d) return null;
  const options = { day: 'numeric', month: 'short', year: 'numeric', ...(withTime && { hour: '2-digit', minute: '2-digit' }) };
  return new Intl.DateTimeFormat(locale(), options).format(d);
}

// Shelves and lists

// One row of box covers per group, each group a platform or a selection such as the
// games in progress: groups are [{ key, system, title, games: [ids], count }], count
// being how many games the heading names when not all ids are games, and draw has for
// an id its system, how to fill its picture and what goes beneath its cover. The view
// keeps where it scrolled to.
function groupLabel(parent, group) {
  const label = el('div', 'shelf-label', parent);
  if (group.system) setArt(el('div', 'art', label), group.system);
  el('span', '', label, group.title);
  el('span', 'count', label, number(group.count ?? group.games.length));
  return label;
}

function drawShelves(view, groups, draw) {
  view.track.replaceChildren();
  const H = innerHeight;
  const pad = SHELF_PAD * innerWidth;
  const coverHeight = SHELF.cover * H;
  const shelfHeight = (SHELF.label + SHELF.cover + SHELF.bar + SHELF.gap) * H;
  groups.forEach((group, s) => {
    const e = el('div', 'shelf', view.track);
    e.style.top = `${s * shelfHeight}px`;
    e.style.height = `${shelfHeight}px`;
    const label = groupLabel(e, group);
    label.style.left = `${pad}px`;
    label.style.height = `${SHELF_NAME * H}px`;
    const row = el('div', 'row', e);
    row.style.left = `${pad}px`;
    row.style.top = `${SHELF.label * H}px`;
    group.x = [];
    group.w = [];
    let x = 0;
    for (const id of group.games) {
      const width = coverHeight * clamp(systemInfo(draw.system(id)).cover || 0.75, 0.6, 1.5);
      const tile = el('div', 'cover', row);
      tile.style.left = `${x}px`;
      tile.style.width = `${width}px`;
      tile.style.height = `${coverHeight}px`;
      el('div', 'glow', tile);
      draw.fill(el('div', 'pic', tile), id);
      draw.beneath(tile, id, coverHeight);
      group.x.push(x);
      group.w.push(width);
      x += width + coverHeight * 0.16;
    }
    group.row = row;
    group.el = e;
  });
  view.track.style.transform = 'none';
  view.scroll = 0;
}

// Names one under the other, as in the theme's game lists; with covers, each name has
// the game's whole box cover beside it and a line of its own beneath (draw.meta)
function drawList(view, groups, covers, draw) {
  view.listTrack.replaceChildren();
  const H = innerHeight;
  const rowHeight = covers ? 0.09 * H : S.fontSizes.listSize * S.fontSizes.listSpacing * H;
  const headHeight = 0.055 * H;
  const list = L.games.gamelistTextlist;
  view.selector = el('div', 'selector', view.listTrack);
  view.selector.style.width = vw(list.selectorWidth);
  view.selector.style.height = covers ? `${rowHeight * 0.5}px` : vh(list.selectorHeight);
  view.selectorOffset = covers ? rowHeight * 0.25 : list.selectorVerticalOffset * H;
  let y = 0;
  groups.forEach(group => {
    const label = groupLabel(view.listTrack, group);
    label.style.top = `${y}px`;
    label.style.height = `${headHeight * 0.6}px`;
    label.style.left = vw(list.horizontalMargin);
    group.label = label;
    group.rows = [];
    y += headHeight;
    for (const id of group.games) {
      const row = el('div', `list-row${covers ? ' with-cover' : ''}`, view.listTrack);
      row.style.top = `${y}px`;
      row.style.height = `${rowHeight}px`;
      if (covers) {
        // The whole cover, in a square so that the names line up whatever its shape
        const pic = el('div', 'pic', row);
        pic.dataset.icon = '1';
        draw.fill(pic, id);
        const text = el('div', 'text', row);
        el('div', 'name', text, draw.name(id));
        draw.meta(el('div', 'meta', text), id);
      } else {
        row.style.lineHeight = `${rowHeight}px`;
        row.textContent = draw.name(id);
        if (draw.decorate) draw.decorate(row, id);
      }
      group.rows.push({ el: row, top: y, height: rowHeight });
      y += rowHeight;
    }
    y += 0.015 * H;
  });
  view.listHeight = y;
}

// The selected row stays in the middle of the list, as in the theme's game lists
function highlightList(view, groups, group, pick) {
  groups.forEach(g => {
    g.label.classList.toggle('active', g === group);
    g.rows.forEach((row, n) => row.el.classList.toggle('selected', g === group && n === pick));
  });
  const row = group.rows[pick];
  view.selector.style.top = `${row.top + view.selectorOffset}px`;
  const height = view.listBox.clientHeight;
  const scroll = clamp(row.top + row.height / 2 - height / 2, 0, Math.max(0, view.listHeight - height));
  view.listTrack.style.transform = `translateY(${-scroll}px)`;
}

// picks holds the chosen cover of every shelf, by its key
function highlightShelves(view, groups, s, pick, picks) {
  const group = groups[s];
  groups.forEach((g, n) => {
    g.el.classList.toggle('active', n === s);
    const chosen = picks[g.key] ?? 0;
    [...g.row.children].forEach((tile, t) => tile.classList.toggle('selected', t === chosen));
  });
  // Scroll the shelf sideways, and the shelves up or down, only as far as needed
  const visible = view.shelves.clientWidth - 2 * SHELF_PAD * innerWidth;
  const peek = SHELF.cover * innerHeight * 0.3;
  group.offset = clamp(group.offset || 0, group.x[pick] + group.w[pick] + peek - visible, Math.max(0, group.x[pick] - peek));
  group.row.style.transform = `translateX(${-group.offset}px)`;
  const height = (SHELF.label + SHELF.cover + SHELF.bar + SHELF.gap) * innerHeight;
  // The gap after the last visible shelf may fall outside
  const bottom = (s + 1) * height - SHELF.gap * innerHeight;
  view.scroll = clamp(view.scroll || 0, bottom - view.shelves.clientHeight, s * height);
  view.track.style.transform = `translateY(${-view.scroll}px)`;
}

// Where a direction leads in the shelves or lists, as [group, item], or null. In the
// lists, up and down go through the names across the groups, left and right jump to
// the group before or after; on the shelves, left and right go along a shelf, up and
// down to the shelf above or below, LT and RT to the first and last.
function moveIn(groups, list, s, n, action) {
  const last = groups.length - 1;
  if (list && ['up', 'down', 'left', 'right'].includes(action)) {
    let [t, m] = [s, n];
    if (action === 'down') [t, m] = m + 1 < groups[t].games.length ? [t, m + 1] : [t + 1, 0];
    if (action === 'up') [t, m] = m > 0 ? [t, m - 1] : [t - 1, t > 0 ? groups[t - 1].games.length - 1 : 0];
    if (action === 'left') [t, m] = m > 0 ? [t, 0] : [t - 1, 0];
    if (action === 'right') [t, m] = [t + 1, 0];
    return t < 0 || t > last ? null : [t, m];
  }
  const moves = { left: [s, n - 1], right: [s, n + 1], up: [s - 1], down: [s + 1], lt: [0, 0], rt: [last] };
  if (!(action in moves)) return null;
  const [t, item] = moves[action];
  if (t < 0 || t > last || (item !== undefined && (item < 0 || item >= groups[t].games.length))) return null;
  if (t === s && item === n) return null;
  return [t, item];
}

// The options menu and dialogs: a card in the surface color over the dimmed page, with
// a title, an optional text and rows of { label, value, plain, note, separated, disabled,
// art, sub }. A row with a value shows arrows to change it when selected; art puts a
// system's drawing before the label and sub a line beneath it. cls is a class of the
// card's own, for a look of its own.
function menuCard() {
  const root = el('div', 'menu-layer', document.body);
  root.hidden = true;
  el('div', 'menu-veil', root);
  el('div', 'menu-glow', root);
  const panel = el('div', 'menu', root);
  const title = el('div', 'menu-title', panel);
  el('span', 'menu-rule', title);
  const titleText = el('span', '', title);
  const text = el('div', 'menu-text', panel);
  const rows = el('div', 'menu-rows', panel);
  return {
    root,
    show(heading, items, selected, body, cls) {
      root.hidden = false;
      panel.className = cls ? `menu ${cls}` : 'menu';
      titleText.textContent = heading;
      text.hidden = !body;
      text.textContent = body || '';
      rows.replaceChildren(...items.map((row, n) => {
        const e = el('div', ['menu-row', n === selected && 'selected', row.separated && 'separated', row.disabled && 'disabled',
          row.sub && 'with-sub'].filter(Boolean).join(' '));
        if (row.art) setArt(el('div', 'art menu-art', e), row.art);
        const words = row.sub ? el('div', 'menu-words', e) : e;
        const label = el('span', 'menu-label', words, row.label);
        if (row.note) el('span', 'menu-note', label, row.note);
        if (row.sub) el('div', 'menu-sub', words, row.sub);
        if (row.value !== undefined) {
          // plain values, such as a username, keep their own letters
          const value = el('span', `menu-value${row.plain ? ' plain' : ''}`, e);
          if (row.arrows !== false) el('span', 'arrow', value, '‹');
          el('span', '', value, row.value);
          if (row.arrows !== false) el('span', 'arrow', value, '›');
        }
        return e;
      }));
      // Long menus scroll, only as far as needed to keep the selected row in view
      const chosen = rows.children[selected];
      if (chosen) {
        const top = chosen.offsetTop;
        const bottom = top + chosen.offsetHeight;
        if (top < rows.scrollTop) rows.scrollTop = top;
        else if (bottom > rows.scrollTop + rows.clientHeight) rows.scrollTop = bottom - rows.clientHeight;
      }
    },
    hide() { root.hidden = true; },
  };
}

// The on-screen keyboard, for addresses, names, passwords and keys, on the menu card:
// the d-pad and A type, X erases, Y adds a space, Start is done and B cancels. A
// keyboard types straight into it, and Ctrl+V pastes. labels names the shift, space
// and OK keys in the page's language; change, if given, makes the hint from the text
// typed so far, such as what a search finds; the page's renderHelp() shows the prompts
// while keyboard.isOpen().

const KEYBOARD = [
  ['1', '2', '3', '4', '5', '6', '7', '8', '9', '0', '-', '_'],
  ['q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p', '\\', '/'],
  ['a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l', '.', ':', '@'],
  ['z', 'x', 'c', 'v', 'b', 'n', 'm', ',', '!', '#', '$', '&'],
  ['shift', 'space', 'erase', 'ok'],
];

const keyboard = {
  current: null,
  layer: null,
  isOpen() {
    return Boolean(this.current);
  },
  open({ title, hint, value = '', mask = false, labels, done, cancel, change }) {
    this.current = { title, hint, value, mask, labels, done, cancel, change, row: 0, col: 0, shift: false };
    typing = e => this.type(e);
    this.changed();
  },
  // The text changed: the hint follows it
  changed() {
    const k = this.current;
    if (k.change) k.hint = k.change(k.value);
    this.render();
  },
  close() {
    this.current = null;
    typing = null;
    if (this.layer) this.layer.hidden = true;
    renderHelp();
  },
  render() {
    const k = this.current;
    if (!k) return;
    if (!this.layer || !this.layer.isConnected) this.layer = el('div', 'menu-layer keyboard-layer', document.body);
    const layer = this.layer;
    layer.hidden = false;
    layer.replaceChildren();
    el('div', 'menu-veil', layer);
    el('div', 'menu-glow', layer);
    const panel = el('div', 'menu keyboard', layer);
    const title = el('div', 'menu-title', panel);
    el('span', 'menu-rule', title);
    el('span', '', title, k.title);
    const field = el('div', 'field', panel);
    el('span', '', field, k.mask ? '•'.repeat(k.value.length) : k.value);
    el('span', 'caret', field);
    if (k.hint) el('div', 'hint', panel, k.hint);
    const grid = el('div', 'keys', panel);
    KEYBOARD.forEach((row, r) => {
      const line = el('div', `key-row${r === KEYBOARD.length - 1 ? ' wide' : ''}`, grid);
      row.forEach((key, c) => {
        const label = { shift: k.labels.shift, space: k.labels.space, erase: '⌫', ok: k.labels.ok }[key]
          || (k.shift ? key.toUpperCase() : key);
        el('div', `key${r === k.row && c === k.col ? ' selected' : ''}${key === 'shift' && k.shift ? ' on' : ''}`, line, label);
      });
    });
    renderHelp();
  },
  press(key) {
    const k = this.current;
    if (key === 'ok') {
      const { done, value } = k;
      this.close();
      return done(value);
    }
    if (key === 'shift') {
      k.shift = !k.shift;
      return this.render();
    }
    if (key === 'erase') k.value = k.value.slice(0, -1);
    else if (key === 'space') k.value += ' ';
    else k.value += k.shift ? key.toUpperCase() : key;
    this.changed();
  },
  act(action) {
    const k = this.current;
    const moves = { up: [-1, 0], down: [1, 0], left: [0, -1], right: [0, 1] };
    if (moves[action]) {
      const [dr, dc] = moves[action];
      const row = (k.row + dr + KEYBOARD.length) % KEYBOARD.length;
      // Moving between the letters and the wide keys keeps to the same part of the row
      const col = dr ? Math.min(KEYBOARD[row].length - 1, Math.floor(k.col * KEYBOARD[row].length / KEYBOARD[k.row].length))
        : (k.col + dc + KEYBOARD[row].length) % KEYBOARD[row].length;
      k.row = row;
      k.col = col;
      play('scroll');
      return this.render();
    }
    if (action === 'a') {
      play('select');
      return this.press(KEYBOARD[k.row][k.col]);
    }
    if (action === 'x') return this.press('erase');
    if (action === 'y') return this.press('space');
    if (action === 'start') return this.press('ok');
    if (action === 'b') {
      play('back');
      const { cancel } = k;
      this.close();
      if (cancel) cancel();
    }
  },
  // Keys typed on a keyboard go into the field; arrows still move on the on-screen keys
  type(e) {
    if (e.key === 'Enter') {
      this.press('ok');
      return true;
    }
    if (e.key === 'Escape') {
      this.act('b');
      return true;
    }
    if (e.key === 'Backspace') {
      this.press('erase');
      return true;
    }
    if (e.key.length === 1 && !e.ctrlKey && !e.altKey && !e.metaKey) {
      this.current.value += e.key;
      this.changed();
      return true;
    }
    return false;
  },
};
addEventListener('paste', e => {
  if (!keyboard.isOpen()) return;
  e.preventDefault();
  keyboard.current.value += (e.clipboardData.getData('text') || '').replace(/[\r\n]+/g, ' ').trim();
  keyboard.changed();
});

// Sounds

const sounds = {};
let audio = null;
function play(name) {
  if (!S.sounds) return;
  if (!audio) audio = new AudioContext();
  const start = buffer => {
    const source = audio.createBufferSource();
    const gain = audio.createGain();
    gain.gain.value = S.volume;
    source.buffer = buffer;
    source.connect(gain).connect(audio.destination);
    source.start();
  };
  if (!sounds[name]) {
    sounds[name] = fetch(themeUrl(`core/sounds/${name}.wav`)).then(r => r.arrayBuffer())
      .then(data => audio.decodeAudioData(data)).catch(() => null);
  }
  sounds[name].then(buffer => buffer && start(buffer));
}

// Input

// As in ES-DE: Enter is A, Backspace is B and Escape is Start. While a page sets
// typing to a function, it receives every key first, for text, and keeps those it
// returns true for.
let typing = null;
const KEYS = {
  ArrowUp: 'up', ArrowDown: 'down', ArrowLeft: 'left', ArrowRight: 'right', Enter: 'a', ' ': 'a',
  Backspace: 'b', Escape: 'start', PageUp: 'lb', PageDown: 'rb', Home: 'lt', End: 'rt', y: 'y', Y: 'y', x: 'x', X: 'x',
  Tab: 'select',
};
addEventListener('keydown', e => {
  if (typing && typing(e)) {
    e.preventDefault();
    return;
  }
  if (!KEYS[e.key]) return;
  e.preventDefault();
  act(KEYS[e.key]);
});

// Controllers in the standard layout: buttons A, B, X, Y, LB, RB, LT, RT, Back, Start and
// the d-pad, or the left stick. Held directions repeat as in ES-DE.
const PAD = { 0: 'a', 1: 'b', 2: 'x', 3: 'y', 4: 'lb', 5: 'rb', 6: 'lt', 7: 'rt', 8: 'select', 9: 'start', 12: 'up', 13: 'down', 14: 'left', 15: 'right' };
const REPEATS = new Set(['up', 'down', 'left', 'right']);
const held = new Map();
function poll(now) {
  const pressed = new Set();
  for (const pad of navigator.getGamepads()) {
    if (!pad) continue;
    for (const [index, action] of Object.entries(PAD)) if (pad.buttons[index] && pad.buttons[index].pressed) pressed.add(action);
    const [x = 0, y = 0] = pad.axes;
    if (y < -0.5) pressed.add('up');
    if (y > 0.5) pressed.add('down');
    if (x < -0.5) pressed.add('left');
    if (x > 0.5) pressed.add('right');
  }
  for (const action of held.keys()) if (!pressed.has(action)) held.delete(action);
  for (const action of pressed) {
    const since = held.get(action);
    if (since === undefined) {
      held.set(action, now);
      act(action);
    } else if (REPEATS.has(action) && now - since > 500) {
      held.set(action, now - 500 + 110);
      act(action);
    }
  }
  requestAnimationFrame(poll);
}

// The session, with the words of its language and the layouts for the screen
async function openSession(strings) {
  S = await (await fetch('/api/session')).json();
  T = strings[S.language.slice(0, 2)] || strings.en;
  document.documentElement.lang = S.language.slice(0, 2);
  L = S.layouts[aspectRatio()];
  return S;
}

function quitApp() {
  play('back');
  fetch('/api/quit', { method: 'POST' }).catch(() => {});
  setTimeout(() => window.close(), 250);
}
