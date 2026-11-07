# Catalogue: keeps the user's games in an archive on a network share or a disk, as
# one zip file each with their details and media (see store.ps1), and brings them back
# into ES-DE, in this or another installation. The saves RetroArch keeps of them go
# to the archive too (see saves.ps1). Like the achievements app it is a
# small web server, reachable only from this computer, with a full-screen Edge window
# that shows the page in the theme's catalogue/ui (see
# common/scripts/windows/server.ps1); ES-DE starts it from its system carousel and comes
# back when it closes. ES-DE only reads its games and collections when it starts, so
# after games were downloaded or removed, or the collections changed, leaving the app
# restarts it (restart.ps1), and the collections are written into ES-DE meanwhile
# (collections.ps1).
#
# Transfers are made one after the other in the background runspace, while the page
# goes on browsing. The settings (the archives, the media to send and to bring) are in
# <ES-DE home>/atelier/catalogue/config.json; state.json remembers the CRCs of files
# whose dates changed and what was last exchanged of every game's saves, and
# collections.json what the collections are in ES-DE.
#
# Usage: powershell -File app.ps1 [--app <ES-DE application folder>] [--data <folder>]
#                                 [--port <number>] [--no-window]
# --no-window only starts the server and prints its address, for testing in a browser.

. ([IO.Path]::Combine($PSScriptRoot, '..', '..', '..', 'common', 'scripts', 'windows', 'server.ps1'))
. ([IO.Path]::Combine($PSScriptRoot, 'saves.ps1'))
. ([IO.Path]::Combine($PSScriptRoot, 'places.ps1'))
. ([IO.Path]::Combine($PSScriptRoot, 'restart.ps1'))
. ([IO.Path]::Combine($PSScriptRoot, 'carousel.ps1'))
. ([IO.Path]::Combine($PSScriptRoot, 'collections.ps1'))

$CatalogueUi = [IO.Path]::Combine($AtelierTheme, 'catalogue', 'ui')
# Atelier's own entries in the carousel are no games, nor is Catalogue's under its first name
$CatalogueOwnSystems = @('retroachievements', 'catalogue', 'gamearchive')
# The part of a transfer each step takes
$CatalogueSteps = @{
  archive = @{ compress = @(0, 0.75); verify = @(0.75, 0.95); media = @(0.95, 1) }
  'update-details' = @{ media = @(0, 1) }
  download = @{ extract = @(0, 0.9); media = @(0.9, 1) }
}

function Get-ArchiveLocation {
  $current = [int]$App.Config['current']
  $locations = $App.Config['locations']
  if ($current -ge 0 -and $current -lt $locations.Count) { return $locations[$current] }
  return $null
}
function Get-SavesKey($loc, $item) { return "$($loc['path'])|$($item.system)/$($item.rel)" }

function Save-CatalogueConfig {
  [Threading.Monitor]::Enter($App.Lock)
  try { Write-Atomic $App.ConfigFile (ConvertTo-JsonText $App.Config -Indent) } finally { [Threading.Monitor]::Exit($App.Lock) }
}
function Save-CatalogueState {
  [Threading.Monitor]::Enter($App.Lock)
  try { Write-Atomic $App.StateFile (ConvertTo-JsonText $App.State) } finally { [Threading.Monitor]::Exit($App.Lock) }
}

function Get-CatalogueSession {
  $session = Get-ThemeSession $App.Es
  $c = $App.Config
  $session.config = @{ locations = $c['locations']; current = $c['current']; archiveMedia = $c['archiveMedia']
    downloadMedia = $c['downloadMedia']; mode = $c['mode']; sort = $c['sort']; section = $c['section'] }
  $session.mediaTypes = $LibraryMediaTypes
  $session.retroarch = [bool]$App.Ra
  $session.device = $App.Device
  # Game videos as ES-DE plays them in the game lists: the theme's timing and sound,
  # as long as ES-DE plays the sound of videos in its views, at its video volume
  $video = Get-ThemeVideo
  $volume = Get-EsdeSetting $App.Es 'SoundVolumeVideos'
  $session.video = @{
    delay = $video.delay; fadeIn = $video.fadeIn
    audio = $video.audio -and (Get-EsdeSetting $App.Es 'ViewsVideoAudio') -cne 'false'
    volume = $(if ($volume) { [double]$volume } else { 80.0 }) / 100
  }
  return $session
}

# The library: every game of ES-DE and of the archive, side by side, read by Library in
# common/scripts/windows/native.cs on a thread of its own: as the app starts, and again
# when the archive or the media compared change. The page asks it for the platforms and
# how their games stand, for the games of a platform a page at a time, and for a game's
# details when it is chosen; after a transfer only its game is read again.
# What it keeps of the archive's catalog is saved in the data folder, cache/, so that a
# start reads from the archive only what changed since (see Catalog in native.cs).

$LibraryKeptScraped = @('name', 'rating', 'releasedate', 'developer', 'publisher', 'genre', 'players')
$LibraryKeptOwn = @('favorite', 'completed', 'kidgame', 'playcount', 'lastplayed', 'hidden', 'altemulator')

function Initialize-CatalogueLibrary {
  [Atelier.LibraryShape]::Set($LibraryMediaTypes, $LibraryFields, $LibraryKeptScraped, $LibraryKeptOwn)
  [Atelier.Catalog]::CacheDir = [IO.Path]::Combine($App.DataDir, 'cache')
  $exts = @{}
  foreach ($type in $LibraryMediaTypes) { $exts[$type] = [string[]](Get-MediaExtensions $type) }
  $App.MediaExts = $exts
}

# ES-DE's systems as the library reads them
function Get-LibrarySystems($es) {
  $list = New-List
  foreach ($name in @($es.systems.Keys)) {
    if ($CatalogueOwnSystems -contains $name) { continue }
    $s = $es.systems[$name]
    $aside = @{}
    foreach ($type in $LibraryMediaTypes) { if (Test-SetAside $type) { $aside[$type] = Get-SetAsideBase $es $name '' } }
    $list.Add(@{ name = $name; fullName = $s.fullName; dir = $s.dir; extensions = [string[]]@($s.extensions); gamelist = Get-GamelistFile $es $name
      mediaDir = [IO.Path]::Combine($es.mediaDir, $name); aside = $aside })
  }
  return , $list
}

# The archive in use when it can be reached, else $null
function Get-ArchiveRoot {
  $loc = Get-ArchiveLocation
  if (-not $loc) { return $null }
  $access = Test-Access $loc['path']
  if ($access.ok -and (Test-Archive $loc['path'])) { return $loc['path'] }
  return $null
}

# Reads the library again, in the background, and the collections of the archive
function Start-LibraryBuild {
  $root = Get-ArchiveRoot
  [void](Read-CatalogueCollections $root)
  [Atelier.Library]::BuildLater($root, $App.Config['archiveMedia'], $App.State['crcs'], (Get-EsdeLanguage $App.Es), $LibraryTempPrefixes, $App.MediaExts,
    (Get-LibrarySystems $App.Es))
}

# The archive chosen and how it can be reached
function Get-CatalogueLocation {
  $loc = Get-ArchiveLocation
  if (-not $loc) { return $null }
  $access = Test-Access $loc['path']
  $isArchive = $access.ok -and (Test-Archive $loc['path'])
  $location = @{ path = $loc['path']; ok = $access.ok; reason = $(if ($access.reason) { $access.reason } else { $null })
    share = $(if ($access.share) { $access.share } else { $null }); archive = $isArchive }
  if ($isArchive) { $location.free = Get-FreeSpace $loc['path'] }
  return $location
}

# What the page gets of the library: the archive, the platforms and how their games
# stand, and the collections, as JSON
function Get-LibraryAnswer([bool]$withLocation) {
  $parts = @()
  if ($withLocation) { $parts += '"location":' + (ConvertTo-JsonText (Get-CatalogueLocation)) }
  $parts += '"library":' + [Atelier.Library]::Summary()
  $parts += '"collections":' + [Atelier.Library]::CollectionsSummary()
  return '{' + ($parts -join ',') + '}'
}

# The games a request names: { ids }, or the games of a platform's section { system,
# section }, or of a collection { collection, where }
function Select-Games($body) {
  if (-not ($body -is [Collections.IDictionary])) { return , [Collections.Generic.List[Atelier.LibGame]]::new() }
  if ($body['collection']) {
    $ids = [Atelier.Library]::CollectionIds([string]$body['collection'], $(if ($body['where']) { [string]$body['where'] } else { 'all' }))
    return , [Atelier.Library]::Pick($ids, $null, $null)
  }
  # Assigned, not the value of an if, which would make a list of one its only id
  $ids = $null
  if ($body['ids'] -is [Collections.IList]) { $ids = $body['ids'] }
  $system = if ($body['system']) { [string]$body['system'] } else { $null }
  $games = [Atelier.Library]::Pick($ids, $system, $(if ($body['section']) { [string]$body['section'] } else { 'all' }))
  # Kept to where they are: in ES-DE (local), only in the archive (remote), never archived (unarchived)
  if ($body['where']) { $games = [Atelier.Library]::Keep($games, [string]$body['where']) }
  return , $games
}

# Saves

# The core RetroArch runs a game with here, by its folder name, $null when it runs in
# another emulator, 'unknown' when it can't be told: the emulator chosen for the game,
# else the system's (in its gamelist), else the system's first command
function Get-LibraryGameCore([string]$system, [string]$label) {
  $cache = $App.CoreCache
  $key = "system-label:$system"
  if (-not $cache.ContainsKey($key)) { $cache[$key] = [Atelier.Gamelist]::AlternativeEmulator((Get-GamelistFile $App.Es $system)) }
  $es = $App.Es
  $sys = $es.systems[$system]
  if (-not $sys -or -not @($sys.commands).Count) { return 'unknown' }
  if (-not $label) { $label = $cache[$key] }
  $command = @($sys.commands)[0].command
  if ($label) { foreach ($c in $sys.commands) { if ($c.label -ceq $label) { $command = $c.command; break } } }
  if (-not $command.Contains('%EMULATOR_RETROARCH%')) { return $null }
  $m = [regex]::Match($command, '%CORE_RETROARCH%[\\/]([\w.-]+?)\.(?:dll|so|dylib)')
  if (-not $m.Success) { return 'unknown' }
  $lib = $m.Groups[1].Value
  $key = "core:$lib"
  if (-not $cache.ContainsKey($key)) {
    $name = $null
    try {
      $info = [IO.File]::ReadAllText([IO.Path]::Combine($App.Ra.infoDir, "$lib.info"))
      $n = [regex]::Match($info, '^\s*corename\s*=\s*"([^"]+)"', 'Multiline')
      if ($n.Success) { $name = $n.Groups[1].Value }
    } catch {}
    $cache[$key] = if ($name) { $name } else { 'unknown' }
  }
  return $cache[$key]
}

# How the saves of a few games stand, by id: { status, files, newest, archive } for the
# games RetroArch runs. Only the games with saves here or in the archive are looked at
# one by one; the others have none.
function Get-LibrarySaves($games, [bool]$all) {
  $out = @{}
  $loc = Get-ArchiveLocation
  $root = Get-ArchiveRoot
  if (-not $App.Ra -or -not $root) { return $out }
  # Saves kept next to the games are looked for in the folders of those asked for
  $dirs = if ($App.Ra.saves.inContent -or $App.Ra.states.inContent) { [Atelier.Library]::ContentDirs($games) } else { $null }
  $found = Find-Saves $App.Ra @() $dirs
  $names = [Collections.Generic.HashSet[string]]::new([string[]]@($found.Keys), [StringComparer]::Ordinal)
  $archived = @{}
  foreach ($system in [Atelier.Library]::SystemsOf($games)) { $archived[$system] = Get-ArchivedSaveGames $root $system }
  $candidates = if ($all) { $games } else { [Atelier.Library]::WithSaves($games, $names, $archived) }
  foreach ($g in $candidates) {
    if (-not $g.Local) { continue }
    $system = $g.SystemName
    $inArchive = $archived[$system].Contains((Get-BaseOf $g.Rel))
    $core = Get-LibraryGameCore $system ($g.Field([Atelier.LibraryShape]::KeptAt('altemulator')))
    if ($null -eq $core) { continue }
    $game = Get-LocalGame $App.Es $system $g.Rel $App.GamelistEdits
    if (-not $game) { continue }
    $saves = Add-SavesCrc (Get-LocalSaves $found $game)
    $manifest = if ($inArchive) { Get-ArchivedSaves $root $game } else { $null }
    $newest = $null
    foreach ($f in $saves) { if ($f.mtime -gt $newest) { $newest = $f.mtime } }
    $archivedSaves = $null
    if ($manifest) { $archivedSaves = @{ saved = $manifest['saved']; device = $manifest['device']; files = @($manifest['files']).Count } }
    $out[$g.Id] = @{ status = Get-SavesStatus $saves $manifest $App.State['saves'][(Get-SavesKey $loc @{ system = $system; rel = $g.Rel })]; files = $saves.Count
      newest = $newest; archive = $archivedSaves }
  }
  return $out
}

# What the actions of a few games can do, with their saves
function Get-LibraryActions($games) {
  $a = [Atelier.Library]::Actions($games)
  $saves = Get-LibrarySaves $games $false
  $upload = @(foreach ($id in $saves.Keys) { if (@('local', 'local-newer', 'conflict') -ccontains $saves[$id].status) { $id } })
  $download = @(foreach ($id in $saves.Keys) { if (@('archive', 'archive-newer', 'conflict') -ccontains $saves[$id].status) { $id } })
  $conflicts = @(foreach ($id in $saves.Keys) { if ($saves[$id].status -ceq 'conflict') { @{ id = $id; archive = $saves[$id].archive } } })
  $a['upload-saves'] = @{ count = $upload.Count; ids = $upload }
  $a['download-saves'] = @{ count = $download.Count; ids = $download }
  $a['conflicts'] = $conflicts
  $a['kept'] = [Atelier.Library]::ArchivedInCollections($games)
  return $a
}

# Collections

function Save-CollectionsRecord { Write-Atomic $App.CollectionsFile (ConvertTo-JsonText $App.Record -Indent) }

# A collection as the page gets it: the games by key, and those its search found here
# when they were found with the search it has now
function Get-CollectionView($c) {
  $found = $App.Record['matches'][$c.id]
  $matches = $null
  if ($c.search -and $found -is [Collections.IDictionary] -and (Test-SameSearch (Read-CollectionSearch $found['search']) $c.search)) {
    $matches = [Atelier.CollectionGames]::Strings($found['games'])
  }
  return @{
    id = $c.id; name = $c.name; created = $c.created; updated = $c.updated; device = $c.device; search = $c.search
    games = $c.games.Keys()
    excluded = $c.excluded.Keys()
    matches = $matches
  }
}

# The searches' results of collections that are gone are forgotten
function Clear-StaleMatches {
  $ids = [Collections.Generic.HashSet[string]]::new([string[]]@(foreach ($c in $App.Collections) { $c.id }), [StringComparer]::Ordinal)
  foreach ($id in @($App.Record['matches'].Keys)) { if (-not $ids.Contains($id)) { [void]$App.Record['matches'].Remove($id) } }
}

# The collections of the archive, with what ES-DE's own editor changed in them since
# they were written; without an archive there are none to show, and ES-DE keeps those
# it has. The library works out what each holds.
function Read-CatalogueCollections([string]$root) {
  if (-not $root) {
    $App.Collections = $null
    [Atelier.Library]::SetCollections($null)
    return $null
  }
  [Threading.Monitor]::Enter($App.CollectionsLock)
  try {
    $collections = Read-Collections $root
    if (Sync-EsdeCollectionEdits $App.Es $root $collections $App.Record $App.Device) { $collections = Read-Collections $root }
    $App.Collections = $collections
    Clear-StaleMatches
    Save-CollectionsRecord
    $views = @(foreach ($c in $collections) { Get-CollectionView $c })
    [Atelier.Library]::SetCollections($views)
  } finally {
    [Threading.Monitor]::Exit($App.CollectionsLock)
  }
  return , $views
}

# What the searches of the collections that keep themselves up to date find here: kept
# for ES-DE, which shows them as they are on this computer
function Update-CollectionMatches {
  if (-not $App.MatchesFound) { $App.MatchesFound = [hashtable]::Synchronized(@{}) }
  $version = [Atelier.Library]::Version
  foreach ($c in @($App.Collections)) {
    if (-not $c -or -not $c.search) { continue }
    # A search is run again only once the library or the search changed since
    $stamp = "$version|$(ConvertTo-JsonText $c.search)"
    $before = $App.Record['matches'][$c.id]
    if ($before -is [Collections.IDictionary] -and $App.MatchesFound[$c.id] -ceq $stamp) { continue }
    $games = [Atelier.Library]::SearchIds($c.search).ToArray()
    $App.MatchesFound[$c.id] = $stamp
    $same = $before -is [Collections.IDictionary] -and (Test-SameSearch (Read-CollectionSearch $before['search']) $c.search) -and (Test-SameLines $before['games'] $games)
    if ($same) { continue }
    $App.Record['matches'][$c.id] = [ordered]@{ search = $c.search; games = $games }
    $App.CollectionsTouched = $true
  }
}

function Find-Collection([string]$id) {
  foreach ($c in @($App.Collections)) { if ($c -and $c.id -ceq $id) { return $c } }
  return $null
}

# Games of the library for a collection's file: CollectionGames takes them as they are
function Get-CollectionEntries($games) { return , $games }

# What adding games to a collection does: those not in it yet are added, or let in again
# when its search finds them and they were taken out; those only in ES-DE are to be
# archived, so that every computer can have them. $ids, or what a search finds.
function Get-AddPlan($c, $body) {
  # Assigned, not taken from an if, which would go through every id
  $ids = $body['ids']
  if ($body['query'] -is [Collections.IDictionary]) { $ids = [Atelier.Library]::SearchOutside($body['query'], $(if ($body['where']) { [string]$body['where'] } else { 'all' }), $c.id) }
  $matched = $null
  if ($c.search) { $matched = [Atelier.Library]::SearchIds($c.search) }
  return [Atelier.CollectionGames]::AddPlan($c.id, $ids, $matched, $c.excluded)
}

# What the page asks of the collections: create, update, delete, add games, take games
# out, keep up with a search or stop, take out the games no longer anywhere, or the games
# the searches find on this computer (matches). plan-add and plan-auto only tell what
# adding or keeping up would archive.
function Invoke-CollectionsRequest($body) {
  $loc = Get-ArchiveLocation
  if (-not $loc -or -not (Test-Archive $loc['path'])) { return @{ ok = $false; reason = 'archive' } }
  $root = $loc['path']
  $answer = @{ ok = $true }
  [Threading.Monitor]::Enter($App.CollectionsLock)
  try {
    $action = [string]$body['action']
    $c = Find-Collection ([string]$body['id'])
    $changed = $true
    switch ($action) {
      'matches' { Update-CollectionMatches; $changed = $false }
      'create' { $answer.id = (New-Collection $root ([string]$body['name']) @() $body['search'] $App.Device).id }
      'update' { if (-not (Update-Collection $root $body $App.Device)) { $answer.ok = $false; $answer.reason = 'gone' } }
      'delete' { Remove-Collection $root ([string]$body['id']) }
      { $_ -in 'plan-add', 'add' } {
        if (-not $c) { $answer.ok = $false; $answer.reason = 'gone'; break }
        $plan = Get-AddPlan $c $body
        $answer.fresh = $plan.fresh.Count
        $answer.archive = @{ count = $plan.archive.Count; size = $plan.archiveSize; media = $plan.archiveMedia
          first = $(if ($plan.archive.Count) { $plan.archive[0].Name } else { $null }) }
        $answer.names = @(for ($i = 0; $i -lt [Math]::Min(3, $plan.fresh.Count); $i++) { $plan.fresh[$i].Name })
        if ($action -ceq 'plan-add' -or -not $plan.fresh.Count) { $changed = $false; break }
        if (-not (Update-Collection $root @{ id = $c.id; add = (Get-CollectionEntries $plan.add); include = [string[]]$plan.include } $App.Device)) { $answer.ok = $false; $answer.reason = 'gone' }
        elseif ($plan.archive.Count) { [void](Add-CatalogueJobs 'archive' $plan.archive $false) }
      }
      'take-out' {
        if (-not $c) { $answer.ok = $false; $answer.reason = 'gone'; break }
        $ids = [Atelier.CollectionGames]::Strings($body['ids'])
        $games = [Atelier.Library]::Pick($ids, $null, $null)
        $matched = $null
        if ($c.search) { $matched = [Atelier.Library]::SearchIds($c.search) }
        # Those added by hand leave it, those its search finds are kept out
        $remove = $c.games.Having($ids, $true)
        $exclude = [Atelier.CollectionGames]::Among($games, $matched, $true)
        $answer.count = $ids.Count
        $answer.names = @(for ($i = 0; $i -lt [Math]::Min(3, $games.Count); $i++) { $games[$i].Name })
        if (-not (Update-Collection $root @{ id = $c.id; remove = $remove; exclude = $exclude } $App.Device)) { $answer.ok = $false; $answer.reason = 'gone' }
      }
      { $_ -in 'plan-auto', 'auto' } {
        if (-not $c) { $answer.ok = $false; $answer.reason = 'gone'; break }
        $search = Read-CollectionSearch $body['search']
        $found = [Atelier.CollectionGames]::OnlyInEsde([Atelier.Library]::Pick([Atelier.Library]::SearchIds($search), $null, $null), [Atelier.Library]::CollectionIds($c.id, 'all'))
        $archive = $found['games']
        $answer.archive = @{ count = $archive.Count; size = $found['size']; media = $found['media']
          first = $(if ($archive.Count) { $archive[0].Name } else { $null }) }
        if ($action -ceq 'plan-auto') { $changed = $false; break }
        if (-not (Update-Collection $root @{ id = $c.id; search = $search } $App.Device)) { $answer.ok = $false; $answer.reason = 'gone' }
        elseif ($archive.Count) { [void](Add-CatalogueJobs 'archive' $archive $false) }
      }
      'stop-auto' {
        if (-not $c) { $answer.ok = $false; $answer.reason = 'gone'; break }
        # The games it holds now stay, added by hand
        $ids = $c.excluded.Having([Atelier.Library]::SearchIds($c.search), $false)
        $keep = [Atelier.Library]::Pick($c.games.Having($ids, $false), $null, $null)
        if (-not (Update-Collection $root @{ id = $c.id; add = (Get-CollectionEntries $keep); clearSearch = $true } $App.Device)) { $answer.ok = $false; $answer.reason = 'gone' }
      }
      'drop-missing' {
        if (-not $c) { $answer.ok = $false; $answer.reason = 'gone'; break }
        $remove = $c.games.Missing()
        if (-not (Update-Collection $root @{ id = $c.id; remove = $remove } $App.Device)) { $answer.ok = $false; $answer.reason = 'gone' }
      }
      default { throw "Unknown action $action" }
    }
    if ($changed) {
      $App.Collections = Read-Collections $root
      Clear-StaleMatches
      [Atelier.Library]::SetCollections(@(foreach ($x in $App.Collections) { Get-CollectionView $x }))
      Update-CollectionMatches
      $App.CollectionsTouched = $true
    }
    Save-CollectionsRecord
  } catch {
    $answer = @{ ok = $false; reason = 'failed'; message = Get-ErrorMessage $_ }
  } finally {
    [Threading.Monitor]::Exit($App.CollectionsLock)
  }
  $answer.collections = [Atelier.Json]::Read([Atelier.Library]::CollectionsSummary())['collections']
  return $answer
}

# The transfers: one at a time, the rest waiting in JobQueue (native.cs), each game once
# per transfer however many are asked for. A batch is what was asked for since the queue
# was last empty, for the progress of the whole. The catalog's changes are written every
# few seconds while a batch runs, and at its end, when the gamelists of ES-DE are written
# too (Complete-CatalogueBatch). The library reads each game again after its transfer;
# after a download or a removal from ES-DE, once its gamelist is written.
function Add-CatalogueJobs([string]$action, $games, [bool]$force) {
  $jobs = $App.Jobs
  [Threading.Monitor]::Enter($jobs)
  try {
    if (-not $jobs.current -and -not [Atelier.JobQueue]::Count) { $jobs.batch = @{ total = 0; done = 0 } }
    $added = [Atelier.JobQueue]::Add($action, $games, $force)
    $jobs.batch.total += $added
    if ($added -and -not $jobs.running) {
      $jobs.running = $true
      Add-BackgroundTask @{ kind = 'queue' }
    }
    return $added
  } finally {
    [Threading.Monitor]::Exit($jobs)
  }
}

function Invoke-CatalogueQueue {
  $jobs = $App.Jobs
  $written = [DateTime]::UtcNow
  # Written every 2 seconds, or less often when writing takes long: the parts of a large
  # system are large, and the transfers come first
  $every = 2.0
  $complete = $false
  while ($true) {
    $job = $null
    [Threading.Monitor]::Enter($jobs)
    try {
      $job = [Atelier.JobQueue]::Take()
      if ($job) {
        $jobs.current = $job
        $jobs.signal = [hashtable]::Synchronized(@{ aborted = $false })
        $signal = $jobs.signal
        $complete = $false
      } elseif ($complete) {
        $jobs.current = $null
        $jobs.signal = $null
        $jobs.running = $false
        return
      }
    } finally {
      [Threading.Monitor]::Exit($jobs)
    }
    # The queue is empty: what waits is written before the page hears the batch ended,
    # and transfers asked for meanwhile follow
    if (-not $job) {
      Complete-CatalogueBatch
      $complete = $true
      continue
    }
    try {
      Invoke-CatalogueJob $job $signal
      $job.result = 'done'
    } catch {
      $code = Get-ErrorCode $_
      $job.result = if ($code -ceq 'aborted') { 'stopped' } elseif ($code -ceq 'conflict') { 'conflict' } else { 'failed' }
      $job.error = Get-ErrorMessage $_
      $job.errorCode = $code
      $detail = (Get-InnerError $_).Detail
      if ($detail) { $job.other = @{ device = $detail['device']; saved = $detail['saved']; file = $detail['file']; name = $detail['name'] } }
      if ($code -cne 'aborted' -and $code -cne 'conflict') { Write-Failure "$($job.action) $($job.id): $($job.error)" }
    }
    # The library follows: at once, or once the gamelist is written
    if (@('download', 'remove-local') -ccontains $job.action) { $App.LaterUpdates.Add(@($job.system, $job.rel)) }
    else { try { [void][Atelier.Library]::Update($job.system, $job.rel) } catch { Write-Failure "library: $(Get-ErrorMessage $_)" } }
    if (([DateTime]::UtcNow - $written).TotalSeconds -ge $every) {
      $took = [Diagnostics.Stopwatch]::StartNew()
      try { Save-ArchiveCatalog $null } catch { Write-Failure "catalog: $(Get-ErrorMessage $_)" }
      $every = [Math]::Min(15.0, [Math]::Max(2.0, 4 * $took.Elapsed.TotalSeconds))
      $written = [DateTime]::UtcNow
    }
    [Threading.Monitor]::Enter($jobs)
    try {
      $job.finished = Get-NowMs
      $jobs.batch.done++
      $jobs.done.Add($job)
      while ($jobs.done.Count -gt 200) { $jobs.done.RemoveAt(0) }
      $jobs.version++
    } finally {
      [Threading.Monitor]::Exit($jobs)
    }
  }
}

# The end of a batch: the catalog's changes still waiting are written, then the gamelists
# of ES-DE, each in one pass, and the games downloaded or removed from ES-DE are read
# again
function Complete-CatalogueBatch {
  try { Save-ArchiveCatalog $null } catch { Write-Failure "catalog: $(Get-ErrorMessage $_)" }
  Save-GamelistEdits $App.Es $App.GamelistEdits
  $later = @($App.LaterUpdates)
  $App.LaterUpdates.Clear()
  foreach ($u in $later) { try { [void][Atelier.Library]::Update($u[0], $u[1]) } catch { Write-Failure "library: $(Get-ErrorMessage $_)" } }
}

function Stop-CatalogueJobs {
  $jobs = $App.Jobs
  [Threading.Monitor]::Enter($jobs)
  try {
    $jobs.batch.total -= [Atelier.JobQueue]::Clear()
    if ($jobs.signal) { $jobs.signal.aborted = $true }
  } finally {
    [Threading.Monitor]::Exit($jobs)
  }
}

# A transfer, on its game as ES-DE and the archive have it now (the library may be older
# than the job): only that game is read again, with its media looked up file by file
function Invoke-CatalogueJob($job, $signal) {
  $loc = Get-ArchiveLocation
  if (-not $loc -or -not (Test-Archive $loc['path'])) { throw "The archive can't be reached" }
  $ctx = @{ es = $App.Es; root = $loc['path']; device = $App.Device; tempDir = $App.TempDir; ra = $App.Ra; dataDir = $App.DataDir
    edits = $App.GamelistEdits }
  $progress = @{ job = $job; steps = $CatalogueSteps[$job.action] }
  if (-not $progress.steps) { $progress.steps = @{} }
  $archived = Get-ArchivedEntry $ctx.root $job.system $job.rel
  $game = Get-LocalGame $App.Es $job.system $job.rel $App.GamelistEdits
  switch ($job.action) {
    'archive' {
      if (-not $game) { throw 'The game is no longer in ES-DE' }
      $index = Get-MediaIndex $App.Es $job.system $job.rel
      if ($archived -and (Test-SameFiles $game $archived $App.State['crcs'])) {
        Set-Progress $progress 'media' 0
        Update-ArchivedDetails $ctx $game $index $App.Config['archiveMedia']
      } else {
        Save-GameToArchive $ctx $game $index $App.Config['archiveMedia'] $progress $signal
      }
    }
    # The details and media of a game as ES-DE has them now, into the archive: the one
    # way new details travel. The zip stays as it is; a game whose files changed since is
    # archived again instead.
    'update-details' {
      if (-not $game) { throw 'The game is no longer in ES-DE' }
      if (-not $archived) { throw 'The game is not in the archive' }
      if (-not (Test-SameFiles $game $archived $App.State['crcs'])) { throw 'The files of the game changed: archive it again' }
      Set-Progress $progress 'media' 0
      Update-ArchivedDetails $ctx $game (Get-MediaIndex $App.Es $job.system $job.rel) $App.Config['archiveMedia']
    }
    'download' {
      if (-not $archived) { throw 'The game is no longer in the archive' }
      $filesToo = -not $game -or -not (Test-SameFiles $game $archived $App.State['crcs'])
      Get-GameFromArchive $ctx $job.system $job.rel $game $filesToo $App.Config['downloadMedia'] $progress $signal
      $App.Jobs.esdeChanged = $true
    }
    'remove-local' {
      if ($game) { Remove-LocalGame $App.Es $game $App.GamelistEdits }
      $App.Jobs.esdeChanged = $true
    }
    'remove-archive' { Remove-ArchivedGame $ctx $job.system $job.rel }
    { $_ -in 'upload-saves', 'download-saves' } {
      if (-not $App.Ra) { throw 'RetroArch was not found' }
      if (-not $game) { throw 'The game is not in ES-DE' }
      Set-Progress $progress 'saves' 0
      $found = Find-Saves $App.Ra @($game)
      $key = Get-SavesKey $loc $job
      $last = $App.State['saves'][$key]
      $App.State['saves'][$key] = if ($job.action -ceq 'upload-saves') { Send-Saves $ctx $game $found $last $job.force } else { Receive-Saves $ctx $game $found $last $job.force }
      Save-CatalogueState
    }
    default { throw "Unknown action $($job.action)" }
  }
}

function Get-JobView($j) {
  if (-not $j) { return $null }
  return @{ action = $j.action; id = $j.id; name = $j.name; size = $j.size; step = $j.step; fraction = $j.fraction
    result = $j.result; error = $j.error; errorCode = $j.errorCode; other = $j.other; finished = $j.finished }
}

# How the transfers go: the one under way, the first waiting and how many wait, those
# done since a time, which of the games watched (those the page shows) wait, and the
# library's version, which changes as the transfers change the games
function Get-JobStatus([double]$since, $watch) {
  $jobs = $App.Jobs
  $watched = [Collections.Generic.List[string]]::new()
  if ($watch -is [Collections.IList]) { foreach ($id in $watch) { if ($id) { $watched.Add([string]$id) } } }
  [Threading.Monitor]::Enter($jobs)
  try {
    return @{
      version = $jobs.version
      library = [Atelier.Library]::Version
      batch = @{ total = $jobs.batch.total; done = $jobs.batch.done }
      current = Get-JobView $jobs.current
      queue = @(foreach ($j in [Atelier.JobQueue]::First(20)) { Get-JobView $j })
      queued = [Atelier.JobQueue]::Count
      waiting = @([Atelier.JobQueue]::Waiting($watched))
      done = @(foreach ($j in $jobs.done) { if ($j.finished -gt $since) { Get-JobView $j } })
      esdeChanged = [bool]$jobs.esdeChanged
      collectionsChanged = [bool]$App.CollectionsTouched
      # Whether leaving restarts ES-DE, so that it shows the changes by itself
      restartsEsde = [bool]$App.EsdeProcess
    }
  } finally {
    [Threading.Monitor]::Exit($jobs)
  }
}

# Settings

function Set-ArchiveLocation($body) {
  $chosen = [string]$body['path']
  if (-not $chosen) { return @{ ok = $false; reason = 'missing' } }
  $access = Test-Access $chosen
  if (-not $access.ok) { return $access }
  # A folder inside an archive, such as a system's, stands for the archive
  $dir = $chosen
  for ($at = $chosen; (Get-Parent $at) -ne $at; $at = Get-Parent $at) {
    if (Test-Archive $at) { $dir = $at; break }
  }
  if (-not (Test-Archive $dir)) {
    if (-not $body['create']) { return @{ ok = $false; reason = 'not-archive' } }
    try { New-Archive $dir } catch { return @{ ok = $false; reason = 'denied'; message = Get-ErrorMessage $_ } }
  }
  [Threading.Monitor]::Enter($App.Lock)
  try {
    $locations = $App.Config['locations']
    $known = -1
    for ($i = 0; $i -lt $locations.Count; $i++) {
      if ([IO.Path]::GetFullPath([string]$locations[$i]['path']).TrimEnd('\') -eq [IO.Path]::GetFullPath($dir).TrimEnd('\')) { $known = $i; break }
    }
    if ($known -ge 0) { $App.Config['current'] = $known }
    else {
      $locations.Add([ordered]@{ path = $dir })
      $App.Config['current'] = $locations.Count - 1
    }
  } finally {
    [Threading.Monitor]::Exit($App.Lock)
  }
  Save-CatalogueConfig
  return @{ ok = $true }
}

function Save-CatalogueSettings($body) {
  $pick = {
    param($list)
    if (-not ($list -is [Collections.IList])) { return $null }
    $kept = New-List
    foreach ($t in $list) { if ($LibraryMediaTypes -ccontains $t) { $kept.Add([string]$t) } }
    , $kept
  }
  [Threading.Monitor]::Enter($App.Lock)
  try {
    $c = $App.Config
    $archiveMedia = & $pick $body['archiveMedia']
    if ($null -ne $archiveMedia) { $c['archiveMedia'] = $archiveMedia }
    $downloadMedia = & $pick $body['downloadMedia']
    if ($null -ne $downloadMedia) { $c['downloadMedia'] = $downloadMedia }
    if (@('grid', 'list', 'covers') -ccontains $body['mode']) { $c['mode'] = $body['mode'] }
    if (@('name', 'size', 'recent') -ccontains $body['sort']) { $c['sort'] = $body['sort'] }
    if (@('archive', 'local', 'unarchived') -ccontains $body['section']) { $c['section'] = $body['section'] }
    $locations = $c['locations']
    if ($body['current'] -is [int] -and $body['current'] -ge 0 -and $body['current'] -lt $locations.Count) { $c['current'] = $body['current'] }
    if ($body['forget'] -is [int] -and $body['forget'] -ge 0 -and $body['forget'] -lt $locations.Count) {
      $locations.RemoveAt($body['forget'])
      $c['current'] = [Math]::Min([int]$c['current'], [Math]::Max(0, $locations.Count - 1))
    }
  } finally {
    [Threading.Monitor]::Exit($App.Lock)
  }
  Save-CatalogueConfig
}

function Read-Body($ctx, [int]$limit) {
  $body = Read-RequestJson $ctx $limit
  if (-not ($body -is [Collections.IDictionary])) { $body = @{} }
  return $body
}

# A request's query, in the UTF-8 the page writes it in: the listener's own QueryString
# reads it in the computer's encoding, which loses the games with accents in their names
function Get-RequestQuery([Uri]$url) {
  $query = @{}
  foreach ($pair in $url.Query.TrimStart('?').Split('&')) {
    $at = $pair.IndexOf('=')
    if ($at -gt 0) { $query[[Uri]::UnescapeDataString($pair.Substring(0, $at))] = [Uri]::UnescapeDataString($pair.Substring($at + 1).Replace('+', ' ')) }
  }
  return $query
}

function Get-QueryInt($query, [string]$name, [int]$fallback) {
  $n = 0
  if ([int]::TryParse([string]$query[$name], [ref]$n)) { return $n }
  return $fallback
}

# Lists of ids can name a whole platform of games
$CatalogueBodyLimit = 64000000

function Invoke-CatalogueRequest($ctx) {
  $route = [Uri]::UnescapeDataString($ctx.Request.Url.AbsolutePath)
  $post = $ctx.Request.HttpMethod -eq 'POST'
  $query = Get-RequestQuery $ctx.Request.Url
  if (Invoke-StaticRoute $ctx $route $CatalogueUi) { return }
  if ($route -ceq '/api/session') { return Send-Response $ctx 200 (Get-CatalogueSession) }
  # The archive, the platforms and the collections; summary leaves out the archive
  if ($route -ceq '/api/library') { return Send-Response $ctx 200 (Get-LibraryAnswer $true) }
  if ($route -ceq '/api/summary') { return Send-Response $ctx 200 (Get-LibraryAnswer $false) }
  # A page of a platform's games in a section, or of a collection's
  if ($route -ceq '/api/games') {
    $from = Get-QueryInt $query 'from' 0
    $count = Get-QueryInt $query 'count' 120
    $sort = if ($query['sort']) { [string]$query['sort'] } else { 'name' }
    $at = if ($query['at']) { [string]$query['at'] } else { $null }
    if ($query['collection']) {
      $where = if ($query['where']) { [string]$query['where'] } else { 'all' }
      return Send-Response $ctx 200 ([Atelier.Library]::CollectionList([string]$query['collection'], $where, $sort, $from, $count, $at))
    }
    $section = if ($query['section']) { [string]$query['section'] } else { 'archive' }
    return Send-Response $ctx 200 ([Atelier.Library]::List([string]$query['system'], $section, $sort, $from, $count, $at))
  }
  # Everything shown of the chosen game, with its saves
  if ($route -ceq '/api/game') {
    $d = [Atelier.Library]::Details([string]$query['id'])
    if (-not $d) { return Send-NotFound $ctx }
    $d['saves'] = $null
    if ($d.local) {
      $saves = Get-LibrarySaves ([Atelier.Library]::Pick(@([string]$query['id']), $null, $null)) $true
      $d['saves'] = $saves[[string]$query['id']]
    }
    return Send-Response $ctx 200 $d
  }
  # What can be done with a few games, a platform's section or a collection's games
  if ($route -ceq '/api/actions' -and $post) {
    return Send-Response $ctx 200 (Get-LibraryActions (Select-Games (Read-Body $ctx $CatalogueBodyLimit)))
  }
  # The ids of a platform's section or a collection's games, to mark them all
  if ($route -ceq '/api/ids' -and $post) {
    $body = Read-Body $ctx 20000
    if ($body['collection']) {
      return Send-Response $ctx 200 (ConvertTo-JsonText ([string[]]@([Atelier.Library]::CollectionIds([string]$body['collection'], $(if ($body['where']) { [string]$body['where'] } else { 'all' })))))
    }
    return Send-Response $ctx 200 ([Atelier.Library]::Ids([string]$body['system'], [string]$body['section'], [string]$body['sort']))
  }
  # The collections' search: a page of what it finds, and the values of its filters
  if ($route -ceq '/api/search' -and $post) {
    $body = Read-Body $ctx 200000
    $search = if ($body['query'] -is [Collections.IDictionary]) { $body['query'] } else { @{} }
    $where = if ($body['where']) { [string]$body['where'] } else { 'all' }
    $collection = if ($body['collection']) { [string]$body['collection'] } else { $null }
    return Send-Response $ctx 200 ([Atelier.Library]::Search($search, $where, [int]$body['from'], $(if ($body['count']) { [int]$body['count'] } else { 120 }), $collection))
  }
  if ($route -ceq '/api/facets' -and $post) {
    $body = Read-Body $ctx 200000
    $search = if ($body['query'] -is [Collections.IDictionary]) { $body['query'] } else { @{} }
    return Send-Response $ctx 200 ([Atelier.Library]::FacetValues($search, $(if ($body['where']) { [string]$body['where'] } else { 'all' })))
  }
  # A game's media: /media/<type>/<id>
  $m = [regex]::Match($route, '^/media/(\w+)/(.+)$')
  if ($m.Success) {
    $file = [Atelier.Library]::MediaPath($m.Groups[2].Value, $m.Groups[1].Value)
    if ($file) { return Send-Stream $ctx $file 600 }
    return Send-NotFound $ctx
  }
  if ($route -ceq '/api/jobs' -and $post) {
    $body = Read-Body $ctx $CatalogueBodyLimit
    $action = [string]$body['action']
    $games = Select-Games $body
    # Of the saves, only the games whose saves can go that way
    if (@('upload-saves', 'download-saves') -ccontains $action -and -not $body['force']) {
      $a = Get-LibraryActions $games
      $games = [Atelier.Library]::Pick($a[$action].ids, $null, $null)
    }
    [void](Add-CatalogueJobs $action $games ([bool]$body['force']))
    return Send-Response $ctx 200 (Get-JobStatus ([double]$body['since']) $body['watch'])
  }
  if ($route -ceq '/api/jobs') {
    $since = 0.0
    [void][double]::TryParse([string]$query['since'], [ref]$since)
    $watch = [string[]]@()
    if ($query['watch']) { $watch = [string[]]([string]$query['watch']).Split("`n") }
    return Send-Response $ctx 200 (Get-JobStatus $since $watch)
  }
  if ($route -ceq '/api/jobs/status' -and $post) {
    $body = Read-Body $ctx 200000
    return Send-Response $ctx 200 (Get-JobStatus ([double]$body['since']) $body['watch'])
  }
  if ($route -ceq '/api/stop' -and $post) {
    Stop-CatalogueJobs
    return Send-Response $ctx 200 @{ ok = $true }
  }
  if ($route -ceq '/api/drives') {
    $known = New-List
    $locations = $App.Config['locations']
    for ($i = 0; $i -lt $locations.Count; $i++) { $known.Add(@{ index = $i; path = $locations[$i]['path'] }) }
    return Send-Response $ctx 200 @{ drives = Get-Drives; locations = $known }
  }
  if ($route -ceq '/api/folders') {
    $dir = [string]$query['path']
    try {
      return Send-Response $ctx 200 (Get-Folders $dir)
    } catch {
      $answer = Test-Access $dir
      $answer.error = $true
      return Send-Response $ctx 200 $answer
    }
  }
  if ($route -ceq '/api/location' -and $post) {
    $answer = Set-ArchiveLocation (Read-Body $ctx 2000)
    if ($answer.ok) { Start-LibraryBuild }
    return Send-Response $ctx 200 $answer
  }
  if ($route -ceq '/api/connect' -and $post) {
    $body = Read-Body $ctx 2000
    $share = Get-ShareOf ([string]$body['share'])
    if (-not $share) { return Send-Response $ctx 200 @{ ok = $false; reason = 'failed' } }
    $answer = Connect-Share $share ([string]$body['user']) ([string]$body['password']) ([bool]$body['remember'])
    if ($answer.ok) { Start-LibraryBuild }
    return Send-Response $ctx 200 $answer
  }
  if ($route -ceq '/api/settings' -and $post) {
    $before = @{ current = [int]$App.Config['current']; media = (@($App.Config['archiveMedia']) -join ',') }
    Save-CatalogueSettings (Read-Body $ctx 20000)
    # Another archive, or other media telling whether a game is the archive's
    if ($before.current -ne [int]$App.Config['current'] -or $before.media -cne (@($App.Config['archiveMedia']) -join ',')) { Start-LibraryBuild }
    return Send-Response $ctx 200 @{ ok = $true; config = (Get-CatalogueSession).config }
  }
  if ($route -ceq '/api/collections' -and $post) {
    return Send-Response $ctx 200 (Invoke-CollectionsRequest (Read-Body $ctx $CatalogueBodyLimit))
  }
  if ($route -ceq '/api/media-sizes') { return Send-Response $ctx 200 ([Atelier.Library]::MediaSizes()) }
  if ($route -ceq '/api/quit' -and $post) {
    Send-Response $ctx 200 @{ ok = $true }
    return Request-Quit
  }
  Send-NotFound $ctx
}

# The work of the background runspace: reading the library, finding the ES-DE that
# started the app, the theme's look and the transfers
function Invoke-CatalogueBackground($task) {
  switch ($task.kind) {
    'warm-up' {
      # What older versions left in the media folders goes first, then the library: a
      # large one takes a few seconds to read
      try { [void](Remove-PlatformBanners $App.Es) } catch { Write-Failure "banners: $(Get-ErrorMessage $_)" }
      Start-LibraryBuild
      [void](Get-ThemeSession $App.Es)
      $App.EsdeProcess = Find-Esde
    }
    'queue' { Invoke-CatalogueQueue }
  }
}

# A transfer stopped halfway leaves nothing behind, and ES-DE starts again when its
# games or the collections changed. The collections are written into ES-DE while it is
# closed, by the process that restarts it; when the app wasn't started by ES-DE they
# are written at once, and ES-DE shows them once it is restarted.
function Invoke-CatalogueExit {
  Stop-CatalogueJobs
  # The zip of a stopped transfer is ended within a moment; the end of its batch writes
  # the catalog and the gamelists, which ES-DE must find written as it starts again
  $end = [DateTime]::UtcNow.AddSeconds(60)
  while ($App.Jobs.running -and [DateTime]::UtcNow -lt $end) { Start-Sleep -Milliseconds 50 }
  if (-not $App.Jobs.running) {
    try { Save-ArchiveCatalog $null } catch { Write-Failure "catalog: $(Get-ErrorMessage $_)" }
    Save-GamelistEdits $App.Es $App.GamelistEdits
  }
  Save-CatalogueState
  $plan = $null
  try {
    $App.Es.settings = [IO.File]::ReadAllText([IO.Path]::Combine($App.Es.home, 'settings', 'es_settings.xml'))
    [Threading.Monitor]::Enter($App.CollectionsLock)
    try { $plan = Get-EsdeCollectionsPlan $App.Es $App.AppArg $App.Collections $App.Record } finally { [Threading.Monitor]::Exit($App.CollectionsLock) }
  } catch {
    Write-Failure "collections: $(Get-ErrorMessage $_)"
  }
  $collectionsChanged = $plan -and $plan.changed
  if (($App.Jobs.esdeChanged -or $collectionsChanged) -and $App.EsdeProcess) {
    if ($collectionsChanged) {
      $planFile = [IO.Path]::Combine($App.DataDir, 'collections-plan.json')
      Write-Atomic $planFile (ConvertTo-JsonText @{ app = $plan.app; written = $plan.written; remove = $plan.remove; record = $App.CollectionsFile })
      $App.EsdeProcess.plan = $planFile
    }
    Restart-Esde $App.EsdeProcess
    $App.EsdeProcess = $null
  } elseif ($collectionsChanged) {
    try { Invoke-EsdeCollectionsPlan $plan $App.CollectionsFile } catch { Write-Failure "collections: $(Get-ErrorMessage $_)" }
  }
  # The archive's gamelists of the systems whose catalog changed, while ES-DE starts
  try { [Atelier.Catalog]::WriteGamelists($LibraryFields) } catch { Write-Failure "gamelists of the archive: $(Get-ErrorMessage $_)" }
  try { [Atelier.Catalog]::SaveCaches() } catch { Write-Failure "copy of the catalog: $(Get-ErrorMessage $_)" }
}

if ($MyInvocation.InvocationName -ne '.') {
  $argv = $args
  $App = [hashtable]::Synchronized(@{})
  $App.AppArg = Get-CliArg $argv '--app'
  try {
    $App.Es = Open-Esde $App.AppArg
  } catch {
    Write-Failure (Get-ErrorMessage $_)
    exit 1
  }
  $App.DataDir = Get-CatalogueDataDir $App.Es (Get-CliArg $argv '--data')
  $App.TempDir = [IO.Path]::Combine($App.DataDir, 'tmp')
  $App.ConfigFile = [IO.Path]::Combine($App.DataDir, 'config.json')
  $App.StateFile = [IO.Path]::Combine($App.DataDir, 'state.json')
  $App.Device = Get-DeviceName
  $App.Ra = Get-RetroArchSaves $App.Es
  $App.EsdeProcess = $null

  $config = [ordered]@{ locations = (New-List); current = 0; archiveMedia = [Collections.Generic.List[object]]$LibraryMediaTypes
    downloadMedia = [Collections.Generic.List[object]]$LibraryMediaTypes; mode = 'grid'; sort = 'name'; section = 'archive' }
  $saved = Read-JsonFile $App.ConfigFile $null
  if ($saved -is [Collections.IDictionary]) { foreach ($k in @($saved.Keys)) { $config[$k] = $saved[$k] } }
  if (-not ($config['locations'] -is [Collections.IList])) { $config['locations'] = New-List }
  $App.Config = $config
  $state = [ordered]@{ crcs = [Collections.Concurrent.ConcurrentDictionary[string, object]]::new(); saves = [Collections.Concurrent.ConcurrentDictionary[string, object]]::new() }
  $savedState = Read-JsonFile $App.StateFile $null
  if ($savedState -is [Collections.IDictionary]) {
    foreach ($k in @($savedState.Keys)) {
      if ($state.Contains($k) -and $savedState[$k] -is [Collections.IDictionary]) { foreach ($e in @($savedState[$k].Keys)) { $state[$k][$e] = $savedState[$k][$e] } }
      elseif (-not $state.Contains($k)) { $state[$k] = $savedState[$k] }
    }
  }
  $App.State = $state
  # The changes to the gamelists a previous run left waiting
  $App.GamelistEdits = New-GamelistEdits ([IO.Path]::Combine($App.DataDir, 'gamelist-edits.jsonl'))
  try { Restore-GamelistEdits $App.Es $App.GamelistEdits } catch { Write-Failure "gamelists: $(Get-ErrorMessage $_)" }

  $App.Lock = [object]::new()
  Initialize-CatalogueLibrary
  $App.CoreCache = [hashtable]::Synchronized(@{})
  $App.LaterUpdates = [Collections.ArrayList]::Synchronized([Collections.ArrayList]::new())
  $App.Jobs = [hashtable]::Synchronized(@{ current = $null; done = (New-List); signal = $null; running = $false
    esdeChanged = $false; version = 0; batch = @{ total = 0; done = 0 } })
  $App.CollectionsFile = [IO.Path]::Combine($App.DataDir, 'collections.json')
  $App.Record = Read-CollectionsRecord $App.CollectionsFile
  $App.Collections = $null
  $App.CollectionsLock = [object]::new()
  $App.CollectionsTouched = $false
  Start-AppServer -name 'Catalogue' -script $PSCommandPath -handler 'Invoke-CatalogueRequest' -dataDir $App.DataDir -argv $argv `
    -background 'Invoke-CatalogueBackground' -beforeExit 'Invoke-CatalogueExit' -tasks @(@{ kind = 'warm-up' })
}
