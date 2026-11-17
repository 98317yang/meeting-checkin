(function (global) {
  'use strict';

  const DEFAULTS = {
    minuteStep: 5,
    title: '選擇時間',
    cancelText: '取消',
    confirmText: '確定'
  };

  function pad(value) {
    return String(value).padStart(2, '0');
  }

  function parseTime(value) {
    const matched = /^(\d{1,2}):(\d{2})$/.exec(String(value || '').trim());
    if (!matched) return null;
    const hour = Number(matched[1]);
    const minute = Number(matched[2]);
    if (hour < 0 || hour > 23 || minute < 0 || minute > 59) return null;
    return { hour, minute };
  }

  class MaterialTimePicker {
    constructor(input, options) {
      this.input = typeof input === 'string' ? document.querySelector(input) : input;
      if (!this.input) throw new Error('找不到時間輸入欄位');

      this.options = Object.assign({}, DEFAULTS, options || {});
      this.stage = 'hour';
      this.hour = 9;
      this.minute = 0;
      this.overlay = null;
      this.dialog = null;
      this.lastFocusedElement = null;

      this.input.setAttribute('aria-haspopup', 'dialog');
      this.input.setAttribute('aria-expanded', 'false');
      this.input.readOnly = true;

      this.handleOpen = (event) => {
        if (event) event.preventDefault();
        this.open();
      };

      // pointerdown 可避免部分 Chrome/Windows 在 readonly 欄位只取得焦點、不觸發預期 click 的情況。
      this.input.addEventListener('pointerdown', this.handleOpen);
      this.input.addEventListener('click', this.handleOpen);
      this.input.addEventListener('keydown', (event) => {
        if (event.key === 'Enter' || event.key === ' ' || event.key === 'ArrowDown') {
          event.preventDefault();
          this.open();
        }
      });

      this.bindTriggers();
    }

    bindTriggers() {
      if (!this.input.id) return;
      document.querySelectorAll(`[data-time-target="${CSS.escape(this.input.id)}"]`).forEach((button) => {
        if (button.dataset.timePickerBound === '1') return;
        button.dataset.timePickerBound = '1';
        button.addEventListener('click', (event) => {
          event.preventDefault();
          this.open();
        });
      });
    }

    open() {
      const currentOverlay = document.querySelector('.mtp-overlay');
      if (currentOverlay) {
        if (this.overlay === currentOverlay) return;
        currentOverlay.remove();
      }

      this.lastFocusedElement = document.activeElement;
      const parsed = parseTime(this.input.value);
      const now = new Date();
      this.hour = parsed ? parsed.hour : now.getHours();
      this.minute = parsed
        ? parsed.minute
        : Math.round(now.getMinutes() / this.options.minuteStep) * this.options.minuteStep;

      if (this.minute >= 60) {
        this.minute = 0;
        this.hour = (this.hour + 1) % 24;
      }

      this.stage = 'hour';
      this.renderDialog();
      this.renderClock();
      document.body.classList.add('mtp-open');
      this.input.setAttribute('aria-expanded', 'true');

      requestAnimationFrame(() => {
        this.dialog?.querySelector('.mtp-number.selected, .mtp-number, .mtp-cancel')?.focus();
      });
    }

    close() {
      if (!this.overlay) return;
      this.overlay.remove();
      this.overlay = null;
      this.dialog = null;
      document.body.classList.remove('mtp-open');
      this.input.setAttribute('aria-expanded', 'false');

      if (this.lastFocusedElement && typeof this.lastFocusedElement.focus === 'function') {
        this.lastFocusedElement.focus();
      }
    }

    confirm() {
      this.input.value = `${pad(this.hour)}:${pad(this.minute)}`;
      this.input.dispatchEvent(new Event('input', { bubbles: true }));
      this.input.dispatchEvent(new Event('change', { bubbles: true }));
      this.close();
    }

    renderDialog() {
      this.overlay = document.createElement('div');
      this.overlay.className = 'mtp-overlay';
      this.overlay.innerHTML = `
        <section class="mtp-dialog" role="dialog" aria-modal="true" aria-label="${this.options.title}">
          <div class="mtp-title">${this.options.title}</div>
          <div class="mtp-display" aria-label="目前選擇時間">
            <button type="button" class="mtp-display-part mtp-display-hour" aria-label="選擇小時"></button>
            <span class="mtp-colon">:</span>
            <button type="button" class="mtp-display-part mtp-display-minute" aria-label="選擇分鐘"></button>
          </div>
          <div class="mtp-clock" aria-live="polite">
            <div class="mtp-hand"></div>
            <div class="mtp-center-dot"></div>
            <div class="mtp-numbers"></div>
          </div>
          <div class="mtp-actions">
            <button type="button" class="mtp-text-button mtp-cancel">${this.options.cancelText}</button>
            <button type="button" class="mtp-text-button mtp-confirm">${this.options.confirmText}</button>
          </div>
        </section>`;

      document.body.appendChild(this.overlay);
      this.dialog = this.overlay.querySelector('.mtp-dialog');

      this.overlay.addEventListener('pointerdown', (event) => {
        if (event.target === this.overlay) this.close();
      });
      this.dialog.querySelector('.mtp-cancel').addEventListener('click', () => this.close());
      this.dialog.querySelector('.mtp-confirm').addEventListener('click', () => this.confirm());
      this.dialog.querySelector('.mtp-display-hour').addEventListener('click', () => {
        this.stage = 'hour';
        this.renderClock();
      });
      this.dialog.querySelector('.mtp-display-minute').addEventListener('click', () => {
        this.stage = 'minute';
        this.renderClock();
      });
      this.overlay.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') this.close();
      });
    }

    renderClock() {
      if (!this.dialog) return;

      const hourButton = this.dialog.querySelector('.mtp-display-hour');
      const minuteButton = this.dialog.querySelector('.mtp-display-minute');
      hourButton.textContent = pad(this.hour);
      minuteButton.textContent = pad(this.minute);
      hourButton.classList.toggle('active', this.stage === 'hour');
      minuteButton.classList.toggle('active', this.stage === 'minute');

      const numbers = this.dialog.querySelector('.mtp-numbers');
      numbers.innerHTML = '';
      const values = this.stage === 'hour'
        ? [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 0]
        : Array.from({ length: 12 }, (_, index) => index * this.options.minuteStep);

      values.forEach((value, index) => {
        const isHour = this.stage === 'hour';
        const inner = isHour && (value === 0 || value >= 13);
        const dialIndex = isHour
          ? (value === 0 ? 12 : value > 12 ? value - 12 : value)
          : (index === 0 ? 12 : index);
        const angle = dialIndex * 30;
        const radius = inner ? 72 : 112;
        const radian = (angle - 90) * Math.PI / 180;
        const x = 140 + radius * Math.cos(radian);
        const y = 140 + radius * Math.sin(radian);
        const selected = isHour ? value === this.hour : value === this.minute;

        const button = document.createElement('button');
        button.type = 'button';
        button.className = `mtp-number${inner ? ' inner' : ''}${selected ? ' selected' : ''}`;
        button.textContent = pad(value);
        button.style.left = `${x}px`;
        button.style.top = `${y}px`;
        button.setAttribute('aria-label', isHour ? `${value} 時` : `${value} 分`);
        button.addEventListener('click', () => {
          if (isHour) {
            this.hour = value;
            this.stage = 'minute';
          } else {
            this.minute = value;
          }
          this.renderClock();
        });
        numbers.appendChild(button);
      });

      const selectedValue = this.stage === 'hour' ? this.hour : this.minute;
      const isInner = this.stage === 'hour' && (selectedValue === 0 || selectedValue >= 13);
      const angle = this.stage === 'hour'
        ? (selectedValue === 0 ? 12 : selectedValue > 12 ? selectedValue - 12 : selectedValue) * 30
        : ((selectedValue / this.options.minuteStep) * 30 || 360);
      const hand = this.dialog.querySelector('.mtp-hand');
      hand.style.height = `${isInner ? 72 : 112}px`;
      hand.style.transform = `rotate(${angle === 360 ? 0 : angle}deg)`;
      this.dialog.querySelector('.mtp-clock').setAttribute(
        'aria-label',
        this.stage === 'hour' ? '請選擇小時' : '請選擇分鐘'
      );
    }

    destroy() {
      this.input.removeEventListener('pointerdown', this.handleOpen);
      this.input.removeEventListener('click', this.handleOpen);
      this.close();
      delete this.input.__materialTimePicker;
    }

    static attach(input, options) {
      const element = typeof input === 'string' ? document.querySelector(input) : input;
      if (!element) throw new Error('找不到時間輸入欄位');
      if (element.__materialTimePicker) {
        element.__materialTimePicker.options = Object.assign(
          {},
          element.__materialTimePicker.options,
          options || {}
        );
        element.__materialTimePicker.bindTriggers();
        return element.__materialTimePicker;
      }
      const picker = new MaterialTimePicker(element, options);
      element.__materialTimePicker = picker;
      return picker;
    }
  }

  function autoAttach() {
    document.querySelectorAll('[data-material-timepicker]').forEach((input) => {
      MaterialTimePicker.attach(input, {
        minuteStep: Number(input.dataset.minuteStep || 5),
        title: input.dataset.timeTitle || DEFAULTS.title
      });
    });
  }

  global.MaterialTimePicker = MaterialTimePicker;
  global.initMaterialTimePickers = autoAttach;

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', autoAttach, { once: true });
  } else {
    autoAttach();
  }
})(window);
