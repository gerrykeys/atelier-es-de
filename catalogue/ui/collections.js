// The Collections, Catalogue's second mode, chosen with Select (see main.js): groups of
// games from ES-DE and from the archive, kept in the archive's @collections folder and
// shown in ES-DE as custom collections once the app leaves (see
// catalogue/scripts/windows/collections.ps1). The first row of the list makes a new one,
// A opens one, whose first tile adds games; B goes back to ES-DE. Adding is a search
// through every detail of every game, narrowed with filters (X), where A puts a game in
// the collection or takes it out. A collection can also keep itself up to date with a
// search: it then holds whatever the search finds, on every computer, but for the games
// taken out by hand. Of the transfers, only those a collection needs are here: a game
// only in ES-DE is archived as it joins a collection, so that every computer can have
// it, and a game only in the archive is downloaded from the collection; the rest is the
// Catalogue's, where a game of a collection opens.
//
// What a collection holds, the search and its filters are worked out by the server
// (Library in common/scripts/windows/native.cs); the page gets the collections with how
// their games stand, and a collection's games or what the search finds a page at a time,
// as the list open of main.js, which draws them.

// The tile that adds games, first in a collection
const ADD = { add: true };
const COLLECTION_FILTERS = ['all', 'local', 'remote'];
const WHERE = ['all', 'local', 'remote'];
const FACETS = ['system', 'genre', 'decade', 'year', 'developer', 'publisher', 'players', 'region', 'favorite', 'completed'];
const SEARCH_KEYS = ['text', ...FACETS];
const COLLECTION_ART = 'custom-collections';

const collections = () => library.collections || [];
const currentCollection = () => collections().find(c => c.id === state.collection) || null;
const emptyQuery = () => Object.fromEntries(SEARCH_KEYS.map(k => [k, '']));
const restartHint = () => (jobs.restartsEsde ? T.notice.hintRestart : T.notice.hintManual);

function setCollectionArt(e) {
  e.style.setProperty('--src', `url(${themeUrl(`systems/controllers/${COLLECTION_ART}.svg`)})`);
}

// What the search counted of a filter's values last, for the filters' menu
let facetsCache = null;

// A new library: what was worked out of the last one goes
function resetCollections() {
  facetsCache = null;
}

// A search as a collection keeps it: only its parts with a value, or null
function cleanQuery(query) {
  const out = {};
  for (const key of SEARCH_KEYS) if (query && String(query[key] || '').trim()) out[key] = String(query[key]).trim();
  return Object.keys(out).length ? out : null;
}
const sameSearch = (a, b) => JSON.stringify(cleanQuery(a)) === JSON.stringify(cleanQuery(b));

function facetLabel(key, value, label) {
  const Q = T.search;
  if (key === 'system') return label || (systemOf(value) || {}).name || value;
  if (key === 'decade') return Q.decade(value);
  if (key === 'players') return Q.players[value] || value;
  if (key === 'favorite' || key === 'completed') return Q.facets[key];
  return label || value;
}

// A search in a few words: "capcom" · Super Nintendo · 1990s
function describe(query) {
  const q = cleanQuery(query) || {};
  return [q.text && `“${q.text}”`, ...FACETS.filter(k => q[k]).map(k => facetLabel(k, q[k]))].filter(Boolean).join('  ·  ');
}

// The server keeps, per computer, what the search of each collection finds here: that
// is what ES-DE shows of it
async function syncMatches() {
  if (!collections().some(c => c.search)) return;
  await api('/api/collections', { action: 'matches' }).catch(() => null);
}

function setCollections(answer) {
  if (answer && answer.collections) library.collections = answer.collections;
  jobs.collectionsChanged = true;
}

function collectionProblem(r) {
  const C = T.collections;
  openOverlay({ title: C.failed, text: r && r.reason === 'gone' ? C.gone : (r && r.message) || T.messages.unreachableText(shortPath((library.location || {}).path)),
    rows: [{ label: T.confirm.ok, run: closeOverlay }] });
}

// A request to the collections; true when it worked. The list open is read again.
async function changeCollections(body) {
  const r = await api('/api/collections', body).catch(() => null);
  if (!r || !r.ok) {
    if (r) setCollections(r);
    collectionProblem(r);
    return null;
  }
  setCollections(r);
  list.version = -1;
  renderLibrary(selectedId());
  return r;
}

// The views

// The list of collections, after the row that makes a new one
function renderCollections() {
  setHeader(null);
  message(G.message, null);
  G.info.textContent = T.collections.count(number(collections().length));
  G.rows = [{ kind: 'new' }, ...collections().map(c => ({ kind: 'collection', c }))];
  drawRows(G.rows.map(r => (r.kind === 'new'
    ? { art: e => { e.classList.add('plus-art'); e.textContent = '+'; }, name: T.collections.new, note: '', cls: 'new-row' }
    : { art: setCollectionArt, name: r.c.name, note: T.games(number(r.c.games)) })));
  state.game = null;
  const at = state.collection ? G.rows.findIndex(r => r.c && r.c.id === state.collection) : -1;
  selectCollectionRow(at >= 0 ? at : clamp(state.collectionPick, 0, G.rows.length - 1));
}

function selectCollectionRow(n) {
  state.collectionPick = n;
  const r = G.rows[n];
  state.collection = r.kind === 'collection' ? r.c.id : null;
  highlightRow(n);
  showCollectionRow();
  renderHelp();
}

function showCollectionRow() {
  const r = G.rows && G.rows[state.collectionPick];
  if (!r) return clearDetails();
  if (r.kind === 'new') {
    showSummary(T.collections.newTitle, '', null, T.collections.newText, 0, 0);
    return G.facts.forEach(f => f('', ''));
  }
  showCollection(r.c);
}

// The details of a collection or of what adds games: how many games are in ES-DE, the
// screenshot of the first that has one
function showSummary(title, caption, image, line, games, fraction) {
  applyColors('');
  clearTimeout(backdropTimer);
  document.getElementById('atmosphere').classList.remove('shown');
  G.media(image || null, COLLECTION_ART);
  G.title.textContent = title;
  G.caption.textContent = caption;
  G.tag([]);
  G.status(games ? T.collections.inEsde : '', games ? fraction : null, line, false, fraction || 0);
  G.next.replaceChildren();
}

function showCollection(c) {
  const C = T.collections;
  showSummary(c.name, c.search ? C.autoCaption : C.caption, c.image,
    c.games ? C.overview(number(c.local), number(c.games)) : C.emptyText, c.games, c.games ? c.local / c.games : 0);
  G.tag([c.search && C.auto]);
  G.facts[0](C.facts.games, number(c.games) + (c.missing ? `  ·  ${C.missing(number(c.missing))}` : ''));
  G.facts[1](C.facts.toInstall, c.install ? `${number(c.install)}  ·  ${bytes(c.installSize)}` : null);
  G.facts[2](C.facts.platforms, c.games ? number(c.platforms.length) : null);
  G.facts[3](C.facts.created, [date(c.created), c.device].filter(Boolean).join('  ·  ') || null);
  el('span', '', G.next, c.search ? C.autoLine(describe(c.search)) : c.platforms.join('  ·  '));
}

// The cover shape most of the games of the list open have, from those it has shown
function commonAspect() {
  const counts = new Map();
  for (const row of list.rows.values()) {
    const a = clamp(systemInfo(row.system).cover || 0.75, 0.6, 1.5);
    counts.set(a, (counts.get(a) || 0) + 1);
  }
  let best = 0.75;
  let most = 0;
  for (const [a, n] of counts) if (n > most) [best, most] = [a, n];
  return best;
}

// The games of the open collection, after the tile that adds games
function renderCollection(keepId) {
  const c = currentCollection();
  setHeader(null, c.name);
  const id = c.id;
  const where = state.collectionFilter;
  const sort = state.sort;
  const expected = where === 'all' ? c.games : where === 'local' ? c.local : c.games - c.local;
  setList(`collection|${id}|${where}|${sort}`, (from, count, at) => api(`/api/games?collection=${encodeURIComponent(id)}&where=${where}`
    + `&sort=${sort}&from=${from}&count=${count}${at ? `&at=${encodeURIComponent(at)}` : ''}`), 1, expected, { key: id, system: null, title: c.name });
  shapeAndShow(keepId);
}

// The list drawn in the shape most of its covers have, once its first page has come
function shapeAndShow(keepId) {
  if (list.known) list.aspect = commonAspect();
  else {
    list.aspect = 0.75;
    listed().then(() => {
      const aspect = commonAspect();
      if (state.mode === 'grid' && aspect !== list.aspect && ['collection', 'search'].includes(state.view)) {
        list.aspect = aspect;
        showList(selectedId());
      }
    });
  }
  showList(keepId);
  message(G.message, null);
  if (state.view === 'collection') G.info.textContent = `${T.games(number(list.total))}  ·  ${T.sorts[state.sort]}`;
}

// The games a search finds, to add to the open collection or take out of it
function renderSearch(keepId) {
  const c = currentCollection();
  setHeader(null, T.search.header(c.name));
  const query = { ...state.query };
  const where = state.where;
  const id = c.id;
  setList(`search|${id}|${where}|${JSON.stringify(cleanQuery(query))}`, (from, count) => api('/api/search', { query, where, from, count, collection: id }),
    0, undefined, { key: 'search', system: null, title: describe(query) || T.search.everything });
  shapeAndShow(keepId);
  G.info.textContent = T.games(number(list.total));
  if (list.known && !list.total) message(G.message, T.search.none, T.search.noneText);
}

// The line above the content: how much of the collections is in ES-DE, or what the
// search found and how much of it is in the collection
function overviewCollections() {
  const C = T.collections;
  if (state.view === 'collections') {
    const { games, local } = library.union;
    return G.overview('', null, collections().length ? C.overviewAll(C.count(number(collections().length)), number(local), number(games)) : C.none,
      false, games ? local / games : 0);
  }
  const c = currentCollection();
  if (!c) return G.overview('', null, '');
  if (state.view === 'collection') {
    const text = c.games ? C.overview(number(c.local), number(c.games)) + (c.install ? C.overviewInstall(number(c.install), bytes(c.installSize)) : '') : C.emptyText;
    return G.overview('', null, text + (c.search ? `  ·  ${C.autoLine(describe(c.search))}` : ''), false, c.games ? c.local / c.games : 0);
  }
  const found = list.known ? list.total : 0;
  const inside = list.extra && list.extra.inCollection !== undefined ? list.extra.inCollection : 0;
  G.overview('', null, `${describe(state.query) || T.search.everything}  ·  ${T.search.inCollection(number(inside), number(found))}`,
    false, found ? inside / found : 0);
}

// The tile and row that add games
function fillAdd(pic) {
  const box = el('div', 'add-tile', pic);
  el('span', 'plus', box, '+');
  el('span', 'label', box, T.collections.addGames);
}

function showAddTile() {
  const c = currentCollection();
  showSummary(T.collections.addGames, c.name, null, T.collections.addHint, 0, 0);
  G.facts.forEach(f => f('', ''));
  G.facts[0](T.collections.facts.games, number(c.games));
}

// Opening and moving

function openCollection(id) {
  Object.assign(state, { collection: id, view: 'collection', collectionFilter: 'all' });
  state.marked.clear();
  state.picks[`collection:${id}`] = 0;
  renderLibrary();
}

// The search, empty with the keyboard open to type at once, or with a search to change;
// typing false leaves the keyboard closed, every game in view, until Y opens it
function openSearch(query = null, typing = !query) {
  state.query = { ...emptyQuery(), ...(query || {}) };
  state.where = 'all';
  state.view = 'search';
  state.picks[pickKey()] = 0;
  facetsCache = null;
  renderLibrary();
  if (typing) openSearchKeyboard();
}

// The keyboard of the search, which counts the games found as the words are typed
function openSearchKeyboard(thenFilters) {
  const back = () => (thenFilters ? openFilters() : renderHelp());
  const counted = new Map();
  let last = '';
  openKeyboard({
    title: T.search.title, value: state.query.text,
    change: text => {
      if (counted.has(text)) return (last = counted.get(text));
      api('/api/search', { query: { ...state.query, text }, where: state.where, from: 0, count: 0, collection: null }).then(r => {
        counted.set(text, T.search.found(number(r.total)));
        if (keyboard.isOpen() && keyboard.current && keyboard.current.value === text) {
          keyboard.current.hint = counted.get(text);
          keyboard.render();
        }
      }).catch(() => {});
      return last;
    },
    done: text => {
      state.query.text = text.trim();
      state.picks[pickKey()] = 0;
      facetsCache = null;
      renderLibrary();
      back();
    },
    cancel: back,
  });
}

function actCollections(action) {
  if (state.view === 'collections') return actCollectionList(action);
  if (state.view === 'collection') return actCollection(action);
  actSearch(action);
}

function actCollectionList(action) {
  const n = state.collectionPick;
  const to = { up: n - 1, down: n + 1, lt: 0, rt: G.rows.length - 1 }[action];
  if (to !== undefined) {
    if (to < 0 || to >= G.rows.length || to === n) return;
    play('scroll');
    return selectCollectionRow(to);
  }
  if (action === 'b') return requestQuit();
  const row = G.rows[n];
  if (action === 'a') {
    play('select');
    return row.kind === 'new' ? newCollection() : openCollection(row.c.id);
  }
  if (action === 'y' && row.kind === 'collection') {
    play('select');
    openCollectionMenu(row.c);
  }
}

function actCollection(action) {
  if (action === 'b') {
    play('back');
    state.view = 'collections';
    state.marked.clear();
    return renderLibrary();
  }
  if (action === 'lb' || action === 'rb') {
    play('quicksysselect');
    state.collectionFilter = cycle(COLLECTION_FILTERS, state.collectionFilter, action === 'lb' ? -1 : 1);
    return renderLibrary(selectedId());
  }
  if (action === 'y') {
    play('select');
    return openCollectionMenu(currentCollection());
  }
  const target = moveGame(state.game, action);
  if (target !== null) {
    play('scroll');
    return selectGame(target);
  }
  if (action === 'a') {
    play('select');
    return rowAt(state.game) === ADD ? openSearch() : openMemberActions();
  }
  if (action === 'x' && selected()) {
    play('favorite');
    setMarks([selected().id], !state.marked.has(selected().id));
  }
}

function actSearch(action) {
  if (action === 'b') {
    play('back');
    state.view = 'collection';
    return renderLibrary();
  }
  if (action === 'lb' || action === 'rb') {
    play('quicksysselect');
    state.where = cycle(WHERE, state.where, action === 'lb' ? -1 : 1);
    facetsCache = null;
    return renderLibrary(selectedId());
  }
  if (action === 'y') {
    play('select');
    return openSearchKeyboard();
  }
  if (action === 'x') {
    play('select');
    return openFilters();
  }
  if (!viewCount()) return;
  const target = moveGame(state.game, action);
  if (target !== null) {
    play('scroll');
    return selectGame(target);
  }
  if (action === 'a' && selected()) toggleMember(selected());
}

// Changing collections

function newCollection() {
  const C = T.collections;
  openKeyboard({ title: C.newTitle, hint: C.nameHint, value: '', cancel: renderHelp, done: async name => {
    if (!name.trim()) return renderHelp();
    const r = await api('/api/collections', { action: 'create', name: name.trim() }).catch(() => null);
    if (!r || !r.ok) return collectionProblem(r);
    setCollections(r);
    Object.assign(state, { collection: r.id, collectionFilter: 'all' });
    state.marked.clear();
    // Its name was just typed: the search opens on every game, without a second keyboard
    openSearch(null, false);
  } });
}

function renameCollection(c) {
  closeOverlay();
  openKeyboard({ title: T.collections.renameTitle, value: c.name, cancel: renderHelp, done: name => {
    if (!name.trim() || name.trim() === c.name) return renderHelp();
    changeCollections({ action: 'update', id: c.id, name: name.trim() });
  } });
}

function confirmDelete(c) {
  const C = T.collections;
  openOverlay({
    title: C.deleteTitle(c.name), text: C.deleteText,
    rows: [
      { label: C.deleteConfirm, run: async () => {
        closeOverlay();
        const r = await api('/api/collections', { action: 'delete', id: c.id }).catch(() => null);
        if (!r || !r.ok) return collectionProblem(r);
        setCollections(r);
        Object.assign(state, { view: 'collections', collection: null });
        renderLibrary();
        notify(C.deleted, c.name, restartHint());
      } },
      { label: T.confirm.cancel, separated: true, run: closeOverlay },
    ],
    selected: 1,
  });
}

// Games only in ES-DE join a collection once they are archived too
function confirmArchive(archive, go) {
  const C = T.collections;
  openOverlay({
    title: C.archiveTitle(archive.count),
    text: C.archiveText(archive.count, archive.first, withMedia(bytes(archive.size), mediaSize(archive.media, 'archiveMedia'), ' · ')),
    rows: [
      { label: C.addAndArchive, run: () => { closeOverlay(); go(); } },
      { label: T.confirm.cancel, separated: true, run: closeOverlay },
    ],
  });
}

// Adds games to a collection: ids, or what a search finds ({ query, where }). Those taken
// out of its search before are let in again; those only in ES-DE are archived as they
// join, after asking.
async function addToCollection(c, what) {
  const C = T.collections;
  const plan = await api('/api/collections', { action: 'plan-add', id: c.id, ...what }).catch(() => null);
  if (!plan || !plan.ok) return collectionProblem(plan);
  if (!plan.fresh) return;
  const go = async () => {
    const r = await changeCollections({ action: 'add', id: c.id, ...what });
    if (!r) return;
    notify(C.added(number(r.fresh), c.name), T.notice.names(r.names), r.archive.count ? C.archiving(number(r.archive.count)) : restartHint());
    pollJobs();
  };
  if (plan.archive.count) return confirmArchive(plan.archive, go);
  play('favorite');
  go();
}

// Takes games out: those added by hand leave it, those its search finds are excluded
async function takeOut(c, ids) {
  for (const id of ids) state.marked.delete(id);
  const r = await changeCollections({ action: 'take-out', id: c.id, ids });
  if (r) notify(T.collections.takenOut(number(r.count), c.name), T.notice.names(r.names), restartHint());
}

function toggleMember(g) {
  const c = currentCollection();
  if (!g.member) return addToCollection(c, { ids: [g.id] });
  play('back');
  takeOut(c, [g.id]);
}

// The collection keeps itself up to date with a search
async function saveSearch(c, search) {
  const plan = await api('/api/collections', { action: 'plan-auto', id: c.id, search }).catch(() => null);
  if (!plan || !plan.ok) return collectionProblem(plan);
  const go = async () => {
    const r = await changeCollections({ action: 'auto', id: c.id, search });
    if (!r) return;
    notify(T.search.autoSaved, describe(search), r.archive.count ? T.collections.archiving(number(r.archive.count)) : restartHint());
    pollJobs();
  };
  if (plan.archive.count) return confirmArchive(plan.archive, go);
  go();
}

// The games it holds now stay, added by hand
async function stopAuto(c) {
  if (await changeCollections({ action: 'stop-auto', id: c.id })) notify(T.search.autoStopped, c.name, restartHint());
}

// Menus

// Y: what can be done with a collection
function openCollectionMenu(c) {
  if (!c) return;
  const C = T.collections;
  const rows = [{ label: C.addGames, run: () => { closeOverlay(); state.collection = c.id; openSearch(); } }];
  if (c.search) rows.push({ label: C.editSearch, run: () => { closeOverlay(); state.collection = c.id; openSearch(c.search); } });
  if (c.install) {
    // No room beside these labels for the media
    rows.push({ label: C.installAll(number(c.install)), note: bytes(c.installSize), separated: true,
      run: () => { closeOverlay(); enqueue('download', { collection: c.id, where: 'remote' }); } });
  }
  if (c.unarchived) {
    rows.push({ label: C.archiveAll(number(c.unarchived)), note: bytes(c.unarchivedSize), separated: !c.install,
      run: () => { closeOverlay(); enqueue('archive', { collection: c.id, where: 'unarchived' }); } });
  }
  if (c.missing) {
    rows.push({ label: C.dropMissing(number(c.missing)), separated: !c.install && !c.unarchived,
      run: () => { closeOverlay(); changeCollections({ action: 'drop-missing', id: c.id }); } });
  }
  rows.push({ label: C.rename, separated: true, run: () => renameCollection(c) });
  rows.push({ label: C.delete, run: () => confirmDelete(c) });
  openOverlay({ title: c.name, text: c.search ? C.autoLine(describe(c.search)) : '', rows });
}

// A: what can be done with the chosen game of a collection, or the marked ones: what
// the collection needs of them, or the Catalogue for the rest
async function openMemberActions() {
  const C = T.collections;
  const A = T.actions;
  const c = currentCollection();
  const g = selected();
  const ids = state.marked.size ? [...state.marked] : g ? [g.id] : [];
  if (!ids.length) return;
  const a = await api('/api/actions', { ids }).catch(() => null);
  if (!a) return;
  const rows = [];
  if (a.install.count) {
    rows.push({ label: A.download, note: note(a.install.count, a.install.size, mediaSize(a.install.media, 'downloadMedia')), run: () => { closeOverlay(); enqueue('download', { ids, where: 'remote' }); } });
  }
  if (a.unarchived.count) {
    rows.push({ label: A.archive, note: note(a.unarchived.count, a.unarchived.size, mediaSize(a.unarchived.media, 'archiveMedia')), run: () => { closeOverlay(); enqueue('archive', { ids, where: 'unarchived' }); } });
  }
  rows.push({ label: C.takeOut, separated: rows.length > 0, run: () => { closeOverlay(); takeOut(c, ids); } });
  if (ids.length === 1 && g) rows.push({ label: C.openInCatalogue, run: () => showInCatalogue(g) });
  if (state.marked.size) rows.push({ label: A.unmarkAll, separated: true, run: () => { closeOverlay(); setMarks([], false); } });
  openOverlay({ title: state.marked.size ? A.titleMarked(number(state.marked.size)) : g.name, rows });
}

// X in the search: the words, what to do with what they find (add it all, keep the
// collection up to date with the search), then the filters. A filter shows when it is
// set or would change what is found: with two values or more, or for favorites and
// completed games, when some of the games found are and some aren't.
async function openFilters() {
  const Q = T.search;
  const c = currentCollection();
  const q = state.query;
  const set = (key, value) => {
    q[key] = value;
    state.picks[pickKey()] = 0;
    facetsCache = null;
    renderLibrary();
    openFilters();
  };
  if (!facetsCache) facetsCache = await api('/api/facets', { query: q, where: state.where }).catch(() => ({}));
  const facets = facetsCache || {};
  const found = list.known ? list.total : 0;
  const outside = found - (list.extra && list.extra.inCollection !== undefined ? list.extra.inCollection : 0);
  openOverlay({
    title: Q.filters,
    build() {
      const rows = [{ label: Q.text, value: q.text || Q.any, plain: Boolean(q.text), arrows: false,
        run: () => { closeOverlay(); openSearchKeyboard(true); } }];
      rows.push({ label: Q.addAll(number(outside)), disabled: !outside,
        run: () => { closeOverlay(); addToCollection(c, { query: { ...q }, where: state.where }); } });
      const search = cleanQuery(q);
      if (search && !sameSearch(search, c.search)) {
        rows.push({ label: c.search ? Q.replaceAuto : Q.makeAuto, run: () => { closeOverlay(); saveSearch(c, search); } });
      }
      if (c.search) rows.push({ label: Q.stopAuto, run: () => { closeOverlay(); stopAuto(c); } });
      let first = true;
      for (const key of FACETS) {
        const values = facets[key] || [];
        const useful = key === 'favorite' || key === 'completed' ? values.length && values[0].count < found : values.length > 1;
        if (!q[key] && !useful) continue;
        const label = q[key] ? facetLabel(key, q[key], (values.find(v => v.value === q[key]) || {}).label) : Q.any;
        rows.push({ label: Q.facets[key], value: label, separated: first,
          change: step => set(key, cycle(['', ...values.map(v => v.value)], q[key], step)),
          run: () => openFacet(key, values, value => set(key, value)) });
        first = false;
      }
      if (search) rows.push({ label: Q.clear, separated: true, run: () => { Object.assign(q, emptyQuery()); set('text', ''); } });
      return rows;
    },
  });
}

// A filter's values, the chosen one first in view
function openFacet(key, values, pick) {
  openOverlay({
    title: T.search.facets[key],
    rows: [{ label: T.search.any, run: () => pick('') }, ...values.map(v => ({ label: facetLabel(key, v.value, v.label), note: number(v.count), run: () => pick(v.value) }))],
    selected: Math.max(0, values.findIndex(v => v.value === state.query[key]) + 1),
    back: openFilters,
  });
}
