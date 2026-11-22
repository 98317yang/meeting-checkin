initShell();
(async () => {
  let meeting = null,
    statsRequestInFlight = false;
  const qrParams = new URLSearchParams(location.search),
    id = qrParams.get('id'),
    qrFrom = qrParams.get('from') === 'detail' ? 'detail' : 'today';
  if (qrFrom === 'detail') {
    backBtn.href = `meeting-detail.html?id=${encodeURIComponent(id)}`;
    backBtn.textContent = '返回會議資料';
  }
  async function refreshStats() {
    if (statsRequestInFlight) return;
    statsRequestInFlight = true;
    try {
      const result = await apiJson(`api/meetings/${encodeURIComponent(id)}/attendance`);
      const allRows = result.rows || [];
      // 固定名單會議只統計名單內的人，和簽到名單、會議資料頁的規則一致。
      const rows = meeting?.allowUnlistedParticipants
        ? allRows
        : allRows.filter((r) => r.participantType !== 'External');
      signedInCount.textContent = rows.filter((x) => x.in).length;
      signedOutCount.textContent = rows.filter((x) => x.out).length;
      const summary = attendanceSummary(meeting, rows);
      expectedStat.hidden = summary.open;
      if (!summary.open) expectedCount.textContent = summary.expected;
    } catch {
    } finally {
      statsRequestInFlight = false;
    }
  }
  try {
    const r = await apiJson(`api/meetings/${id}`),
      m = r.meeting;
    meeting = m;
    title.textContent = m.name;
    if (m.relation !== 'host') throw new Error('您沒有權限查看此會議 QR Code。');
    qrImage.src = `api/qr-code/${encodeURIComponent(m.qrCode)}?v=20260811-rc1-pathfix`;
    info.innerHTML = `<p><b>日期時間：</b>${esc(m.date)} ${esc(m.start)}-${esc(m.end)}</p><p><b>地點：</b>${esc(m.location || '無')}</p><p><b>主持人：</b>${esc(Array.isArray(m.hosts) && m.hosts.length ? m.hosts.join('、') : m.host || '無')}</p><p><b>備註：</b>${esc(m.note || '無')}</p>`;
    expectedStat.hidden = !!m.allowUnlistedParticipants;
    const updateQrState = () => {
      const state = meetingAccessState(m),
        { openAt, end } = meetingWindow(m),
        hm = (d) =>
          `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
      qrNotice.textContent =
        state === 'closed'
          ? `本會議已於 ${hm(end)} 結束，無法再簽到。`
          : state === 'pending'
            ? `簽到將於 ${m.date === localDateString(openAt) ? '' : localDateString(openAt) + ' '}${hm(openAt)} 開放，可先列印或投影此 QR Code。`
            : '請使用 LINE 掃描 QR Code 完成簽到／簽退';
      qrNotice.classList.toggle('notice-muted', state === 'closed');
      qrImage.style.opacity = state === 'closed' ? '.35' : '';
      regenerateBtn.hidden = state === 'closed';
    };
    // QR Code 外流時讓舊碼失效。舊碼與用舊碼進入的手機畫面都會失效，現場的人重新掃描即可。
    regenerateBtn.addEventListener('click', async () => {
      if (
        !(await showConfirm(
          '重新產生後，舊的 QR Code（包含已列印或被拍照傳出去的）會立即失效，已掃碼但還沒簽退的人需要重新掃描。已完成的簽到不受影響。',
          { confirmText: '重新產生' },
        ))
      )
        return;
      regenerateBtn.disabled = true;
      try {
        const result = await apiJson(`api/meetings/${encodeURIComponent(id)}/qr-code/regenerate`, {
          method: 'POST',
        });
        m.qrCode = result.qrCode;
        qrImage.src = `api/qr-code/${encodeURIComponent(m.qrCode)}`;
        await showDialog('已產生新的 QR Code，舊的已失效。', { type: 'success' });
      } catch (e) {
        showDialog(e.message);
      } finally {
        regenerateBtn.disabled = false;
      }
    });
    updateQrState();
    setInterval(updateQrState, 30000);
    if (m.relation === 'host') {
      listBtn.style.display = 'inline-block';
      listBtn.href = `attendance.html?id=${encodeURIComponent(m.id)}&from=qr&qrFrom=${qrFrom}`;
      await refreshStats();
      setInterval(refreshStats, 5000);
    }
  } catch (e) {
    info.textContent = e.message;
    qrImage.removeAttribute('src');
    showDialog(e.message);
  }
})();
