// The page of Catalogue, in two modes of its own that Select chooses between, as the
// header shows: the Catalogue and the Collections (collections.js). The Catalogue has
// three sections, which LB/RB go through: the Archive, all that the archive holds,
// installed or not; In ES-DE, the games installed here, archived or not; and Not
// archived, the games of ES-DE that the archive doesn't have. Each lists its platforms,
// and for the one chosen with A its games, as a grid of box covers or a list, each with
// an icon for how it stands (installed, to sync, to archive, to install, can't be
// installed), and for the chosen game what it is and what can be done with it. A opens
// the actions for the game, or for the games marked with X; Y those for the whole
// platform; B goes back to the platforms; Start opens the options, where the archive's
// folder and the media to send and to bring are chosen. The app always opens in the
// Catalogue, in the section used last; each mode is found again as it was left. Transfers run on the server one after the other while the page goes
// on, whichever mode is open. Built on the parts all of Atelier's apps share, in
// common/ui/atelier.js.
//
// A library can hold hundreds of thousands of games, even in one platform, so the page
// never has them all: the server (Library in common/scripts/windows/native.cs) tells it
// how the platforms stand, gives the games of the list open a page at a time, and a
// game's details when it is chosen. Only the covers and rows in view, and a few around
// them, are on the page.

let library = { location: null, summary: null, collections: [], union: { games: 0, local: 0 } };
// section: catalogue or collections; view: platforms or games (of a platform) in the
// Catalogue, collections (the list), collection (its games) or search (games to add to
// it) in the Collections; filter: the section of the Catalogue (FILTERS); game: the
// place of the chosen game in the list open
const state = {
  screen: 'library', section: 'catalogue', view: 'platforms', platform: null, platformPick: 0, filter: 'archive', mode: 'grid',
  sort: 'name', picks: {}, game: null, marked: new Set(), markedAll: null, overlay: null,
  collection: null, collectionPick: 1, collectionFilter: 'all', query: null, where: 'all',
};
const SECTIONS = ['catalogue', 'collections'];
// What the mode that isn't open keeps: its view and its marked games
const away = { catalogue: { view: 'platforms', marked: new Set() }, collections: { view: 'collections', marked: new Set() } };
const G = {}; // elements of the library
const B = { path: null, rows: [], selected: 0, info: null }; // the folder chooser
let platforms = []; // the platforms shown, with how many games of the section they have
let jobs = { current: null, queue: [], queued: 0, waiting: [], done: [], esdeChanged: false };
let waiting = new Set();
let since = 0;
const outcomes = []; // finished transfers not yet reported
const failures = new Map(); // the last error of a game, by id
const parts = new Map(); // the bars and tiles on the page, by their place in the list
const details = new Map(); // what the server told of a game, by id, for the library's version

// The sections of the Catalogue: the archive, ES-DE, and the games of ES-DE never archived
const FILTERS = ['archive', 'local', 'unarchived'];
const SORTS = ['name', 'size', 'recent'];
const MODES = ['grid', 'list', 'covers'];
// In fractions of the screen height: the progress block at the top of the content
const OVERVIEW = 0.115;
// The games the server gives at once
const PAGE = 150;

const sizeOf = g => (g.local ? g.local.size : g.archive ? g.archive.size : 0);
const libraryVersion = () => (library.summary ? library.summary.version : 0);
// Whether the page has the library as the server read it last. Not while the server
// reads it, the first time or again after another archive or other media to compare
// were chosen: what the page had then is no longer so, and an archive just chosen
// would look empty, so the page waits for it as for the first reading.
const libraryReady = () => Boolean(library.summary && library.summary.ready && !library.summary.building);
// The library couldn't be read, and isn't being read again
const libraryFailed = () => Boolean(library.summary && !library.summary.ready && !library.summary.building && library.summary.error);

// How a game stands, as its icon shows it: installed (in ES-DE and in the archive, the
// same), sync (in both, but changed in ES-DE since it was archived: the archive's folder
// is never edited by hand), upload (in ES-DE only), download (in the archive only) or
// unavailable (in the archive only, of a system this ES-DE doesn't have)
function stateOf(g) {
  if (!g.local) return g.known ? 'download' : 'unavailable';
  return g.status === 'archived' ? 'installed' : g.status === 'local' ? 'upload' : 'sync';
}

// Whether a game is in a section: in the archive, in ES-DE, or in ES-DE and never archived
const inSection = (g, section = state.filter) => (section === 'archive' ? Boolean(g.archive)
  : section === 'local' ? Boolean(g.local) : g.status === 'local');

// The icon of a state: a disc with a sign, at the corner of a cover, or inline before a
// word with the class inline
function stateIcon(parent, cls = '') {
  return el('span', `state-icon${cls ? ` ${cls}` : ''}`, parent);
}

function bytes(n) {
  if (!n) return `0 ${T.units[0]}`;
  const unit = Math.min(T.units.length - 1, Math.floor(Math.log(n) / Math.log(1024)));
  const value = n / 1024 ** unit;
  return `${new Intl.NumberFormat(locale(), { maximumFractionDigits: value < 10 && unit ? 1 : 0 }).format(value)} ${T.units[unit]}`;
}

// The size of media by type ({ type: size }): of every type, or of those a setting
// chooses (archiveMedia, downloadMedia), the ones a transfer takes
function mediaSize(byType, key) {
  if (!byType) return 0;
  const types = key ? S.config[key] || [] : Object.keys(byType);
  return types.reduce((n, t) => n + (byType[t] || 0), 0);
}

// A size in words, then the size of the media that go with it when there are any:
// "1,2 GB  ·  media 85 MB". The size of the games never counts their media.
function withMedia(text, media, sep = '  ·  ') {
  return media ? `${text}${sep}${T.mediaSize(bytes(media))}` : text;
}

// A folder as a short name: its first and last parts, "NAS › retro"
function shortPath(p) {
  if (!p) return '';
  const parts = p.split(/[\\/]+/).filter(Boolean);
  return parts.length > 1 ? `${parts[0]} › ${parts[parts.length - 1]}` : parts[0] || p;
}

async function api(route, body) {
  const res = await fetch(route, body === undefined ? undefined : { method: 'POST', body: JSON.stringify(body) });
  return res.json();
}

// The list open, a page at a time: the games of a platform's section, of a collection
// (after the tile that adds games, offset 1) or what a search finds. source(from, count,
// at) asks the server for a page; at names a game whose place in the list comes with it.
// When the library changes, the pages are asked for again, the rows had kept meanwhile.
const list = { key: null, source: null, offset: 0, total: 0, known: false, rows: new Map(), fresh: new Set(), pending: new Set(), version: -1, extra: null, group: null };
const waiters = [];

// The row at a place in the list: the tile that adds games (ADD), a game, or undefined
// while its page hasn't come
const rowAt = n => (n === null || n === undefined || n < 0 ? undefined : n < list.offset ? ADD : list.rows.get(n - list.offset));
const viewCount = () => list.offset + list.total;
// The chosen game, if it is one and has come
function selected() {
  const row = rowAt(state.game);
  return row && row !== ADD ? row : null;
}
const selectedId = () => (selected() ? selected().id : null);

function setList(key, source, offset, expected, group) {
  if (list.key !== key) {
    Object.assign(list, { key, source, offset, total: expected || 0, known: false, rows: new Map(), fresh: new Set(), pending: new Set(), version: libraryVersion(), extra: null });
  } else if (list.version !== libraryVersion()) {
    list.version = libraryVersion();
    list.fresh = new Set();
    if (!list.known && expected !== undefined) list.total = expected;
  }
  list.source = source;
  list.group = group;
}

// The pages of the places from first to last, asked for when they haven't come
function want(first, last) {
  if (!list.source) return;
  const from = Math.max(0, Math.floor((first - list.offset) / PAGE));
  const to = Math.floor(Math.max(0, last - 1 - list.offset) / PAGE);
  for (let p = from; p <= to; p++) fetchPage(p);
  if (!list.known && !list.pending.size) fetchPage(0);
}

async function fetchPage(p) {
  if (list.fresh.has(p) || list.pending.has(p)) return;
  const key = list.key;
  const version = list.version;
  list.pending.add(p);
  let r = null;
  try {
    r = await list.source(p * PAGE, PAGE, null);
  } catch {
    r = null;
  }
  if (list.key !== key) return;
  list.pending.delete(p);
  if (!r || !r.games) return;
  if (version !== list.version) return fetchPage(p);
  list.fresh.add(p);
  const totalChanged = !list.known || r.total !== list.total;
  list.total = r.total;
  list.known = true;
  list.extra = r;
  r.games.forEach((row, i) => list.rows.set(r.from + i, row));
  for (const n of [...list.rows.keys()]) if (n >= r.total) list.rows.delete(n);
  onPage(r, totalChanged);
  while (waiters.length) waiters.shift()();
}

// Once the list open has come, as far as its first page
const listed = () => new Promise(resolve => (list.known || !list.source ? resolve() : waiters.push(resolve)));

// A page came: the list is drawn again if its length changed, else only the games it
// brought; the chosen one is shown if it is among them
function onPage(r, totalChanged) {
  if (!['games', 'collection', 'search'].includes(state.view) || state.screen !== 'library') return;
  if (totalChanged) {
    drawList();
    G.info.textContent = listInfo();
    renderTabs();
    renderOverview();
    const count = viewCount();
    if (!count) {
      state.game = null;
      clearDetails();
      emptyList();
      return;
    }
    message(G.message, null);
    return selectGame(clamp(state.game || 0, 0, count - 1));
  }
  for (let i = 0; i < r.games.length; i++) refreshTile(r.from + i + list.offset);
  markSelected();
  const n = state.game - list.offset;
  if (n >= r.from && n < r.from + r.games.length) showGame();
  if (state.view === 'search') {
    renderTabs();
    renderOverview();
  }
}

// The place of a game in the list open, then that game chosen; else the one chosen there last
async function locate(id) {
  const key = list.key;
  let place = -1;
  try {
    const r = await list.source(0, 0, id);
    place = r && r.place !== undefined ? r.place : -1;
  } catch {
    place = -1;
  }
  if (list.key !== key) return;
  const count = viewCount();
  if (!count) return;
  selectGame(place >= 0 ? place + list.offset : clamp(state.picks[pickKey()] || 0, 0, count - 1));
}

// Building the page

function buildLibrary() {
  if (G.media) G.media.stop();
  const root = document.getElementById('library');
  root.replaceChildren();
  const l = L.achievements;
  const b = contentBox();
  // The header: the mode open and where in it, and at the right end of the content the
  // other mode, after the button that chooses. Where the content is too narrow for both,
  // as on 4:3 screens, the other mode follows the open one, over the details.
  G.headerArt = place(el('div', 'art', root), l.headerArt);
  const flow = b.right - l.header.pos[0] < 0.3;
  const right = flow ? l.header.pos[0] + l.header.size[0] : b.right;
  const head = place(el('div', `line caps header modes${flow ? ' flow' : ''}`, root),
    { ...l.header, size: [right - l.header.pos[0], l.header.size[1]] }, { line: true });
  G.headerTitle = el('span', 'here', head);
  G.headerPlace = el('span', 'place', head);
  G.other = el('span', 'other', head);
  G.otherButton = el('span', 'art', G.other);
  G.otherName = el('span', '', G.other);
  G.corner = corner(root, l);
  G.overview = progressBlock(root, b.left, b.top + 0.012, b.right - b.left, { scroll: true });
  G.tabs = place(el('div', 'tabs', root), { pos: [b.left, b.top + 0.012], size: [b.right - b.left, 0.04] }, { origin: [0, 0.5] });
  G.shelves = place(el('div', 'shelves', root),
    { pos: [b.left - SHELF_PAD, b.top + OVERVIEW], size: [b.right - b.left + 2 * SHELF_PAD, b.bottom - b.top - OVERVIEW + 0.02] });
  G.track = el('div', 'track', G.shelves);
  G.listBox = place(el('div', 'listbox', root), { pos: [b.left, b.top + OVERVIEW], size: [b.right - b.left, b.bottom - b.top - OVERVIEW + 0.02] });
  G.listTrack = el('div', 'track', G.listBox);
  G.message = place(el('div', 'message', root), { pos: [b.left, b.top + OVERVIEW + 0.03], size: [b.right - b.left, 0.3] });
  G.info = place(el('div', 'line caps-light info', root), l.gamelistInfo, { line: true });

  G.title = place(el('div', 'title', root), l.gameTitle);
  place(el('div', 'rule', root), l.captionRule);
  G.caption = place(el('div', 'line caps caption', root), l.genre, { line: true });
  G.tag = tag(root, l);
  const d = l.description;
  G.status = progressBlock(root, d.pos[0], d.pos[1] + 0.018, d.size[0], { scroll: true });
  G.media = mediaCard(root, l, 'videoStill');
  G.facts = [
    fact(root, l, 'labelReleased', 'releaseDate'), fact(root, l, 'labelDeveloper', 'developer'),
    fact(root, l, 'labelPublisher', 'publisher'), fact(root, l, 'labelPlayers', 'players'),
  ];
  // What a game's facts show, place by place. Where the theme leaves the publisher out
  // (4:3), its saves are left out with it, and when it was archived takes the developer's
  // place, the one with room for two lines.
  G.gameFacts = l.labelPublisher && l.labelPublisher.pos ? ['size', 'media', 'archived', 'saves'] : ['size', 'archived', 'saves', 'media'];
  G.next = place(el('div', 'next', root), l.badges);
  G.tiles = new Map();
}

// The folder chooser: a list on the left, what the chosen row is on the right
function buildBrowse() {
  const root = document.getElementById('browse');
  root.replaceChildren();
  const l = L.achievements;
  const b = contentBox();
  const { art } = header(root, l, T.browse.title);
  art.style.setProperty('--src', `url(${themeUrl('systems/controllers/catalogue.svg')})`);
  B.path = place(el('div', 'line caps-light browse-path', root), { pos: [b.left, b.top + 0.012], size: [b.right - b.left, 0.04] },
    { line: true, origin: [0, 0.5] });
  B.box = place(el('div', 'listbox', root), { pos: [b.left, b.top + OVERVIEW * 0.6], size: [b.right - b.left, b.bottom - b.top - OVERVIEW * 0.6 + 0.02] });
  B.track = el('div', 'track', B.box);
  B.message = place(el('div', 'message', root), { pos: [b.left, b.top + OVERVIEW], size: [b.right - b.left, 0.3] });
  B.title = place(el('div', 'title', root), l.gameTitle);
  place(el('div', 'rule', root), l.captionRule);
  B.caption = place(el('div', 'line caps caption', root), l.genre, { line: true });
  const d = l.description;
  B.status = progressBlock(root, d.pos[0], d.pos[1] + 0.018, d.size[0]);
  B.facts = [fact(root, l, 'labelReleased', 'releaseDate'), fact(root, l, 'labelDeveloper', 'developer')];
}

function buildMenu() {
  if (G.menu) G.menu.root.remove();
  G.menu = menuCard();
  // The notice of finished transfers, centered on the header line
  if (G.notice) G.notice.remove();
  G.notice = place(el('div', 'notice-layer', document.body), { pos: [0.5, L.achievements.header.pos[1]] }, { origin: [0.5, 0.5] });
}

// A notice on the header line: a title, a line beneath it and a hint, sliding in and
// fading away after a while
let noticeTimer = null;
function notify(title, line, hint) {
  const card = el('div', 'notice');
  el('div', 'tick', card);
  const text = el('div', 'text', card);
  el('div', 'notice-title', text, title);
  el('div', 'notice-line', text, line);
  if (hint) el('div', 'notice-hint', text, hint);
  G.notice.replaceChildren(card);
  // Shown on the next frame, so that it slides in
  requestAnimationFrame(() => requestAnimationFrame(() => card.classList.add('shown')));
  clearTimeout(noticeTimer);
  noticeTimer = setTimeout(() => card.classList.remove('shown'), 5000);
}

// Tells that the transfers asked for are done: what was done, to which games, and for
// games brought or removed, when ES-DE shows them. The covers of those games light up.
function showNotice(done) {
  if (!done.length) return;
  const N = T.notice;
  const byAction = new Map();
  for (const job of done) byAction.set(job.action, [...(byAction.get(job.action) || []), job]);
  let title;
  let line;
  if (byAction.size === 1) {
    const [action, jobsDone] = [...byAction][0];
    title = N.done[action](number(jobsDone.length));
    line = N.names(jobsDone.map(j => j.name));
  } else {
    title = N.allDone;
    line = [...byAction].map(([action, jobsDone]) => N.count[action](number(jobsDone.length))).join('  ·  ');
  }
  const changedEsde = done.some(j => j.action === 'download' || j.action === 'remove-local');
  notify(title, line, changedEsde ? (jobs.restartsEsde ? N.hintRestart : N.hintManual) : '');
  play('favorite');
  const ids = new Set(done.map(j => j.id));
  for (const [n, p] of parts) {
    const row = rowAt(n);
    if (!row || !ids.has(row.id)) continue;
    p.tile.classList.add('done');
    setTimeout(() => p.tile.classList.remove('done'), 2600);
  }
}

// Help prompts for what is on screen; the game video holds meanwhile while a menu, the
// keyboard or the folder chooser covers it
function renderHelp() {
  if (G.media) {
    if (state.overlay || keyboard.isOpen() || state.screen !== 'library') G.media.pause();
    else G.media.resume();
  }
  if (keyboard.isOpen()) {
    return showHelp([['dpad_all', T.help.choose], ['a', T.help.type], ['x', T.help.erase], ['y', T.help.space],
      ['start', T.help.done], ['b', T.help.cancel]]);
  }
  if (state.overlay) {
    const change = state.overlay.rows.some(r => r.change);
    return showHelp([[state.overlay.across ? 'dpad_leftright' : 'dpad_updown', T.help.choose],
      ...(change ? [['dpad_leftright', T.help.change]] : []), ['a', T.help.select], ['b', T.help.close]]);
  }
  if (state.screen === 'browse') {
    return showHelp([['dpad_updown', T.help.choose], ['a', T.help.open], ...(B.info && B.info.path ? [['y', T.help.use]] : []),
      ['b', T.help.up]]);
  }
  if (blocked()) return showHelp([['a', T.help.select], ['b', T.help.back], ['start', T.help.menu]]);
  const H = T.help;
  if (state.view === 'platforms') {
    return showHelp([['dpad_updown', H.choose], ['button_lr', H.section], ['a', H.open], ...(state.platform ? [['y', H.actions]] : []),
      ['b', H.back], ['start', H.menu]]);
  }
  if (state.view === 'collections') {
    const row = G.rows && G.rows[state.collectionPick];
    const isNew = !row || row.kind === 'new';
    return showHelp([['dpad_updown', H.choose], ['a', isNew ? H.create : H.open], ...(isNew ? [] : [['y', H.collection]]),
      ['b', H.back], ['start', H.menu]]);
  }
  if (state.view === 'collection') {
    return showHelp([['dpad_all', H.choose], ['button_lr', H.filter], ['x', H.mark], ['y', H.collection],
      ['a', rowAt(state.game) === ADD ? H.add : H.actions], ['b', H.back], ['start', H.menu]]);
  }
  if (state.view === 'search') {
    const g = selected();
    return showHelp([['dpad_all', H.choose], ...(g ? [['a', g.member ? H.takeOut : H.add]] : []), ['y', H.search], ['x', H.filters],
      ['button_lr', H.where], ['b', H.finish]]);
  }
  showHelp([['dpad_all', H.choose], ['button_lr', H.section], ['x', H.mark], ['y', H.platform],
    ['a', H.actions], ['b', H.back], ['start', H.menu]]);
}

// The header names the mode open and where in it: the platform whose games are shown,
// with its drawing, or the collection; then the other mode, unless there is no archive
function setHeader(system, where) {
  const collections = state.section === 'collections';
  if (system) setArt(G.headerArt, system);
  else if (collections) setCollectionArt(G.headerArt);
  else G.headerArt.style.setProperty('--src', `url(${themeUrl('systems/controllers/catalogue.svg')})`);
  G.headerTitle.textContent = sectionName(state.section);
  G.headerPlace.textContent = where ? `›  ${where}` : '';
  G.other.hidden = blocked();
  G.otherButton.style.setProperty('--src', `url(${helpIcon('back')})`);
  G.otherName.textContent = sectionName(collections ? 'catalogue' : 'collections');
}

const sectionName = s => (s === 'collections' ? T.collections.title : T.title);

function showCorner() {
  G.corner.textContent = library.location ? shortPath(library.location.path) : '';
}

// The library: its platforms, as the server counts their games

const systemsOf = () => (library.summary ? library.summary.systems : []);
const systemOf = id => systemsOf().find(s => s.id === id);

// How many of a platform's games a section has, from their counts by state: installed,
// sync, upload, download, unavailable
function sectionCount(s, section = state.filter) {
  const [installed, sync, upload, download, unavailable] = s.counts;
  if (section === 'archive') return installed + sync + download + unavailable;
  if (section === 'local') return installed + sync + upload;
  return upload;
}

// The counts and sizes of a few platforms together
function aggregate(systems) {
  const a = { counts: [0, 0, 0, 0, 0], packed: 0, size: 0, upload: 0, archiveMedia: 0, localMedia: 0, uploadMedia: 0 };
  for (const s of systems) {
    s.counts.forEach((c, i) => { a.counts[i] += c; });
    for (const k of ['packed', 'size', 'upload', 'archiveMedia', 'localMedia', 'uploadMedia']) a[k] += s[k] || 0;
  }
  return a;
}

// How games stand in a section, in words beneath a bar: in the archive, how many are
// installed; in ES-DE, how many are archived as they are; not archived, how much there
// is to archive. label names what the bar measures.
function sectionLine(a, section = state.filter) {
  const O = T.sectionLine;
  const [installed, sync, upload, download, unavailable] = a.counts;
  if (section === 'archive') {
    const n = installed + sync + download + unavailable;
    return { label: O.label.archive, fraction: n ? (installed + sync) / n : 0, text: n ? [O.installed(number(installed + sync), number(n)),
      download && O.toInstall(number(download)), unavailable && O.unavailable(number(unavailable)), withMedia(bytes(a.packed), a.archiveMedia)]
      .filter(Boolean).join('  ·  ') : '' };
  }
  if (section === 'local') {
    const n = installed + sync + upload;
    return { label: O.label.local, fraction: n ? installed / n : 0, text: n ? [O.archived(number(installed), number(n)),
      sync && O.toSync(number(sync)), upload && O.unarchived(number(upload)), withMedia(bytes(a.size), a.localMedia)].filter(Boolean).join('  ·  ') : '' };
  }
  return { label: O.label.unarchived, fraction: 0, text: upload ? withMedia(`${O.toArchive(number(upload))}  ·  ${bytes(a.upload)}`, a.uploadMedia) : '' };
}

// The platforms with games in the section, by name: [{ key, system, title, count, s }]
function platformData() {
  const names = new Intl.Collator(locale());
  return systemsOf().map(s => ({ key: s.id, system: s.id, title: s.name, count: sectionCount(s), s }))
    .filter(p => p.count > 0).sort((a, b) => names.compare(a.title, b.title));
}

// The sections of the Catalogue, or the filters of the open collection or of what the
// search found, each with the number of games it shows
function renderTabs() {
  const c = currentCollection();
  const counts = list.extra && list.extra.counts ? list.extra.counts : {};
  const [filters, chosen, label, count] = {
    collection: [COLLECTION_FILTERS, state.collectionFilter, f => T.collections.filters[f],
      f => (!c ? 0 : f === 'all' ? c.games : f === 'local' ? c.local : c.games - c.local)],
    search: [WHERE, state.where, f => T.search.where[f], f => counts[f] || 0],
  }[state.view] || [FILTERS, state.filter, f => T.filters[f], f => systemsOf().reduce((sum, s) => sum + sectionCount(s, f), 0)];
  // The sections have no count while the library is read
  const counted = state.view === 'collection' || state.view === 'search' || libraryReady();
  G.tabs.replaceChildren(...filters.map(f => {
    const n = count(f);
    const tab = el('span', `tab${f === chosen ? ' active' : ''}${n || !counted ? '' : ' empty'}`, null, label(f));
    if (counted) el('span', 'count', tab, number(n));
    return tab;
  }));
  // Where the line is too short for all of them, as on a 4:3 screen, it slides to keep
  // the chosen one in view
  const active = G.tabs.querySelector('.active');
  G.tabs.scrollLeft = active ? Math.max(0, active.offsetLeft + active.offsetWidth - G.tabs.clientWidth) : 0;
}

// How the games of the section stand, those of the open platform, or all of them only
// in Not archived; or the transfer under way, in words beneath a bar; the sections take
// the whole line above it. While the library is read, a light runs along the bar.
function renderOverview() {
  const job = jobs.current;
  G.overview.track.classList.toggle('sweep', !job && !blocked() && !libraryReady() && !libraryFailed());
  if (job) {
    const batch = jobs.batch || { total: 1, done: 0 };
    const fraction = (batch.done + (job.fraction || 0)) / Math.max(1, batch.total);
    const step = job.step ? `  ·  ${T.steps[job.step] || job.step}` : '';
    const count = batch.total > 1 ? `  ·  ${T.of(number(batch.done + 1), number(batch.total))}` : '';
    G.overview('', null, `${T.working[job.action] || job.action}  ·  ${job.name}${step}${count}  ·  ${percent(fraction)}`, false, fraction);
    return;
  }
  if (blocked()) return G.overview('', null, '');
  // In the Catalogue the message beneath says it in full
  if (!libraryReady()) return G.overview('', null, state.section === 'collections' ? T.reading : '', false, 0);
  if (state.section === 'collections') return overviewCollections();
  const s = state.view === 'games' ? systemOf(state.platform) : null;
  if (!s && state.filter !== 'unarchived') return G.overview('', null, '');
  const line = sectionLine(aggregate(s ? [s] : systemsOf()));
  G.overview('', null, line.text, false, line.fraction);
}

// Box covers come from ES-DE's media or the archive's, else the name is set on the
// surface color as in the theme's grid; in the list, where there is no room for the
// name, the platform's drawing stands in. A game whose page hasn't come shows the surface.
// A cover that can't be read, its file gone or the archive out of reach, counts as none.
function fillCover(pic, g) {
  if (!g) {
    el('div', 'typo', pic);
    return;
  }
  const without = () => {
    if (pic.dataset.icon) setArt(el('div', 'art', pic), g.system);
    else el('div', 'typo', pic, g.name);
  };
  if (g.cover) {
    const img = el('img');
    img.loading = 'lazy';
    img.addEventListener('error', () => {
      if (pic.firstChild !== img) return;
      pic.replaceChildren();
      without();
    }, { once: true });
    img.src = g.cover;
    pic.replaceChildren(img);
  } else without();
}

// What the grid and lists show of a game: its cover with the icon of its state at the
// corner, beneath it a line for its transfer, and in the lists the icon and a word for
// its state. In a collection the first tile adds games (ADD).
const drawGame = {
  system: row => (row === ADD ? COLLECTION_ART : row ? row.system : ''),
  name: row => (row === ADD ? T.collections.addGames : row ? row.name : ''),
  fill(pic, row) {
    if (row === ADD) return fillAdd(pic);
    fillCover(pic, row);
  },
  beneath(tile, n, coverHeight) {
    const row = rowAt(n);
    if (row === ADD) return tile.classList.add('add');
    const H = innerHeight;
    const line = bar(tile);
    line.track.style.top = `${coverHeight + 0.01 * H}px`;
    line.track.style.height = `${Math.max(1, Math.round(0.0025 * H))}px`;
    el('div', 'check', tile);
    el('div', 'badge', tile);
    const icon = stateIcon(tile);
    // At the corner of the cover itself, which is narrower than its tile when its shape
    // differs from the grid's, as in a collection that mixes platforms
    const img = tile.querySelector('.pic img');
    if (img) {
      const fit = () => {
        if (!img.naturalWidth) return;
        const gap = (tile.clientWidth - img.offsetWidth) / 2;
        if (gap > 0) icon.style.right = `calc(${gap}px - 1.1vh)`;
      };
      if (img.complete) fit();
      else img.addEventListener('load', fit, { once: true });
    }
    parts.set(n, { tile, line, icon });
    decorate(n);
  },
  meta(meta, n) {
    const rowEl = meta.closest('.list-row');
    if (rowAt(n) === ADD) return rowEl.classList.add('add');
    const line = bar(meta);
    const icon = stateIcon(meta, 'inline');
    const text = el('span', 'state', meta);
    el('div', 'check', rowEl);
    parts.set(n, { tile: rowEl, line, icon, text });
    decorate(n);
  },
  decorate(rowEl, n) {
    if (rowAt(n) === ADD) {
      // Without a cover beside it, a plus says what the row does
      rowEl.textContent = `+  ${rowEl.textContent}`;
      return rowEl.classList.add('add');
    }
    const end = el('span', 'row-state', rowEl);
    const icon = stateIcon(end, 'inline');
    const text = el('span', '', end);
    el('div', 'check', rowEl);
    rowEl.classList.add('with-state');
    parts.set(n, { tile: rowEl, icon, text });
    decorate(n);
  },
};

// A game's marks on the page: the icon and word of its state, its transfer, whether it
// is marked or waiting, and in the search whether it is in the collection
function decorate(n) {
  const p = parts.get(n);
  const g = rowAt(n);
  if (!p || !g || g === ADD) return;
  const job = jobs.current && jobs.current.id === g.id ? jobs.current : null;
  const isWaiting = !job && waiting.has(g.id);
  const member = state.view === 'search' && Boolean(g.member);
  const s = stateOf(g);
  p.tile.classList.remove('status-local', 'status-archived', 'status-changed', 'status-details', 'status-remote');
  p.tile.classList.add(`status-${g.status}`);
  p.tile.classList.toggle('marked', state.marked.has(g.id));
  p.tile.classList.toggle('member', member);
  p.tile.classList.toggle('waiting', isWaiting);
  p.tile.classList.toggle('busy', Boolean(job));
  if (p.icon) p.icon.dataset.state = s;
  if (p.line) p.line(job ? job.fraction || 0 : 0);
  if (p.text) p.text.textContent = job ? `${T.steps[job.step] || T.working[job.action]} ${percent(job.fraction || 0)}`
    : isWaiting ? T.queued : `${member ? T.collections.inCollection : T.status[s]}  ·  ${bytes(sizeOf(g))}`;
}

const decorateAll = () => { for (const n of parts.keys()) decorate(n); };

// Whether there is no archive to show: none chosen yet, or it can't be reached
const blocked = () => !library.location || !library.location.ok || !library.location.archive;

// The page as it is now: the platforms or the games of the open one, or the collections,
// one of them or its search; keepId keeps a game chosen when the games are drawn again
function renderLibrary(keepId) {
  // Without an archive the page only says why, and how to choose one, in the Catalogue;
  // while the library is read, that it is
  platforms = blocked() || !libraryReady() ? [] : platformData();
  if (blocked()) {
    enterSection('catalogue');
    state.view = 'platforms';
  }
  if ((state.view === 'collection' || state.view === 'search') && !currentCollection()) state.view = 'collections';
  G.tabs.hidden = blocked() || state.view === 'collections';
  if (state.view === 'games') renderGames(keepId);
  else if (state.view === 'collections') renderCollections();
  else if (state.view === 'collection') renderCollection(keepId);
  else if (state.view === 'search') renderSearch(keepId);
  else renderPlatforms();
  if (blocked()) showProblem();
  renderTabs();
  renderOverview();
  showCorner();
  renderHelp();
}

// Rows one under the other, each with a drawing before its name and a word at its end,
// as the platforms and the collections are listed: items are [{ art(e), name, note, cls }]
function drawRows(items) {
  list.key = null;
  list.source = null;
  parts.clear();
  G.tiles = new Map();
  G.shelves.hidden = true;
  G.listBox.hidden = false;
  G.listTrack.replaceChildren();
  G.listTrack.style.transform = 'none';
  const H = innerHeight;
  const L2 = L.games.gamelistTextlist;
  const rowHeight = S.fontSizes.listSize * S.fontSizes.listSpacing * H * 1.3;
  G.selector = el('div', 'selector', G.listTrack);
  G.selector.style.width = vw(L2.selectorWidth);
  G.selector.style.height = vh(L2.selectorHeight);
  // Nothing to choose when a filter leaves no row
  G.selector.hidden = !items.length;
  G.rowEls = items.map((item, n) => {
    const row = el('div', `list-row platform-row with-state${item.cls ? ` ${item.cls}` : ''}`, G.listTrack);
    row.style.top = `${n * rowHeight}px`;
    row.style.height = `${rowHeight}px`;
    item.art(el('div', 'art', row));
    el('span', 'name', row, item.name);
    el('span', 'row-state', row, item.note);
    return row;
  });
  G.rowHeight = rowHeight;
}

// The selected row stays in the middle of the list
function highlightRow(n) {
  G.rowEls.forEach((row, i) => row.classList.toggle('selected', i === n));
  const top = n * G.rowHeight;
  G.selector.style.top = `${top + (G.rowHeight - G.selector.offsetHeight) / 2}px`;
  const height = G.listBox.clientHeight;
  const scroll = clamp(top + G.rowHeight / 2 - height / 2, 0, Math.max(0, G.rowEls.length * G.rowHeight - height));
  G.listTrack.style.transform = `translateY(${-scroll}px)`;
}

// The platforms, one under the other with their drawing and how many games they have
function renderPlatforms() {
  setHeader(null);
  drawRows(platforms.map(p => ({ art: e => setArt(e, p.system), name: p.title, note: T.games(number(p.count)) })));
  G.info.textContent = blocked() || !libraryReady() ? '' : T.platforms(number(platforms.length));
  if (!blocked()) {
    if (libraryFailed()) message(G.message, T.messages.readFailed, library.summary.error);
    else if (!libraryReady()) message(G.message, T.messages.reading, T.messages.readingText);
    else emptySection(platforms.length);
  }
  state.game = null;
  if (!platforms.length) return clearDetails();
  const at = platforms.findIndex(p => p.system === state.platform);
  selectPlatform(at >= 0 ? at : clamp(state.platformPick, 0, platforms.length - 1));
}

function selectPlatform(n) {
  state.platformPick = n;
  state.platform = platforms[n].system;
  highlightRow(n);
  showPlatform(platforms[n]);
  renderHelp();
}

// Why a section shows no platform or game: nothing in the archive yet, nothing in ES-DE,
// or everything archived
function emptySection(count) {
  const M = T.messages.emptySection[state.filter];
  message(G.message, count ? null : M[0], M[1]);
}

// The message of an empty list: of a platform's section, a collection or a search
function emptyList() {
  if (state.view === 'games') return emptySection(0);
  if (state.view === 'search') return message(G.message, T.search.none, T.search.noneText);
  message(G.message, null);
}

// The chosen platform: its drawing, how its games stand in the section, and how many of
// them each section has
function showPlatform(p) {
  const s = p.s;
  const [installed, sync, upload, download, unavailable] = s.counts;
  const inArchive = installed + sync + download + unavailable;
  const inEsde = installed + sync + upload;
  const line = sectionLine(aggregate([s]));
  const P = T.platform;
  applyColors(p.system);
  clearTimeout(backdropTimer);
  document.getElementById('atmosphere').classList.remove('shown');
  G.media(null, p.system);
  G.title.textContent = p.title;
  G.caption.textContent = T.games(number(p.count));
  G.tag([inEsde && T.tags.local, inArchive && T.tags.archive]);
  G.status(line.label, null, line.text, false, line.fraction);
  // No room beside these for the media: the line of the section has them
  G.facts[0](P.archive, inArchive ? `${number(inArchive)}  ·  ${bytes(s.packed)}` : null);
  G.facts[1](P.local, inEsde ? `${number(inEsde)}  ·  ${bytes(s.size)}` : null);
  G.facts[2](P.unarchived, upload ? `${number(upload)}  ·  ${bytes(s.upload)}` : null);
  G.facts[3](P.toInstall, download ? number(download) : null);
  G.next.replaceChildren();
}

// The games of the open platform in the section: a grid of covers, or a list
function renderGames(keepId) {
  const s = systemOf(state.platform);
  const title = s ? s.name : state.platform;
  setHeader(state.platform, title);
  // While the server reads the library, there is no list to show yet
  if (!libraryReady()) {
    drawRows([]);
    if (libraryFailed()) message(G.message, T.messages.readFailed, library.summary.error);
    else message(G.message, T.messages.reading, T.messages.readingText);
    return clearDetails();
  }
  const system = state.platform;
  const section = state.filter;
  const sort = state.sort;
  setList(`games|${system}|${section}|${sort}`, (from, count, at) => api(`/api/games?system=${encodeURIComponent(system)}&section=${section}`
    + `&sort=${sort}&from=${from}&count=${count}${at ? `&at=${encodeURIComponent(at)}` : ''}`), 0, s ? sectionCount(s, section) : 0,
  { key: system, system, title });
  list.aspect = systemInfo(system).cover || 0.75;
  showList(keepId);
}

// The list open drawn, and a game chosen in it: the one kept, else the one chosen there last
function showList(keepId) {
  drawList();
  G.info.textContent = listInfo();
  const count = viewCount();
  if (!count) {
    state.game = null;
    if (list.known) emptyList();
    else {
      message(G.message, null);
      // Empty as far as the page knows: the server tells
      want(0, PAGE);
    }
    clearDetails();
    if (state.view === 'games') applyColors(state.platform);
    return;
  }
  message(G.message, null);
  if (keepId) {
    state.game = clamp(state.picks[pickKey()] || 0, 0, count - 1);
    selectGame(state.game);
    locate(keepId);
  } else selectGame(clamp(state.picks[pickKey()] || 0, 0, count - 1));
}

// The line beneath the list: how many games, and in what order but for a search's
const listInfo = () => (state.view === 'search' ? T.games(number(list.total)) : `${T.games(number(list.total))}  ·  ${T.sorts[state.sort]}`);

// Where each view keeps the game chosen in it
const pickKey = () => (state.view === 'games' ? `${state.platform}|${state.filter}` : `${state.view}:${state.collection}`);

// The games of the list open: as a grid of covers of the list's shape, or as a list
// under its heading, only those in view drawn (see renderWindow)
function drawList() {
  parts.clear();
  G.tiles = new Map();
  G.shelves.hidden = state.mode !== 'grid';
  G.listBox.hidden = state.mode === 'grid';
  if (state.mode === 'grid') layoutGrid(list.aspect || 0.75);
  else layoutRows(state.mode === 'covers');
}

// Covers in rows across the content, as many as fit, the rows scrolling up and down
function layoutGrid(aspect) {
  G.track.replaceChildren();
  G.track.style.transform = 'none';
  G.scroll = 0;
  const H = innerHeight;
  const pad = SHELF_PAD * innerWidth;
  const coverHeight = SHELF.cover * H;
  const width = coverHeight * clamp(aspect || 0.75, 0.6, 1.5);
  const gap = coverHeight * 0.16;
  const columns = Math.max(1, Math.floor((G.shelves.clientWidth - 2 * pad + gap) / (width + gap)));
  const rowHeight = (SHELF.cover + SHELF.bar + 0.035) * H;
  // Room above the first row for the light around a selected cover
  const top = 0.025 * H;
  const holder = el('div', 'shelf active cover-grid', G.track);
  holder.style.height = `${top + Math.ceil(viewCount() / columns) * rowHeight}px`;
  G.grid = { holder, columns, rowHeight, top, width, gap, coverHeight, pad };
}

// Names one under the other under the list's heading; with covers, each name has the
// game's whole box cover beside it and a line of its own beneath
function layoutRows(covers) {
  G.listTrack.replaceChildren();
  G.listTrack.style.transform = 'none';
  G.listScroll = 0;
  const H = innerHeight;
  const rowHeight = covers ? 0.09 * H : S.fontSizes.listSize * S.fontSizes.listSpacing * H;
  const headHeight = 0.055 * H;
  const L2 = L.games.gamelistTextlist;
  G.selector = el('div', 'selector', G.listTrack);
  G.selector.style.width = vw(L2.selectorWidth);
  G.selector.style.height = covers ? `${rowHeight * 0.5}px` : vh(L2.selectorHeight);
  G.selector.hidden = !viewCount();
  const group = list.group || { key: '', title: '' };
  G.label = null;
  if (viewCount()) {
    G.label = groupLabel(G.listTrack, { ...group, games: [], count: list.total });
    G.label.style.top = '0px';
    G.label.style.height = `${headHeight * 0.6}px`;
    G.label.style.left = vw(L2.horizontalMargin);
    G.label.classList.add('active');
  }
  G.rows2 = { covers, rowHeight, headHeight, selectorOffset: covers ? rowHeight * 0.25 : L2.selectorVerticalOffset * H, height: headHeight + viewCount() * rowHeight + 0.015 * H };
}

// The covers or rows in view, and a few around them: those that left the view leave the
// page, those that came are drawn, and the pages they need are asked for
function renderWindow() {
  const count = viewCount();
  let first;
  let last;
  if (state.mode === 'grid') {
    const { columns, rowHeight, top } = G.grid;
    const height = G.shelves.clientHeight;
    const firstRow = Math.max(0, Math.floor((G.scroll - top) / rowHeight) - 2);
    const lastRow = Math.floor((G.scroll + height) / rowHeight) + 2;
    first = firstRow * columns;
    last = Math.min(count, (lastRow + 1) * columns);
  } else {
    const { rowHeight, headHeight } = G.rows2;
    const height = G.listBox.clientHeight;
    first = Math.max(0, Math.floor((G.listScroll - headHeight) / rowHeight) - 8);
    last = Math.min(count, Math.ceil((G.listScroll + height - headHeight) / rowHeight) + 8);
  }
  for (const [n, tile] of G.tiles) {
    if (n >= first && n < last) continue;
    tile.remove();
    G.tiles.delete(n);
    parts.delete(n);
  }
  for (let n = first; n < last; n++) if (!G.tiles.has(n)) G.tiles.set(n, makeTile(n));
  want(first, last);
  markSelected();
}

function makeTile(n) {
  const row = rowAt(n);
  if (state.mode === 'grid') {
    const { holder, columns, rowHeight, top, width, gap, coverHeight, pad } = G.grid;
    const tile = el('div', 'cover', holder);
    tile.style.left = `${pad + (n % columns) * (width + gap)}px`;
    tile.style.top = `${top + Math.floor(n / columns) * rowHeight}px`;
    tile.style.width = `${width}px`;
    tile.style.height = `${coverHeight}px`;
    el('div', 'glow', tile);
    drawGame.fill(el('div', 'pic', tile), row);
    if (row) drawGame.beneath(tile, n, coverHeight);
    return tile;
  }
  const { covers, rowHeight, headHeight } = G.rows2;
  const rowEl = el('div', `list-row${covers ? ' with-cover' : ''}`, G.listTrack);
  rowEl.style.top = `${headHeight + n * rowHeight}px`;
  rowEl.style.height = `${rowHeight}px`;
  if (covers) {
    const pic = el('div', 'pic', rowEl);
    pic.dataset.icon = '1';
    drawGame.fill(pic, row);
    const text = el('div', 'text', rowEl);
    el('div', 'name', text, drawGame.name(row));
    if (row) drawGame.meta(el('div', 'meta', text), n);
  } else {
    rowEl.style.lineHeight = `${rowHeight}px`;
    rowEl.textContent = drawGame.name(row);
    if (row) drawGame.decorate(rowEl, n);
  }
  return rowEl;
}

// A tile drawn again, its game having come or changed
function refreshTile(n) {
  const tile = G.tiles.get(n);
  if (!tile) return;
  tile.remove();
  parts.delete(n);
  G.tiles.set(n, makeTile(n));
}

function markSelected() {
  for (const [n, tile] of G.tiles) tile.classList.toggle('selected', n === state.game);
}

function selectGame(n) {
  const count = viewCount();
  if (!count) return;
  n = clamp(n, 0, count - 1);
  state.picks[pickKey()] = n;
  state.game = n;
  if (state.mode === 'grid') {
    const { columns, rowHeight, top } = G.grid;
    // Scrolls only as far as needed to keep the selected row in view
    const rowTop = Math.floor(n / columns) * rowHeight;
    G.scroll = clamp(G.scroll || 0, rowTop + top + rowHeight - G.shelves.clientHeight, rowTop);
    G.track.style.transform = `translateY(${-G.scroll}px)`;
  } else {
    // The selected row stays in the middle of the list, as in the theme's game lists
    const { rowHeight, headHeight, selectorOffset, height } = G.rows2;
    const top = headHeight + n * rowHeight;
    G.selector.style.top = `${top + selectorOffset}px`;
    const box = G.listBox.clientHeight;
    G.listScroll = clamp(top + rowHeight / 2 - box / 2, 0, Math.max(0, height - box));
    G.listTrack.style.transform = `translateY(${-G.listScroll}px)`;
  }
  renderWindow();
  showGame();
  // In a collection A adds games on the first tile, in the search it puts the game in
  // the collection or takes it out
  if (state.view === 'collection' || state.view === 'search') renderHelp();
}

// Where a direction leads in the list open, or null
function moveGame(n, action) {
  const count = viewCount();
  if (state.mode !== 'grid') {
    const to = { up: n - 1, down: n + 1, left: 0, lt: 0, rt: count - 1 }[action];
    return to === undefined || to < 0 || to >= count || to === n ? null : to;
  }
  const { columns } = G.grid;
  const row = Math.floor(n / columns);
  const lastRow = Math.floor((count - 1) / columns);
  const to = {
    left: n % columns ? n - 1 : null, right: (n + 1) % columns && n + 1 < count ? n + 1 : null,
    up: row > 0 ? n - columns : null,
    // Down to the last cover when the row below is shorter
    down: row < lastRow ? Math.min(n + columns, count - 1) : null,
    lt: 0, rt: count - 1,
  }[action];
  return to === undefined || to === null || to === n ? null : to;
}

// What keeps the archive from showing, in place of the shelves of an empty library
function showProblem() {
  const loc = library.location;
  const M = T.messages;
  if (!loc) return message(G.message, M.none, M.noneText);
  if (loc.reason === 'credentials') return message(G.message, M.credentials, M.credentialsText(loc.share));
  if (loc.reason === 'missing') return message(G.message, M.missing, M.missingText(loc.path));
  if (!loc.ok) return message(G.message, M.unreachable, M.unreachableText(loc.path));
  if (!loc.archive) return message(G.message, M.notArchive, M.notArchiveText(loc.path));
}

// The details beside the content: of the chosen game, platform or collection
function refreshDetails() {
  if (['games', 'collection', 'search'].includes(state.view)) return showGame();
  if (state.view === 'collections') return showCollectionRow();
  if (platforms[state.platformPick]) showPlatform(platforms[state.platformPick]);
}

function clearDetails() {
  G.title.textContent = '';
  G.caption.textContent = '';
  G.tag([]);
  G.status('', null, '');
  G.media(null, 'catalogue');
  G.facts.forEach(f => f('', ''));
  G.next.replaceChildren();
  applyColors('');
}

// A game's details as the server gives them, asked for once it stays chosen a moment:
// moving fast across covers asks only for the one the selection stops at
let detailsTimer = null;
function askDetails(id) {
  if (details.has(id)) return;
  clearTimeout(detailsTimer);
  detailsTimer = setTimeout(async () => {
    let d = null;
    try { d = await api(`/api/game?id=${encodeURIComponent(id)}`); } catch { d = null; }
    if (!d || !d.id) return;
    details.set(id, d);
    if (selectedId() === id) showGame();
  }, details.size ? 90 : 0);
}

let backdropTimer = null;
function showGame() {
  const row = rowAt(state.game);
  if (row === ADD) return showAddTile();
  if (!row) {
    G.title.textContent = '';
    G.caption.textContent = '';
    G.tag([]);
    G.status('', null, '');
    G.facts.forEach(f => f('', ''));
    G.next.replaceChildren();
    return;
  }
  const d = details.get(row.id);
  if (!d) askDetails(row.id);
  applyColors(row.system);
  G.media(d ? d.image : null, row.system, d ? d.video : null);
  G.title.textContent = row.name;
  G.caption.textContent = row.systemName;
  const member = state.view === 'search' && row.member;
  G.tag([row.local && T.tags.local, row.archive && T.tags.archive, member && T.collections.inCollection,
    state.marked.has(row.id) && T.tags.marked]);
  showState(row);
  // The media of ES-DE when it has the game, as for its size, else the archive's
  const media = row.local ? row.local.media : row.archive.media;
  const facts = {
    size: [T.facts.size, row.local && row.archive ? `${bytes(row.local.size)}  ·  ${T.packed(bytes(row.archive.packed))}`
      : row.archive ? T.packed(bytes(row.archive.packed)) : bytes(row.local.size)],
    media: [T.facts.media, media ? bytes(media) : null],
    archived: [T.facts.archived, row.archive ? [date(row.archive.archived), d && d.archive && d.archive.device].filter(Boolean).join('  ·  ') : null],
    saves: [T.facts.saves, d && d.saves ? T.savesState[d.saves.status] : null],
  };
  G.gameFacts.forEach((k, i) => G.facts[i](...facts[k]));

  const atmosphere = document.getElementById('atmosphere');
  atmosphere.classList.remove('shown');
  clearTimeout(backdropTimer);
  if (S.colors.atmosphereVisible === 'true' && d && d.backdrop) {
    backdropTimer = setTimeout(() => {
      atmosphere.onload = () => atmosphere.classList.add('shown');
      if (atmosphere.getAttribute('src') === d.backdrop) atmosphere.classList.add('shown');
      else atmosphere.src = d.backdrop;
    }, 250);
  }
}

// How the chosen game stands, with the icon of its state, or how its transfer goes, and
// beneath the facts its maker and kind, or what went wrong with it
function showState(g) {
  const job = jobs.current && jobs.current.id === g.id ? jobs.current : null;
  const isWaiting = !job && waiting.has(g.id);
  const failure = failures.get(g.id);
  if (job) G.status(T.working[job.action], job.fraction || 0, T.steps[job.step] || '');
  else {
    // The bar is full for a game installed and archived as it is; what needs doing, to
    // archive or to sync, is said in the accent
    const s = stateOf(g);
    const line = isWaiting ? T.queued : T.statusLine[s === 'sync' ? g.status : s];
    G.status(T.status[s], null, line, s === 'sync' || s === 'upload', s === 'installed' ? 1 : 0);
    const icon = stateIcon(null, 'inline');
    icon.dataset.state = s;
    G.status.label.prepend(icon);
  }
  G.next.replaceChildren();
  const d = details.get(g.id);
  if (failure) {
    el('span', 'caps', G.next, T.confirm.failed(1));
    el('span', '', G.next, failure);
  } else if (d) {
    const f = d.fields;
    const words = [f.developer, f.released, f.genre, f.players && `${S.labels.langPlayers || T.facts.players} ${f.players}`];
    el('span', '', G.next, words.filter(Boolean).join('  ·  '));
  }
}

// Reading the library

function applyAnswer(r, withLocation) {
  if (!r) return;
  if (withLocation) library.location = r.location;
  const before = libraryVersion();
  library.summary = r.library;
  library.collections = (r.collections && r.collections.collections) || [];
  library.union = { games: r.collections ? r.collections.games : 0, local: r.collections ? r.collections.local : 0 };
  if (libraryVersion() !== before) details.clear();
}

async function loadLibrary(keepId) {
  if (!libraryReady()) message(G.message, T.loading);
  let r = null;
  try {
    r = await api('/api/library');
  } catch {
    r = null;
  }
  if (!r) r = { location: null, library: null, collections: null };
  applyAnswer(r, true);
  resetCollections();
  renderLibrary(keepId);
  if (!blocked() && library.summary && (!library.summary.ready || library.summary.building)) watchBuild();
  else if (libraryReady()) syncMatches();
}

// While the server reads the library, the page asks how far it is, then shows it
let buildTimer = null;
function watchBuild() {
  clearTimeout(buildTimer);
  buildTimer = setTimeout(async () => {
    let r = null;
    try { r = await api('/api/summary'); } catch { r = null; }
    if (!r) return watchBuild();
    applyAnswer(r, false);
    if (libraryReady()) {
      renderLibrary(selectedId());
      return syncMatches();
    }
    // It couldn't be read: the page says why
    if (libraryFailed()) return renderLibrary(selectedId());
    renderOverview();
    watchBuild();
  }, 400);
}

// The library changed as transfers went: how the platforms stand, and the games of the
// list open, are asked for again, a moment later so that a quick run of transfers asks once
let refreshTimer = null;
function refreshSoon() {
  if (refreshTimer) return;
  refreshTimer = setTimeout(async () => {
    refreshTimer = null;
    let r = null;
    try { r = await api('/api/summary'); } catch { r = null; }
    if (!r) return;
    applyAnswer(r, false);
    refreshView();
  }, 800);
}

function refreshView() {
  if (state.screen !== 'library' || state.overlay || keyboard.isOpen()) {
    renderOverview();
    return;
  }
  if (['games', 'collection', 'search'].includes(state.view) && list.key) {
    platforms = platformData();
    if (list.version !== libraryVersion()) {
      list.version = libraryVersion();
      list.fresh = new Set();
      renderWindow();
    }
    renderTabs();
    renderOverview();
    return refreshDetails();
  }
  renderLibrary(selectedId());
}

// Transfers

// target: { ids }, or the games of a platform's section { system, section }, or of a
// collection { collection, where }; with where, only the games there
async function enqueue(action, target, force) {
  play('select');
  const result = await api('/api/jobs', { action, ...target, force: Boolean(force), since, watch: watched() });
  // Games acted on are no longer marked
  if (target.ids) {
    for (const id of target.ids) {
      failures.delete(id);
      state.marked.delete(id);
    }
  } else state.marked.clear();
  state.markedAll = null;
  handleJobs(result);
  refreshDetails();
}

// The games on the page, whose waiting the server tells
function watched() {
  const ids = [];
  for (const n of G.tiles.keys()) {
    const row = rowAt(n);
    if (row && row !== ADD) ids.push(row.id);
  }
  return ids;
}

let pollTimer = null;
function handleJobs(result) {
  if (!result) return;
  jobs = result;
  waiting = new Set(result.waiting || []);
  for (const job of result.done) {
    since = Math.max(since, job.finished);
    outcomes.push(job);
    if (job.result === 'failed') failures.set(job.id, job.errorCode === 'space' ? T.errors.space
      : job.errorCode === 'incomplete' ? T.errors.incomplete(job.other.file, job.other.name) : job.error);
  }
  renderOverview();
  decorateAll();
  if (selected()) showState(selected());
  // The warning follows the transfers it tells of
  const o = state.overlay;
  if (o && o.whileBusy && busy() && o.text !== busyText()) {
    o.text = busyText();
    renderOverlay();
  }
  if (result.library && library.summary && result.library !== library.summary.version) refreshSoon();
  clearTimeout(pollTimer);
  if (jobs.current || jobs.queued) pollTimer = setTimeout(pollJobs, 400);
  else if (outcomes.length) settle();
}

async function pollJobs() {
  try {
    handleJobs(await api('/api/jobs/status', { since, watch: watched() }));
  } catch {
    pollTimer = setTimeout(pollJobs, 1500);
  }
}

const busy = () => Boolean(jobs.current || jobs.queued);

// The warning on leaving: what runs now, how many wait and whether some are done already
function busyText() {
  const job = jobs.current || jobs.queue[0];
  return T.confirm.quitBusy(T.doing[job.action](job.name), number(jobs.queued - (jobs.current ? 0 : 1)), jobs.batch.done);
}

// Once the queue is empty: the library as it is now, then what needs the user. What the
// user has open stays, but for the warning of transfers running: none are left
async function settle() {
  const finished = outcomes.splice(0);
  if (state.overlay && state.overlay.whileBusy) closeOverlay();
  await loadLibrary(selectedId());
  showNotice(finished.filter(j => j.result === 'done'));
  const failed = finished.filter(j => j.result === 'failed');
  const conflicts = finished.filter(j => j.result === 'conflict');
  if (failed.length) {
    openOverlay({
      title: T.confirm.failed(failed.length),
      text: failed.slice(0, 4).map(j => `${j.name}: ${failures.get(j.id) || j.error}`).join('\n'),
      rows: [{ label: T.confirm.ok, run: () => nextConflict(conflicts) }],
      back: () => nextConflict(conflicts),
    });
  } else if (conflicts.length) nextConflict(conflicts);
}

// Saves that changed on both sides, one game at a time
function nextConflict(conflicts) {
  closeOverlay();
  const job = conflicts.shift();
  if (!job) return;
  const other = job.other || {};
  const when = date(other.saved, true) || '';
  openOverlay({
    title: T.confirm.conflictTitle,
    text: `${job.name}\n${job.action === 'upload-saves' ? T.confirm.conflictUpload(other.device || '?', when) : T.confirm.conflictDownload(other.device || '?', when)}`,
    rows: [
      { label: T.confirm.keepHere, run: () => { enqueue('upload-saves', { ids: [job.id] }, true); nextConflict(conflicts); } },
      { label: T.confirm.keepArchive, run: () => { enqueue('download-saves', { ids: [job.id] }, true); nextConflict(conflicts); } },
      { label: T.confirm.later, separated: true, run: () => nextConflict(conflicts) },
    ],
    back: () => nextConflict(conflicts),
  });
}

// Actions

// How many games and how much a transfer takes: their size, then their media's
function note(count, size, media) {
  const text = withMedia(bytes(size), media);
  return count > 1 ? `${number(count)}  ·  ${text}` : text;
}

// The actions for a few games, as the server counts them (/api/actions): those that make
// sense for at least one of them, on target (see enqueue)
function actionRows(a, single, target) {
  const A = T.actions;
  const rows = [];
  const archive = a.archive;
  if (archive.count) {
    rows.push({ label: single ? { local: A.archive, changed: A.archiveAgain }[archive.first] : a.allFresh ? A.archiveNew : A.archiveMany,
      note: note(archive.count, archive.size, mediaSize(archive.media, 'archiveMedia')), run: () => { closeOverlay(); enqueue('archive', target); } });
  }
  const detailsCount = a['update-details'].count;
  if (detailsCount) {
    rows.push({ label: A.archiveDetails, note: single ? '' : number(detailsCount),
      run: () => { closeOverlay(); enqueue('update-details', target); } });
  }
  const download = a.download;
  if (download.count) {
    rows.push({ label: single ? { remote: A.download, changed: A.downloadAgain }[download.first] : A.downloadMany,
      note: note(download.count, download.size, mediaSize(download.media, 'downloadMedia')), run: () => { closeOverlay(); enqueue('download', target); } });
  }
  if (a['upload-saves'].count) {
    rows.push({ label: A.uploadSaves, note: single ? '' : number(a['upload-saves'].count), run: () => saveTransfer('upload-saves', a) });
  }
  if (a['download-saves'].count) {
    rows.push({ label: A.downloadSaves, note: single ? '' : number(a['download-saves'].count), run: () => saveTransfer('download-saves', a) });
  }
  if (a['remove-local'].count) rows.push({ label: A.removeLocal, separated: true, run: () => confirmRemoveLocal(a, target) });
  if (a['remove-archive'].count) {
    rows.push({ label: A.removeArchive, separated: !a['remove-local'].count, run: () => confirmRemoveArchive(a, target) });
  }
  return rows;
}

// Saves changed on both sides are only replaced once the user says so
function saveTransfer(action, a) {
  closeOverlay();
  const conflicts = (a.conflicts || []).filter(c => a[action].ids.includes(c.id));
  const conflictIds = new Set(conflicts.map(c => c.id));
  const plain = a[action].ids.filter(id => !conflictIds.has(id));
  if (plain.length) enqueue(action, { ids: plain });
  const c = conflicts[0];
  if (!c) return;
  const other = c.archive || {};
  const when = date(other.saved, true) || '';
  openOverlay({
    title: T.confirm.conflictTitle,
    text: `${conflicts.length > 1 ? T.games(number(conflicts.length)) : a.name || ''}\n${action === 'upload-saves'
      ? T.confirm.conflictUpload(other.device || '?', when) : T.confirm.conflictDownload(other.device || '?', when)}`,
    rows: [
      { label: T.confirm.replace, run: () => { closeOverlay(); enqueue(action, { ids: [...conflictIds] }, true); } },
      { label: T.confirm.cancel, separated: true, run: closeOverlay },
    ],
  });
}

function confirmRemoveLocal(a, target) {
  const C = T.confirm;
  const count = a['remove-local'].count;
  const rows = [];
  if (a.unsafe) {
    rows.push({ label: C.archiveFirst, run: async () => {
      closeOverlay();
      await enqueue('archive', target);
      enqueue('remove-local', target);
    } });
  }
  rows.push({ label: T.actions.removeLocal, run: () => { closeOverlay(); enqueue('remove-local', target); } });
  if (a.inArchive) {
    rows.push({ label: C.removeBoth, run: async () => {
      closeOverlay();
      await enqueue('remove-local', target);
      enqueue('remove-archive', target);
    } });
  }
  rows.push({ label: C.cancel, separated: true, run: closeOverlay });
  openOverlay({
    title: C.removeLocal(count),
    text: [C.removeLocalText, a.unsafe ? C.removeLocalUnsafe(a.unsafe) : ''].filter(Boolean).join('\n'),
    rows,
    selected: rows.length - 1,
  });
}

function confirmRemoveArchive(a, target) {
  const C = T.confirm;
  const count = a['remove-archive'].count;
  // A collection keeps naming its games, wherever they are
  openOverlay({
    title: C.removeArchive(count),
    text: [C.removeArchiveText, C.removeArchiveOnly(a.onlyArchive), a.kept ? C.removeArchiveKept(count === 1 ? 1 : a.kept) : ''].filter(Boolean).join('\n'),
    rows: [
      { label: C.remove, run: () => { closeOverlay(); enqueue('remove-archive', target); } },
      { label: C.cancel, separated: true, run: closeOverlay },
    ],
    selected: 1,
  });
}

// The games marked, as a target for the server: a whole platform's section when all of
// it was marked at once, else their ids
function markedTarget() {
  if (state.markedAll && state.marked.size === state.markedAll.count) return state.markedAll.target;
  return { ids: [...state.marked] };
}

// The actions for the chosen game, or the marked ones
async function openActions() {
  const g = selected();
  const target = state.marked.size ? markedTarget() : g ? { ids: [g.id] } : null;
  if (!target) return;
  const count = state.marked.size || 1;
  let a = null;
  try { a = await api('/api/actions', target); } catch { a = null; }
  if (!a) return;
  const rows = actionRows(a, count === 1, target);
  if (state.marked.size) rows.push({ label: T.actions.unmarkAll, separated: true, run: () => { closeOverlay(); setMarks([], false); } });
  openOverlay({
    title: count > 1 ? T.actions.titleMarked(number(count)) : a.name || (g && g.name) || '',
    text: rows.length ? '' : T.actions.none,
    rows: rows.length ? rows : [{ label: T.confirm.ok, run: closeOverlay }],
  });
}

// The actions for every game of the chosen or open platform in the section
async function openPlatformActions() {
  const platform = platforms.find(p => p.system === state.platform);
  if (!platform) return;
  const target = { system: platform.system, section: state.filter };
  let a = null;
  try { a = await api('/api/actions', target); } catch { a = null; }
  if (!a) return;
  const rows = actionRows(a, false, target);
  const key = `${platform.system}|${state.filter}`;
  const allMarked = Boolean(state.markedAll && state.markedAll.key === key && state.marked.size === state.markedAll.count);
  rows.push({ label: allMarked ? T.actions.unmarkAll : T.actions.markAll, separated: true, run: async () => {
    closeOverlay();
    if (allMarked) return setMarks([], false);
    const ids = await api('/api/ids', { system: platform.system, section: state.filter, sort: 'name' });
    setMarks(ids, true);
    state.markedAll = { key, target, count: state.marked.size };
  } });
  openOverlay({ title: T.actions.titleSystem(platform.title), text: T.games(number(platform.count)), rows });
}

// Marks or unmarks games by id; none given and off, every mark goes
function setMarks(ids, on) {
  if (!ids.length && !on) state.marked.clear();
  for (const id of ids) {
    if (on) state.marked.add(id);
    else state.marked.delete(id);
  }
  if (!on) state.markedAll = null;
  decorateAll();
  refreshDetails();
}

// Overlays: the options, actions and dialogs, on the shared menu card. An overlay is
// { title, text, rows: [{ label, value, note, separated, disabled, run, change }],
// selected, back, build, opener, cls, across, whileBusy } where build makes its rows again
// after a change, opener is the button that opened it, which closes it too, cls a look of
// its own, across that its rows stand side by side, chosen with left and right, and
// whileBusy that it goes once the transfers end.

function openOverlay(overlay) {
  const rows = overlay.build ? overlay.build() : overlay.rows;
  const first = rows.findIndex(r => !r.disabled);
  state.overlay = { ...overlay, rows, selected: overlay.selected ?? Math.max(0, first) };
  renderOverlay();
}

function closeOverlay() {
  state.overlay = null;
  renderOverlay();
}

function renderOverlay() {
  renderHelp();
  const o = state.overlay;
  if (!o) return G.menu.hide();
  if (o.build) o.rows = o.build();
  o.selected = clamp(o.selected, 0, o.rows.length - 1);
  G.menu.show(o.title, o.rows, o.selected, o.text, o.cls);
  // The selected row scrolls when its label and note don't fit, as the selected game
  // of a list does in ES-DE
  const label = G.menu.root.querySelector('.menu-row.selected .menu-label');
  if (label) marquee(label);
}

function actOverlay(action) {
  const o = state.overlay;
  const row = o.rows[o.selected];
  if (action === 'b' || action === o.opener) {
    play('back');
    return o.back ? o.back() : closeOverlay();
  }
  const [back, on] = o.across ? ['left', 'right'] : ['up', 'down'];
  if (action === back || action === on) {
    const step = action === back ? -1 : 1;
    let n = o.selected;
    do n = (n + step + o.rows.length) % o.rows.length; while (o.rows[n].disabled && n !== o.selected);
    if (n === o.selected) return;
    play('scroll');
    o.selected = n;
    return renderOverlay();
  }
  if (!row || row.disabled) return;
  if ((action === 'left' || action === 'right') && row.change) {
    play('select');
    row.change(action === 'left' ? -1 : 1);
    return renderOverlay();
  }
  if (action === 'a') {
    play('select');
    if (row.run) row.run();
    else if (row.change) {
      row.change(1);
      renderOverlay();
    }
  }
}

// The options menu, opened with Start as in ES-DE; the media to send and to bring
// belong to the Catalogue
function openMenu() {
  const M = T.menu;
  openOverlay({
    title: M.title,
    opener: 'start',
    build() {
      const C = S.config;
      const catalogue = state.section === 'catalogue';
      const rows = [];
      if (C.locations.length) {
        rows.push({ label: M.archive, value: shortPath((C.locations[C.current] || {}).path), arrows: C.locations.length > 1,
          change: C.locations.length > 1 ? step => switchLocation((C.current + step + C.locations.length) % C.locations.length) : null });
      }
      rows.push({ label: M.choose, run: () => { closeOverlay(); openBrowse(null); } });
      if (catalogue) {
        rows.push({ label: M.archiveMedia, separated: true, run: () => openMedia('archive') });
        rows.push({ label: M.downloadMedia, run: () => openMedia('download') });
      }
      rows.push({ label: M.view, value: T.modes[state.mode], separated: true, change: step => {
        state.mode = cycle(MODES, state.mode, step);
        saveSettings({ mode: state.mode });
        renderLibrary(selectedId());
      } });
      rows.push({ label: M.sort, value: T.sorts[state.sort], change: step => {
        state.sort = cycle(SORTS, state.sort, step);
        saveSettings({ sort: state.sort });
        renderLibrary(selectedId());
      } });
      if (busy()) rows.push({ label: M.stop, separated: true, run: () => { closeOverlay(); api('/api/stop', {}).then(pollJobs); } });
      rows.push({ label: M.exit, separated: !busy(), run: requestQuit });
      return rows;
    },
  });
}

function saveSettings(change) {
  api('/api/settings', change).then(r => { if (r.config) S.config = r.config; }).catch(() => {});
}

async function switchLocation(index) {
  const r = await api('/api/settings', { current: index });
  S.config = r.config;
  await loadLibrary();
  if (state.overlay) renderOverlay();
}

// The media types to send to the archive or bring from it, with their sizes
async function openMedia(direction) {
  const key = direction === 'archive' ? 'archiveMedia' : 'downloadMedia';
  const chosen = new Set(S.config[key]);
  let sizes = null;
  const changed = () => S.config[key].length !== chosen.size || S.config[key].some(t => !chosen.has(t));
  const leave = () => {
    if (changed()) {
      const types = S.mediaTypes.filter(t => chosen.has(t));
      api('/api/settings', { [key]: types }).then(r => {
        S.config = r.config;
        if (key === 'archiveMedia') loadLibrary(selectedId());
      });
    }
    openMenu();
  };
  openOverlay({
    title: direction === 'archive' ? T.media.archiveTitle : T.media.downloadTitle,
    text: T.media.text,
    build: () => S.mediaTypes.map(type => ({
      label: T.media.types[type], value: chosen.has(type) ? T.media.yes : T.media.no,
      note: sizes && sizes[direction][type] ? bytes(sizes[direction][type]) : '',
      change: () => {
        if (chosen.has(type)) chosen.delete(type);
        else chosen.add(type);
      },
    })),
    back: leave,
  });
  sizes = await api('/api/media-sizes').catch(() => null);
  if (state.overlay && state.overlay.title.startsWith(direction === 'archive' ? T.media.archiveTitle : T.media.downloadTitle)) renderOverlay();
}

// Going back to ES-DE: transfers stop first, and downloaded or removed games and the
// collections show after ES-DE restarts
function requestQuit() {
  const C = T.confirm;
  if (busy()) {
    return openOverlay({
      title: T.menu.exit, text: busyText(),
      rows: [
        { label: C.stopAndQuit, run: async () => {
          closeOverlay();
          await api('/api/stop', {});
          for (let i = 0; i < 100; i++) {
            const j = await api(`/api/jobs?since=${since}`);
            if (!j.current) break;
            await new Promise(r => setTimeout(r, 200));
          }
          jobs = await api(`/api/jobs?since=${since}`);
          requestQuit();
        } },
        { label: C.stay, separated: true, run: closeOverlay },
      ],
      selected: 1,
      whileBusy: true,
    });
  }
  // Started from ES-DE, the app restarts it on leaving; otherwise it says how to see them
  if ((jobs.esdeChanged || jobs.collectionsChanged) && !jobs.restartsEsde) {
    return openOverlay({
      title: T.menu.exit, text: C.restart,
      rows: [{ label: C.backToEsde, run: quitApp }, { label: C.stay, separated: true, run: closeOverlay }],
    });
  }
  quitApp();
}

// The folder chooser

function show(screen) {
  state.screen = screen;
  document.getElementById('library').hidden = screen !== 'library';
  document.getElementById('browse').hidden = screen !== 'browse';
  renderHelp();
}

// The drives and the archives known, or the folders in a folder
async function openBrowse(dir) {
  show('browse');
  clearTimeout(backdropTimer);
  document.getElementById('atmosphere').classList.remove('shown');
  applyColors('');
  B.info = null;
  B.rows = [];
  message(B.message, T.loading);
  drawBrowse();
  if (!dir) {
    const { drives, locations } = await api('/api/drives');
    const typeName = { local: T.browse.local, removable: T.browse.removable, network: T.browse.networkDrive };
    B.rows = [
      ...locations.map(l => ({ kind: 'location', label: shortPath(l.path), path: l.path, note: T.browse.saved })),
      ...drives.map(d => ({ kind: 'folder', label: d.label ? `${d.label} (${d.letter})` : d.letter, path: d.path,
        note: typeName[d.type] || '', free: d.free, type: d.type })),
      { kind: 'network', label: T.browse.network },
    ];
    B.info = { path: null };
  } else {
    const info = await api(`/api/folders?path=${encodeURIComponent(dir)}`);
    if (info.error) return folderProblem(dir, info);
    B.info = info;
    B.rows = [
      { kind: 'use', label: T.browse.use },
      { kind: 'up', label: info.parent ? `‹  ${T.browse.up}` : `‹  ${T.browse.drives}`, path: info.parent },
      ...info.folders.map(f => ({ kind: 'folder', label: f.name, path: f.path })),
    ];
  }
  // In an archive, using it is the likely choice; elsewhere, going on to a folder in it
  B.selected = dir && !B.info.archive && B.rows.length > 2 ? 2 : 0;
  message(B.message, null);
  drawBrowse();
}

function drawBrowse() {
  const H = innerHeight;
  const L2 = L.games.gamelistTextlist;
  const rowHeight = S.fontSizes.listSize * S.fontSizes.listSpacing * H;
  B.track.replaceChildren();
  B.path.textContent = B.info && B.info.path ? B.info.path : T.browse.drives;
  const selector = el('div', 'selector', B.track);
  selector.style.width = vw(L2.selectorWidth);
  selector.style.height = vh(L2.selectorHeight);
  B.rowEls = B.rows.map((row, n) => {
    const e = el('div', `list-row browse-row${row.kind === 'use' ? ' use' : ''}`, B.track);
    e.style.top = `${n * rowHeight}px`;
    e.style.height = e.style.lineHeight = `${rowHeight}px`;
    e.textContent = row.label;
    if (row.note) el('span', 'row-state', e, row.note);
    return e;
  });
  B.rowHeight = rowHeight;
  B.selector = selector;
  selectBrowse(B.selected);
  renderHelp();
}

function selectBrowse(n) {
  if (!B.rows.length) return;
  B.selected = clamp(n, 0, B.rows.length - 1);
  B.rowEls.forEach((e, i) => e.classList.toggle('selected', i === B.selected));
  const top = B.selected * B.rowHeight;
  B.selector.style.top = `${top + L.games.gamelistTextlist.selectorVerticalOffset * innerHeight}px`;
  const height = B.box.clientHeight;
  const scroll = clamp(top + B.rowHeight / 2 - height / 2, 0, Math.max(0, B.rows.length * B.rowHeight - height));
  B.track.style.transform = `translateY(${-scroll}px)`;
  const row = B.rows[B.selected];
  const info = B.info || {};
  B.title.textContent = row.kind === 'use' ? (info.path || '').split(/[\\/]/).filter(Boolean).pop() || info.path : row.label;
  B.caption.textContent = row.path || info.path || '';
  const isArchive = row.kind === 'location' || (row.kind === 'use' && info.archive);
  B.status(isArchive ? T.browse.isArchive : row.kind === 'use' ? T.browse.notArchive : '', null, '');
  const free = row.free ?? (row.kind === 'use' ? info.free : null);
  B.facts[0](T.browse.free, free != null && isFinite(free) ? bytes(free) : null);
  B.facts[1](T.browse.type, row.note || null);
}

function folderProblem(dir, info) {
  message(B.message, null);
  if (info.reason === 'credentials') return openCredentials(info.share, () => openBrowse(dir), () => openBrowse(null));
  const M = T.messages;
  const [title, text] = info.reason === 'missing' ? [M.missing, M.missingText(dir)]
    : info.reason === 'denied' ? [M.denied, M.deniedText(dir)] : [M.unreachable, M.unreachableText(dir)];
  openOverlay({ title, text,
    rows: [{ label: T.confirm.ok, run: () => { closeOverlay(); openBrowse(null); } }], back: () => { closeOverlay(); openBrowse(null); } });
}

async function useFolder(dir, create) {
  const r = await api('/api/location', { path: dir, create: Boolean(create) });
  if (r.ok) {
    S.config = (await api('/api/settings', {})).config;
    show('library');
    closeOverlay();
    return loadLibrary();
  }
  if (r.reason === 'not-archive') {
    return openOverlay({ title: T.messages.notArchive, text: T.confirm.makeArchive,
      rows: [{ label: T.confirm.make, run: () => useFolder(dir, true) }, { label: T.confirm.cancel, separated: true, run: closeOverlay }] });
  }
  if (r.reason === 'credentials') return openCredentials(r.share, () => useFolder(dir, create), closeOverlay);
  folderProblem(dir, r);
}

function actBrowse(action) {
  const row = B.rows[B.selected];
  if ((action === 'up' || action === 'down') && B.rows.length > 1) {
    play('scroll');
    return selectBrowse((B.selected + (action === 'up' ? -1 : 1) + B.rows.length) % B.rows.length);
  }
  if (action === 'lt' || action === 'rt') return selectBrowse(action === 'lt' ? 0 : B.rows.length - 1);
  if (action === 'b') {
    play('back');
    if (!B.info || !B.info.path) {
      show('library');
      return refreshDetails();
    }
    return openBrowse(B.info.parent);
  }
  if (action === 'y' && B.info && B.info.path) return useFolder(B.info.path);
  if (action !== 'a' || !row) return;
  play('select');
  if (row.kind === 'use') return useFolder(B.info.path);
  if (row.kind === 'location') return useFolder(row.path);
  if (row.kind === 'up') return openBrowse(row.path);
  if (row.kind === 'folder') return openBrowse(row.path);
  if (row.kind === 'network') {
    openKeyboard({ title: T.browse.address, hint: T.browse.addressHint, value: '\\\\', done: value => {
      const address = value.trim().replace(/\//g, '\\');
      return address.length > 2 ? openBrowse(address) : drawBrowse();
    }, cancel: drawBrowse });
  }
}

// Signing in to a share: user name and password, through Windows
function openCredentials(share, retry, cancel) {
  const C = T.credentials;
  const form = { user: '', password: '', remember: true, error: '' };
  const reopen = () => openOverlay(overlay);
  const overlay = {
    title: C.title,
    build() {
      overlay.text = [C.text(share), form.error].filter(Boolean).join('\n');
      return [
        { label: C.user, value: form.user || '—', plain: true, arrows: false, run: () => openKeyboard({ title: C.user, value: form.user,
          done: v => { form.user = v; reopen(); }, cancel: reopen }) },
        { label: C.password, value: form.password ? '•'.repeat(Math.min(12, form.password.length)) : '—', arrows: false,
          run: () => openKeyboard({ title: C.password, value: form.password, mask: true, done: v => { form.password = v; reopen(); }, cancel: reopen }) },
        { label: C.remember, value: form.remember ? T.media.yes : T.media.no, change: () => { form.remember = !form.remember; } },
        { label: C.connect, separated: true, disabled: !form.user, run: async () => {
          const r = await api('/api/connect', { share, user: form.user, password: form.password, remember: form.remember });
          if (r.ok) {
            closeOverlay();
            return retry();
          }
          form.error = r.reason === 'credentials' ? C.wrong : r.reason === 'other-user' ? C.otherUser : C.failed;
          renderOverlay();
        } },
      ];
    },
    back: () => { closeOverlay(); cancel(); },
  };
  reopen();
}

// The on-screen keyboard of atelier.js, in place of a menu
function openKeyboard(options) {
  state.overlay = null;
  G.menu.hide();
  keyboard.open({ ...options, labels: T.keyboard });
}

// The modes
//
// Select opens the choice between the two, as two squares side by side, with the one
// that isn't open chosen, so that Select then A goes over to it. Each is found again as
// it was left: its view, its platform or collection, its game and its marked games.

function openSections() {
  const C = T.sections;
  const counts = { catalogue: T.games(number(library.summary ? library.summary.total : 0)), collections: T.collections.count(number(collections().length)) };
  openOverlay({
    title: C.title,
    opener: 'select',
    cls: 'sections',
    across: true,
    rows: SECTIONS.map(s => ({
      label: sectionName(s), note: counts[s], sub: C.text[s], art: s === 'collections' ? COLLECTION_ART : 'catalogue',
      // The tag font draws the word in brackets as an outlined tag
      value: s === state.section ? `(${C.current})` : undefined, arrows: false, run: () => switchSection(s),
    })),
    selected: SECTIONS.findIndex(s => s !== state.section),
  });
}

function switchSection(to) {
  closeOverlay();
  if (to === state.section) return;
  play('quicksysselect');
  enterSection(to);
  renderLibrary();
}

// The open mode keeps its place; the page then slides in from the side of the other,
// the Catalogue being on the left
function enterSection(to) {
  if (to === state.section) return;
  away[state.section] = { view: state.view, marked: state.marked };
  state.section = to;
  ({ view: state.view, marked: state.marked } = away[to]);
  state.markedAll = null;
  const page = document.getElementById('library');
  page.classList.remove('from-left', 'from-right');
  // Read once, so that the slide starts again
  void page.offsetWidth;
  if (!blocked()) page.classList.add(to === 'catalogue' ? 'from-left' : 'from-right');
}

// From a collection: the game among the other games of its platform, in the section
// open if it is there, else in the archive or, never archived, in Not archived
function showInCatalogue(g) {
  closeOverlay();
  play('quicksysselect');
  enterSection('catalogue');
  Object.assign(state, { view: 'games', platform: g.system });
  if (!inSection(g)) state.filter = g.archive ? 'archive' : 'unarchived';
  renderLibrary(g.id);
}

// Input

function actLibrary(action) {
  // B goes back from a platform's games to the platforms, and from there to ES-DE
  if (action === 'b' && state.view === 'games') {
    play('back');
    state.view = 'platforms';
    return renderLibrary();
  }
  if (state.section === 'collections' && !blocked()) return actCollections(action);
  if (action === 'b') return requestQuit();
  // Without an archive, A leads to one: signing in, or choosing a folder
  if (blocked()) {
    if (action !== 'a') return;
    play('select');
    const loc = library.location;
    if (loc && loc.reason === 'credentials') return openCredentials(loc.share, () => loadLibrary(), () => {});
    return openBrowse(null);
  }
  if (action === 'lb' || action === 'rb') {
    play('quicksysselect');
    state.filter = cycle(FILTERS, state.filter, action === 'lb' ? -1 : 1);
    saveSettings({ section: state.filter });
    return renderLibrary(selectedId());
  }
  if (action === 'y') {
    if (!state.platform) return;
    play('select');
    return openPlatformActions();
  }
  if (state.view === 'platforms') return actPlatforms(action);
  if (!viewCount()) return;
  const target = moveGame(state.game, action);
  if (target !== null) {
    play('scroll');
    return selectGame(target);
  }
  if (action === 'a') {
    play('select');
    return openActions();
  }
  if (action === 'x' && selected()) {
    play('favorite');
    setMarks([selected().id], !state.marked.has(selected().id));
  }
}

// The platforms: up and down choose one, A opens its games
function actPlatforms(action) {
  if (!platforms.length) return;
  const n = state.platformPick;
  const to = { up: n - 1, down: n + 1, lt: 0, rt: platforms.length - 1 }[action];
  if (to !== undefined) {
    if (to < 0 || to >= platforms.length || to === n) return;
    play('scroll');
    return selectPlatform(to);
  }
  if (action === 'a') {
    play('select');
    state.view = 'games';
    renderLibrary();
  }
}

function act(action) {
  if (keyboard.isOpen()) return keyboard.act(action);
  if (state.overlay) return actOverlay(action);
  if (action === 'start') {
    play('select');
    return openMenu();
  }
  // Without an archive there are no collections to go to
  if (action === 'select') {
    if (state.screen !== 'library' || blocked()) return;
    play('select');
    return openSections();
  }
  if (state.screen === 'browse') return actBrowse(action);
  actLibrary(action);
}

// Start

// Once the server has read the library
const libraryRead = () => new Promise(resolve => {
  const check = () => (libraryReady() || blocked() ? resolve() : setTimeout(check, 200));
  check();
});

async function start() {
  await openSession(STRINGS);
  if (MODES.includes(S.config.mode)) state.mode = S.config.mode;
  if (SORTS.includes(S.config.sort)) state.sort = S.config.sort;
  if (FILTERS.includes(S.config.section)) state.filter = S.config.section;
  const layout = () => {
    L = S.layouts[aspectRatio()];
    applySizes();
    buildLibrary();
    buildBrowse();
    buildMenu();
    applyColors('');
    renderHelp();
  };
  layout();
  requestAnimationFrame(poll);
  // For testing: #mode=<m>&filter=<section>&sort=<s>, &open=<system> opens a platform's
  // games, &game=<n> (or &pick=<n>) picks the nth game of the list open, &menu opens the
  // options, &actions those of the game, &platform those of the platform, &browse the
  // folder chooser, &modes the choice of mode; &collections the Collections,
  // &collection=<n> the nth, its tile &pick=<n>, &search=<words> its search, &filters and
  // &keyboard the search's filters and keyboard, &cmenu the collection's menu
  const params = new URLSearchParams(location.hash.slice(1));
  if (MODES.includes(params.get('mode'))) state.mode = params.get('mode');
  if (FILTERS.includes(params.get('filter'))) state.filter = params.get('filter');
  if (SORTS.includes(params.get('sort'))) state.sort = params.get('sort');
  if (params.has('open')) Object.assign(state, { view: 'games', platform: params.get('open') });
  await loadLibrary();
  await libraryRead();
  await listed();
  const pick = params.get('game') || params.get('pick');
  if (params.has('collections') || params.has('collection')) {
    enterSection('collections');
    renderLibrary();
  }
  if (params.has('collection')) {
    const c = collections()[Number(params.get('collection')) || 0];
    if (c) {
      openCollection(c.id);
      await listed();
      if (pick) selectGame(Number(pick));
      if (params.has('search')) {
        openSearch({ text: params.get('search') }, false);
        await listed();
      }
    }
  } else if (pick && viewCount()) selectGame(Number(pick));
  if (pick || params.has('actions')) await new Promise(r => setTimeout(r, 300));
  if (params.has('filters')) openFilters();
  if (params.has('keyboard')) openSearchKeyboard();
  if (params.has('cmenu')) openCollectionMenu(currentCollection());
  if (params.has('menu')) openMenu();
  if (params.has('modes')) openSections();
  if (params.has('actions')) openActions();
  if (params.has('platform')) openPlatformActions();
  if (params.has('browse')) openBrowse(params.get('browse') || null);
  pollJobs();
  addEventListener('resize', () => {
    layout();
    renderLibrary(selectedId());
    if (state.screen === 'browse') drawBrowse();
    if (state.overlay) renderOverlay();
    if (keyboard.isOpen()) keyboard.render();
  });
}

start();
