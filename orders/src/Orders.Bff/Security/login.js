// Shows the password on request and marks the button busy while the form is sent; the form works without it.
(() => {
  const password = document.getElementById('password');
  const reveal = document.getElementById('reveal');
  const submit = document.querySelector('#login-form [type="submit"]');
  const submitText = document.getElementById('submit-text');
  reveal.hidden = false;
  reveal.addEventListener('click', () => {
    const shown = password.type === 'password';
    password.type = shown ? 'text' : 'password';
    reveal.setAttribute('aria-pressed', String(shown));
    reveal.querySelector('.eye').toggleAttribute('hidden', shown);
    reveal.querySelector('.eye-slash').toggleAttribute('hidden', !shown);
    password.focus();
  });
  document.getElementById('login-form').addEventListener('submit', () => {
    submit.disabled = true;
    submitText.textContent = 'Entrando…';
  });
  // Back from the next page, the browser may restore this one as it was left: busy.
  window.addEventListener('pageshow', () => {
    submit.disabled = false;
    submitText.textContent = 'Entrar';
  });
})();
