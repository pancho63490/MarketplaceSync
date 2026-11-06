// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// Formularios lentos (fotos, etc.): <form data-busy="Texto…"> desactiva el botón y muestra el texto mientras trabaja.
document.addEventListener('submit', function (e) {
    var form = e.target;
    if (!(form instanceof HTMLFormElement) || !form.dataset.busy || e.defaultPrevented) return;
    var button = e.submitter || form.querySelector('button[type=submit]');
    if (!button) return;
    setTimeout(function () { button.disabled = true; button.textContent = form.dataset.busy; }, 0);
});
