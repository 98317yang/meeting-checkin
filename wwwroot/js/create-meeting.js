initShell();
const form = document.querySelector('#meetingForm');
const user = getUser();
const currentId = currentUserID();
const editingId = new URLSearchParams(location.search).get('id');
const selected = new Map();
const staffCache = new Map();
const locationSelect = document.querySelector('#location');
const customLocation = document.querySelector('#customLocation');
const participantChecklist = document.querySelector('#participantChecklist');
const participantDept = document.querySelector('#participantDept');
const selectedPeople = document.querySelector('#selectedPeople');
const selectedCount = document.querySelector('#selectedCount');
const walkIn = document.querySelector('#allowWalkIn');
const departmentOptions = document.querySelector('#departmentOptions');
const meetingNote = document.querySelector('#meetingNote');
let endTimeEdited = false;
let departments = [];
let existing = null;

const hostControls = [
  [document.querySelector('#host1Dept'), document.querySelector('#host1'), false],
  [document.querySelector('#host2Dept'), document.querySelector('#host2'), true],
  [document.querySelector('#host3Dept'), document.querySelector('#host3'), true],
];

if (!currentId) {
  notifyWarning('登入資料缺少 UserID，請重新登入');
  setTimeout(() => (location.href = './'), 900);
}
if (editingId) {
  document.title = '修改會議';
  pageHeading.textContent = '修改會議';
  pageDescription.textContent = '修改完成後，系統將更新最後存檔時間。';
  submitButton.textContent = '儲存修改';
  cancelButton.href = `meeting-detail.html?id=${editingId}`;
}

const deptLabel = (d) => `${d.departmentID} - ${d.departmentName}`;
function deptCode(value) {
  const v = (value || '').trim();
  if (!v) return '';
  const exact = departments.find(
    (d) =>
      d.departmentID.toLowerCase() === v.toLowerCase() ||
      deptLabel(d).toLowerCase() === v.toLowerCase(),
  );
  if (exact) return exact.departmentID;
  const before = v.split(' - ')[0].trim();
  return departments.some((d) => d.departmentID.toLowerCase() === before.toLowerCase())
    ? before
    : '';
}
function setDeptInput(input, code) {
  const d = departments.find((x) => x.departmentID === code);
  input.value = d ? deptLabel(d) : code || '';
}
async function loadStaff(dept) {
  if (!dept) return [];
  if (!staffCache.has(dept))
    staffCache.set(
      dept,
      cachedApi(`api/users?departmentId=${encodeURIComponent(dept)}`, {}, 300000),
    );
  return await staffCache.get(dept);
}
async function fillHostPeople(deptInput, personSelect, optional = true, selectedId = '') {
  const dept = deptCode(deptInput.value);
  if (!dept) {
    personSelect.innerHTML = optional
      ? '<option value="">不指定</option>'
      : '<option value="">請先選擇單位</option>';
    return;
  }
  setDeptInput(deptInput, dept);
  const rows = await loadStaff(dept);
  personSelect.innerHTML =
    (optional ? '<option value="">不指定</option>' : '<option value="">請選擇人員</option>') +
    rows
      .map(
        (x) => `<option value="${esc(x.userID)}">${esc(x.name)}（${esc(x.employeeId)}）</option>`,
      )
      .join('');
  if (selectedId) personSelect.value = selectedId;
}
async function setHost(index, userId) {
  if (!userId) return;
  try {
    const result = await cachedApi(`api/users/profile/${encodeURIComponent(userId)}`, {}, 300000);
    const profile = result.user || {};
    const dept = (profile.departmentId || profile.dpid || '').trim();
    if (dept) {
      setDeptInput(hostControls[index][0], dept);
      await fillHostPeople(
        hostControls[index][0],
        hostControls[index][1],
        hostControls[index][2],
        userId,
      );
      return;
    }
  } catch {}
  // 僅在個人資料查不到單位時才使用舊式逐單位搜尋作為相容備援。
  for (const dept of departments.map((x) => x.departmentID)) {
    const rows = await loadStaff(dept);
    const person = rows.find((x) => x.userID === userId);
    if (person) {
      setDeptInput(hostControls[index][0], dept);
      await fillHostPeople(
        hostControls[index][0],
        hostControls[index][1],
        hostControls[index][2],
        userId,
      );
      return;
    }
  }
}
function hostIds() {
  return [host1.value, host2.value, host3.value].filter(Boolean);
}
async function renderSelected() {
  const ids = new Set(hostIds());
  const hostRows = (
    await Promise.all(
      hostControls.map(async (control, i) => {
        const id = control[1].value;
        if (!id) return null;
        const dept = deptCode(control[0].value);
        const rows = await loadStaff(dept);
        const person = rows.find((x) => x.userID === id);
        return {
          ...(person || { userID: id, name: id, dept: '' }),
          isHost: true,
          hostIndex: i + 1,
        };
      }),
    )
  ).filter(Boolean);
  const manual = [...selected.values()].filter((x) => !ids.has(x.userID));
  selectedCount.textContent = new Set([...ids, ...manual.map((x) => x.userID)]).size;
  const hostHtml = hostRows.map(
    (x) =>
      `<span class="selected-chip host-chip"><small>主持人 ${x.hostIndex}</small>${esc(x.dept || x.department || '未設定')}／${esc(x.name)}</span>`,
  );
  const manualHtml = manual.map(
    (x) =>
      `<span class="selected-chip"><small>院內</small>${esc(x.dept || x.department || '未設定')}／${esc(x.name)}<button type="button" data-id="${esc(x.userID)}">×</button></span>`,
  );
  selectedPeople.innerHTML =
    hostHtml.length || manualHtml.length
      ? hostHtml.concat(manualHtml).join('')
      : '<span class="hint">尚未選擇人員</span>';
  selectedPeople.querySelectorAll('button[data-id]').forEach(
    (b) =>
      (b.onclick = () => {
        selected.delete(b.dataset.id);
        renderSelected();
      }),
  );
}
let checklistRows = new Map();
function renderChecklist(rows, { showDept = false, emptyText = '此單位目前沒有可選人員' } = {}) {
  checklistRows = new Map(rows.map((x) => [x.userID, x]));
  const taken = new Set([...selected.keys(), ...hostIds()]);
  participantChecklist.innerHTML = rows.length
    ? rows
        .map((x) => {
          const already = taken.has(x.userID);
          return `<label class="participant-check-item${already ? ' is-added' : ''}"><input type="checkbox" value="${esc(x.userID)}"${already ? ' disabled' : ''}><span>${esc(x.name)}（${esc(x.employeeId)}）${showDept && x.dept ? `<small> · ${esc(x.dept)}</small>` : ''}${already ? '<small> · 已在名單</small>' : ''}</span></label>`;
        })
        .join('')
    : `<span class="hint">${emptyText}</span>`;
}
async function loadParticipantChecklist() {
  const dept = deptCode(participantDept.value);
  if (!dept) {
    checklistRows = new Map();
    participantChecklist.innerHTML = '<span class="hint">請從下拉建議選擇完整單位</span>';
    return;
  }
  participantSearch.value = '';
  setDeptInput(participantDept, dept);
  renderChecklist(await loadStaff(dept));
}
let searchTimer = null,
  searchSeq = 0;
function onParticipantSearch() {
  clearTimeout(searchTimer);
  searchTimer = setTimeout(async () => {
    const q = participantSearch.value.trim(),
      seq = ++searchSeq;
    if (!q) {
      if (deptCode(participantDept.value)) loadParticipantChecklist();
      else {
        checklistRows = new Map();
        participantChecklist.innerHTML = '<span class="hint">請先選擇單位或輸入姓名搜尋</span>';
      }
      return;
    }
    participantDept.value = '';
    try {
      const rows = await apiJson(`api/users/search?q=${encodeURIComponent(q)}`);
      if (seq !== searchSeq) return;
      renderChecklist(rows, { showDept: true, emptyText: '找不到符合的人員' });
    } catch (e) {
      notifyError(e.message);
    }
  }, 250);
}
function updateLocationControl() {
  const has = document.querySelector('input[name="hasLocation"]:checked')?.value === '1';
  locationSelect.hidden = !has;
  locationSelect.required = false;
  if (!has) {
    locationSelect.value = '';
    customLocation.hidden = true;
    customLocation.required = false;
    customLocation.value = '';
  } else {
    updateCustomLocation();
  }
}
function isCustomLocation() {
  const t = locationSelect.selectedOptions?.[0]?.textContent?.trim() || '';
  return locationSelect.value === 'custom' || t === '自行填寫';
}
function updateCustomLocation() {
  const custom = isCustomLocation();
  customLocation.hidden = !custom;
  customLocation.required = custom;
  if (!custom) customLocation.value = '';
}
const showCenterDialog = (message) => showDialog(message);
function addOneHour(value) {
  if (!/^\d{2}:\d{2}$/.test(value)) return '';
  const [h, m] = value.split(':').map(Number);
  const total = (h * 60 + m + 60) % (24 * 60);
  return (
    String(Math.floor(total / 60)).padStart(2, '0') + ':' + String(total % 60).padStart(2, '0')
  );
}
const OPEN_NOTE = '允許非名單人員加入';
function syncOpenNote() {
  const lines = meetingNote.value.split(/\r?\n/).filter((x) => x.trim() !== OPEN_NOTE);
  meetingNote.value = lines.join('\n').trim();
}

async function initialize() {
  try {
    const [locations, depts] = await Promise.all([
      apiJson('api/meeting-locations'),
      cachedApi('api/departments', {}, 300000),
    ]);
    departments = depts || [];
    locationSelect.innerHTML =
      '<option value="">請選擇地點</option>' +
      locations
        .map((x) => `<option value="${x.locationID}">${esc(x.locationName)}</option>`)
        .join('') +
      '<option value="custom">自行填寫</option>';
    locationSelect.onchange = updateCustomLocation;
    departmentOptions.innerHTML = departments
      .map((d) => `<option value="${esc(deptLabel(d))}"></option>`)
      .join('');
    for (const [input, person, opt] of hostControls) {
      input.addEventListener('change', async () => {
        await fillHostPeople(input, person, opt);
        await renderSelected();
      });
      person.addEventListener('change', renderSelected);
    }
    participantDept.addEventListener('change', loadParticipantChecklist);
    participantSearch.addEventListener('input', onParticipantSearch);
    document
      .querySelectorAll('input[name="hasLocation"]')
      .forEach((x) => (x.onchange = updateLocationControl));
    addParticipant.onclick = async () => {
      const checked = [
        ...participantChecklist.querySelectorAll('input[type="checkbox"]:checked'),
      ].map((x) => x.value);
      if (!checked.length) return notifyWarning('請至少勾選一位人員');
      let duplicate = false;
      for (const id of checked) {
        if (selected.has(id) || hostIds().includes(id)) {
          duplicate = true;
          continue;
        }
        const person = checklistRows.get(id);
        if (person) selected.set(id, person);
      }
      await renderSelected();
      renderChecklist([...checklistRows.values()], { showDept: !!participantSearch.value.trim() });
      if (duplicate) showCenterDialog('部分人員已在本次會議名單中，系統未重複加入。');
    };
    meetingStart.addEventListener('change', () => {
      if (!endTimeEdited || !meetingEnd.value) meetingEnd.value = addOneHour(meetingStart.value);
    });
    meetingEnd.addEventListener('change', () => {
      endTimeEdited = true;
    });

    if (editingId) {
      const result = await apiJson(`api/meetings/${editingId}`);
      existing = result.meeting;
      if (existing.relation !== 'host') throw new Error('只有會議發起者可以修改會議');
      form.elements.name.value = existing.name || '';
      form.elements.subName.value = existing.subName || '';
      form.elements.date.value = existing.date;
      form.elements.start.value = existing.start;
      form.elements.end.value = existing.end;
      meetingNote.value = existing.note || '';
      syncOpenNote();
      walkIn.checked = !!existing.allowWalkIn;
      document.querySelector(
        `input[name="hasLocation"][value="${existing.hasLocation ? '1' : '0'}"]`,
      ).checked = true;
      updateLocationControl();
      if (existing.hasLocation) {
        if (existing.locationID) {
          locationSelect.value = String(existing.locationID);
        } else if (existing.location && existing.location !== '無') {
          locationSelect.value = 'custom';
          updateCustomLocation();
          customLocation.value = existing.location;
        }
      }
      await Promise.all([
        setHost(0, existing.host1UserID),
        setHost(1, existing.host2UserID),
        setHost(2, existing.host3UserID),
      ]);
      const currentHosts = new Set(hostIds());
      (result.participants || []).forEach((x) => {
        if (
          String(x.participantType || '').toLowerCase() === 'internal' &&
          (x.source || 'Invited') === 'Invited' &&
          x.userID &&
          !currentHosts.has(x.userID)
        )
          selected.set(x.userID, {
            ...x,
            dept: (x.dept || x.department || x.organization || '').trim(),
          });
      });
      await renderSelected();
      // 依會議狀態鎖定欄位。後端也會檢查，這裡是讓使用者一開始就知道哪些不能改。
      const locks = [];
      if (existing.hasAttendance) {
        meetingDate.disabled = true;
        locks.push('已有人簽到，會議日期不能修改');
      }
      if (new Date(existing.endDateTime) < new Date()) {
        [
          host1Dept,
          host1,
          host2Dept,
          host2,
          host3Dept,
          host3,
          walkIn,
          participantDept,
          participantSearch,
          addParticipant,
        ].forEach((el) => {
          if (el) el.disabled = true;
        });
        participantChecklist.classList.add('disabled');
        selectedPeople.classList.add('disabled');
        locks.push('會議已結束，主持人、參與名單與開放設定不能修改');
      }
      if (locks.length) {
        const notice = document.createElement('div');
        notice.className = 'notice';
        notice.style.marginBottom = '16px';
        notice.textContent = locks.join('；') + '。';
        form.prepend(notice);
      }
    } else {
      form.elements.date.value = localDateString();
      updateLocationControl();
      const currentDepartment = (user.departmentId || user.dpid || '').trim();
      if (currentDepartment) {
        setDeptInput(hostControls[0][0], currentDepartment);
        await fillHostPeople(hostControls[0][0], hostControls[0][1], false, currentId);
        if (!hostControls[0][1].value) {
          const opt = [...hostControls[0][1].options].find((x) => x.value === currentId);
          if (opt) hostControls[0][1].value = currentId;
        }
      }
      await renderSelected();
    }
  } catch (e) {
    console.error(e);
    notifyError(e.message);
  }
}

if (typeof MaterialTimePicker === 'function') {
  MaterialTimePicker.attach('#meetingStart', { minuteStep: 5, title: '選擇會議開始時間' });
  MaterialTimePicker.attach('#meetingEnd', { minuteStep: 5, title: '選擇會議結束時間' });
}

form.onsubmit = async (event) => {
  event.preventDefault();
  syncOpenNote();
  const has = document.querySelector('input[name="hasLocation"]:checked')?.value === '1';
  const useCustomLocation = isCustomLocation();
  if (has && !useCustomLocation && !locationSelect.value) {
    notifyWarning('勾選有會議地點時，請選擇地點');
    return locationSelect.focus();
  }
  if (has && useCustomLocation && !customLocation.value.trim()) {
    notifyWarning('請輸入會議地點');
    return customLocation.focus();
  }
  if (!form.elements.start.value || !form.elements.end.value)
    return notifyWarning('請選擇會議開始及結束時間');
  if (form.elements.end.value <= form.elements.start.value)
    return notifyWarning('會議結束時間必須晚於開始時間');
  const hosts = hostIds();
  if (!host1.value) return notifyWarning('請選擇主持人 1');
  if (new Set(hosts).size !== hosts.length) return notifyWarning('主持人不可重複');
  const payload = {
    name: meetingName.value.trim(),
    subName: meetingSubName.value.trim(),
    date: meetingDate.value,
    start: meetingStart.value,
    end: meetingEnd.value,
    hasLocation: has,
    locationID: has && !useCustomLocation ? locationSelect.value : null,
    customLocation: has && useCustomLocation ? customLocation.value.trim() : null,
    host1UserID: host1.value,
    host2UserID: host2.value || null,
    host3UserID: host3.value || null,
    note: meetingNote.value.trim(),
    participantUserIDs: [...selected.keys()],
    allowWalkIn: walkIn.checked,
    rowVersion: existing?.rowVersion || null,
  };
  submitButton.disabled = true;
  try {
    const result = await apiJson(editingId ? `api/meetings/${editingId}` : 'api/meetings', {
      method: editingId ? 'PUT' : 'POST',
      body: JSON.stringify(payload),
    });
    sessionStorage.setItem(
      'meetingFlash',
      editingId
        ? '會議資料已更新'
        : `會議已建立${result.meetingNo ? '，編號：' + result.meetingNo : ''}`,
    );
    location.href = `meeting-detail.html?id=${encodeURIComponent(result.meetingID)}`;
  } catch (e) {
    notifyError(e.message);
    submitButton.disabled = false;
  }
};
initialize();
