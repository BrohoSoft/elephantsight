// Applica il tema prima che la pagina venga disegnata, così non si vede il
// lampo del tema sbagliato al caricamento. È un file e non uno script inline
// perché la Content Security Policy ammette solo script serviti dal dominio.
// La logica è la stessa di src/theme.ts, che prende il controllo dopo.
(function () {
  var preference = "system";
  try {
    preference = localStorage.getItem("flarelytics.theme") || "system";
  } catch (e) {}
  var dark = preference === "dark" || (preference === "system" && window.matchMedia("(prefers-color-scheme: dark)").matches);
  document.documentElement.dataset.theme = dark ? "dark" : "light";
})();
