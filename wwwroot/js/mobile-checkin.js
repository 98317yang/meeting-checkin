const params = new URLSearchParams(location.search);
const source = (params.get('source') || '').trim().toLowerCase();
const testEmployeeId = (params.get('testEmployeeId') || '').trim();
const testGuest = (params.get('testGuest') || '').trim() === '1';
let meetingId = params.get('meeting') || params.get('id');
const meetingToken = (params.get('token') || '').trim();

let mobileSession = sessionStorage.getItem('meetingMobileSession') || '';
let userJson = sessionStorage.getItem('meetingLoginUser') || '';
let meetingJson = sessionStorage.getItem('meetingLiffMeeting') || '';

function readLiffSession() {
  if (
    !['liff', 'dev'].includes(source) ||
    !meetingToken ||
    !mobileSession ||
    !userJson ||
    !meetingJson
  ) {
    return {
      ok: false,
      message: '此頁面必須由 LINE 會議 QR Code 驗證後進入，請重新掃描 QR Code。',
    };
  }

  try {
    const authorizedUser = JSON.parse(userJson);
    const authorizedMeeting = JSON.parse(meetingJson);
    const authorizedToken = String(authorizedMeeting.meetingToken || '').trim();

    if (
      !authorizedUser ||
      !authorizedMeeting ||
      !authorizedToken ||
      authorizedToken !== meetingToken
    ) {
      return { ok: false, message: '會議驗證資訊不一致，請重新掃描正確的 QR Code。' };
    }

    return { ok: true, user: authorizedUser, meeting: authorizedMeeting };
  } catch {
    return { ok: false, message: 'LINE 會議驗證資訊已失效，請重新掃描 QR Code。' };
  }
}

let authorizedSession = readLiffSession();

async function devLogin() {
  if (source !== 'dev') return true;
  if (!meetingToken || (!testEmployeeId && !testGuest)) {
    renderError('Development 測試模式需要 token，並指定 testEmployeeId 或 testGuest=1。');
    return false;
  }
  try {
    const result = await apiJson(
      testGuest ? 'api/dev/mobile-guest-session' : 'api/dev/mobile-session',
      {
        method: 'POST',
        body: JSON.stringify(
          testGuest ? { meetingToken } : { employeeNo: testEmployeeId, meetingToken },
        ),
      },
    );
    mobileSession = result.mobileSession || '';
    userJson = JSON.stringify(result.user || {});
    meetingJson = JSON.stringify({ ...result.meeting, meetingToken });
    sessionStorage.setItem('meetingMobileSession', mobileSession);
    sessionStorage.setItem('meetingLoginUser', userJson);
    sessionStorage.setItem('meetingLiffMeeting', meetingJson);
    authorizedSession = readLiffSession();
    return authorizedSession.ok;
  } catch (e) {
    renderError(e.message || '無法建立 Development 手機測試 Session。');
    return false;
  }
}

async function mobileApiJson(url, options = {}) {
  if (!authorizedSession.ok || !mobileSession) {
    throw new Error(authorizedSession.message || 'LINE 會議驗證已失效，請重新掃描 QR Code。');
  }

  return apiJson(url, {
    ...options,
    headers: {
      ...(options.headers || {}),
      'X-Meeting-Session': mobileSession,
    },
  });
}

let user = authorizedSession.ok ? authorizedSession.user : {};
let meeting = null,
  attendance = {},
  participants = [];
let selectedInternalProxyEmployeeNo = '';
let selectedExternalIndex = '';
let refreshTimer = null;
let loadingAttendance = false;
let loadingPage = false;
let guestEdited = false;

const userEmployeeNo = () =>
  String(
    user.employeeNo || user.employeeId || user.sntopid || user.userID || user.operatorId || '',
  ).trim();
const isInternal = (row) => String(row.participantType || '').toLowerCase() === 'internal';
const isExternal = (row) => String(row.participantType || '').toLowerCase() === 'external';
const formatPerson = (row) => {
  if (isInternal(row))
    return `${row.name || row.employeeId || ''}${row.employeeId ? `（${row.employeeId}）` : ''}`;
  return `${row.guestName || row.name || ''}${row.guestOrg || row.organization ? `｜${row.guestOrg || row.organization}` : ''}`;
};

function needGuestProfile() {
  return !!(
    authorizedSession?.user?.requiresProfile || authorizedSession?.meeting?.requiresProfile
  );
}

function renderGuestProfile() {
  const authMeeting = authorizedSession.meeting || {};
  const topUser = document.getElementById('mobileTopUser');
  if (topUser) {
    topUser.innerHTML = `<strong class="mobile-top-user-name">訪客</strong>`;
  }
  mobileContent.innerHTML = `
        <section class="mobile-meeting-hero">
          <h1>${esc(authMeeting.name || '會議簽到')}</h1>
          ${authMeeting.subName ? `<p class="mobile-meeting-subtitle">${esc(authMeeting.subName)}</p>` : ''}
          <div class="mobile-meeting-facts">
            <div><span>日期</span><strong>${esc(authMeeting.date || '未設定')}</strong></div>
            <div><span>時間</span><strong>${esc(authMeeting.start || '--:--')}－${esc(authMeeting.end || '--:--')}</strong></div>
            <div class="wide"><span>地點</span><strong>${esc(authMeeting.location || '無')}</strong></div>
          </div>
        </section>
        <section class="mobile-operation-card">
          <h2 class="mobile-section-heading">填寫簽到資料</h2>
          <p class="mobile-section-description">請直接填寫以下資料。</p>
          <p class="mobile-help">院內員工請先洽資訊室完成 LINE 綁定。</p>
          <div class="field guest-profile-field"><label for="guestOrganization">單位</label><input id="guestOrganization" type="text" maxlength="200" autocomplete="organization" placeholder="請輸入單位"></div>
          <div class="field guest-profile-field"><label for="guestName">姓名</label><input id="guestName" type="text" maxlength="100" autocomplete="name" placeholder="請輸入姓名"></div>
          <div class="field guest-profile-field"><label for="guestTitle">職稱</label><input id="guestTitle" type="text" maxlength="100" placeholder="請輸入職稱"></div>
          <div class="guest-profile-actions"><button id="guestProfileConfirm" class="mobile-action-btn checkin" type="button">確定並進入簽到</button></div>
          <p id="guestProfileState" class="mobile-help">未完整輸入資料前無法使用簽到功能。</p>
        </section>`;

  document.querySelectorAll('#guestOrganization,#guestName,#guestTitle').forEach((input) => {
    input.addEventListener('input', () => {
      guestEdited = true;
    });
  });
  document.getElementById('guestProfileConfirm')?.addEventListener('click', saveGuestProfile);
}

async function saveGuestProfile() {
  if (loadingAttendance) return;
  const organization = (document.getElementById('guestOrganization')?.value || '').trim();
  const name = (document.getElementById('guestName')?.value || '').trim();
  const title = (document.getElementById('guestTitle')?.value || '').trim();
  const state = document.getElementById('guestProfileState');
  if (!organization || !name || !title) {
    await showMobileNotice('請完整輸入單位、姓名及職稱。', 'warning');
    return;
  }
  const confirmed = await confirmGuestProfile({ organization, name, title });
  if (!confirmed) return;
  const meetingNo = authorizedSession.meeting?.meetingNo || authorizedSession.meeting?.id || '';
  if (!meetingNo) return renderError('找不到會議編號，請重新掃描 QR Code。');
  loadingAttendance = true;
  try {
    const result = await mobileApiJson(
      `api/meetings/${encodeURIComponent(meetingNo)}/guest-profile`,
      {
        method: 'POST',
        body: JSON.stringify({ organization, name, title }),
      },
    );
    mobileSession = result.mobileSession || mobileSession;
    const nextUser = {
      ...(authorizedSession.user || {}),
      ...(result.user || {}),
      requiresProfile: false,
    };
    const nextMeeting = { ...(authorizedSession.meeting || {}), requiresProfile: false };
    sessionStorage.setItem('meetingMobileSession', mobileSession);
    sessionStorage.setItem('meetingLoginUser', JSON.stringify(nextUser));
    sessionStorage.setItem('meetingLiffMeeting', JSON.stringify({ ...nextMeeting, meetingToken }));
    userJson = JSON.stringify(nextUser);
    meetingJson = JSON.stringify({ ...nextMeeting, meetingToken });
    authorizedSession = { ok: true, user: nextUser, meeting: nextMeeting };
    user = nextUser;
    guestEdited = false;
    await loadPage();
  } catch (e) {
    await showMobileNotice(e.message || '資料儲存失敗。', 'error');
  } finally {
    loadingAttendance = false;
  }
}

async function loadPage() {
  if (!authorizedSession.ok) return renderError(authorizedSession.message);
  if (!meetingToken) return renderError('缺少會議 QR Token');
  if (needGuestProfile()) {
    renderGuestProfile();
    return;
  }
  if (loadingPage) return;
  loadingPage = true;
  try {
    user = authorizedSession.user;
    const resolvedEmployeeNo = userEmployeeNo();
    if (!resolvedEmployeeNo) {
      return renderError('尚未取得可用的簽到身分，請關閉此頁後重新掃描 QR Code。');
    }

    const liffMeetingNo =
      authorizedSession.meeting?.meetingNo || authorizedSession.meeting?.id || meetingId;
    if (liffMeetingNo) meetingId = liffMeetingNo;
    const detailUrl = `api/meetings/by-token/${encodeURIComponent(meetingToken)}`;
    const attendancePromise = meetingId
      ? mobileApiJson(`api/meetings/${encodeURIComponent(meetingId)}/my-attendance`).catch(
          () => ({}),
        )
      : Promise.resolve(null);
    const [result, attendanceResult] = await Promise.all([
      mobileApiJson(detailUrl),
      attendancePromise,
    ]);
    meetingId = result.meeting?.meetingNo || result.meeting?.id || meetingId;
    meeting = result.meeting || result;
    participants = result.participants || [];
    attendance =
      attendanceResult === null
        ? await mobileApiJson(`api/meetings/${encodeURIComponent(meetingId)}/my-attendance`).catch(
            () => ({}),
          )
        : attendanceResult;
    render();
  } catch (e) {
    renderError(e.message || '無法載入會議資料');
  } finally {
    loadingPage = false;
  }
}

function renderError(message) {
  const blockedMessage = '本會議不允許非指定名單人員加入。';
  const title = String(message || '').trim() === blockedMessage ? '無法參與會議' : '無法開啟會議';
  mobileContent.innerHTML = `<section class="mobile-error"><h1>${esc(title)}</h1><p>${esc(message)}</p></section>`;
}

function render() {
  const accessState = getAccess(meeting);
  const isHost = meeting.relation === 'host';
  const signedIn = !!attendance.signInTime;
  const signedOut = !!attendance.signOutTime;
  const employeeNo = userEmployeeNo();
  const myProxyNo = String(attendance.proxyEmployeeNo || '').trim();
  const selfProxySigned = !!myProxyNo;
  const myProxyName = attendance.proxyName || myProxyNo || '代理人';
  const displayName = user.name || '未取得';
  const displayDepartment = user.dpcnm || user.department || '未取得';
  const displayTitle = user.dutnm || user.title || '未取得';

  const topUser = document.getElementById('mobileTopUser');
  if (topUser) {
    topUser.innerHTML = `
          <span class="mobile-top-user-dept">${esc(displayDepartment)}</span>
          <strong class="mobile-top-user-name">${esc(displayName)}${String(user.participantType || '').toLowerCase() === 'external' ? '' : ` · ${esc(employeeNo || '未取得')}`}</strong>
          <small class="mobile-top-user-meta">${esc(displayTitle)}</small>
        `;
  }

  const internalRows = participants.filter(isInternal);
  const externalRows = participants.filter(isExternal);
  const canProxy = (row) =>
    !row.signInTime || String(row.proxyEmployeeNo || '').trim() === employeeNo;
  const staffProxyRows = internalRows.filter(
    (row) => String(row.employeeId || '') !== employeeNo && canProxy(row),
  );
  const guestProxyIndexes = externalRows
    .map((row, index) => ({ row, index }))
    .filter((x) => canProxy(x.row));

  mobileContent.innerHTML = `
        <section class="mobile-meeting-hero">
          <h1>${esc(meeting.name)}</h1>
          ${meeting.subName ? `<p class="mobile-meeting-subtitle">${esc(meeting.subName)}</p>` : ''}
          <div class="mobile-meeting-facts">
            <div><span>日期</span><strong>${esc(meeting.date || '未設定')}</strong></div>
            <div><span>時間</span><strong>${esc(meeting.start || '--:--')}－${esc(meeting.end || '--:--')}</strong></div>
            <div class="wide"><span>地點</span><strong>${esc(meeting.location || meeting.customLocation || '無')}</strong></div>
          </div>
        </section>

        <section class="mobile-operation-card">
          <h2 class="mobile-section-heading">本人簽到／簽退</h2>
          <div class="mobile-record-summary">
            <div><span>簽到時間</span><strong>${esc(attendance.signInTime || '尚未簽到')}</strong></div>
            <div><span>簽退時間</span><strong>${esc(attendance.signOutTime || '尚未簽退')}</strong></div>
          </div>
          <div class="mobile-actions primary-mobile-actions">
            <button id="checkInBtn" class="mobile-action-btn checkin" type="button" ${selfProxySigned || signedIn || accessState !== 'open' ? 'disabled' : ''}>${selfProxySigned ? '已由代理人簽到' : signedIn ? '已完成簽到' : '簽到'}</button>
            <button id="checkOutBtn" class="mobile-action-btn checkout" type="button" ${selfProxySigned || !signedIn || signedOut ? 'disabled' : ''}>${selfProxySigned ? '由代理人簽退' : signedOut ? '已完成簽退' : '簽退'}</button>
          </div>
          <p class="mobile-help">${
            selfProxySigned
              ? signedOut
                ? `已由 ${esc(myProxyName)}（${esc(myProxyNo)}）完成代簽到／簽退。`
                : `已由 ${esc(myProxyName)}（${esc(myProxyNo)}）代簽，請由原代簽人完成簽退。`
              : accessState === 'pending'
                ? '目前尚未開放簽到。'
                : accessState === 'closed'
                  ? '本會議已結束，無法再簽到。'
                  : '本人簽到與簽退會立即寫入簽到名單。'
          }</p>
        </section>

        <section class="mobile-operation-card mobile-delegation-card ${meeting.allowWalkIn ? 'is-disabled' : ''}">
          <h2 class="mobile-section-heading">院內人員代簽</h2>
          <p class="mobile-section-description">${meeting.allowWalkIn ? '此會議允許非名單人員加入，因此不可代簽。' : '所有本次會議的院內參與者皆可替其他院內人員代簽，系統會記錄實際操作人。'}</p>
          <div class="field">
            <label for="internalProxyPerson">代簽對象</label>
            <select id="internalProxyPerson" ${meeting.allowWalkIn ? 'disabled' : ''}>
              <option value="">請選擇院內參與者</option>
              ${staffProxyRows
                .map(
                  (row, index) => `
                <option value="${esc(row.employeeId || '')}" data-index="${index}" ${String(row.employeeId || '') === selectedInternalProxyEmployeeNo ? 'selected' : ''}>
                  ${esc(formatPerson(row))}
                </option>`,
                )
                .join('')}
            </select>
          </div>
          <div id="internalProxyState" class="mobile-selection-state">${meeting.allowWalkIn ? '此會議不可代簽' : '請先選擇代簽對象。'}</div>
          <div class="mobile-actions">
            <button id="internalProxySignIn" class="mobile-action-btn checkin" type="button" disabled>代為簽到</button>
            <button id="internalProxySignOut" class="mobile-action-btn checkout" type="button" disabled>代為簽退</button>
          </div>
        </section>


      `;

  document.getElementById('checkInBtn')?.addEventListener('click', () => {
    if (!selfProxySigned) saveOwnAttendance('signin');
  });
  document.getElementById('checkOutBtn')?.addEventListener('click', () => {
    if (!selfProxySigned)
      confirmAction(
        '確定要簽退嗎？',
        '簽退後無法再次於手機修改簽退時間。如需更正，請由主持人於簽到單調整。',
        () => saveOwnAttendance('signout'),
      );
  });

  const internalSelect = document.getElementById('internalProxyPerson');

  function refreshProxyInputs() {
    const row = internalRows.find(
      (x) => String(x.employeeId || '') === String(internalSelect?.value || ''),
    );
    const state = document.getElementById('internalProxyState');
    const signInBtn = document.getElementById('internalProxySignIn');
    const signOutBtn = document.getElementById('internalProxySignOut');

    if (meeting?.allowWalkIn) {
      if (state) state.textContent = '此會議不可代簽';
      if (signInBtn) signInBtn.disabled = true;
      if (signOutBtn) signOutBtn.disabled = true;
      return;
    }
    if (!row) {
      if (state) state.textContent = '請先選擇代簽對象。';
      if (signInBtn) signInBtn.disabled = true;
      if (signOutBtn) signOutBtn.disabled = true;
      return;
    }

    selectedInternalProxyEmployeeNo = String(row.employeeId || '');
    if (state) {
      const signInText = row.signInTime ? row.signInTime : '尚未簽到';
      const signOutText = row.signOutTime ? row.signOutTime : '尚未簽退';

      state.textContent = `${formatPerson(row)}｜簽到：${signInText}｜簽退：${signOutText}`;
    }

    if (signInBtn) signInBtn.disabled = !!row.signInTime || accessState !== 'open';
    if (signOutBtn) signOutBtn.disabled = !row.signInTime || !!row.signOutTime;
  }

  internalSelect?.addEventListener('change', () => {
    selectedInternalProxyEmployeeNo = internalSelect.value || '';
    refreshProxyInputs();
  });
  refreshProxyInputs();

  document
    .getElementById('internalProxySignIn')
    ?.addEventListener('click', () => saveInternalProxy('signin'));
  document
    .getElementById('internalProxySignOut')
    ?.addEventListener('click', () => saveInternalProxy('signout'));

  const externalSelect = document.getElementById('externalPerson');

  function refreshGuestInputs() {
    const row = externalRows[Number(externalSelect?.value)];
    const state = document.getElementById('externalPersonState');
    const signInBtn = document.getElementById('externalSignIn');
    const signOutBtn = document.getElementById('externalSignOut');

    if (!row || externalSelect?.value === '') {
      if (state)
        state.textContent = guestProxyIndexes.length
          ? '請先選擇院外人員。'
          : '目前沒有可操作的院外人員。';
      if (signInBtn) signInBtn.disabled = true;
      if (signOutBtn) signOutBtn.disabled = true;
      return;
    }

    selectedExternalIndex = externalSelect.value;

    const signInText = row.signInTime ? row.signInTime : '尚未簽到';
    const signOutText = row.signOutTime ? row.signOutTime : '尚未簽退';

    if (state) {
      state.textContent = `${formatPerson(row)}｜簽到：${signInText}｜簽退：${signOutText}`;
    }

    if (signInBtn) signInBtn.disabled = !!row.signInTime || accessState !== 'open';
    if (signOutBtn) signOutBtn.disabled = !row.signInTime || !!row.signOutTime;
  }

  externalSelect?.addEventListener('change', () => {
    selectedExternalIndex = externalSelect.value;
    refreshGuestInputs();
  });
  refreshGuestInputs();

  document
    .getElementById('externalSignIn')
    ?.addEventListener('click', () => saveExternalAttendance('signin'));
  document
    .getElementById('externalSignOut')
    ?.addEventListener('click', () => saveExternalAttendance('signout'));
}

async function saveOwnAttendance(action) {
  if (loadingAttendance) return;
  loadingAttendance = true;
  try {
    await mobileApiJson(`api/meetings/${encodeURIComponent(meetingId)}/attendance/${action}`, {
      method: 'POST',
      body: JSON.stringify({ employeeNo: userEmployeeNo() }),
    });
    await showMobileNotice(
      action === 'signin'
        ? '簽到成功，電腦端簽到名單將自動更新。'
        : '簽退成功，電腦端簽到名單將自動更新。',
      'success',
    );
    await loadPage();
  } catch (e) {
    showMobileNotice(e.message, 'error');
  } finally {
    loadingAttendance = false;
  }
}

async function saveInternalProxy(action) {
  if (loadingAttendance) return;
  if (meeting?.allowWalkIn) return showMobileNotice('此會議不可代簽。');
  const select = document.getElementById('internalProxyPerson');
  const employeeNo = select?.value || '';
  if (!employeeNo) return showMobileNotice('請先選擇院內代簽對象。');
  const row = participants.find((x) => isInternal(x) && String(x.employeeId || '') === employeeNo);
  if (!row) return showMobileNotice('找不到代簽對象。');
  if (action === 'signin' && row.signInTime) return showMobileNotice('此人員已完成簽到。');
  if (action === 'signout' && !row.signInTime)
    return showMobileNotice('此人員尚未簽到，不能代為簽退。');
  if (action === 'signout' && row.signOutTime) return showMobileNotice('此人員已完成簽退。');

  const execute = async () => {
    loadingAttendance = true;
    try {
      await mobileApiJson(`api/meetings/${encodeURIComponent(meetingId)}/attendance/proxy`, {
        method: 'POST',
        body: JSON.stringify({
          action,
          participantType: 'internal',
          participantEmployeeNo: employeeNo,
          guestName: null,
          guestOrg: null,
          proxyEmployeeNo: userEmployeeNo(),
          operatedByEmployeeNo: userEmployeeNo(),
        }),
      });
      await showMobileNotice(
        action === 'signin'
          ? '代簽到成功，簽到名單將自動更新。'
          : '代簽退成功，簽到名單將自動更新。',
        'success',
      );
      selectedInternalProxyEmployeeNo = employeeNo;
      await loadPage();
    } catch (e) {
      showMobileNotice(e.message, 'error');
      await loadPage();
    } finally {
      loadingAttendance = false;
    }
  };
  if (action === 'signout')
    return confirmAction(
      '確定要代為簽退嗎？',
      `${formatPerson(row)}。簽退後無法再次於手機修改簽退時間。如需更正，請由主持人於簽到單調整。`,
      execute,
    );
  execute();
}

async function saveExternalAttendance(action) {
  if (loadingAttendance) return;
  const select = document.getElementById('externalPerson');
  if (!select || select.value === '') return showMobileNotice('請先選擇院外人員。');

  const row = participants.filter(isExternal)[Number(select.value)];
  if (!row) return showMobileNotice('找不到院外人員資料。');

  if (action === 'signin' && row.signInTime) return showMobileNotice('此院外人員已完成簽到。');

  if (action === 'signout' && !row.signInTime)
    return showMobileNotice('此院外人員尚未簽到，不能簽退。');

  if (action === 'signout' && row.signOutTime) return showMobileNotice('此院外人員已完成簽退。');

  const execute = async () => {
    loadingAttendance = true;
    try {
      await mobileApiJson(`api/meetings/${encodeURIComponent(meetingId)}/attendance/proxy`, {
        method: 'POST',
        body: JSON.stringify({
          action,
          participantType: 'external',
          participantEmployeeNo: null,
          guestName: row.guestName || row.name,
          guestOrg: row.guestOrg || row.organization,
          proxyEmployeeNo: userEmployeeNo(),
          operatedByEmployeeNo: userEmployeeNo(),
        }),
      });

      await showMobileNotice(
        action === 'signin'
          ? '院外人員簽到成功，電腦端簽到名單將自動更新。'
          : '院外人員簽退成功，電腦端簽到名單將自動更新。',
        'success',
      );

      selectedExternalIndex = select.value;
      await loadPage();
    } catch (e) {
      showMobileNotice(e.message, 'error');
      await loadPage();
    } finally {
      loadingAttendance = false;
    }
  };

  if (action === 'signout') {
    return confirmAction(
      '確定要替此院外人員簽退嗎？',
      `${formatPerson(row)}。簽退後無法再次於手機修改簽退時間。如需更正，請由主持人於簽到單調整。`,
      execute,
    );
  }

  execute();
}

function confirmGuestProfile({ organization, name, title }) {
  return new Promise((resolve) => {
    const overlay = document.createElement('div');
    overlay.className = 'mobile-confirm-overlay';
    overlay.innerHTML = `<section class="mobile-confirm-dialog guest-confirm-dialog"><div class="confirm-symbol">!</div><h2>請確認簽到資料</h2><div class="guest-confirm-summary"><div><span>單位</span><strong>${esc(organization)}</strong></div><div><span>姓名</span><strong>${esc(name)}</strong></div><div><span>職稱</span><strong>${esc(title)}</strong></div></div><p>確認後將以此資料進入簽到介面。</p><div class="mobile-confirm-actions"><button class="btn btn-light" data-cancel>取消</button><button class="btn btn-primary" data-confirm>確認</button></div></section>`;
    document.body.appendChild(overlay);
    overlay.querySelector('[data-cancel]').onclick = () => {
      overlay.remove();
      resolve(false);
    };
    overlay.querySelector('[data-confirm]').onclick = () => {
      overlay.remove();
      resolve(true);
    };
  });
}

function confirmAction(title, message, onConfirm) {
  const overlay = document.createElement('div');
  overlay.className = 'mobile-confirm-overlay';
  overlay.innerHTML = `<section class="mobile-confirm-dialog"><div class="confirm-symbol">!</div><h2>${esc(title)}</h2><p>${esc(message)}</p><div class="mobile-confirm-actions"><button class="btn btn-light" data-cancel>取消</button><button class="btn btn-primary" data-confirm>確定</button></div></section>`;
  document.body.appendChild(overlay);
  overlay.querySelector('[data-cancel]').onclick = () => overlay.remove();
  overlay.querySelector('[data-confirm]').onclick = () => {
    overlay.remove();
    onConfirm();
  };
}

function showMobileNotice(message, type = 'warning') {
  return showDialog(message, { type });
}

function startMobileAutoRefresh() {
  if (refreshTimer) clearInterval(refreshTimer);
  refreshTimer = setInterval(() => {
    if (
      !document.hidden &&
      !loadingAttendance &&
      !loadingPage &&
      !guestEdited &&
      !document.activeElement?.matches?.('#guestOrganization,#guestName,#guestTitle')
    )
      loadPage();
  }, 10000);
}
async function startMobilePage() {
  const ok = await devLogin();
  if (!ok) return;
  await loadPage();
  startMobileAutoRefresh();
}
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', startMobilePage, { once: true });
} else {
  startMobilePage();
}
document.addEventListener('visibilitychange', () => {
  if (!document.hidden && !loadingAttendance && !loadingPage && !guestEdited) loadPage();
});
