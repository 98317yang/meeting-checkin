initShell();
const attendanceParams = new URLSearchParams(location.search);
const meetingId = attendanceParams.get('id');
const returnSource = (attendanceParams.get('from') || '').trim().toLowerCase();
// originals：載入時每一列的原始值。存檔時只送出有改過的列，並附上原始值，
// 讓後端確認這段期間沒有人改過（例如本人用手機簽到），避免用過期的畫面蓋掉新紀錄。
const rowSnapshot = (r) => ({
  in: r.in || '',
  out: r.out || '',
  operator: r.proxyEmployeeNo || '',
  note: r.note || '',
});
let originals = new Map();
let meeting = null,
  rows = [],
  dirty = false,
  departments = [],
  usersByDepartment = new Map(),
  autoRefreshTimer = null,
  loadInFlight = false;
back.href = meetingId
  ? returnSource === 'qr'
    ? `qr.html?id=${encodeURIComponent(meetingId)}&from=${encodeURIComponent(attendanceParams.get('qrFrom') || 'today')}`
    : `meeting-detail.html?id=${encodeURIComponent(meetingId)}`
  : 'my-meetings.html';
back.textContent =
  returnSource === 'qr' ? '返回 QR Code' : meetingId ? '返回會議資料' : '返回我的會議';
const fullName = (name, id) => (id ? `${name || ''}（${id}）` : name || '');
const rocYear = (date) => {
  const y = Number(String(date || '').slice(0, 4));
  return Number.isFinite(y) && y > 1911 ? y - 1911 : '';
};
const reportSubtitle = (m) => {
  const year = rocYear(m?.date);
  const prefix = year ? `${year}年度` : '';
  const period = (m?.subName || '').trim();
  return `${prefix}${period ? ` ${period}` : ''}會議簽到單`;
};
const timeParts = (value) => {
  const match = String(value || '').match(/^(\d{2}):(\d{2})$/);
  return match ? { hour: match[1], minute: match[2] } : { hour: '', minute: '' };
};
const timePicker = (value, kind) =>
  `<input class="time-input-inline" type="time" step="60" data-time-kind="${kind}" value="${esc(timeParts(value).hour ? value : '')}" aria-label="${kind === 'in' ? '簽到時間' : '簽退時間'}"><span class="print-time" data-print-kind="${kind}">${esc(value || '')}</span>`;
const departmentOptions = (selected) =>
  `<option value="">不指定</option>${departments.map((d) => `<option value="${esc(d.departmentID)}"${d.departmentName === selected || d.departmentID === selected ? ' selected' : ''}>${esc(d.departmentName)}</option>`).join('')}`;
async function getUsers(departmentId) {
  if (!departmentId) return [];
  if (!usersByDepartment.has(departmentId))
    usersByDepartment.set(
      departmentId,
      apiJsonCached(`api/users?departmentId=${encodeURIComponent(departmentId)}`, {}, 300000),
    );
  return await usersByDepartment.get(departmentId);
}
// 標記為出席（選「出席」或指定代簽人）但還沒有簽到時間時，以現在時間作為簽到時間。
// 沒有簽到時間的出席紀錄存不進資料庫，不能只在畫面上顯示出席。
// 未出席原因只給沒來的人填；一標記出席就清空並鎖住（後端也會清掉）。
function lockAbsenceReason(tr, row, locked) {
  row.note = locked ? '' : row.note;
  const input = tr.querySelector('input[data-k="note"]');
  if (input) {
    input.disabled = locked;
    if (locked) input.value = '';
  }
}
function fillSignInIfMissing(tr, row) {
  lockAbsenceReason(tr, row, true);
  if (row.in) return;
  const now = new Date();
  row.in = `${String(now.getHours()).padStart(2, '0')}:${String(now.getMinutes()).padStart(2, '0')}`;
  tr.querySelector('input[data-time-kind="in"]').value = row.in;
  tr.querySelector('[data-print-kind="in"]').textContent = row.in;
}
async function proxyUserOptions(row, departmentId) {
  const users = await getUsers(departmentId);
  return `<option value="">不指定</option>${users.map((u) => `<option value="${esc(u.userID)}"${u.userID === row.proxyEmployeeNo ? ' selected' : ''}>${esc(fullName(u.name, u.employeeId))}</option>`).join('')}`;
}
function updateStats() {
  const s = attendanceSummary(meeting, rows);
  actual.textContent = s.attended;
  expectedStat.hidden = s.open;
  rateStat.hidden = s.open;
  openStat.hidden = !s.open;
  if (!s.open) {
    expected.textContent = s.expected;
    rate.textContent = `${s.rate}%`;
  }
}
function resetAttendanceRow(tr, row) {
  row.in = '';
  row.out = '';
  row.attendanceStatus = '未出席';
  row.proxyEmployeeNo = '';
  row.proxyName = '';
  row.proxyDepartment = '';
  row.note = '';
  lockAbsenceReason(tr, row, false);
  const reason = tr.querySelector('input[data-k="note"]');
  if (reason) reason.placeholder = '未出席原因';
  for (const kind of ['in', 'out']) {
    const input = tr.querySelector(`input[data-time-kind="${kind}"]`);
    if (input) input.value = '';
    const print = tr.querySelector(`[data-print-kind="${kind}"]`);
    if (print) print.textContent = '';
  }
  const status = tr.querySelector('[data-status]');
  if (status) status.value = '未出席';
  const printStatus = tr.querySelector('[data-print-status]');
  if (printStatus) printStatus.textContent = '未出席';
  const dept = tr.querySelector('[data-proxy-department]');
  if (dept) dept.value = '';
  const proxy = tr.querySelector('[data-proxy-user]');
  if (proxy) proxy.innerHTML = '<option value="">不指定</option>';
  const printDept = tr.querySelector('[data-print-proxy-department]');
  if (printDept) printDept.textContent = '';
  const printProxy = tr.querySelector('[data-print-proxy-user]');
  if (printProxy) printProxy.textContent = '';
  const note = tr.querySelector('[data-k="note"]');
  if (note) note.value = '';
}
async function render() {
  tbody.innerHTML = rows.length
    ? rows
        .map(
          (r, i) => `<tr data-i="${i}">
        <td>${timePicker(r.in, 'in')}</td>
        <td>${timePicker(r.out, 'out')}</td>
        <td class="readonly">${esc(r.department || '')}</td>
        <td class="readonly"><div class="person-main">${esc(fullName(r.name, r.employeeId))}</div></td>
        <td class="readonly">${esc(r.title || '')}</td>
        <td><select class="attendance-status-select" data-status><option value="出席"${r.attendanceStatus === '出席' ? ' selected' : ''}>出席</option><option value="未出席"${r.attendanceStatus !== '出席' ? ' selected' : ''}>未出席</option></select><span class="print-cell-value" data-print-status>${esc(r.attendanceStatus || '未出席')}</span></td>
        <td><select data-proxy-department>${departmentOptions(r.proxyDepartment || '')}</select><span class="print-cell-value" data-print-proxy-department>${esc(r.proxyDepartment || '')}</span></td>
        <td><select data-proxy-user><option value="${esc(r.proxyEmployeeNo || '')}">${esc(r.proxyEmployeeNo ? fullName(r.proxyName, r.proxyEmployeeNo) : '不指定')}</option></select><span class="print-cell-value" data-print-proxy-user>${esc(r.proxyEmployeeNo ? fullName(r.proxyName, r.proxyEmployeeNo) : '')}</span></td>
        <td><input data-k="note" value="${esc(r.note || '')}" placeholder="${r.in ? '' : '未出席原因'}"${r.in ? ' disabled' : ''}></td>
      </tr>`,
        )
        .join('')
    : '<tr><td colspan="9" class="readonly">此會議尚未設定參與人員。</td></tr>';
  const proxyRows = [...tbody.querySelectorAll('tr[data-i]')].map((tr) => {
    const index = Number(tr.dataset.i),
      row = rows[index];
    const selectedDept = departments.find(
      (d) => d.departmentName === row.proxyDepartment || d.departmentID === row.proxyDepartment,
    );
    return { tr, row, selectedDept };
  });
  const proxyDepartmentIds = [
    ...new Set(proxyRows.filter((x) => x.selectedDept).map((x) => x.selectedDept.departmentID)),
  ];
  await Promise.all(proxyDepartmentIds.map(getUsers));
  for (const item of proxyRows) {
    if (!item.selectedDept) continue;
    const deptSelect = item.tr.querySelector('[data-proxy-department]');
    deptSelect.value = item.selectedDept.departmentID;
    item.tr.querySelector('[data-proxy-user]').innerHTML = await proxyUserOptions(
      item.row,
      item.selectedDept.departmentID,
    );
  }
  tbody.querySelectorAll('input[data-k]').forEach((el) =>
    el.addEventListener('input', () => {
      const index = Number(el.closest('tr').dataset.i);
      rows[index][el.dataset.k] = el.value;
      dirty = true;
    }),
  );
  tbody.querySelectorAll('input[data-time-kind]').forEach((el) =>
    el.addEventListener('change', () => {
      const tr = el.closest('tr'),
        index = Number(tr.dataset.i),
        row = rows[index],
        kind = el.dataset.timeKind;
      const value = (el.value || '').slice(0, 5);
      if (/^\d{2}:\d{2}$/.test(value)) {
        row[kind] = value;
        tr.querySelector(`[data-print-kind="${kind}"]`).textContent = row[kind];
        if (kind === 'in') {
          row.attendanceStatus = '出席';
          tr.querySelector('[data-status]').value = '出席';
          tr.querySelector('[data-print-status]').textContent = '出席';
          lockAbsenceReason(tr, row, true);
        }
      } else if (!value) {
        row[kind] = '';
        tr.querySelector(`[data-print-kind="${kind}"]`).textContent = '';
        if (kind === 'in') resetAttendanceRow(tr, row);
      }
      dirty = true;
      updateStats();
    }),
  );
  tbody.querySelectorAll('select[data-status]').forEach((el) =>
    el.addEventListener('change', () => {
      const tr = el.closest('tr'),
        index = Number(tr.dataset.i),
        row = rows[index];
      row.attendanceStatus = el.value;
      tr.querySelector('[data-print-status]').textContent = el.value;
      if (el.value === '未出席') {
        resetAttendanceRow(tr, row);
      } else {
        fillSignInIfMissing(tr, row);
      }
      dirty = true;
      updateStats();
    }),
  );
  tbody.querySelectorAll('[data-proxy-department]').forEach((el) =>
    el.addEventListener('change', async () => {
      const tr = el.closest('tr'),
        index = Number(tr.dataset.i),
        row = rows[index],
        dept = departments.find((d) => d.departmentID === el.value);
      row.proxyDepartment = dept?.departmentName || '';
      row.proxyEmployeeNo = '';
      row.proxyName = '';
      tr.querySelector('[data-proxy-user]').innerHTML = await proxyUserOptions(row, el.value);
      tr.querySelector('[data-print-proxy-department]').textContent = row.proxyDepartment;
      tr.querySelector('[data-print-proxy-user]').textContent = '';
      dirty = true;
    }),
  );
  tbody.querySelectorAll('[data-proxy-user]').forEach((el) =>
    el.addEventListener('change', async () => {
      const tr = el.closest('tr'),
        index = Number(tr.dataset.i),
        row = rows[index],
        deptId = tr.querySelector('[data-proxy-department]').value,
        users = await getUsers(deptId),
        user = users.find((u) => u.userID === el.value);
      row.proxyEmployeeNo = user?.userID || '';
      row.proxyName = user?.name || '';
      row.proxyDepartment = user?.dept || row.proxyDepartment || '';
      tr.querySelector('[data-print-proxy-user]').textContent = row.proxyEmployeeNo
        ? fullName(row.proxyName, row.proxyEmployeeNo)
        : '';
      if (row.proxyEmployeeNo) {
        row.attendanceStatus = '出席';
        tr.querySelector('[data-status]').value = '出席';
        tr.querySelector('[data-print-status]').textContent = '出席';
        fillSignInIfMissing(tr, row);
      }
      dirty = true;
      updateStats();
    }),
  );
  updateStats();
}
async function load({ silent = false } = {}) {
  if (!meetingId) {
    title.textContent = '找不到會議';
    save.disabled = true;
    return;
  }
  if ((silent && dirty) || loadInFlight) return;
  loadInFlight = true;
  try {
    const [result, depts] = await Promise.all([
      apiJson(`api/meetings/${encodeURIComponent(meetingId)}/attendance`),
      departments.length
        ? Promise.resolve(departments)
        : apiJsonCached('api/departments', {}, 300000),
    ]);
    if (!result || typeof result !== 'object' || !result.meeting) {
      throw new Error(
        '簽到名單 API 回傳格式不正確，請確認 IIS Application 路徑與後端 API 是否正常。',
      );
    }
    meeting = result.meeting;
    rows = result.rows || [];
    originals = new Map(rows.map((r) => [r.userID, rowSnapshot(r)]));
    departments = depts || [];
    title.textContent = meeting.name;
    subTitle.textContent = reportSubtitle(meeting);
    meetingTime.textContent = `${meeting.date} ${meeting.start}～${meeting.end}`;
    meetingHosts.textContent =
      Array.isArray(meeting.hosts) && meeting.hosts.length
        ? meeting.hosts.join('、')
        : meeting.host || '無';
    meetingLocation.textContent = meeting.location || '無';
    await render();
  } catch (e) {
    if (!silent) {
      title.textContent = '無法讀取簽到名單';
      subTitle.textContent = e.message;
      tbody.innerHTML =
        '<tr><td colspan="9" class="readonly">請確認資料庫欄位及會議權限。</td></tr>';
      save.disabled = true;
    }
  } finally {
    loadInFlight = false;
  }
}
save.addEventListener('click', async () => {
  const empty = { in: '', out: '', operator: '', note: '' };
  const changed = rows.filter((r) => {
    const o = originals.get(r.userID) || empty,
      c = rowSnapshot(r);
    return c.in !== o.in || c.out !== o.out || c.operator !== o.operator || c.note !== o.note;
  });
  if (!changed.length) {
    dirty = false;
    notifySuccess('沒有需要儲存的變更');
    return;
  }
  save.disabled = true;
  try {
    await apiJson(`api/meetings/${encodeURIComponent(meetingId)}/attendance`, {
      method: 'PUT',
      body: JSON.stringify({
        rows: changed.map((r) => ({
          userID: r.userID,
          in: r.in || null,
          out: r.out || null,
          operator: r.proxyEmployeeNo || '',
          note: r.note || '',
          original: originals.get(r.userID) || empty,
        })),
      }),
    });
    dirty = false;
    await showDialog('簽到名單已儲存', { type: 'success' });
    await load();
  } catch (e) {
    notifyError(e.message);
  } finally {
    save.disabled = false;
  }
});
excel.addEventListener('click', () => meeting && downloadExcel(rows, meeting));
pdf.addEventListener('click', () => window.print());
window.addEventListener('beforeunload', (e) => {
  if (dirty) {
    e.preventDefault();
    e.returnValue = '';
  }
});
document.addEventListener('visibilitychange', () => {
  if (!document.hidden && !dirty) load({ silent: true });
});
autoRefreshTimer = setInterval(() => {
  if (!document.hidden && !dirty) load({ silent: true });
}, 5000);
load();
