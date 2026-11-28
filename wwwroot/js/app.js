const DEMO_USER = {
  name: '',
  employeeId: '',
  sntopid: '',
  department: '',
  departmentId: '',
  dpcnm: '',
  title: '',
  dutnm: '',
};
function getUser() {
  try {
    return { ...DEMO_USER, ...JSON.parse(sessionStorage.getItem('meetingLoginUser') || '{}') };
  } catch {
    return DEMO_USER;
  }
}

const LOGIN_PAGES = new Set([
  'dashboard.html',
  'sign-in.html',
  'my-meetings.html',
  'create-meeting.html',
  'meeting-detail.html',
  'qr.html',
  'attendance.html',
]);
function currentPageName() {
  return (location.pathname.split('/').pop() || '').toLowerCase();
}
function requireLogin() {
  if (!LOGIN_PAGES.has(currentPageName())) return true;
  if (sessionStorage.getItem('meetingLoginUser')) return true;
  location.replace('./');
  return false;
}
// 清除瀏覽器裡的登入資料。真正的登入狀態在伺服器簽發的 HttpOnly cookie，登出時要一併請伺服器清除。
function clearDesktopSession() {
  [
    'meetingLoginUser',
    'meetingFlash',
    'meetingLoginUserProfileAt',
    'meetingLiffMeeting',
    'meetingLiffUser',
  ].forEach((k) => sessionStorage.removeItem(k));
  try {
    localStorage.removeItem('meetingLoginUser');
    localStorage.removeItem('meetingLiffUser');
  } catch {}
}
async function logoutDesktop() {
  try {
    await fetch('api/auth/logout', { method: 'POST' });
  } catch {}
  clearDesktopSession();
  location.replace('./');
}
requireLogin();
window.addEventListener('pageshow', () => requireLogin());
window.addEventListener('focus', () => requireLogin());
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) requireLogin();
});

function qs(s) {
  return document.querySelector(s);
}
function setPageBusy(isBusy, message = '資料載入中…') {
  let overlay = document.getElementById('pageBusyOverlay');
  if (isBusy) {
    if (!overlay) {
      overlay = document.createElement('div');
      overlay.id = 'pageBusyOverlay';
      overlay.className = 'page-busy-overlay';
      overlay.innerHTML = `<div class="page-busy-card"><span class="page-busy-spinner" aria-hidden="true"></span><span class="page-busy-text"></span></div>`;
      document.body.appendChild(overlay);
    }
    const text = overlay.querySelector('.page-busy-text');
    if (text) text.textContent = message;
    requestAnimationFrame(() => overlay.classList.add('show'));
  } else if (overlay) {
    overlay.classList.remove('show');
    setTimeout(() => overlay.remove(), 120);
  }
}

const pageCache = new Map();
const pendingRequests = new Map();
async function cachedApi(url, options = {}, ttlMs = 30000) {
  const method = (options.method || 'GET').toUpperCase();
  if (method !== 'GET') return apiJson(url, options);
  const now = Date.now();
  const hit = pageCache.get(url);
  if (hit && now - hit.time < ttlMs) return hit.data;
  if (pendingRequests.has(url)) return pendingRequests.get(url);
  const request = apiJson(url, options)
    .then((data) => {
      pageCache.set(url, { time: Date.now(), data });
      return data;
    })
    .finally(() => pendingRequests.delete(url));
  pendingRequests.set(url, request);
  return request;
}
function esc(s = '') {
  return String(s).replace(
    /[&<>"']/g,
    (m) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[m],
  );
}
function localDateString(d = new Date()) {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}
function meetingWindow(m) {
  const date = String(m?.date || '');
  const start = new Date(m?.startDateTime || `${date}T${m?.start || '00:00'}:00`);
  const end = new Date(m?.endDateTime || `${date}T${m?.end || '00:00'}:00`);
  // checkInOpenAt comes from the server (CheckIn:OpenMinutesBeforeStart); fall back to the start time.
  const openAt = new Date(m?.checkInOpenAt || start);
  return { start, end, openAt };
}
function getAccess(m, now = new Date()) {
  const { end, openAt } = meetingWindow(m);
  if (now < openAt) return 'pending';
  if (now > end) return 'closed';
  return 'open';
}
// 相容舊頁面名稱，避免瀏覽器快取舊 HTML 時發生 effectiveStatus 未定義。
window.getAccess = getAccess;
window.effectiveStatus = getAccess;
function formatDateTimeHM(value) {
  const d = new Date(value);
  return Number.isNaN(d.getTime())
    ? ''
    : `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}
function shell(active = '') {
  const u = getUser();
  const displayName = u.name || '使用者';
  const displayEmployee = (u.sntopid || u.employeeId || '').trim() || '未設定';
  const displayDepartment = (u.dpcnm || u.department || u.departmentId || '').trim() || '未設定';
  const displayTitle = (u.dutnm || u.title || '').trim() || '未設定';
  return `<header class="app-header"><a class="brand" href="dashboard.html" aria-label="回主選單"><img src="assets/logo.svg" alt="示範醫院"></a><div class="header-actions">${currentPageName() === 'dashboard.html' ? '' : '<a class="main-menu-btn" href="dashboard.html">主選單</a>'}<div class="user-wrap"><button class="user-btn" id="userBtn"><span class="avatar">${esc(displayName[0] || '使')}</span><span class="user-text"><strong>${esc(displayName)}</strong><span>${esc(displayDepartment)} · ${esc(displayTitle)}</span></span>⌄</button><div class="user-menu" id="userMenu"><div class="user-card"><strong>${esc(displayName)}</strong><br>員工編號：${esc(displayEmployee)}<br>單位：${esc(displayDepartment)}<br>職稱：${esc(displayTitle)}</div><button class="menu-link" id="logoutBtn">登出</button></div></div></div></header>`;
}
function renderShell() {
  const root = qs('#shell');
  if (!root) return;
  root.innerHTML = shell();
  qs('#userBtn').onclick = () => qs('#userMenu').classList.toggle('open');
  qs('#logoutBtn').onclick = logoutDesktop;
}
async function refreshLoginUser() {
  const current = getUser();
  const employeeNo = (current.sntopid || current.employeeId || current.userID || '').trim();
  if (!employeeNo) return;
  const PROFILE_TTL_MS = 5 * 60 * 1000;
  const lastRefresh = Number(sessionStorage.getItem('meetingLoginUserProfileAt') || 0);
  const hasProfile = !!(
    (current.name || '').trim() && (current.department || current.dpcnm || '').trim()
  );
  if (hasProfile && Date.now() - lastRefresh < PROFILE_TTL_MS) return;
  try {
    const result = await apiJson(`api/users/profile/${encodeURIComponent(employeeNo)}`);
    const profile = result.user || {};
    let departmentName = (profile.dpcnm || profile.department || '').trim();
    const departmentId = (
      profile.departmentId ||
      profile.dpid ||
      current.departmentId ||
      current.dpid ||
      ''
    ).trim();
    if (!departmentName && departmentId) {
      try {
        const departments = await cachedApi('api/departments', {}, 300000);
        departmentName = (
          departments.find(
            (x) =>
              String(x.departmentID || '')
                .trim()
                .toUpperCase() === departmentId.toUpperCase(),
          )?.departmentName || ''
        ).trim();
      } catch {}
    }
    const merged = {
      ...current,
      ...profile,
      name: profile.name || current.name || '',
      account: current.account || profile.account || '',
      employeeId: profile.employeeId || current.employeeId || employeeNo,
      sntopid: profile.sntopid || current.sntopid || employeeNo,
      userID: profile.userID || current.userID || employeeNo,
      operatorId: profile.operatorId || current.operatorId || employeeNo,
      department: departmentName || current.department || departmentId || '',
      dpcnm: departmentName || current.dpcnm || current.department || '',
      departmentId: departmentId,
      dpid: departmentId,
      title: profile.title || current.title || '',
      dutnm: profile.dutnm || current.dutnm || current.title || '',
    };
    sessionStorage.setItem('meetingLoginUser', JSON.stringify(merged));
    sessionStorage.setItem('meetingLoginUserProfileAt', String(Date.now()));
    renderShell();
  } catch {}
}
function initShell() {
  renderShell();
  if (!window.__shellClickBound) {
    document.addEventListener('click', (e) => {
      if (!e.target.closest('.user-wrap')) qs('#userMenu')?.classList.remove('open');
    });
    window.__shellClickBound = true;
  }
  refreshLoginUser();
}
function getDialog() {
  let root = qs('#systemDialog');
  if (root) return root;
  root = document.createElement('div');
  root.id = 'systemDialog';
  root.className = 'system-dialog-backdrop';
  root.innerHTML = `<div class="system-dialog" role="dialog" aria-modal="true" aria-labelledby="systemDialogMessage">
    <div class="system-dialog-icon" id="systemDialogIcon">!</div>
    <div class="system-dialog-message" id="systemDialogMessage"></div>
    <div class="system-dialog-actions">
      <button class="btn btn-light" id="systemDialogCancel" type="button">取消</button>
      <button class="btn btn-primary" id="systemDialogConfirm" type="button">確定</button>
    </div>
  </div>`;
  document.body.appendChild(root);
  return root;
}
function showDialog(message, { type = 'warning', confirmText = '確定' } = {}) {
  return new Promise((resolve) => {
    const root = getDialog(),
      icon = qs('#systemDialogIcon'),
      msg = qs('#systemDialogMessage'),
      cancel = qs('#systemDialogCancel'),
      confirm = qs('#systemDialogConfirm');
    root.className = `system-dialog-backdrop open type-${type}`;
    icon.textContent =
      type === 'success' ? '✓' : type === 'error' ? '×' : type === 'info' ? 'i' : '!';
    msg.textContent = message || '';
    cancel.style.display = 'none';
    confirm.textContent = confirmText;
    const close = () => {
      root.classList.remove('open');
      confirm.onclick = null;
      root.onclick = null;
      resolve(true);
    };
    confirm.onclick = close;
    root.onclick = (e) => {
      if (e.target === root) close();
    };
    setTimeout(() => confirm.focus(), 0);
  });
}
function showConfirm(
  message,
  { type = 'warning', confirmText = '確定', cancelText = '取消' } = {},
) {
  return new Promise((resolve) => {
    const root = getDialog(),
      icon = qs('#systemDialogIcon'),
      msg = qs('#systemDialogMessage'),
      cancel = qs('#systemDialogCancel'),
      confirm = qs('#systemDialogConfirm');
    root.className = `system-dialog-backdrop open type-${type}`;
    icon.textContent =
      type === 'success' ? '✓' : type === 'error' ? '×' : type === 'info' ? 'i' : '!';
    msg.textContent = message || '';
    cancel.style.display = 'inline-flex';
    cancel.textContent = cancelText;
    confirm.textContent = confirmText;
    const close = (value) => {
      root.classList.remove('open');
      confirm.onclick = null;
      cancel.onclick = null;
      root.onclick = null;
      resolve(value);
    };
    confirm.onclick = () => close(true);
    cancel.onclick = () => close(false);
    root.onclick = (e) => {
      if (e.target === root) close(false);
    };
    setTimeout(() => confirm.focus(), 0);
  });
}
function notifySuccess(message) {
  return showDialog(message, { type: 'success' });
}
function notifyWarning(message) {
  return showDialog(message, { type: 'warning' });
}
function notifyError(message) {
  return showDialog(message, { type: 'error' });
}
function notifyInfo(message) {
  return showDialog(message, { type: 'info' });
}
function toast(msg, type = 'warning') {
  return showDialog(msg, { type });
}
window.showDialog = showDialog;
window.showConfirm = showConfirm;
window.notifySuccess = notifySuccess;
window.notifyWarning = notifyWarning;
window.notifyError = notifyError;
window.notifyInfo = notifyInfo;
function meetingCard(m, mode = 'sign') {
  const isMine = mode === 'mine';
  const canViewQr = m.relation === 'host';
  const href = isMine
    ? `meeting-detail.html?id=${encodeURIComponent(m.id)}`
    : canViewQr
      ? `qr.html?id=${encodeURIComponent(m.id)}`
      : `meeting-detail.html?id=${encodeURIComponent(m.id)}&from=today`;
  const buttonText = isMine ? '查看會議' : canViewQr ? '查看 QR Code' : '查看會議資訊';
  const relationText = m.relation === 'host' ? '我發起的' : '我受邀的';
  const state = getAccess(m);
  const stateTag = isMine
    ? `<span class="tag ${state}">${{ open: '進行中', pending: '尚未開始', closed: '已結束' }[state]}</span>`
    : '';
  const hosts = Array.isArray(m.hosts) && m.hosts.length ? m.hosts.join('、') : m.host || '無';
  const deleteButton =
    isMine && m.canDelete
      ? `<button class="btn btn-outline meeting-quick-delete" type="button" data-delete-meeting="${esc(m.id)}" data-delete-name="${esc(m.name)}" aria-label="刪除 ${esc(m.name)}" title="刪除會議">🗑</button>`
      : '';
  return `<article class="meeting-card"><div><h3>${esc(m.name)}</h3><div class="meeting-meta"><span>📅 ${m.date} ${m.start}-${m.end}</span><span>📍 ${esc(m.location || '無')}</span><span>主持人：${esc(hosts)}</span></div><div class="tags">${stateTag}${m.relation === 'host' ? '' : `<span class="tag ${m.relation}">${relationText}</span>`}</div></div><div class="meeting-card-actions"><a class="btn btn-primary" href="${href}">${buttonText}</a>${deleteButton}</div></article>`;
}

function xmlEscape(value = '') {
  return String(value).replace(
    /[&<>"']/g,
    (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' })[c],
  );
}
function crc32(bytes) {
  let c = 0xffffffff;
  for (const b of bytes) {
    c ^= b;
    for (let k = 0; k < 8; k++) c = (c >>> 1) ^ (c & 1 ? 0xedb88320 : 0);
  }
  return (c ^ 0xffffffff) >>> 0;
}
function makeZip(files) {
  const enc = new TextEncoder(),
    parts = [],
    central = [];
  let offset = 0;
  const u16 = (n) => new Uint8Array([n & 255, (n >>> 8) & 255]);
  const u32 = (n) => new Uint8Array([n & 255, (n >>> 8) & 255, (n >>> 16) & 255, (n >>> 24) & 255]);
  for (const [name, text] of files) {
    const n = enc.encode(name),
      d = enc.encode(text),
      crc = crc32(d);
    const local = new Uint8Array([
      ...u32(0x04034b50),
      ...u16(20),
      ...u16(0),
      ...u16(0),
      ...u16(0),
      ...u16(0),
      ...u32(crc),
      ...u32(d.length),
      ...u32(d.length),
      ...u16(n.length),
      ...u16(0),
      ...n,
    ]);
    parts.push(local, d);
    const cent = new Uint8Array([
      ...u32(0x02014b50),
      ...u16(20),
      ...u16(20),
      ...u16(0),
      ...u16(0),
      ...u16(0),
      ...u16(0),
      ...u32(crc),
      ...u32(d.length),
      ...u32(d.length),
      ...u16(n.length),
      ...u16(0),
      ...u16(0),
      ...u16(0),
      ...u16(0),
      ...u32(0),
      ...u32(offset),
      ...n,
    ]);
    central.push(cent);
    offset += local.length + d.length;
  }
  const centralSize = central.reduce((a, b) => a + b.length, 0),
    end = new Uint8Array([
      ...u32(0x06054b50),
      ...u16(0),
      ...u16(0),
      ...u16(files.length),
      ...u16(files.length),
      ...u32(centralSize),
      ...u32(offset),
      ...u16(0),
    ]);
  return new Blob([...parts, ...central, end], {
    type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  });
}
function xlsxCell(ref, value, style = 0) {
  return `<c r="${ref}" t="inlineStr" s="${style}"><is><t xml:space="preserve">${xmlEscape(value ?? '')}</t></is></c>`;
}
// 出席統計規則：
//   固定名單會議：應到人數 = 參與名單人數，出席率 = 名單內實到 ÷ 名單人數。
//   開放非名單人員加入：沒有應到人數與出席率，只統計實到人數。
function attendanceSummary(m, rows) {
  // 有簽到時間才算出席；資料庫不允許沒有簽到時間的出席紀錄。
  const present = (r) => !!r.in;
  if (m?.allowWalkIn) return { open: true, attended: rows.filter(present).length };
  // 出席率的分母只算受邀者；現場加入的院內人員與訪客都不計入。
  const listed = rows.filter((r) =>
    r.source ? r.source === 'Invited' : r.participantType !== 'External',
  );
  const attended = listed.filter(present).length,
    expected = listed.length;
  return {
    open: false,
    attended,
    expected,
    rate: expected ? Math.round((attended / expected) * 100) : 0,
  };
}
function summaryText(s) {
  return s.open
    ? [`實到人數：${s.attended}`, '開放非名單人員加入，不計算出席率']
    : [`應到人數：${s.expected}`, `實到人數：${s.attended}`, `出席率：${s.rate}%`];
}
function downloadExcel(rows, m) {
  const summaryParts = summaryText(attendanceSummary(m, rows));
  const headers = [
    '簽到時間',
    '簽退時間',
    '單位',
    '姓名（員工編號）',
    '職稱',
    '出席狀況',
    '代理人單位',
    '代理人姓名（員工編號）',
    '未出席原因',
  ];
  const person = (name, id) => (id ? `${name || ''}（${id}）` : name || '');
  const year = Number(String(m.date || '').slice(0, 4)),
    rocYear = Number.isFinite(year) && year > 1911 ? year - 1911 : '';
  const period = (m.subName || '').trim(),
    subtitle = `${rocYear ? `${rocYear}年度` : ''}${period ? ` ${period}` : ''}會議簽到單`;
  const sheetRows = [];
  sheetRows.push(`<row r="1" ht="28" customHeight="1">${xlsxCell('A1', '示範醫院', 1)}</row>`);
  sheetRows.push(`<row r="2" ht="32" customHeight="1">${xlsxCell('A2', m.name, 2)}</row>`);
  sheetRows.push(`<row r="3" ht="28" customHeight="1">${xlsxCell('A3', subtitle, 1)}</row>`);
  sheetRows.push(
    `<row r="4">${xlsxCell('A4', `時間：${m.date} ${m.start}～${m.end}`, 3)}${xlsxCell('E4', `地點：${m.location || '無'}`, 3)}</row>`,
  );
  sheetRows.push(
    `<row r="5" ht="28" customHeight="1">${headers.map((h, i) => xlsxCell(String.fromCharCode(65 + i) + '5', h, 4)).join('')}</row>`,
  );
  rows.forEach((r, i) => {
    const n = i + 6,
      vals = [
        r.in,
        r.out,
        r.department,
        person(r.name, r.employeeId),
        r.title,
        r.attendanceStatus,
        r.proxyDepartment,
        person(r.proxyName, r.proxyEmployeeNo),
        r.note,
      ];
    sheetRows.push(
      `<row r="${n}" ht="24" customHeight="1">${vals.map((v, j) => xlsxCell(String.fromCharCode(65 + j) + n, v, 5)).join('')}</row>`,
    );
  });
  const statRow = rows.length + 7;
  sheetRows.push(
    `<row r="${statRow}" ht="24" customHeight="1">${summaryParts.map((t, i) => xlsxCell(['A', 'C', 'E'][i] + statRow, t, 3)).join('')}</row>`,
  );
  const sheet = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cols><col min="1" max="2" width="11" customWidth="1"/><col min="3" max="3" width="18" customWidth="1"/><col min="4" max="4" width="24" customWidth="1"/><col min="5" max="5" width="18" customWidth="1"/><col min="6" max="6" width="12" customWidth="1"/><col min="7" max="7" width="18" customWidth="1"/><col min="8" max="8" width="25" customWidth="1"/><col min="9" max="9" width="18" customWidth="1"/></cols><sheetData>${sheetRows.join('')}</sheetData><mergeCells count="5"><mergeCell ref="A1:I1"/><mergeCell ref="A2:I2"/><mergeCell ref="A3:I3"/><mergeCell ref="A4:D4"/><mergeCell ref="E4:I4"/></mergeCells><pageMargins left="0.3" right="0.3" top="0.5" bottom="0.5" header="0.2" footer="0.2"/><pageSetup orientation="landscape" paperSize="9" fitToWidth="1" fitToHeight="0"/></worksheet>`;
  const styles = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="3"><font><sz val="11"/><name val="Microsoft JhengHei"/></font><font><b/><sz val="16"/><name val="Microsoft JhengHei"/></font><font><b/><sz val="20"/><name val="Microsoft JhengHei"/></font></fonts><fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEDEDED"/><bgColor indexed="64"/></patternFill></fill></fills><borders count="2"><border/><border><left style="thin"/><right style="thin"/><top style="thin"/><bottom style="thin"/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="6"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf><xf numFmtId="0" fontId="2" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center"/></xf><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment vertical="center"/></xf><xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf><xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyAlignment="1"><alignment horizontal="center" vertical="center" wrapText="1"/></xf></cellXfs><cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>`;
  const files = [
    [
      '[Content_Types].xml',
      '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>',
    ],
    [
      '_rels/.rels',
      '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
    ],
    [
      'xl/workbook.xml',
      '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="簽到名單" sheetId="1" r:id="rId1"/></sheets></workbook>',
    ],
    [
      'xl/_rels/workbook.xml.rels',
      '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>',
    ],
    ['xl/worksheets/sheet1.xml', sheet],
    ['xl/styles.xml', styles],
  ];
  const blob = makeZip(files),
    a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = `${m.name}_簽到名單.xlsx`;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

async function apiJson(url, options = {}) {
  const requestOptions = { ...options };
  const headers = new Headers(options.headers || {});
  if (options.body != null && !(options.body instanceof FormData) && !headers.has('Content-Type')) {
    headers.set('Content-Type', 'application/json');
  }
  requestOptions.headers = headers;
  const response = await fetch(url, requestOptions);
  // 登入 cookie 逾時或不存在：電腦版頁面直接回到登入頁（登入頁本身的帳密錯誤不在此處理）。
  if (response.status === 401 && LOGIN_PAGES.has(currentPageName())) {
    clearDesktopSession();
    location.replace('./');
    throw new Error('登入已逾時，請重新登入。');
  }
  const contentType = (response.headers.get('content-type') || '').toLowerCase();

  if (!contentType.includes('application/json')) {
    const raw = await response.text();
    const preview = (raw || '').replace(/\s+/g, ' ').trim().slice(0, 180);
    throw new Error(`API 回傳非 JSON (${response.status})：${url}${preview ? `｜${preview}` : ''}`);
  }

  let data;
  try {
    data = await response.json();
  } catch {
    throw new Error(`API JSON 解析失敗 (${response.status})：${url}`);
  }

  if (!response.ok) {
    throw new Error(data?.message || `API 呼叫失敗 (${response.status})：${url}`);
  }

  if (data === null || typeof data !== 'object') {
    throw new Error(`API 回傳內容無效：${url}`);
  }

  return data;
}
function currentUserID() {
  const u = getUser();
  return u.userID || u.operatorId || u.employeeNo || u.employeeId || u.sntopid || '';
}
function toMeeting(value) {
  return value?.meeting || value;
}

// 90 分鐘閒置自動登出（所有登入後頁面共用）
(function setupIdleLogout() {
  if (!LOGIN_PAGES.has(currentPageName()) || !sessionStorage.getItem('meetingLoginUser')) return;
  const IDLE_MS = 90 * 60 * 1000;
  const WARNING_MS = 60 * 1000;
  let logoutTimer, warningTimer;
  function goLogin() {
    logoutDesktop();
  }
  function showIdleWarning() {
    if (document.querySelector('#idleWarningOverlay')) return;
    const overlay = document.createElement('div');
    overlay.id = 'idleWarningOverlay';
    overlay.className = 'confirm-overlay';
    overlay.innerHTML =
      '<section class="confirm-dialog center-message" role="alertdialog" aria-modal="true"><div class="dialog-icon">!</div><h2>即將自動登出</h2><p>系統已閒置 89 分鐘，若 1 分鐘內沒有操作將自動登出。</p><div class="actions"><button class="btn btn-primary" type="button">繼續使用</button></div></section>';
    document.body.appendChild(overlay);
    overlay.querySelector('button').onclick = () => {
      overlay.remove();
      resetIdleTimer();
    };
  }
  function resetIdleTimer() {
    clearTimeout(logoutTimer);
    clearTimeout(warningTimer);
    document.querySelector('#idleWarningOverlay')?.remove();
    warningTimer = setTimeout(showIdleWarning, IDLE_MS - WARNING_MS);
    logoutTimer = setTimeout(goLogin, IDLE_MS);
  }
  ['click', 'keydown', 'input', 'change', 'touchstart', 'pointerdown'].forEach((eventName) =>
    document.addEventListener(eventName, resetIdleTimer, { passive: true }),
  );
  const originalFetch = window.fetch;
  window.fetch = async function (...args) {
    resetIdleTimer();
    return originalFetch.apply(this, args);
  };
  resetIdleTimer();
})();
