const API_BASE = '/api/v1.0';
let token = localStorage.getItem('admin_token') || null;
let currentPage = 'dashboard';
let buildsTimer = null;
let buildLogTimer = null;
let searchTimer = null;
let userQuery = '';
let serverBuilds = []; // completed builds offered by the server-modal build picker

// ---------- helpers ----------

function esc(value) {
    if (value === null || value === undefined) return '';
    return String(value)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

function val(id) {
    const el = document.getElementById(id);
    return el ? String(el.value || '').trim() : '';
}

function fmtDate(unixSeconds) {
    if (!unixSeconds) return 'Never';
    const d = new Date(Number(unixSeconds) * 1000);
    return isNaN(d.getTime()) ? 'Never' : d.toLocaleString();
}

function fmtBanExpiry(unixSeconds) {
    if (!unixSeconds) return 'permanent';
    return 'until ' + fmtDate(unixSeconds);
}

// Build failure messages are prefixed with an attempt counter ("2|error text").
function cleanError(message) {
    if (!message) return '';
    const parts = String(message).split('|');
    return parts.length > 1 ? parts.slice(1).join('|') : message;
}

// ---------- byte / size / log-time formatting ----------

function fmtBytes(bytes) {
    if (bytes === null || bytes === undefined) return '—';
    if (bytes >= 1073741824) return (bytes / 1073741824).toFixed(2) + ' GB';
    if (bytes >= 1048576) return (bytes / 1048576).toFixed(1) + ' MB';
    if (bytes >= 1024) return (bytes / 1024).toFixed(1) + ' KB';
    return bytes + ' B';
}

function fmtSpeed(bytesPerSecond) {
    if (!bytesPerSecond || bytesPerSecond <= 0) return '';
    return fmtBytes(bytesPerSecond) + '/s';
}

// ISO timestamps from System.Text.Json include 7 fractional digits; JS Date handles 3,
// so normalize before parsing to stay safe across browsers.
function fmtLogTime(value) {
    if (!value) return '';
    const d = new Date(String(value).replace(/(\.\d{3})\d+/, '$1'));
    return isNaN(d.getTime()) ? '' : d.toLocaleString();
}

// ---------- toast notifications ----------
//
// The admin panel never uses blocking toast('error', ): errors and confirmations slide in from
// the top of the screen (same pattern as the launcher's toast) and disappear on their own.

const TOAST_ICONS = {
    error: '<svg class="toast-ico" viewBox="0 0 24 24"><circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12.5"/><line x1="12" y1="16" x2="12.01" y2="16"/></svg>',
    success: '<svg class="toast-ico" viewBox="0 0 24 24"><path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.27"/></svg>',
    info: '<svg class="toast-ico" viewBox="0 0 24 24"><circle cx="12" cy="12" r="10"/><line x1="12" y1="16" x2="12" y2="12"/><line x1="12" y1="8" x2="12.01" y2="8"/></svg>'
};

function toast(type, message) {
    if (!message) return;
    const host = document.getElementById('toasts');
    if (!host) return;

    const kind = TOAST_ICONS[type] ? type : 'info';
    const el = document.createElement('div');
    el.className = 'toast toast-' + kind;
    el.innerHTML = TOAST_ICONS[kind] + '<span class="toast-msg"></span>' +
        '<button class="toast-x" aria-label="Dismiss">&times;</button>';
    el.querySelector('.toast-msg').textContent = String(message);
    host.appendChild(el);

    let done = false;
    const remove = () => {
        if (done) return;
        done = true;
        clearTimeout(timer);
        el.classList.add('toast-out');
        setTimeout(() => el.remove(), 260);
    };
    const timer = setTimeout(remove, kind === 'error' ? 7000 : 4000);
    el.querySelector('.toast-x').addEventListener('click', remove);

    // Never let notifications pile up.
    while (host.children.length > 4) host.firstElementChild.remove();
}

// The API answers with machine-readable codes (BUILD_NOT_FOUND, ...) which mean nothing
// to an admin at 2 a.m. Known codes are translated; anything else is passed through.
const API_ERROR_TEXT = {
    'BUILD_NOT_FOUND': 'Build not found — it may have been deleted.',
    'MODPACK_NOT_FOUND': 'Modrinth project or version not found. Check the slug / version ID.',
    'SERVER_ALREADY_EXISTS': 'A server with this name already exists.',
    'BUILD_IN_PROGRESS': 'The build is being processed right now. Wait for it to finish.',
    'INVALID_FILE_TYPE': 'Only .mrpack or .zip (CurseForge) files are accepted.',
    'VALIDATION_ERROR': 'Validation failed — check the form fields.',
    'UNAUTHORIZED': 'Your session is not valid. Please sign in again.',
    'FORBIDDEN': 'Admin access is required for this action.',
    'NOT_FOUND': 'The requested record was not found.',
    'RATE_LIMITED': 'Too many requests — wait a few seconds and try again.',
    'MAINTENANCE': 'The API is in maintenance mode.'
};

function friendlyError(message) {
    if (!message) return message;
    const key = String(message).trim().toUpperCase();
    return API_ERROR_TEXT[key] || message;
}

// ---------- auth ----------

async function login(username, password) {
    let res;
    try {
        res = await fetch(`${API_BASE}/login`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ login: username, password: password })
        });
    } catch (e) {
        throw new Error('Network error: the API is not responding. Check that the server is running.');
    }
    if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        throw new Error(err.message || 'Invalid username or password');
    }
    const data = await res.json();
    if (!data.token) throw new Error('No token returned by the API');
    token = data.token;
    localStorage.setItem('admin_token', token);
}

function logout() {
    token = null;
    localStorage.removeItem('admin_token');
    showScreen('login');
}

// ---------- api helper ----------

async function api(path, options = {}) {
    // For FormData bodies the browser must set Content-Type itself (with the
    // multipart boundary); forcing application/json makes ASP.NET reject the
    // request with 415 Unsupported Media Type before it reaches the controller.
    const isFormData = typeof FormData !== 'undefined' && options.body instanceof FormData;
    let res;
    try {
        res = await fetch(`${API_BASE}${path}`, {
            ...options,
            headers: {
                ...(isFormData ? {} : { 'Content-Type': 'application/json' }),
                'Authorization': `Bearer ${token}`,
                ...(options.headers || {})
            }
        });
    } catch (e) {
        // fetch only rejects on network-level failures (API down, DNS, CORS).
        throw new Error('Network error: the API is not responding. Check that the server is running.');
    }

    if (res.status === 401) {
        logout();
        throw new Error('Session expired, please sign in again');
    }

    const text = await res.text();
    let body = null;
    if (text) {
        try { body = JSON.parse(text); } catch (e) { body = text; }
    }

    if (!res.ok) {
        const message = body && typeof body === 'object'
            ? (body.Message || body.message || body.Error || body.error)
            : body;
        throw new Error(friendlyError(message || `HTTP ${res.status}`));
    }

    return body;
}

// ---------- navigation ----------

document.querySelectorAll('.sidebar a').forEach(link => {
    link.addEventListener('click', (e) => {
        e.preventDefault();
        const page = link.dataset.page;
        document.querySelectorAll('.sidebar a').forEach(l => l.classList.remove('active'));
        link.classList.add('active');
        document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
        document.getElementById(`page-${page}`).classList.add('active');
        loadPage(page);
    });
});

async function loadPage(page) {
    if (page !== 'builds') stopBuildsRefresh();
    currentPage = page;
    switch (page) {
        case 'dashboard': await loadDashboard(); break;
        case 'servers': await loadServers(); break;
        case 'builds': await loadBuilds(); startBuildsRefresh(); break;
        case 'presets': await loadPresets(); break;
        case 'users': await loadUsers(); break;
        case 'settings': await loadSettings(); break;
    }
}

// ---------- dashboard ----------

async function loadDashboard() {
    try {
        const [users, servers, builds, maintenance] = await Promise.all([
            api('/admin/users?page=1&pageSize=1'),
            api('/admin/servers'),
            api('/builds'),
            api('/admin/status/maintenance')
        ]);
        document.getElementById('stat-users').textContent = users.totalCount || 0;
        document.getElementById('stat-servers').textContent = (servers || []).filter(s => s.isActive).length;
        document.getElementById('stat-builds').textContent = (builds || []).length;
        const mt = document.getElementById('stat-maintenance');
        mt.textContent = maintenance.isMaintenance ? 'ON' : 'OFF';
        mt.className = maintenance.isMaintenance ? 'is-on' : 'is-off';
    } catch (e) {
        console.error(e);
        toast('error', 'Failed to load dashboard stats: ' + e.message);
    }
}

// ---------- servers ----------

function shortBuildId(id) {
    return id ? String(id).substring(0, 8) + '…' : '—';
}

function buildBadge(buildId) {
    if (buildId === null || buildId === undefined) {
        return '<span class="badge badge-off" title="Auto: newest completed build with matching versions is served">auto</span>';
    }
    if (buildId === '') return '<span class="badge badge-off" title="No manifest attached">none</span>';
    return `<span class="badge badge-on" title="Pinned build: ${esc(buildId)}">${esc(String(buildId).substring(0, 8))}…</span>`;
}

// Completed builds whose MC / loader / loader-build match the versions currently picked in the form.
function matchingCompletedBuilds() {
    const mc = val('server-version').toLowerCase();
    const loader = val('server-loader').toLowerCase();
    const lv = val('server-loader-version').trim();
    return (serverBuilds || []).filter(b =>
        b.status === 'completed' &&
        String(b.mcVersion || '').trim().toLowerCase() === mc &&
        String(b.loaderType || '').trim().toLowerCase() === loader &&
        String(b.loaderVersion || '').trim() === lv
    );
}

// Rebuilds the "Client Build" dropdown of the server modal, keeping `chosen` selected
// when possible. Values: __auto__ (follow matching builds), __none__ (no manifest),
// or a concrete build id.
function renderServerBuildPicker(chosen) {
    const el = document.getElementById('server-build');
    if (!el) return;
    const builds = serverBuilds || [];

    let target = (chosen === '' || chosen === '__none__') ? '__none__'
        : (chosen === null || chosen === undefined || chosen === '__auto__' || chosen === false) ? '__auto__'
        : String(chosen);

    const matches = matchingCompletedBuilds();
    const match = matches[0];
    const autoLabel = match
        ? `Auto — ${shortBuildId(match.buildId)} (${match.name})`
        : (val('server-version') ? 'Auto — no matching build yet' : 'Auto — choose versions first');

    const opts = [
        `<option value="__auto__">${esc(autoLabel)}</option>`,
        '<option value="__none__">— no manifest —</option>'
    ];

    let found = target === '__auto__' || target === '__none__';
    for (const b of builds) {
        if (b.status !== 'completed') continue;
        found = found || String(b.buildId) === target;
        const label = `${b.name} · MC ${b.mcVersion} · ${b.loaderType}${b.loaderVersion ? ' ' + b.loaderVersion : ''} · ${shortBuildId(b.buildId)}`;
        opts.push(`<option value="${esc(b.buildId)}">${esc(label)}</option>`);
    }
    // The pinned build may have been deleted; keep it visible so Save behaves predictably.
    if (target !== '__auto__' && target !== '__none__' && !found) {
        opts.push(`<option value="${esc(target)}">${esc(shortBuildId(target))} (build was deleted)</option>`);
    }

    el.innerHTML = opts.join('');
    el.value = target;
}

async function loadServers() {
    try {
        // /admin/servers returns every server (including inactive),
        // unlike the public /servers endpoint which only serves active servers.
        const servers = await api('/admin/servers');
        const tbody = document.querySelector('#servers-table tbody');

        if (!servers || !servers.length) {
            tbody.innerHTML = '<tr><td colspan="8">No servers yet. Use "Add Server" to create one.</td></tr>';
            return;
        }

        tbody.innerHTML = servers.map(s => `
            <tr>
                <td>${s.id}</td>
                <td>${esc(s.serverName)}</td>
                <td>${esc(s.appVersion)}</td>
                <td>${esc(s.loaderType)}</td>
                <td>${buildBadge(s.buildId)}</td>
                <td>${esc(s.ipAddress)}</td>
                <td><span class="status ${s.isActive ? 'status-active' : 'status-inactive'}">${s.isActive ? 'Active' : 'Inactive'}</span></td>
                <td>
                    <button class="btn btn-sm" onclick="editServer(${s.id})">Edit</button>
                    <button class="btn btn-sm btn-danger" onclick="deleteServer(${s.id})">Delete</button>
                </td>
            </tr>
        `).join('');
    } catch (e) {
        console.error(e);
        toast('error', 'Failed to load servers: ' + e.message);
    }
}

// ---------- presets ----------

async function loadPresets() {
    try {
        const presets = await api('/admin/presets');
        const tbody = document.querySelector('#presets-table tbody');

        if (!presets || !presets.length) {
            tbody.innerHTML = '<tr><td colspan="7">No presets yet. Use "Add Preset" to create one.</td></tr>';
            return;
        }

        tbody.innerHTML = presets.map(p => `
            <tr>
                <td>${p.id}</td>
                <td>${esc(p.name)}</td>
                <td>${esc(p.gameVersion)}</td>
                <td>${esc(p.loaderType)}</td>
                <td>${esc(p.loaderVersion || '')}</td>
                <td>${(p.mods || []).length}</td>
                <td>
                    <button class="btn btn-sm" onclick="editPreset(${p.id})">Edit</button>
                    <button class="btn btn-sm" onclick="managePresetMods(${p.id})">Mods</button>
                    <button class="btn btn-sm btn-danger" onclick="deletePreset(${p.id})">Delete</button>
                </td>
            </tr>
        `).join('');
    } catch (e) {
        console.error(e);
        toast('error', 'Failed to load presets: ' + e.message);
    }
}

async function editPreset(id) {
    let preset;
    try {
        preset = await api(`/admin/presets/${id}`);
    } catch (e) {
        toast('error', e.message);
        return;
    }
    showModal(`
        <h3>Edit Preset</h3>
        <div class="form-group">
            <label>Name</label>
            <input type="text" id="preset-name" value="${esc(preset.name)}">
        </div>
        <div class="form-group">
            <label>Minecraft Version</label>
            <input type="text" id="preset-game-version" value="${esc(preset.gameVersion)}">
        </div>
        <div class="form-group">
            <label>Loader</label>
            <select id="preset-loader-type">
                ${LOADER_OPTIONS.map(l => `<option value="${l}" ${l === preset.loaderType ? 'selected' : ''}>${l}</option>`).join('')}
            </select>
        </div>
        <div class="form-group">
            <label>Loader Version</label>
            <input type="text" id="preset-loader-version" value="${esc(preset.loaderVersion || '')}">
        </div>
        <button class="btn btn-primary" onclick="savePreset(${id})">Save</button>
    `);
}

async function savePreset(id) {
    const data = {
        name: val('preset-name'),
        gameVersion: val('preset-game-version'),
        loaderType: val('preset-loader-type'),
        loaderVersion: val('preset-loader-version') || null
    };
    try {
        await api(`/admin/presets/${id}`, { method: 'PUT', body: JSON.stringify(data) });
        closeModal();
        toast('success', 'Preset saved.');
        loadPresets();
    } catch (e) {
        toast('error', e.message);
    }
}

async function deletePreset(id) {
    if (!confirm('Delete this preset?')) return;
    try {
        await api(`/admin/presets/${id}`, { method: 'DELETE' });
        toast('success', 'Preset deleted.');
        loadPresets();
    } catch (e) {
        toast('error', e.message);
    }
}

async function managePresetMods(presetId) {
    let mods;
    try {
        mods = await api(`/admin/presets/${presetId}/mods`);
    } catch (e) {
        toast('error', e.message);
        return;
    }
    showModal(`
        <h3>Manage Mods</h3>
        <div class="form-group">
            <label>Search Modrinth</label>
            <input type="text" id="modrinth-search" placeholder="Search mods...">
            <button class="btn btn-sm" onclick="searchModrinth(${presetId})">Search</button>
        </div>
        <div id="modrinth-results"></div>
        <h4>Current Mods</h4>
        <table class="table">
            <thead><tr><th>Name</th><th>Type</th><th>Folder</th><th>Status</th><th>Actions</th></tr></thead>
            <tbody>
                ${mods.map(m => `
                    <tr>
                        <td>${esc(m.modrinthSlugOrId || m.customFileName || '')}</td>
                        <td>${esc(m.sourceType)}</td>
                        <td>${esc(m.targetFolder)}</td>
                        <td><span class="status ${m.isEnabled ? 'status-active' : 'status-inactive'}">${m.isEnabled ? 'Enabled' : 'Disabled'}</span></td>
                        <td>
                            <button class="btn btn-sm" onclick="togglePresetMod(${presetId}, '${m.id}')">${m.isEnabled ? 'Disable' : 'Enable'}</button>
                            <button class="btn btn-sm btn-danger" onclick="deletePresetMod(${presetId}, '${m.id}')">Delete</button>
                        </td>
                    </tr>
                `).join('')}
            </tbody>
        </table>
        <h4>Upload Custom Mod</h4>
        <div class="form-group">
            <label>File (.jar for mods, .zip for shaderpacks)</label>
            <input type="file" id="custom-mod-file" accept=".jar,.zip">
        </div>
        <div class="form-group">
            <label>Target Folder</label>
            <select id="custom-mod-folder">
                <option value="mods">mods</option>
                <option value="shaderpacks">shaderpacks</option>
                <option value="resourcepacks">resourcepacks</option>
            </select>
        </div>
        <button class="btn btn-primary" onclick="uploadCustomMod(${presetId})">Upload</button>
    `);
}

async function searchModrinth(presetId) {
    const query = val('modrinth-search');
    if (!query) {
        toast('info', 'Type a mod name to search Modrinth.');
        return;
    }

    const btnEl = document.querySelector('#modrinth-search + button');
    if (btnEl) { btnEl.disabled = true; btnEl.textContent = 'Searching…'; }

    try {
        const results = await api(`/admin/modrinth/search?query=${encodeURIComponent(query)}&mcVersion=1.21.1&loader=fabric`);
        const container = document.getElementById('modrinth-results');
        if (!container) return;
        if (!results.hits || !results.hits.length) {
            container.innerHTML = '<p class="hint">No Modrinth projects matched your search.</p>';
            return;
        }
        container.innerHTML = '<table class="table"><thead><tr><th>Name</th><th>Description</th><th>Actions</th></tr></thead><tbody>' +
            results.hits.map(h => `
                <tr>
                    <td>${esc(h.title)}</td>
                    <td>${esc(h.description)}</td>
                    <td><button class="btn btn-sm" onclick="addModrinthMod(${presetId}, '${esc(h.slug)}')">Add</button></td>
                </tr>
            `).join('') + '</tbody></table>';
    } catch (e) {
        toast('error', e.message);
    } finally {
        if (btnEl) { btnEl.disabled = false; btnEl.textContent = 'Search'; }
    }
}

async function addModrinthMod(presetId, slug) {
    try {
        await api(`/admin/presets/${presetId}/mods/modrinth`, {
            method: 'POST',
            body: JSON.stringify({ slugOrId: slug, targetFolder: 'mods' })
        });
        toast('success', `Mod "${slug}" added to the preset.`);
        managePresetMods(presetId);
    } catch (e) {
        toast('error', e.message);
    }
}

async function togglePresetMod(presetId, modId) {
    try {
        await api(`/admin/presets/${presetId}/mods/${modId}/toggle`, { method: 'PATCH', body: '{}' });
        managePresetMods(presetId);
    } catch (e) {
        toast('error', e.message);
    }
}

async function deletePresetMod(presetId, modId) {
    if (!confirm('Delete this mod?')) return;
    try {
        await api(`/admin/presets/${presetId}/mods/${modId}`, { method: 'DELETE' });
        toast('success', 'Mod removed from the preset.');
        managePresetMods(presetId);
    } catch (e) {
        toast('error', e.message);
    }
}

async function uploadCustomMod(presetId) {
    const fileInput = document.getElementById('custom-mod-file');
    if (!fileInput.files.length) {
        toast('error', 'Choose a file to upload first.');
        return;
    }
    try {
        const formData = new FormData();
        formData.append('file', fileInput.files[0]);
        formData.append('targetFolder', val('custom-mod-folder'));
        await api(`/admin/presets/${presetId}/mods/custom`, { method: 'POST', body: formData });
        toast('success', 'Custom file uploaded.');
        managePresetMods(presetId);
    } catch (e) {
        toast('error', e.message);
    }
}

document.getElementById('add-preset-btn').addEventListener('click', () => {
    showModal(`
        <h3>Add Preset</h3>
        <div class="form-group">
            <label>Name</label>
            <input type="text" id="preset-name" placeholder="My Preset">
        </div>
        <div class="form-group">
            <label>Minecraft Version</label>
            <input type="text" id="preset-game-version" placeholder="1.21.1">
        </div>
        <div class="form-group">
            <label>Loader</label>
            <select id="preset-loader-type">
                ${LOADER_OPTIONS.map(l => `<option value="${l}">${l}</option>`).join('')}
            </select>
        </div>
        <div class="form-group">
            <label>Loader Version</label>
            <input type="text" id="preset-loader-version" placeholder="0.19.5">
        </div>
        <button class="btn btn-primary" onclick="createPreset()">Create</button>
    `);
});

async function createPreset() {
    const data = {
        name: val('preset-name'),
        gameVersion: val('preset-game-version'),
        loaderType: val('preset-loader-type'),
        loaderVersion: val('preset-loader-version') || null
    };
    try {
        await api('/admin/presets', { method: 'POST', body: JSON.stringify(data) });
        closeModal();
        toast('success', 'Preset created.');
        loadPresets();
    } catch (e) {
        toast('error', e.message);
    }
}

const LOADER_OPTIONS = ['vanilla', 'fabric', 'forge', 'neoforge'];

// ---------- version auto-discovery ----------
//
// Loader, Minecraft and loader-build versions all come from the API
// (/versions/minecraft and /versions/loader), so nothing has to be typed by hand.
// The API returns every list newest-first and flags the recommended entry.

function versionOptions(list, preferred, emptyLabel) {
    const versions = Array.isArray(list) ? list.slice() : [];
    const wanted = preferred === null || preferred === undefined ? '' : String(preferred);

    // Keep a stored (possibly hand-entered) value selectable while editing an existing record.
    if (wanted && !versions.some(v => String(v.version) === wanted)) {
        versions.unshift({ version: wanted, type: 'current', recommended: false });
    }

    const options = [];
    if (emptyLabel) options.push(`<option value="">${esc(emptyLabel)}</option>`);

    if (!versions.length) {
        return options.length ? options.join('') : '<option value="">(no versions available)</option>';
    }

    const chosen = wanted || String((versions.find(v => v.recommended) || versions[0]).version);

    for (const v of versions) {
        const note = v.recommended ? 'recommended' : (v.type && v.type !== 'release' ? v.type : '');
        options.push(
            `<option value="${esc(v.version)}" ${String(v.version) === chosen ? 'selected' : ''}>` +
            `${esc(v.version)}${note ? ` (${esc(note)})` : ''}</option>`
        );
    }

    return options.join('');
}

function showVersionsHint(prefix, message) {
    const el = document.getElementById(`${prefix}-versions-hint`);
    if (!el) return;
    el.textContent = message || '';
    el.style.display = message ? '' : 'none';
}

// Wires the Loader -> Minecraft version -> loader build cascade inside a modal.
// `prefix` is "server" or "build"; `initial` keeps stored values selected while editing;
// `onChange` (optional) fires whenever the chosen versions change, so modal code can react.
function initVersionPickers(prefix, initial, onChange) {
    const loaderEl = document.getElementById(`${prefix}-loader`);
    const versionEl = document.getElementById(`${prefix}-version`);
    const loaderVersionEl = document.getElementById(`${prefix}-loader-version`);
    const prereleaseEl = document.getElementById(`${prefix}-prerelease`);
    if (!loaderEl || !versionEl || !loaderVersionEl) return;

    const settings = initial || {};
    if (onChange) settings.onChange = onChange;
    let token = 0;

    const currentLoader = () => (loaderEl.value || 'vanilla').toLowerCase();
    const withPrerelease = () => !!(prereleaseEl && prereleaseEl.checked);

    // Loader, Minecraft version and the "prerelease" checkbox can all fire requests at once, so
    // every DOM write is guarded by the token of the request that started it.
    async function loadMinecraft(preferred, mine) {
        versionEl.innerHTML = '<option value="">loading…</option>';
        let res = null;
        let error = null;
        try {
            res = await api(`/versions/minecraft?loader=${encodeURIComponent(currentLoader())}&includePrerelease=${withPrerelease()}`);
        } catch (e) {
            error = e;
        }
        if (token !== mine) return; // a newer selection won

        if (error) {
            versionEl.innerHTML = versionOptions([], preferred);
            showVersionsHint(prefix, 'Could not load Minecraft versions: ' + error.message);
            settings.onChange && settings.onChange();
            return;
        }

        versionEl.innerHTML = versionOptions(res && res.versions, preferred);
        showVersionsHint(prefix, res && res.message);
        settings.onChange && settings.onChange();
    }

    async function loadBuilds(preferred, mine) {
        const mcVersion = String(versionEl.value || '').trim();
        const loader = currentLoader();

        if (!mcVersion) {
            loaderVersionEl.innerHTML = '<option value="">choose a Minecraft version first</option>';
            settings.onChange && settings.onChange();
            return;
        }

        if (loader === 'vanilla') {
            loaderVersionEl.innerHTML = versionOptions([], '', 'not needed for vanilla');
            settings.onChange && settings.onChange();
            return;
        }

        loaderVersionEl.innerHTML = '<option value="">loading…</option>';
        let res = null;
        let error = null;
        try {
            res = await api(`/versions/loader?loader=${encodeURIComponent(loader)}&mcVersion=${encodeURIComponent(mcVersion)}&includePrerelease=${withPrerelease()}`);
        } catch (e) {
            error = e;
        }
        if (token !== mine) return; // a newer selection won

        if (error) {
            loaderVersionEl.innerHTML = versionOptions([], preferred, 'latest (automatic)');
            showVersionsHint(prefix, `Could not load ${loader} builds: ` + error.message);
            settings.onChange && settings.onChange();
            return;
        }

        const versions = (res && res.versions) || [];
        loaderVersionEl.innerHTML = versionOptions(versions, preferred, 'latest (automatic)');

        if (versions.length) {
            showVersionsHint(prefix, '');
        } else {
            showVersionsHint(prefix, (res && res.message)
                || `No ${loader} builds listed for Minecraft ${mcVersion}; the newest one will be used automatically.`);
        }
        settings.onChange && settings.onChange();
    }

    async function refreshMinecraft(preferredMc, preferredBuild) {
        const mine = ++token;
        await loadMinecraft(preferredMc || '', mine);
        if (token !== mine) return; // a newer selection won
        await loadBuilds(preferredBuild || '', mine);
    }

    async function refreshBuilds(preferred) {
        const mine = ++token;
        await loadBuilds(preferred || '', mine);
    }

    loaderEl.addEventListener('change', () => refreshMinecraft('', ''));
    versionEl.addEventListener('change', () => refreshBuilds(''));
    if (prereleaseEl) prereleaseEl.addEventListener('change', () => refreshMinecraft(versionEl.value, ''));

    refreshMinecraft(settings.mcVersion || '', settings.loaderVersion || '');
}

function serverModal(server, builds) {
    const s = server || {};
    const c = s.config || {};
    const isEdit = s.id !== undefined && s.id !== null;
    const selected = (s.loaderType || 'vanilla').toLowerCase();

    serverBuilds = builds || [];

    const options = LOADER_OPTIONS.map(l =>
        `<option value="${l}" ${l === selected ? 'selected' : ''}>${l}</option>`
    ).join('');

    showModal(`
        <h3>${isEdit ? 'Edit Server #' + esc(s.id) : 'Add Server'}</h3>
        <div class="form-group">
            <label>Server Name</label>
            <input type="text" id="server-name" placeholder="My Server" value="${esc(s.serverName)}">
        </div>
        <div class="form-group">
            <label>Loader</label>
            <select id="server-loader">${options}</select>
        </div>
        <div class="form-group">
            <label>Minecraft Version</label>
            <select id="server-version"><option value="">loading…</option></select>
        </div>
        <div class="form-group">
            <label>Loader Version</label>
            <select id="server-loader-version"><option value="">loading…</option></select>
        </div>
        <div class="form-group check-row">
            <input type="checkbox" id="server-prerelease">
            <label for="server-prerelease" class="inline">Include beta / snapshot versions</label>
        </div>
        <p class="hint" id="server-versions-hint" style="display:none"></p>
        <div class="form-group">
            <label>Client Build (manifest)</label>
            <select id="server-build"><option value="">loading…</option></select>
        </div>
        <p class="hint" id="server-build-hint" style="display:none"></p>
        <div class="form-group">
            <label>IP / Host</label>
            <input type="text" id="server-ip" placeholder="play.example.com" value="${esc(s.ipAddress)}">
        </div>
        <div class="form-group check-row">
            <input type="checkbox" id="server-active" ${s.isActive === false ? '' : 'checked'}>
            <label for="server-active" class="inline">Visible to players</label>
        </div>
        <div class="btn-group">
            <button class="btn btn-primary" onclick="${isEdit ? `saveServer(${s.id})` : 'saveServer()'}">Save</button>
            <button class="btn" onclick="closeModal()">Cancel</button>
        </div>
    `);

    // Keep the "Client Build" choice across the async version loads, and re-render it whenever
    // the chosen MC / loader versions change so the "Auto" option reflects the matching build.
    let serverBuildChoice = (s.buildId === null || s.buildId === undefined) ? '__auto__'
        : (s.buildId === '' ? '__none__' : s.buildId);
    initVersionPickers('server', { mcVersion: s.appVersion, loaderVersion: c.loaderVersion }, () => renderServerBuildPicker(serverBuildChoice));
    renderServerBuildPicker(serverBuildChoice);
    const buildEl = document.getElementById('server-build');
    if (buildEl) buildEl.addEventListener('change', () => { serverBuildChoice = buildEl.value; });
}

async function addServer() {
    try {
        const builds = await api('/builds');
        serverModal(null, builds);
    } catch (e) {
        // The build picker is optional - open the modal anyway.
        serverModal(null, []);
    }
}

async function editServer(id) {
    try {
        // Single server endpoint also returns config + build manifest straight from the database.
        const [server, builds] = await Promise.all([
            api(`/admin/servers/${id}`),
            api('/builds')
        ]);
        serverModal(server, builds);
    } catch (e) {
        toast('error', 'Failed to load server: ' + e.message);
    }
}

async function saveServer(id) {
    const buildEl = document.getElementById('server-build');
    let buildId;
    if (buildEl) {
        buildId = buildEl.value === '__auto__' ? null
            : (buildEl.value === '__none__' ? '' : buildEl.value);
    }
    const data = {
        serverName: val('server-name'),
        appVersion: val('server-version'),
        loaderType: val('server-loader'),
        ipAddress: val('server-ip'),
        typeVersion: val('server-loader-version'),
        isActive: document.getElementById('server-active').checked,
        // null -> auto (newest matching build), '' -> no manifest, buildId -> pinned build.
        buildId
    };

    if (!data.serverName || !data.appVersion || !data.ipAddress) {
        toast('error', 'Server name, Minecraft version and IP are required.');
        return;
    }

    try {
        if (id) {
            await api(`/admin/servers/${id}`, { method: 'PUT', body: JSON.stringify(data) });
        } else {
            await api('/admin/servers', { method: 'POST', body: JSON.stringify(data) });
        }
        closeModal();
        toast('success', id ? 'Server saved.' : 'Server created.');
        await loadServers();
    } catch (e) {
        toast('error', e.message);
    }
}

async function deleteServer(id) {
    if (!confirm('Delete this server?')) return;
    try {
        await api(`/admin/servers/${id}`, { method: 'DELETE' });
        toast('success', 'Server deleted.');
        await loadServers();
    } catch (e) {
        toast('error', e.message);
    }
}

// ---------- builds ----------

function addBuild() {
    const options = LOADER_OPTIONS.map(l => `<option value="${l}">${l}</option>`).join('');
    showModal(`
        <h3>Create Build</h3>
        <div class="form-group">
            <label>Build Name</label>
            <input type="text" id="build-name" placeholder="Tech Pack">
        </div>
        <div class="form-group">
            <label>Loader</label>
            <select id="build-loader">${options}</select>
        </div>
        <div class="form-group">
            <label>Minecraft Version</label>
            <select id="build-version"><option value="">loading…</option></select>
        </div>
        <div class="form-group">
            <label>Loader Version</label>
            <select id="build-loader-version"><option value="">loading…</option></select>
        </div>
        <div class="form-group check-row">
            <input type="checkbox" id="build-prerelease">
            <label for="build-prerelease" class="inline">Include beta / snapshot versions</label>
        </div>
        <p class="hint" id="build-versions-hint" style="display:none"></p>
        <div class="btn-group">
            <button class="btn btn-primary" onclick="saveBuild()">Create</button>
            <button class="btn" onclick="closeModal()">Cancel</button>
        </div>
    `);

    initVersionPickers('build', {});
}

async function saveBuild() {
    const data = {
        name: val('build-name'),
        mcVersion: val('build-version'),
        loader: val('build-loader'),
        loaderVersion: val('build-loader-version') || null
    };

    if (!data.name || !data.mcVersion) {
        toast('error', 'Build name and Minecraft version are required.');
        return;
    }

    try {
        await api('/builds', { method: 'POST', body: JSON.stringify(data) });
        closeModal();
        toast('success', 'Build queued — track its progress in the table.');
        await loadBuilds();
    } catch (e) {
        toast('error', e.message);
    }
}

// ---------- modpack import (.mrpack / CurseForge zip) ----------

function importModpack(focusCurseForgeFile) {
    showModal(`
        <h3>Import Modpack (.mrpack or CurseForge .zip)</h3>
        <div class="form-group">
            <label>Bind build to server (optional)</label>
            <select id="mp-server"><option value="">— none —</option></select>
        </div>
        <h4>From Modrinth</h4>
        <div class="form-group">
            <label>Project slug or ID *</label>
            <input type="text" id="mp-slug" placeholder="fabulously-optimized">
        </div>
        <div class="form-group">
            <label>Build name (optional, shared with file import)</label>
            <input type="text" id="mp-name" placeholder="defaults to the modpack name">
        </div>
        <div class="form-group">
            <label>Version ID (optional — latest release is used)</label>
            <input type="text" id="mp-version-id" placeholder="e.g. abc123...">
        </div>
        <div class="btn-group">
            <button class="btn btn-primary" id="mp-modrinth-btn" onclick="importModpackFromModrinth()">Import from Modrinth</button>
        </div>
        <hr>
        <h4>From file (Modrinth or CurseForge)</h4>
        <div class="form-group">
            <label>.mrpack (Modrinth) or .zip (CurseForge) file, up to 1 GB</label>
            <input type="file" id="mp-file" accept=".mrpack,.zip,application/zip">
            <p class="hint">The format is detected from the archive contents, not from the file name.
            CurseForge packs need <code>CurseForgeApiKey</code> in the server's .env.</p>
        </div>
        <div class="btn-group">
            <button class="btn btn-primary" id="mp-file-btn" onclick="importModpackFromFile()">Import file</button>
            <button class="btn" onclick="closeModal()">Cancel</button>
        </div>
        <p class="hint">The server auto-detects the format (Modrinth .mrpack or CurseForge .zip).
        It prepares the matching engine (Fabric / Forge / NeoForge), downloads all client files
        and applies pack overrides. Follow progress in the builds table.</p>
    `);

    loadModpackServers();

    // "Import from CurseForge" only makes sense for a pack downloaded from curseforge.com, so
    // jump straight to the file field instead of making the admin hunt for it.
    if (focusCurseForgeFile) {
        const fileInput = document.getElementById('mp-file');
        if (fileInput) {
            fileInput.scrollIntoView({ block: 'center' });
            fileInput.focus();
        }
    }
}

async function loadModpackServers() {
    try {
        const servers = await api('/admin/servers');
        const sel = document.getElementById('mp-server');
        if (!sel) return;
        sel.innerHTML = '<option value="">— none —</option>' +
            (servers || []).map(s => `<option value="${s.id}">${esc(s.serverName)}</option>`).join('');
    } catch (e) {
        console.error('Failed to load servers for binding:', e);
    }
}

function modpackServerId() {
    const sel = document.getElementById('mp-server');
    return sel && sel.value ? Number(sel.value) : null;
}

async function importModpackFromModrinth() {
    const slug = val('mp-slug');
    if (!slug) {
        toast('error', 'Enter a Modrinth project slug or ID.');
        return;
    }

    const btn = document.getElementById('mp-modrinth-btn');
    if (btn) { btn.disabled = true; btn.textContent = 'Downloading pack info…'; }

    try {
        const build = await api('/admin/builds/import-modpack/modrinth', {
            method: 'POST',
            body: JSON.stringify({
                name: val('mp-name') || null,
                slug: slug,
                versionId: val('mp-version-id') || null,
                serverId: modpackServerId()
            })
        });
        closeModal();
        await loadBuilds();
        toast('success', `Build queued: ${build.name} (${build.mcVersion}, ${build.loaderType}). Track it in the builds table.`);
    } catch (e) {
        toast('error', e.message);
    } finally {
        if (btn) { btn.disabled = false; btn.textContent = 'Import from Modrinth'; }
    }
}

async function importModpackFromFile() {
    const input = document.getElementById('mp-file');
    const file = input && input.files && input.files[0];
    if (!file) {
        toast('error', 'Choose a .mrpack or .zip file.');
        return;
    }
    if (!/(\.mrpack|\.zip)$/i.test(file.name)) {
        toast('error', 'Only .mrpack or .zip (CurseForge) files are accepted.');
        return;
    }

    const btn = document.getElementById('mp-file-btn');
    if (btn) { btn.disabled = true; btn.textContent = 'Uploading…'; }

    try {
        const fd = new FormData();
        fd.append('file', file);
        const name = val('mp-name');
        if (name) fd.append('name', name);
        const serverId = modpackServerId();
        if (serverId) fd.append('serverId', String(serverId));

        const build = await api('/admin/builds/import-modpack/file', { method: 'POST', body: fd });
        closeModal();
        await loadBuilds();
        toast('success', `Build queued: ${build.name} (${build.mcVersion}, ${build.loaderType}). Track it in the builds table.`);
    } catch (e) {
        toast('error', e.message);
    } finally {
        if (btn) { btn.disabled = false; btn.textContent = 'Import file'; }
    }
}

async function loadBuilds(silent = false) {
    try {
        const builds = await api('/builds');
        const tbody = document.querySelector('#builds-table tbody');

        if (!builds || !builds.length) {
            tbody.innerHTML = '<tr><td colspan="8">No builds yet. Use "Create Build" to queue one.</td></tr>';
            return;
        }

        tbody.innerHTML = builds.map(b => {
            const busy = b.status === 'downloading' || b.status === 'hashing' || b.status === 'pending';
            const canDelete = b.status === 'completed' || b.status === 'failed';
            const error = cleanError(b.errorMessage);
            const progress = (b.status === 'downloading' || b.status === 'hashing')
                && b.progressPercent !== null && b.progressPercent !== undefined
                ? renderBuildProgress(b)
                : '';
            return `
            <tr>
                <td>${b.id}</td>
                <td>${esc(b.name)}${b.buildType === 'ModpackImport' ? ' <small class="muted">modpack</small>' : ''}</td>
                <td>${esc(b.mcVersion)}</td>
                <td>${esc(b.loaderType)}${b.loaderVersion ? ` <small>${esc(b.loaderVersion)}</small>` : ''}</td>
                <td>
                    <span class="status status-${esc(b.status)}" ${error && b.status === 'failed' ? `title="${esc(error)}"` : ''}>${esc(b.status)}</span>
                    ${progress}
                </td>
                <td>${b.fileCount || 0}${b.totalSize ? ` <small>(${(b.totalSize / 1048576).toFixed(1)} MB)</small>` : ''}</td>
                <td>${esc((b.buildId || '').substring(0, 8))}…</td>
                <td>
                    <button class="btn btn-sm" onclick="viewBuild('${esc(b.buildId)}')">Manifest</button>
                    <button class="btn btn-sm" onclick="viewBuildLogs(${b.id}, '${esc(b.name)}')">Logs</button>
                    ${canDelete ? `<button class="btn btn-sm btn-danger" onclick="deleteBuild(${b.id})">Delete</button>` : (busy ? '<small class="muted">in progress</small>' : '')}
                </td>
            </tr>`;
        }).join('');
    } catch (e) {
        if (!silent) {
            console.error(e);
            toast('error', 'Failed to load builds: ' + e.message);
        }
    }
}

// Live download / install progress: percent bar + bytes + speed + current step/file.
function renderBuildProgress(b) {
    const pct = Math.max(0, Math.min(100, b.progressPercent));
    const speed = fmtSpeed(b.downloadSpeed);
    let meta = `${pct}% · ${fmtBytes(b.bytesDownloaded)} / ${fmtBytes(b.totalBytes)}`;
    if (speed) meta += ` · ${speed}`;
    if (b.downloadedFiles !== null && b.downloadedFiles !== undefined && b.totalFiles) {
        meta += ` · files ${b.downloadedFiles}/${b.totalFiles}`;
    }
    return `
        <div class="build-progress">
            <div class="progress-track"><div class="progress-fill" style="width:${pct}%"></div></div>
            <div class="progress-meta">${esc(meta)}${b.currentStep ? `<div>${esc(b.currentStep)}</div>` : ''}${b.currentFile ? `<div class="muted">${esc(b.currentFile)}</div>` : ''}</div>
        </div>`;
}

// Statuses change in the background, so the table refreshes itself while visible.
// 10s keeps the view fresh without crowding the 15 req/10s IP rate limit.
function startBuildsRefresh() {
    stopBuildsRefresh();
    buildsTimer = setInterval(() => {
        if (currentPage === 'builds' && !document.getElementById('modal').classList.contains('active')) {
            loadBuilds(true);
        }
    }, 10000);
}

function stopBuildsRefresh() {
    if (buildsTimer) {
        clearInterval(buildsTimer);
        buildsTimer = null;
    }
}

async function viewBuild(buildId) {
    try {
        const manifest = await api(`/builds/${buildId}/manifest`);
        if (!manifest) {
            toast('info', 'Manifest is not available yet (build is not completed).');
            return;
        }
        showModal(`
            <h3>Build Manifest: ${esc(manifest.displayName)}</h3>
            <p><strong>Game Version:</strong> ${esc(manifest.gameVersion)}</p>
            <p><strong>Loader:</strong> ${esc(manifest.loaderType)} ${esc(manifest.loaderVersion || '')}</p>
            <p><strong>Files:</strong> ${(manifest.files || []).length}</p>
            <pre class="json-view">${esc(JSON.stringify(manifest, null, 2))}</pre>
            <div class="btn-group">
                <button class="btn" onclick="closeModal()">Close</button>
            </div>
        `);
    } catch (e) {
        toast('error', e.message);
    }
}

async function deleteBuild(id) {
    if (!confirm('Delete this build and all of its uploaded files?')) return;
    try {
        await api(`/builds/${id}`, { method: 'DELETE' });
        toast('success', 'Build deleted.');
        await loadBuilds();
    } catch (e) {
        toast('error', e.message);
    }
}

// ---------- build log viewer ----------

// Opens a modal that streams the build's processing log; new lines are polled every
// two seconds while the modal is open, so an in-flight build's log stays live.
function viewBuildLogs(id, name) {
    stopBuildLogRefresh();
    showModal(`
        <h3>Build Logs: ${esc(name)}</h3>
        <div id="build-log-view" class="log-view"><div class="muted">Loading…</div></div>
        <div class="btn-group">
            <button class="btn" onclick="closeModal()">Close</button>
        </div>
    `);

    let lastId = 0;
    let first = true;

    const tick = async () => {
        const modal = document.getElementById('modal');
        const view = document.getElementById('build-log-view');
        if (!modal || !modal.classList.contains('active') || !view) {
            stopBuildLogRefresh();
            return;
        }
        try {
            const data = await api(`/builds/${id}/logs?afterId=${lastId}`);
            const lines = (data && data.logs) || [];
            if (lines.length && first) {
                view.innerHTML = '';
                first = false;
            }
            if (lines.length) {
                lastId = data.latestId || lines[lines.length - 1].id;
                view.insertAdjacentHTML('beforeend', lines.map(l =>
                    `<div class="log-line log-${esc(l.level || 'info')}">
                        <span class="log-time">${fmtLogTime(l.createdAt)}</span>
                        <span>${esc(l.message)}</span>
                     </div>`
                ).join(''));
                view.scrollTop = view.scrollHeight;
            } else if (first) {
                view.innerHTML = '<div class="muted">No log lines recorded yet.</div>';
                first = false;
            }
        } catch (e) {
            if (first) {
                view.innerHTML = `<div class="log-line log-error">Failed to load logs: ${esc(e.message)}</div>`;
                first = false;
            }
        }
    };

    tick();
    buildLogTimer = setInterval(tick, 2000);
}

function stopBuildLogRefresh() {
    if (buildLogTimer) {
        clearInterval(buildLogTimer);
        buildLogTimer = null;
    }
}

// ---------- users ----------

async function loadUsers() {
    try {
        let rows;
        if (userQuery) {
            const data = await api(`/admin/users/search?query=${encodeURIComponent(userQuery)}`);
            rows = data || [];
        } else {
            const data = await api('/admin/users?page=1&pageSize=50');
            rows = data.data || [];
        }

        const tbody = document.querySelector('#users-table tbody');

        if (!rows.length) {
            tbody.innerHTML = '<tr><td colspan="7">No users found.</td></tr>';
            return;
        }

        tbody.innerHTML = rows.map(u => {
            const banned = !!u.isBanned;
            const banTitle = banned
                ? `Ban reason: ${esc(u.banReason || 'no reason')}, ${fmtBanExpiry(u.banExpiresAt)}`
                : '';
            const roleClass = u.role === 'admin' ? 'status-role-admin' : 'status-role-user';
            return `
            <tr>
                <td>${u.id}</td>
                <td>${esc(u.username)}</td>
                <td>${u.uuid ? esc(String(u.uuid).substring(0, 8)) + '…' : '—'}</td>
                <td><span class="status ${roleClass}">${esc(u.role)}</span></td>
                <td>${banned
                    ? `<span class="status status-banned" title="${banTitle}">Banned</span>`
                    : '<span class="status status-active">OK</span>'}</td>
                <td>${u.lastLogin ? fmtDate(u.lastLogin) : 'Never'}</td>
                <td>
                    ${banned
                        ? `<button class="btn btn-sm" onclick="unbanUser(${u.id})">Unban</button>`
                        : `<button class="btn btn-sm btn-danger" onclick="banUser(${u.id})">Ban</button>`}
                    <button class="btn btn-sm" onclick="changeRole(${u.id}, '${esc(u.role)}')">Role</button>
                    <button class="btn btn-sm" onclick="changePassword(${u.id}, '${esc(u.username)}')">Password</button>
                </td>
            </tr>`;
        }).join('');
    } catch (e) {
        console.error(e);
        toast('error', 'Failed to load users: ' + e.message);
    }
}

// ---------- settings ----------

async function loadSettings() {
    try {
        const data = await api('/admin/status/maintenance');
        document.getElementById('maintenance-toggle').checked = !!data.isMaintenance;
        document.getElementById('maintenance-state').textContent = data.isMaintenance ? 'ON' : 'OFF';
    } catch (e) {
        console.error(e);
    }
}

// ---------- modal ----------

function showModal(html) {
    document.getElementById('modal-body').innerHTML = html;
    // Dialogs with tables or raw JSON (manifest, preset mods, Modrinth search) need room.
    const wide = /<(table|pre)\b|class="[^"]*\blog-view\b/.test(html);
    document.getElementById('modal').classList.toggle('wide', wide);
    document.getElementById('modal').classList.add('active');
}

function closeModal() {
    stopBuildLogRefresh();
    document.getElementById('modal').classList.remove('active');
}

document.querySelector('.close').addEventListener('click', closeModal);
document.getElementById('modal').addEventListener('click', (e) => {
    if (e.target.id === 'modal') closeModal();
});

// ---------- user actions ----------

function banUser(id) {
    showModal(`
        <h3>Ban User ${id}</h3>
        <div class="form-group">
            <label>Reason</label>
            <input type="text" id="ban-reason" placeholder="Reason for ban">
        </div>
        <div class="form-group">
            <label>Duration (days, empty = permanent)</label>
            <input type="number" id="ban-duration" placeholder="Permanent">
        </div>
        <div class="btn-group">
            <button class="btn btn-danger" onclick="confirmBan(${id})">Ban</button>
            <button class="btn" onclick="closeModal()">Cancel</button>
        </div>
    `);
}

async function confirmBan(id) {
    try {
        const days = val('ban-duration');
        const data = {
            reason: val('ban-reason') || null,
            durationDays: days ? parseInt(days, 10) : null
        };
        await api(`/admin/users/${id}/ban`, { method: 'POST', body: JSON.stringify(data) });
        closeModal();
        toast('success', 'User banned.');
        await loadUsers();
    } catch (e) {
        toast('error', e.message);
    }
}

async function unbanUser(id) {
    if (!confirm('Lift the ban for user #' + id + '?')) return;
    try {
        await api(`/admin/users/${id}/unban`, { method: 'POST', body: JSON.stringify({}) });
        toast('success', 'Ban lifted.');
        await loadUsers();
    } catch (e) {
        toast('error', e.message);
    }
}

function changeRole(id, currentRole) {
    showModal(`
        <h3>Change Role - User ${id}</h3>
        <div class="form-group">
            <label>New Role</label>
            <select id="new-role">
                <option value="user" ${currentRole === 'user' ? 'selected' : ''}>User</option>
                <option value="admin" ${currentRole === 'admin' ? 'selected' : ''}>Admin</option>
            </select>
        </div>
        <div class="btn-group">
            <button class="btn btn-primary" onclick="confirmRole(${id})">Change</button>
            <button class="btn" onclick="closeModal()">Cancel</button>
        </div>
    `);
}

async function confirmRole(id) {
    try {
        const role = val('new-role');
        await api(`/admin/users/${id}/role`, {
            method: 'PATCH',
            body: JSON.stringify({ role })
        });
        closeModal();
        toast('success', 'Role updated.');
        await loadUsers();
    } catch (e) {
        toast('error', e.message);
    }
}

function changePassword(id, username) {
    showModal(`
        <h3>Reset password - ${esc(username || id)}</h3>
        <div class="form-group">
            <label>New password (min 6 characters)</label>
            <input type="text" id="new-password" placeholder="New password">
        </div>
        <div class="btn-group">
            <button class="btn btn-primary" onclick="confirmPassword(${id})">Save</button>
            <button class="btn" onclick="closeModal()">Cancel</button>
        </div>
    `);
}

async function confirmPassword(id) {
    try {
        const password = val('new-password');
        if (!password || password.length < 6) {
            toast('error', 'Password must be at least 6 characters.');
            return;
        }
        await api(`/admin/users/${id}/change-password`, {
            method: 'PATCH',
            body: JSON.stringify({ newPassword: password })
        });
        closeModal();
        toast('success', 'Password changed.');
    } catch (e) {
        toast('error', e.message);
    }
}

// ---------- wiring ----------

document.getElementById('add-server-btn').addEventListener('click', addServer);
document.getElementById('add-build-btn').addEventListener('click', addBuild);
document.getElementById('import-modpack-btn').addEventListener('click', () => importModpack(false));
document.getElementById('import-curseforge-btn').addEventListener('click', () => importModpack(true));

document.getElementById('user-search').addEventListener('input', (e) => {
    clearTimeout(searchTimer);
    const query = e.target.value.trim();
    searchTimer = setTimeout(() => {
        userQuery = query;
        loadUsers();
    }, 300);
});

document.getElementById('maintenance-toggle').addEventListener('change', async (e) => {
    try {
        await api(`/admin/status/maintenance?enabled=${e.target.checked}`, { method: 'POST' });
        document.getElementById('maintenance-state').textContent = e.target.checked ? 'ON' : 'OFF';
    } catch (err) {
        toast('error', err.message);
        e.target.checked = !e.target.checked;
        document.getElementById('maintenance-state').textContent = e.target.checked ? 'ON' : 'OFF';
    }
});

document.getElementById('login-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    const btn = e.target.querySelector('button[type="submit"]');
    const errBox = document.getElementById('login-error');
    errBox.textContent = '';
    if (btn) { btn.disabled = true; btn.textContent = 'Signing in…'; }
    try {
        await login(
            document.getElementById('login-username').value,
            document.getElementById('login-password').value
        );
        showScreen('main');
        loadPage('dashboard');
    } catch (err) {
        errBox.textContent = err.message;
    } finally {
        if (btn) { btn.disabled = false; btn.textContent = 'Sign in'; }
    }
});

document.getElementById('logout-btn').addEventListener('click', logout);

// ---------- init ----------

function showScreen(name) {
    document.querySelectorAll('.screen').forEach(s => s.classList.remove('active'));
    document.getElementById(`${name}-screen`).classList.add('active');
}

if (token) {
    showScreen('main');
    loadPage('dashboard');
} else {
    showScreen('login');
}
