initShell();
function meetingSection(title, items, emptyText) {
  return `<section class="today-section"><h2>${title}</h2><div class="meeting-list">${items.length ? items.map((m) => meetingCard(m)).join('') : `<div class="panel today-empty">${emptyText}</div>`}</div></section>`;
}
(async () => {
  try {
    const today = localDateString(),
      q = new URLSearchParams({ startDate: today, endDate: today });
    const data = await apiJson('api/meetings?' + q);
    const groups = { open: [], pending: [], closed: [] };
    data
      .sort((a, b) => String(a.start).localeCompare(String(b.start)))
      .forEach((m) => groups[getAccess(m)].push(m));
    list.innerHTML = data.length
      ? [
          ['進行中', groups.open],
          ['稍後開始', groups.pending],
          ['已結束', groups.closed],
        ]
          .filter(([, items]) => items.length)
          .map(([t, items]) => meetingSection(t, items, ''))
          .join('')
      : `<div class="panel empty-state"><p>今天沒有您主持的會議。</p><a class="btn btn-primary" href="create-meeting.html">發起會議</a></div>`;
  } catch (e) {
    list.innerHTML = `<div class="panel">${esc(e.message)}</div>`;
  }
})();
