initShell();
document.querySelector('#welcome').textContent = `您好，${getUser().name || '使用者'}`;
(async () => {
  try {
    const today = localDateString(),
      q = new URLSearchParams({ startDate: today, endDate: today });
    const list = await apiJson('api/meetings?' + q);
    const left = list.filter((m) => getAccess(m) !== 'closed').length;
    todaySummary.textContent = list.length
      ? `今天有 ${list.length} 場您主持的會議${left ? `，其中 ${left} 場尚未結束` : ''}。`
      : '今天沒有您主持的會議。';
  } catch {}
})();
