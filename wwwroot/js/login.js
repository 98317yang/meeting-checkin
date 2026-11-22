const loginForm = document.querySelector('#loginForm');
const accountInput = document.querySelector('#account');
const passwordInput = document.querySelector('#password');
const passwordToggle = document.querySelector('#passwordToggle');
const statusMessage = document.querySelector('#statusMessage');
const capsLockIcon = document.querySelector('#capsLockIcon');
const loginButton = loginForm.querySelector('.login-button');

function setFieldError(input, message) {
  const inputWrap = input.closest('.input-wrap');
  const errorElement = document.querySelector(`#${input.id}Error`);
  inputWrap.classList.toggle('is-invalid', Boolean(message));
  input.setAttribute('aria-invalid', Boolean(message).toString());
  errorElement.textContent = message;
}

function clearStatus() {
  statusMessage.textContent = '';
  statusMessage.className = 'status-message';
}

function validateForm() {
  let isValid = true;
  setFieldError(accountInput, '');
  setFieldError(passwordInput, '');

  if (!accountInput.value.trim()) {
    setFieldError(accountInput, '請輸入帳號。');
    isValid = false;
  }

  if (!passwordInput.value) {
    setFieldError(passwordInput, '請輸入密碼。');
    isValid = false;
  }

  return isValid;
}

function setLoading(isLoading) {
  loginButton.disabled = isLoading;
  loginButton.classList.toggle('is-loading', isLoading);
  const label = loginButton.querySelector('.login-label');
  if (label) {
    label.textContent = isLoading ? '登入驗證中...' : '登入系統';
  }
}

passwordToggle.addEventListener('click', () => {
  const show = passwordInput.type === 'password';
  passwordInput.type = show ? 'text' : 'password';
  passwordToggle.classList.toggle('is-visible', show);
  passwordToggle.setAttribute('aria-pressed', show.toString());
  passwordToggle.setAttribute('aria-label', show ? '隱藏密碼' : '顯示密碼');
});

function updateCapsLockHint(event) {
  if (!capsLockIcon) return;
  const isOn = Boolean(event?.getModifierState?.('CapsLock'));
  capsLockIcon.hidden = !isOn;
}

accountInput.addEventListener('input', () => {
  const start = accountInput.selectionStart;
  const end = accountInput.selectionEnd;
  const upper = accountInput.value.toUpperCase();
  if (accountInput.value !== upper) {
    accountInput.value = upper;
    try {
      accountInput.setSelectionRange(start, end);
    } catch {}
  }
});

['keydown', 'keyup'].forEach((type) => {
  passwordInput.addEventListener(type, updateCapsLockHint);
});
passwordInput.addEventListener('focus', updateCapsLockHint);
passwordInput.addEventListener('blur', () => {
  if (capsLockIcon) capsLockIcon.hidden = true;
});

[accountInput, passwordInput].forEach((input) => {
  input.addEventListener('input', () => {
    if (input.getAttribute('aria-invalid') === 'true') {
      setFieldError(input, '');
    }
    clearStatus();
  });
});

loginForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  clearStatus();

  if (!validateForm()) {
    statusMessage.textContent = '請確認帳號與密碼是否完整。';
    statusMessage.classList.add('is-error');
    loginForm.querySelector('[aria-invalid="true"]')?.focus();
    return;
  }

  setLoading(true);

  try {
    const response = await fetch('api/auth/login', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        account: accountInput.value.trim().toUpperCase(),
        password: passwordInput.value,
      }),
    });

    const result = await response.json().catch(() => null);

    if (!response.ok || !result?.success) {
      statusMessage.textContent =
        response.status === 401
          ? '帳號或密碼輸入錯誤。'
          : result?.message || '目前無法登入，請稍後再試。';
      statusMessage.classList.add('is-error');
      passwordInput.value = '';
      passwordInput.focus();
      return;
    }

    sessionStorage.setItem('meetingLoginUser', JSON.stringify(result.user));
    sessionStorage.setItem('meetingLoginUserProfileAt', String(Date.now()));
    statusMessage.textContent = '登入成功。';
    statusMessage.classList.add('is-success');
    passwordInput.value = '';

    window.location.replace('dashboard.html');
  } catch (error) {
    console.error('登入發生錯誤：', error);
    statusMessage.textContent = '目前無法連線至登入服務，請稍後再試。';
    statusMessage.classList.add('is-error');
  } finally {
    setLoading(false);
  }
});
