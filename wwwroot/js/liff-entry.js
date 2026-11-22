const content = document.getElementById('liffContent');
const escText = (value) =>
  String(value ?? '').replace(
    /[&<>'"]/g,
    (ch) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[ch],
  );
const setStatus = (text) => {
  content.innerHTML = `<div class="mobile-loading">${escText(text)}</div>`;
};

function showError(message, code = '') {
  const title = String(code || '').trim() === 'NOT_INVITED' ? '無法參與會議' : '無法開啟會議';
  content.innerHTML = `<section class="mobile-error"><h1>${escText(title)}</h1><p>${escText(message)}</p><button id="closeEntry" class="btn btn-primary" type="button">關閉</button></section>`;
  document.getElementById('closeEntry').onclick = () => {
    if (window.liff && liff.isInClient()) liff.closeWindow();
    else history.back();
  };
}

async function readJson(response) {
  const text = await response.text();
  let data = {};
  try {
    data = text ? JSON.parse(text) : {};
  } catch {
    data = { message: text };
  }
  if (!response.ok) {
    const error = new Error(data.message || '系統處理失敗');
    error.code = data.code || '';
    throw error;
  }
  return data;
}

function enterMeeting(result, token) {
  if (!result.mobileSession) throw new Error('無法建立會議登入狀態，請重新掃描 QR Code。');
  sessionStorage.removeItem('meetingLoginUser');
  sessionStorage.removeItem('meetingLiffMeeting');
  sessionStorage.removeItem('meetingMobileSession');
  sessionStorage.setItem('meetingLoginUser', JSON.stringify(result.user));
  sessionStorage.setItem(
    'meetingLiffMeeting',
    JSON.stringify({ ...result.meeting, meetingToken: token }),
  );
  sessionStorage.setItem('meetingMobileSession', result.mobileSession);
  location.replace(`mobile-checkin.html?token=${encodeURIComponent(token)}&source=liff`);
}

function authorize(token, idToken) {
  return fetch('api/liff/meeting/authorize', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ type: 'meeting', meetingToken: token, idToken }),
  }).then(readJson);
}

// 僅在會議不開放名單外人員時出現：必須先綁定院內身分才能進入。
function showBindForm(token, idToken, message) {
  content.innerHTML = `
        <section class="mobile-section">
          <h1>首次使用，請先綁定</h1>
          <p>${escText(message || '此會議僅限參與名單內的院內人員簽到。')}</p>
          <form class="bind-form" id="bindForm">
            <label>院內帳號
              <input id="bindAccount" type="text" autocomplete="username" autocapitalize="characters" required>
            </label>
            <label>密碼
              <input id="bindPassword" type="password" autocomplete="current-password" required>
            </label>
            <div class="bind-actions">
              <button class="btn btn-primary" type="submit" id="bindSubmit">綁定並進入會議</button>
            </div>
          </form>
          <p class="bind-note">綁定後，往後以同一個 LINE 帳號掃描會議 QR Code 時，系統會自動帶入您的單位、姓名與職稱，不需要再輸入密碼。</p>
          <p class="bind-note" id="bindError" style="color:#b51f2a"></p>
        </section>`;
  const form = document.getElementById('bindForm');
  const submit = document.getElementById('bindSubmit');
  const errorBox = document.getElementById('bindError');
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    errorBox.textContent = '';
    submit.disabled = true;
    submit.textContent = '綁定中…';
    try {
      await fetch('api/liff/bind', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          idToken,
          account: document.getElementById('bindAccount').value,
          password: document.getElementById('bindPassword').value,
        }),
      }).then(readJson);
      setStatus('綁定成功，正在進入會議…');
      enterMeeting(await authorize(token, idToken), token);
    } catch (error) {
      errorBox.textContent = error.message || '綁定失敗';
      submit.disabled = false;
      submit.textContent = '綁定並進入會議';
    }
  });
}

// LIFF URL 上的參數會先被收進 liff.state，由 liff.init() 轉址還原，
// 因此一定要等 init 完成後才讀取；若轉址尚未完成，則自行從 liff.state 取出。
function readMeetingParams() {
  const search = new URLSearchParams(location.search);
  const state = search.get('liff.state');
  if (!state) return search;
  return new URLSearchParams(state.startsWith('?') ? state.slice(1) : state);
}

async function start() {
  try {
    const config = await fetch('api/liff/config').then(readJson);
    await liff.init({ liffId: config.liffId });

    const params = readMeetingParams();
    const type = params.get('type') || 'meeting';
    const token = (params.get('token') || '').trim();
    if (type !== 'meeting' || !token) throw new Error('QR Code 缺少會議識別資料。');

    // 在 LINE 外的瀏覽器開啟時，先導向 LINE 登入再回到原網址。
    if (!liff.isLoggedIn()) {
      setStatus('正在前往 LINE 登入…');
      liff.login({ redirectUri: location.href });
      return;
    }

    const idToken = liff.getIDToken();
    if (!idToken) throw new Error('無法取得 LINE 身分驗證資料，請確認 LIFF 已開啟 openid 權限。');

    setStatus('正在載入會議資料…');
    try {
      enterMeeting(await authorize(token, idToken), token);
    } catch (error) {
      // 未綁定院內身分，且此會議不開放名單外人員：提供綁定流程。
      if (error.code === 'ACCOUNT_NOT_LINKED') {
        showBindForm(token, idToken, error.message);
        return;
      }
      throw error;
    }
  } catch (error) {
    showError(error.message || '無法開啟會議', error.code || '');
  }
}

start();
