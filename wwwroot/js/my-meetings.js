initShell();
const flash = sessionStorage.getItem('meetingFlash');
if (flash) {
  sessionStorage.removeItem('meetingFlash');
  setTimeout(() => notifySuccess(flash), 100);
}

const pageSize = 15;
let allMeetings = [];
let currentPage = 1;
const startKey = (m) => String(m.startDateTime || `${m.date}T${m.start}`);
// 全部會議放在同一個清單：進行中與即將舉行的在前（由近到遠），已結束的在後（由新到舊）。
// 每張卡片本身會標示會議狀態。
function sortedMeetings() {
  const active = allMeetings
    .filter((m) => getAccess(m) !== 'closed')
    .sort((a, b) => startKey(a).localeCompare(startKey(b)));
  const ended = allMeetings
    .filter((m) => getAccess(m) === 'closed')
    .sort((a, b) => startKey(b).localeCompare(startKey(a)));
  return [...active, ...ended];
}

function renderPagination() {
  const rows = sortedMeetings();
  const totalPages = Math.max(1, Math.ceil(rows.length / pageSize));
  if (currentPage > totalPages) currentPage = totalPages;
  pagination.innerHTML =
    rows.length > pageSize
      ? Array.from(
          { length: totalPages },
          (_, i) =>
            `<button type="button" class="${i + 1 === currentPage ? 'active' : ''}" data-page="${i + 1}">${i + 1}</button>`,
        ).join('')
      : '';
}

function renderPage() {
  renderPagination();
  const start = (currentPage - 1) * pageSize;
  const pageRows = sortedMeetings().slice(start, start + pageSize);
  const filtered = !!(keyword.value.trim() || from.value || to.value);
  list.innerHTML =
    pageRows.map((m) => meetingCard(m, 'mine')).join('') ||
    (filtered
      ? '<div class="panel empty-state"><p>沒有符合查詢條件的會議。</p></div>'
      : '<div class="panel empty-state"><p>目前還沒有會議。</p><a class="btn btn-primary" href="create-meeting.html">發起會議</a></div>');
}

async function render() {
  setPageBusy(true, '正在載入會議…');
  try {
    const q = new URLSearchParams();
    if (keyword.value.trim()) q.set('keyword', keyword.value.trim());
    if (from.value) q.set('startDate', from.value);
    if (to.value) q.set('endDate', to.value);
    allMeetings = await apiJson('api/meetings?' + q);
    currentPage = 1;
    renderPage();
  } catch (e) {
    allMeetings = [];
    list.innerHTML = `<div class="panel">${esc(e.message)}</div>`;
    pagination.innerHTML = '';
  } finally {
    setPageBusy(false);
  }
}

pagination.addEventListener('click', (e) => {
  const button = e.target.closest('[data-page]');
  if (!button) return;
  currentPage = Number(button.dataset.page) || 1;
  renderPage();
  window.scrollTo({ top: 0, behavior: 'smooth' });
});

list.addEventListener('click', async (e) => {
  const button = e.target.closest('[data-delete-meeting]');
  if (!button) return;
  e.preventDefault();
  const meetingNo = button.dataset.deleteMeeting;
  const name = button.dataset.deleteName || meetingNo;
  const ok = await showConfirm(`確定要刪除「${name}」嗎？`, `刪除後無法復原。`, {
    confirmText: '確定刪除',
    cancelText: '取消',
  });
  if (!ok) return;
  button.disabled = true;
  try {
    await apiJson(`api/meetings/${encodeURIComponent(meetingNo)}`, { method: 'DELETE' });
    allMeetings = allMeetings.filter((m) => String(m.id) !== String(meetingNo));
    renderPage();
    await showDialog('會議已刪除', { type: 'success' });
  } catch (err) {
    button.disabled = false;
    notifyError(err.message);
  }
});

search.onclick = render;
render();
