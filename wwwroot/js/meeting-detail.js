initShell();
const params = new URLSearchParams(location.search),
  id = params.get('id'),
  content = document.querySelector('#content');
let meeting;
const backTarget =
  params.get('from') === 'today'
    ? { href: 'sign-in.html', text: '返回今日會議' }
    : { href: 'my-meetings.html', text: '返回我的會議' };
const flash = sessionStorage.getItem('meetingFlash');
if (flash) {
  sessionStorage.removeItem('meetingFlash');
  setTimeout(() => notifySuccess(flash), 100);
}
const formatDate = (value) => String(value || '').replaceAll('-', '/');
(async () => {
  try {
    const r = await apiJson(`api/meetings/${id}`);
    meeting = r.meeting;
    title.textContent = meeting.name;
    sub.textContent = meeting.subName || '';
    const last = meeting.lastSavedAt
      ? new Date(meeting.lastSavedAt).toLocaleString('zh-TW', {
          hour12: false,
          year: 'numeric',
          month: '2-digit',
          day: '2-digit',
          hour: '2-digit',
          minute: '2-digit',
        })
      : '—';
    const hosts = (meeting.hosts || [meeting.host]).filter(Boolean);
    const hostText = hosts.length ? hosts.map((x) => esc(x)).join('、') : '—';
    const note = String(meeting.note || '').trim();
    content.classList.add('meeting-detail-panel');
    const isHost = meeting.relation === 'host';
    const state = getAccess(meeting);
    const stateText = { open: '進行中', pending: '尚未開始', closed: '已結束' }[state];
    title.innerHTML = `${esc(meeting.name)} <span class="tag ${state} detail-state-tag">${stateText}</span>`;
    const qrBtn = (cls) =>
      `<a class="btn ${cls}" href="qr.html?id=${encodeURIComponent(id)}&from=detail">開啟 QR Code</a>`;
    const listBtn = (cls) =>
      `<a class="btn ${cls}" href="attendance.html?id=${encodeURIComponent(id)}">查看簽到名單</a>`;
    // 已結束的會議最常做的是看簽到名單；尚未結束則以開啟 QR Code 為主要操作。
    const primaryActions = !isHost
      ? ''
      : (state === 'closed'
          ? listBtn('btn-primary') + qrBtn('btn-outline')
          : qrBtn('btn-primary') + listBtn('btn-outline')) +
        `<a class="btn btn-outline" href="create-meeting.html?id=${encodeURIComponent(id)}">修改會議</a>`;
    content.innerHTML = `
        <div class="detail-actions detail-actions-top">
          <div class="detail-actions-primary">${isHost ? primaryActions : ''}</div>
          <div class="detail-actions-secondary">
            <a class="btn btn-light" href="${backTarget.href}">${backTarget.text}</a>
            ${meeting.canDelete ? `<button class="btn btn-danger-outline" id="deleteMeeting">刪除會議</button>` : ''}
          </div>
        </div>
        <section class="detail-summary">
          <div class="detail-main-grid">
            <div class="detail-main-item"><span class="detail-label">日期</span><span class="detail-value">${esc(formatDate(meeting.date))}</span></div>
            <div class="detail-main-item"><span class="detail-label">時間</span><span class="detail-value">${esc(meeting.start)}～${esc(meeting.end)}</span></div>
            <div class="detail-main-item"><span class="detail-label">地點</span><span class="detail-value">${esc(meeting.location || '無')}</span></div>
          </div>
          <div class="detail-hosts"><span class="detail-label">主持人</span><span class="detail-value">${hostText}</span></div>
          <div class="attendance-block">
            <span class="detail-label">${meeting.allowWalkIn ? '出席狀況（開放非名單人員加入，不計算出席率）' : '出席狀況'}</span>
            <div id="attendanceSummaryBox" class="attendance-summary">${meeting.allowWalkIn ? '' : `應到 <span class="attendance-value">${meeting.participantCount ?? 0}</span><span class="attendance-unit">人</span>`}</div>
          </div>
          <div class="detail-meta">會議編號 ${esc(meeting.meetingNo || meeting.id)}　·　最後更新 ${esc(last)}</div>
        </section>
        ${note ? `<div class="note-card"><span class="detail-label">會議說明／備註</span><span class="detail-value">${esc(note)}</span></div>` : ''}`;
    if (isHost) {
      apiJson(`api/meetings/${encodeURIComponent(id)}/attendance`)
        .then((result) => {
          const s = attendanceSummary(meeting, result.rows || []);
          document.querySelector('#attendanceSummaryBox').innerHTML = s.open
            ? `實到 <span class="attendance-value">${s.attended}</span><span class="attendance-unit">人</span>`
            : `應到 <span class="attendance-value">${s.expected}</span><span class="attendance-unit">人</span>　實到 <span class="attendance-value">${s.attended}</span><span class="attendance-unit">人</span>　出席率 <span class="attendance-value">${s.rate}%</span>`;
        })
        .catch(() => {});
    }
    document.querySelector('#deleteMeeting')?.addEventListener('click', removeMeeting);
  } catch (e) {
    title.textContent = '找不到會議';
    sub.textContent = e.message;
    content.innerHTML = '<a class="btn btn-light" href="my-meetings.html">返回我的會議</a>';
  }
})();
async function removeMeeting() {
  if (!(await showConfirm(`確定刪除「${meeting.name}」？`, { confirmText: '確定刪除' }))) return;
  try {
    await apiJson(`api/meetings/${id}`, { method: 'DELETE' });
    sessionStorage.setItem('meetingFlash', '會議已刪除');
    location.href = 'my-meetings.html';
  } catch (e) {
    notifyError(e.message);
  }
}
